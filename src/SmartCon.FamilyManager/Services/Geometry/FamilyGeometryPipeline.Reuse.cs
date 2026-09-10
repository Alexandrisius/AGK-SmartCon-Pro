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
    /// #252: the CURRENT version's stored section hashes (already rewritten
    /// by the overwrite) match the captured pre-overwrite baseline on every
    /// preview-relevant key — the same key set tier 1 uses
    /// (<see cref="TryReuseFromPreviousVersionAsync"/>). False when the
    /// current analytics are missing (legacy path / cleared columns) — the
    /// pipeline then takes the normal delete + extract route.
    /// </summary>
    private async Task<bool> CurrentSectionsMatchBaselineAsync(
        string catalogItemId,
        string versionLabel,
        IReadOnlyDictionary<string, string> baseline,
        CancellationToken ct)
    {
        try
        {
            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);
            string? currentJson;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT section_hashes FROM catalog_versions
                    WHERE catalog_item_id = @itemId AND version_label = @label
                    LIMIT 1
                    """;
                cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                currentJson = Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
            var current = ContentSectionJsonSerializer.Deserialize(currentJson);
            if (current is null)
            {
                return false;
            }

            foreach (var key in new[] { "DEF", "GEOM", "TYPES", "NESTED", "NONSHARED", "NESTEDHASH" })
            {
                if (!current.TryGetValue(key, out var currentHash)
                    || !baseline.TryGetValue(key, out var baselineHash)
                    || !string.Equals(currentHash, baselineHash, StringComparison.Ordinal))
                {
                    SmartConLogger.Debug(
                        $"Preview reuse (overwrite): section '{key}' differs from the pre-overwrite content — full pipeline");
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SmartConLogger.Debug($"CurrentSectionsMatchBaselineAsync skipped: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// #249 (Phase 5), reuse tier 1: re-links the previous version's
    /// pooled preview assets when its DEF/GEOM/TYPES section hashes
    /// match the CURRENT version's stored section hashes (written by the
    /// import transaction before this hook runs). A match proves the
    /// per-type preview inputs are identical — no extraction, no GLB
    /// write, no new files. Returns <c>true</c> when the pipeline's work
    /// is done (assets re-linked or the terminal no-geometry marker
    /// carried over).
    /// </summary>
    private async Task<bool> TryReuseFromPreviousVersionAsync(
        string catalogItemId, string versionLabel, CancellationToken ct)
    {
        try
        {
            using var connection = _database.CreateConnection();
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // The CURRENT version's section hashes — no analytics (legacy
            // import path) → no reuse decision possible.
            string? currentJson;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT section_hashes FROM catalog_versions
                    WHERE catalog_item_id = @itemId AND version_label = @label
                    LIMIT 1
                    """;
                cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                currentJson = Convert.ToString(await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false));
            }
            var current = ContentSectionJsonSerializer.Deserialize(currentJson);
            if (current is null)
            {
                return false;
            }

            // The latest OTHER version with section analytics.
            string? previousLabel = null;
            string? previousJson = null;
            int? previousGlbState = null;
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT version_label, section_hashes, glb_state FROM catalog_versions
                    WHERE catalog_item_id = @itemId AND version_label <> @label
                      AND section_hashes IS NOT NULL
                    ORDER BY published_at_utc DESC
                    LIMIT 1
                    """;
                cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    previousLabel = reader.GetString(0);
                    previousJson = reader.IsDBNull(1) ? null : reader.GetString(1);
                    previousGlbState = reader.IsDBNull(2) ? null : reader.GetInt32(2);
                }
            }
            var previous = ContentSectionJsonSerializer.Deserialize(previousJson);
            if (previous is null || previousLabel is null)
            {
                return false;
            }

            foreach (var key in new[] { "DEF", "GEOM", "TYPES", "NESTED", "NONSHARED", "NESTEDHASH" })
            {
                if (!current.TryGetValue(key, out var currentHash)
                    || !previous.TryGetValue(key, out var previousHash)
                    || !string.Equals(currentHash, previousHash, StringComparison.Ordinal))
                {
                    SmartConLogger.Debug(
                        $"Preview reuse: section '{key}' differs from {previousLabel} — full pipeline");
                    return false;
                }
            }

            // The previous version was a terminal no-geometry family —
            // the same content yields the same verdict, carry the marker.
            if (previousGlbState == -1)
            {
                await WriteGlbStateAsync(catalogItemId, versionLabel, -1, ct).ConfigureAwait(false);
                SmartConLogger.Info(
                    $"Preview reuse: carried over the terminal no-geometry marker from {previousLabel}");
                return true;
            }

            // Re-link the previous version's POOLED auto-preview rows
            // (they reference pool files — the rows, never the bytes).
            // LEGACY (pre-CAS) rows point inside the previous version's
            // own directory — re-linking them would break the preview the
            // moment that directory is deleted with the old version
            // (validator HIGH-1). Only pool paths are re-linkable; a
            // previous version without pooled rows sends us through the
            // full pipeline (which then writes into the pool).
            var linked = 0;
            var previousAssets = new List<(string FileName, string RelativePath, long SizeBytes, string? Description)>();
            using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT file_name, relative_path, size_bytes, description FROM family_assets
                    WHERE catalog_item_id = @itemId AND version_label = @prevLabel
                      AND asset_type = 'Model3D' AND description LIKE @prefix
                      AND relative_path LIKE @poolPrefix
                    """;
                cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                cmd.Parameters.Add(new SqliteParameter("@prevLabel", previousLabel));
                cmd.Parameters.Add(new SqliteParameter("@prefix",
                    FamilyGeometryGlbWriter.AutoExtractedAssetDescriptionPrefix + "%"));
                cmd.Parameters.Add(new SqliteParameter("@poolPrefix",
                    LocalCatalog.StoragePathResolver.SharedPreviewPoolRelativePrefix + "%"));
                using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    previousAssets.Add((
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.GetInt64(2),
                        reader.IsDBNull(3) ? null : reader.GetString(3)));
                }
            }

            if (previousAssets.Count == 0)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow.ToString("o");
            foreach (var asset in previousAssets)
            {
                using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText = """
                    INSERT INTO family_assets (id, catalog_item_id, version_label, asset_type, file_name, relative_path, size_bytes, description, created_at_utc, is_primary)
                    VALUES (@id, @itemId, @label, 'Model3D', @fileName, @relPath, @size, @description, @created, 0)
                    """;
                insertCmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
                insertCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                insertCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                insertCmd.Parameters.Add(new SqliteParameter("@fileName", asset.FileName));
                insertCmd.Parameters.Add(new SqliteParameter("@relPath", asset.RelativePath));
                insertCmd.Parameters.Add(new SqliteParameter("@size", asset.SizeBytes));
                insertCmd.Parameters.Add(new SqliteParameter("@description", (object?)asset.Description ?? DBNull.Value));
                insertCmd.Parameters.Add(new SqliteParameter("@created", now));
                await insertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                linked++;
            }

            await WriteGlbStateAsync(catalogItemId, versionLabel, null, ct).ConfigureAwait(false);
            SmartConLogger.Info(
                $"Preview reuse: re-linked {linked} pooled preview asset(s) from {previousLabel} " +
                $"(DEF/GEOM/TYPES sections match) — extraction and GLB writes skipped");
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"TryReuseFromPreviousVersionAsync skipped: {ex.Message}");
            return false;
        }
    }
}
