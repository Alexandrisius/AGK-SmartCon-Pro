using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Geometry;

/// <summary>
/// Default implementation of <see cref="IFamilyGeometryPipeline"/>. Combines
/// <see cref="IFamilyGeometryExtractor"/> (Revit-side), <see cref="IGlbWriter"/>
/// (SharpGLTF, pure C#), and <see cref="IFamilyAssetService"/> (managed-storage
/// asset registration) into a single best-effort pipeline.
/// </summary>
/// <remarks>
/// <b>Threading:</b> the extractor — which calls Revit API
/// (<c>OpenDocumentFile</c>, <c>get_Geometry</c>, <c>Face.Triangulate</c>) —
/// is invoked via <see cref="IFamilyManagerAwaitableEvent.RaiseAsync{T}"/>
/// so it runs on the Revit UI thread (I-01). The GLB write, asset cleanup
/// and registration happen on the calling (background) thread using their
/// own async SQLite/I/O operations.
/// <para>
/// <b>Failure contract:</b> all exceptions are caught and logged as Warn
/// with an <c>[Action: ...]</c> suggestion (smartcon-logging L9). The
/// pipeline NEVER rethrows — the import transaction already committed
/// before the hook was reached, and a preview failure must not abort the
/// import UX.
/// </para>
/// </remarks>
public sealed class FamilyGeometryPipeline : IFamilyGeometryPipeline
{
    private readonly IFamilyGeometryExtractor _extractor;
    private readonly IGlbWriter _glbWriter;
    private readonly IFamilyAssetService _assetService;
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly LocalCatalog.LocalCatalogDatabase _database;

