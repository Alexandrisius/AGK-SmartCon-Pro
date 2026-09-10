using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

internal sealed partial class LocalCatalogProvider
{
    public async Task<FamilyCatalogVersion?> GetVersionByIdAsync(
        string catalogItemId, string versionId, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM catalog_versions
            WHERE catalog_item_id = @itemId AND id = @versionId
            LIMIT 1
            """;
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return ReadCatalogVersion(reader);
    }

    public async Task<FamilyCatalogVersion?> GetVersionByLabelAsync(
        string catalogItemId, string versionLabel, int targetRevitMajorVersion = 0, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();

        if (targetRevitMajorVersion > 0)
        {
            cmd.CommandText = """
                SELECT * FROM catalog_versions
                WHERE catalog_item_id = @itemId AND version_label = @label
                ORDER BY ABS(revit_major_version - @targetRevit) ASC, revit_major_version DESC
                LIMIT 1
                """;
            cmd.Parameters.Add(new SqliteParameter("@targetRevit", targetRevitMajorVersion));
        }
        else
        {
            cmd.CommandText = """
                SELECT * FROM catalog_versions
                WHERE catalog_item_id = @itemId AND version_label = @label
                ORDER BY revit_major_version DESC
                LIMIT 1
                """;
        }
        cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
        cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));

        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
            return null;

        return ReadCatalogVersion(reader);
    }

    public async Task<SetActiveVersionResult> SetActiveVersionAsync(
        string catalogItemId, string versionLabel, CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("FMVersion",
            ("Method", nameof(SetActiveVersionAsync)),
            ("CatalogItemId", catalogItemId),
            ("VersionLabel", versionLabel));

        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA foreign_keys = ON";
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        using var tx = connection.BeginTransaction();
        try
        {
            // Step 1: Read previous current_version_label + name, the target
            // version's content_hash, and the target version's file name.
            // Issue #126: the catalog item name follows the ACTIVE version's
            // file name, so MakeActive onto a version stored under a
            // different file name renames the item accordingly.
            // If multiple Revit variants exist for the same label, pick the closest to current Revit (highest).
            string? previousLabel;
            string? previousName = null;
            string? versionContentHash = null;
            int? versionHashFormat = null;
            string? targetFileName = null;
            string? familySource = null;
            int? revitCategoryId = null;

            using (var readCmd = connection.CreateCommand())
            {
                readCmd.Transaction = tx;
                readCmd.CommandText = """
                    SELECT ci.current_version_label AS prev_label,
                           ci.name AS prev_name,
                           ci.family_source AS family_source,
                           ci.revit_category_id AS revit_category_id,
                           cv.content_hash AS target_hash,
                           cv.hash_format_version AS target_hash_fmt,
                           ff.file_name AS target_file_name
                    FROM catalog_items ci
                    LEFT JOIN catalog_versions cv
                        ON cv.catalog_item_id = ci.id
                       AND cv.version_label = @label
                    LEFT JOIN family_files ff
                        ON ff.id = cv.file_id
                    WHERE ci.id = @itemId
                    ORDER BY cv.revit_major_version DESC
                    LIMIT 1
                    """;
                readCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                readCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));

                using var reader = await readCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                if (!await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    tx.Rollback();
                    SmartConLogger.Warn($"catalog_items row not found for id={catalogItemId} [Action: verify catalogItemId is valid]");
                    return new SetActiveVersionResult(
                        Success: false, CatalogItemId: catalogItemId, VersionLabel: versionLabel,
                        PreviousVersionLabel: null, ActivatedAtUtc: DateTimeOffset.UtcNow,
                        ContentHashSynced: false,
                        ErrorMessage: $"catalog_items row not found for id={catalogItemId}");
                }

                previousLabel = reader.IsDBNull(reader.GetOrdinal("prev_label"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("prev_label"));
                previousName = reader.IsDBNull(reader.GetOrdinal("prev_name"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("prev_name"));
                versionContentHash = reader.IsDBNull(reader.GetOrdinal("target_hash"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("target_hash"));
                versionHashFormat = reader.IsDBNull(reader.GetOrdinal("target_hash_fmt"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("target_hash_fmt"));
                targetFileName = reader.IsDBNull(reader.GetOrdinal("target_file_name"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("target_file_name"));
                familySource = reader.IsDBNull(reader.GetOrdinal("family_source"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("family_source"));
                revitCategoryId = reader.IsDBNull(reader.GetOrdinal("revit_category_id"))
                    ? null
                    : reader.GetInt32(reader.GetOrdinal("revit_category_id"));
            }

            // Check that at least one row in catalog_versions matched the label.
            if (versionContentHash is null && versionHashFormat is null)
            {
                // Distinguish "version label not found" from "found but both columns NULL".
                using var verifyCmd = connection.CreateCommand();
                verifyCmd.Transaction = tx;
                verifyCmd.CommandText = "SELECT COUNT(*) FROM catalog_versions WHERE catalog_item_id = @itemId AND version_label = @label";
                verifyCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
                verifyCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                var countObj = await verifyCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
                var count = countObj is long l ? (int)l : 0;
                if (count == 0)
                {
                    tx.Rollback();
                    SmartConLogger.Warn($"no catalog_versions row found for itemId={catalogItemId} label={versionLabel} [Action: make sure the version exists before activating]");
                    return new SetActiveVersionResult(
                        Success: false, CatalogItemId: catalogItemId, VersionLabel: versionLabel,
                        PreviousVersionLabel: previousLabel, ActivatedAtUtc: DateTimeOffset.UtcNow,
                        ContentHashSynced: false,
                        ErrorMessage: $"Version with label '{versionLabel}' not found for catalog item {catalogItemId}",
                        PreviousName: previousName);
                }
            }

            // Issue #126: resolve the item name from the activated version's
            // file name (without extension). When the version has no file
            // record the name is left untouched.
            var newName = !string.IsNullOrEmpty(targetFileName)
                ? Path.GetFileNameWithoutExtension(targetFileName)
                : null;
            var nameChanged = newName is not null
                && !string.Equals(newName, previousName, StringComparison.Ordinal);

            // Step 2: Atomically switch the pointer + sync content_hash (ADR-041 A.03)
            // + adopt the activated version's file name (Issue #126).
            using (var updCmd = connection.CreateCommand())
            {
                updCmd.Transaction = tx;
                updCmd.CommandText = """
                    UPDATE catalog_items
                    SET current_version_label = @label,
                        content_hash = @contentHash,
                        hash_format_version = @hashFmt,
                        name = COALESCE(@newName, name),
                        normalized_name = COALESCE(@newNormalizedName, normalized_name),
                        updated_at_utc = @now
                    WHERE id = @itemId
                    """;
                updCmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
                updCmd.Parameters.Add(new SqliteParameter("@contentHash", (object?)versionContentHash ?? DBNull.Value));
                updCmd.Parameters.Add(new SqliteParameter("@hashFmt", versionHashFormat.HasValue ? (object)versionHashFormat.Value : DBNull.Value));
                updCmd.Parameters.Add(new SqliteParameter("@newName",
                    nameChanged ? (object)newName! : DBNull.Value));
                updCmd.Parameters.Add(new SqliteParameter("@newNormalizedName",
                    nameChanged
                        ? (object)Core.Services.FamilyManager.FamilyNameNormalizer.Normalize(newName!)
                        : DBNull.Value));
                updCmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
                updCmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));

                var rowsAffected = await updCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                if (rowsAffected == 0)
                {
                    tx.Rollback();
                    return new SetActiveVersionResult(
                        Success: false, CatalogItemId: catalogItemId, VersionLabel: versionLabel,
                        PreviousVersionLabel: previousLabel, ActivatedAtUtc: DateTimeOffset.UtcNow,
                        ContentHashSynced: false,
                        ErrorMessage: "catalog_items UPDATE affected 0 rows",
                        PreviousName: previousName);
                }
            }

            // ADR-072 World B (audit M12): the activated version's routing
            // dependency links must reflect the item-level routing rules —
            // links are version-scoped, so a version activated AFTER an
            // editor save keeps its import-time links otherwise (drift
            // badge / delete-guard / clip indicator degrade). Rebuild them
            // in the same transaction for every Revit variant of the label.
            await RebuildRoutingLinksForActivatedVersionAsync(
                connection, tx, catalogItemId, versionLabel, familySource, revitCategoryId, ct)
                .ConfigureAwait(false);

            tx.Commit();
            var hashSynced = versionContentHash is not null;
            SmartConLogger.Info(
                $"switched active version: prev={(previousLabel ?? "<null>")} new={versionLabel} " +
                $"hashSynced={hashSynced} nameChanged={nameChanged}" +
                (nameChanged ? $" ('{previousName}' -> '{newName}')" : string.Empty));
            return new SetActiveVersionResult(
                Success: true, CatalogItemId: catalogItemId, VersionLabel: versionLabel,
                PreviousVersionLabel: previousLabel, ActivatedAtUtc: DateTimeOffset.UtcNow,
                ContentHashSynced: hashSynced,
                NameChanged: nameChanged,
                PreviousName: previousName,
                NewName: nameChanged ? newName : previousName);
        }
        catch (Exception)
        {
            tx.Rollback();
            throw;
        }
    }

    /// <summary>
    /// ADR-072 World B (audit M12): rebuilds the activated version's routing
    /// dependency links from the item-level routing rules (V37) so they never
    /// lag behind editor saves. No-op for non-system / non-MEPCurve items and
    /// for legacy items without item-level routing rows (their links stay as
    /// imported — the unhealed legacy state has no curated truth to apply).
    /// </summary>
    private async Task RebuildRoutingLinksForActivatedVersionAsync(
        SqliteConnection connection, SqliteTransaction tx,
        string catalogItemId, string versionLabel,
        string? familySource, int? revitCategoryId, CancellationToken ct)
    {
        if (!string.Equals(familySource, "system", StringComparison.Ordinal)
            || !RoutingGroupCatalog.IsMepCurveCategory(revitCategoryId))
        {
            return;
        }

        var routingRules = new LocalFamilyRoutingRuleRepository(_database);
        if (!await routingRules.HasAnyForItemAsync(catalogItemId, ct).ConfigureAwait(false))
            return;
        var (rules, _) = await routingRules.ReadForItemAsync(catalogItemId, ct).ConfigureAwait(false);

        var versionIds = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT id FROM catalog_versions
                WHERE catalog_item_id = @itemId AND version_label = @label
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", catalogItemId));
            cmd.Parameters.Add(new SqliteParameter("@label", versionLabel));
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                versionIds.Add(reader.GetString(0));
            }
        }

        var linksWritten = 0;
        foreach (var versionId in versionIds)
        {
            linksWritten += await RoutingDependencyLinkRebuilder
                .RebuildAsync(connection, tx, this, catalogItemId, versionId, rules, ct)
                .ConfigureAwait(false);
        }
        SmartConLogger.Info(
            $"routing links rebuilt for activated version '{versionLabel}' " +
            $"({versionIds.Count} variant(s), {linksWritten} links)");
    }

}
