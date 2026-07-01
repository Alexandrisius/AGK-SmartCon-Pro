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
        IReadOnlyList<FamilyGeometryPerType>? geometryPerType,
        string? managedRfaPath,
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
            ("FilePath", Path.GetFileName(managedRfaPath ?? "")));

        // 1. Obtain geometry: either pre-extracted from Prepare (H1)
        //    or extract now from managed .rfa (H2/H3).
        IReadOnlyList<FamilyGeometryPerType>? geometry = geometryPerType;

        if (geometry is null || geometry.Count == 0)
        {
            if (string.IsNullOrEmpty(managedRfaPath))
            {
                SmartConLogger.Warn(
                    $"Geometry pipeline skipped: no pre-extracted geometry and no managedRfaPath for '{familyName}' v{versionLabel} " +
                    "[Action: import continues; 3D preview will be unavailable for this version]");
                return;
            }

            // H2/H3 path: extract geometry from managed .rfa (one OpenDocumentFile).
            geometry = await _awaitableEvent.RaiseAsync(
                (app) => _extractor.ExtractAsync(managedRfaPath!, familyName, ct).GetAwaiter().GetResult(),
                ct).ConfigureAwait(false);

            if (geometry is null || geometry.Count == 0)
            {
                SmartConLogger.Info(
                    $"Geometry pipeline skipped: no preview extracted for '{familyName}' v{versionLabel}");
                return;
            }
        }

        // 2. Delete any previous auto-extracted Preview assets for this
        //    (catalog_item_id, version_label) — supports OverwriteCurrent
        //    (ADR-040) where the same version is re-imported with new
        //    geometry.
        await DeletePreviousAutoExtractedAssetAsync(catalogItemId, versionLabel, ct).ConfigureAwait(false);

        // 3. Write N GLBs (one per type) and register each as a Model3D asset.
        foreach (var gpt in geometry)
        {
            ct.ThrowIfCancellationRequested();

            if (gpt.IsEmpty)
            {
                SmartConLogger.Info(
                    $"Skipping type '{gpt.TypeName}' — empty geometry");
                continue;
            }

            var preview = new FamilyGeometryPreview(
                catalogItemId, versionLabel,
                string.IsNullOrEmpty(gpt.TypeName) ? gpt.FamilyName : $"{gpt.FamilyName} [{gpt.TypeName}]",
                gpt.Meshes);

            string? tempPath = null;
            try
            {
                tempPath = Path.Combine(Path.GetTempPath(),
                    $"sc_preview_{Guid.NewGuid().ToString("N")}.glb");
                var ok = await _glbWriter.WriteAsync(preview, tempPath, ct).ConfigureAwait(false);
                if (!ok)
                {
                    SmartConLogger.Warn(
                        $"GLB write failed for type '{gpt.TypeName}' '{familyName}' v{versionLabel} " +
                        "[Action: import continues; this type's 3D preview will be unavailable]");
                    continue;
                }

                // Description encodes the type name so the UI can
                // filter/select per-type assets.
                var description = gpt.TypeName.Length == 0
                    ? FamilyGeometryGlbWriter.AutoExtractedAssetDescriptionPrefix + familyName + "::"
                    : FamilyGeometryGlbWriter.AutoExtractedAssetDescriptionPrefix + familyName + "::" + gpt.TypeName;

                var asset = await _assetService.AddAssetAsync(
                    catalogItemId, versionLabel, FamilyAssetType.Model3D, tempPath, description, ct).ConfigureAwait(false);

                SmartConLogger.Info(
                    $"Geometry pipeline OK: type='{gpt.TypeName}', GLB asset={asset.Id}, file='{asset.FileName}', " +
                    $"{gpt.Meshes.Count} meshes, {gpt.TotalTriangleCount} triangles");
            }
            finally
            {
                if (tempPath is not null)
                {
                    try { if (File.Exists(tempPath)) File.Delete(tempPath); }
                    catch (Exception cleanupEx)
                    {
                        SmartConLogger.Debug($"Temp GLB cleanup failed for '{Path.GetFileName(tempPath)}': {cleanupEx.Message}");
                    }
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