    public FamilyGeometryPipeline(
        IFamilyGeometryExtractor extractor,
        IGlbWriter glbWriter,
        IFamilyAssetService assetService,
        IFamilyManagerAwaitableEvent awaitableEvent,
        LocalCatalog.LocalCatalogDatabase database)
    {
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _glbWriter = glbWriter ?? throw new ArgumentNullException(nameof(glbWriter));
        _assetService = assetService ?? throw new ArgumentNullException(nameof(assetService));
        _awaitableEvent = awaitableEvent ?? throw new ArgumentNullException(nameof(awaitableEvent));
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task RunAsync(
        string managedRfaPath,
        string catalogItemId,
        string versionId,
        string versionLabel,
        string familyName,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("Geo3DPipeline",
            ("Method", nameof(RunAsync)),
            ("CatalogItemId", catalogItemId),
            ("VersionLabel", versionLabel),
            ("VersionId", versionId),
            ("FilePath", Path.GetFileName(managedRfaPath)));

        string? tempPath = null;
        try
        {
            // 1. Extract geometry on the Revit UI thread (I-01).
            //    ExtractAsync runs synchronously and returns Task.FromResult
            //    — GetAwaiter().GetResult() is safe (no deadlock risk).
            var preview = await _awaitableEvent.RaiseAsync(
                (app) => _extractor.ExtractAsync(managedRfaPath, catalogItemId, versionLabel, ct).GetAwaiter().GetResult(),
                ct).ConfigureAwait(false);

            if (preview is null || preview.IsEmpty)
            {
                SmartConLogger.Info(
                    $"Geometry pipeline skipped: no preview extracted for '{familyName}' v{versionLabel}");
                return;
            }

            // 2. Write GLB to a temporary file (IFamilyAssetService.AddAssetAsync
            //    copies it into managed storage afterwards).
            tempPath = Path.Combine(Path.GetTempPath(), $"sc_preview_{Guid.NewGuid():N}.glb");
            var ok = await _glbWriter.WriteAsync(preview, tempPath, ct).ConfigureAwait(false);
            if (!ok)
            {
                SmartConLogger.Warn(
                    $"GLB write failed for '{familyName}' v{versionLabel} " +
                    "[Action: import continues; 3D preview will be unavailable for this version]");
                return;
            }

            // 3. Delete any previous auto-extracted Preview asset for this
            //    (catalog_item_id, version_label) — supports OverwriteCurrent
            //    (ADR-040) where the same version is re-imported with new
            //    geometry. Without this, every OverwriteCurrent would
            //    accumulate a new GLB alongside the old one.
            await DeletePreviousAutoExtractedAssetAsync(catalogItemId, versionLabel, ct).ConfigureAwait(false);

            // 4. Register the GLB through the asset service, marking it with
            //    the auto-extracted-preview: prefix so the UI can tell it
            //    apart from user-uploaded Model3D files.
            var description = FamilyGeometryGlbWriter.AutoExtractedAssetDescriptionPrefix + familyName;
            var asset = await _assetService.AddAssetAsync(
                catalogItemId, versionLabel, FamilyAssetType.Model3D, tempPath, description, ct).ConfigureAwait(false);

            SmartConLogger.Info(
                $"Geometry pipeline OK: GLB registered as asset {asset.Id}, file='{asset.FileName}', " +
                $"{preview.Meshes.Count} meshes, {preview.TotalTriangleCount} triangles");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Geometry pipeline failed for '{familyName}' v{versionLabel}: " +
                $"{ex.GetType().Name}: {ex.Message} " +
                "[Action: import continues; 3D preview will be unavailable for this version — check smartcon.log for details]");
        }
        finally
        {
            if (tempPath is not null)
            {
                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch (Exception cleanupEx)
                {
                    SmartConLogger.Debug($"Temp GLB cleanup failed for '{Path.GetFileName(tempPath)}': {cleanupEx.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Removes any previous Model3D asset whose description starts with the
    /// <see cref="FamilyGeometryGlbWriter.AutoExtractedAssetDescriptionPrefix"/>,
    /// for the same (catalogItemId, versionLabel). Also deletes the physical
    /// GLB file from managed storage so the version directory does not
    /// accumulate stale previews on OverwriteCurrent.
    /// </summary>
    private async Task DeletePreviousAutoExtractedAssetAsync(
        string catalogItemId,
        string versionLabel,
        CancellationToken ct)
    {
        try
        {
            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            var staleAssets = new List<(string Id, string? RelativePath)>();

            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText = """
                    SELECT id, relative_path FROM family_assets
                    WHERE catalog_item_id = @itemId
                      AND version_label = @label
                      AND asset_type = 'Model3D'
                      AND description LIKE @prefix
                    """;
                selectCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                selectCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                selectCmd.Parameters.Add(new SqliteParameter("@prefix",
                    FamilyGeometryGlbWriter.AutoExtractedAssetDescriptionPrefix + "%"));

                using var reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    var id = reader.GetString(0);
                    var relPath = reader.IsDBNull(1) ? null : reader.GetString(1);
                    staleAssets.Add((id, relPath));
                }
            }

            if (staleAssets.Count == 0) return;

            var dbRoot = _database.GetDatabaseRoot();
            foreach (var (id, relPath) in staleAssets)
            {
                try
                {
                    using var delCmd = connection.CreateCommand();
                    delCmd.CommandText = "DELETE FROM family_assets WHERE id = @id";
                    delCmd.Parameters.Add(new SqliteParameter("@id", id));
                    await delCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                    if (!string.IsNullOrEmpty(relPath))
                    {
                        var absPath = Path.Combine(dbRoot, relPath);
                        if (File.Exists(absPath))
                        {
                            try { File.Delete(absPath); }
                            catch (Exception ioEx)
                            {
                                SmartConLogger.Debug($"Stale GLB file delete failed for '{Path.GetFileName(absPath)}': {ioEx.Message}");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug($"Stale GLB asset cleanup failed for id={id}: {ex.Message}");
                }
            }

            SmartConLogger.Debug($"Deleted {staleAssets.Count} stale auto-extracted preview asset(s) for {(catalogItemId, versionLabel)}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"DeletePreviousAutoExtractedAssetAsync skipped: {ex.Message}");
        }
    }
}
