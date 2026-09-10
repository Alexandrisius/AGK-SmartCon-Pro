using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Geometry;

public sealed partial class FamilyGeometryPipeline
{
    /// <summary>
    /// Writes one type's GLB into the shared CAS pool: serialize to a
    /// temp file IN THE TARGET SHARD DIRECTORY (same volume → the move
    /// is an atomic rename; a %TEMP%-based move could degrade to
    /// copy+delete and publish a truncated file on a crash — validator
    /// LOW-1), then rename onto the pool path. A concurrent writer
    /// winning the race is fine: pool files are immutable and
    /// content-identical for the same hash, so an
    /// <see cref="IOException"/> from the losing rename is treated as a
    /// race-win, never as a failure (validator MED-3). Returns
    /// <c>false</c> only on a GLB serialization failure.
    /// </summary>
    private async Task<bool> WriteGlbToPoolAsync(
        FamilyGeometryPerType gpt, string pooledAbsPath, string view3dHash, CancellationToken ct)
    {
        var preview = new FamilyGeometryPreview(
            string.Empty, string.Empty,
            // #249 (Phase 5): content-pure bytes — a NEUTRAL root node
            // name (no family/type names in the pooled bytes; the asset
            // row's description carries the display name).
            "preview",
            gpt.Meshes);

        string? tempPath = null;
        try
        {
            _pathResolver.EnsureSharedPreviewDirectory(view3dHash);
            // Same-directory temp → atomic rename on every filesystem.
            tempPath = pooledAbsPath + ".tmp-" + Guid.NewGuid().ToString("N");
            var ok = await _glbWriter.WriteAsync(preview, tempPath, ct).ConfigureAwait(false);
            if (!ok)
            {
                return false;
            }

            if (File.Exists(pooledAbsPath))
            {
                // A concurrent writer won the race before the rename —
                // identical content by construction (the name IS the
                // content hash).
                return true;
            }
            try
            {
                File.Move(tempPath, pooledAbsPath);
            }
            catch (IOException)
            {
                // Race-loser: the file appeared between the check and the
                // rename — same content, so this IS the win case.
                return File.Exists(pooledAbsPath);
            }
            return true;
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

    /// <summary>
    /// #157: writes/clears the terminal "no extractable geometry" marker on
    /// every variant of (<paramref name="catalogItemId"/>,
    /// <paramref name="versionLabel"/>) — the content is identical across
    /// variants, so the marker is too. <paramref name="state"/> = -1 marks
    /// terminal no-geometry (glb-v1 detection clears);
    /// <see langword="null"/> heals it (only rows currently at -1 are
    /// touched). Best-effort: a marker failure never breaks the pipeline.
    /// </summary>
    private async Task WriteGlbStateAsync(
        string catalogItemId, string versionLabel, int? state, CancellationToken ct)
    {
        try
        {
            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = state is not null
                ? """
                    UPDATE catalog_versions SET glb_state = @state
                    WHERE catalog_item_id = @itemId AND version_label = @label
                    """
                : """
                    UPDATE catalog_versions SET glb_state = NULL
                    WHERE catalog_item_id = @itemId AND version_label = @label
                      AND glb_state = -1
                    """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
            if (state is not null)
                cmd.Parameters.Add(new SqliteParameter("@state", state.Value));
            var rows = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            SmartConLogger.Debug(
                $"WriteGlbStateAsync: item={catalogItemId}, v={versionLabel}, state={(state?.ToString() ?? "NULL")}, rows={rows}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"WriteGlbStateAsync skipped: {ex.Message}");
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
                        if (LocalCatalog.StoragePathResolver.IsSharedPreviewPoolPath(relPath))
                        {
                            // #249 (Phase 5): pooled files are shared —
                            // delete only when the last reference is gone
                            // (refcount, Plan v3).
                            await LocalCatalog.SharedPreviewPoolCleanup
                                .DeletePoolFileIfOrphanedAsync(_database, relPath, ct)
                                .ConfigureAwait(false);
                        }
                        else
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
