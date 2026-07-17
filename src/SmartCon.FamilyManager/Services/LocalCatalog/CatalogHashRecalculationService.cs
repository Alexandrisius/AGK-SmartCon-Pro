using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// One-shot hash-recalculation migration (Issue #126). See
/// <see cref="ICatalogHashRecalculationService"/> for the contract.
/// </summary>
/// <remarks>
/// Algorithm:
/// <list type="number">
/// <item>System-family rows are re-flagged to format v2 instantly
/// (their v1 canonical string never contained a name — the hashes are
/// already rename-invariant; recomputing them from the isolated .rvt
/// would risk a mismatch against project-extracted hashes).</item>
/// <item>Loadable pending versions are grouped by
/// (catalog_item_id, version_label). ONE file per group is opened (the
/// highest <c>revit_major_version</c> not newer than the running Revit)
/// — the content is identical across Revit variants, so the computed
/// hash is applied to ALL variants of the group, including variants
/// saved in a newer Revit.</item>
/// <item>Files are opened one at a time (open → extract → close).
/// Documents are NOT held open: unlike batch import there is no SaveAs
/// phase, and holding dozens of documents wastes memory while repeated
/// open/close cycles hit the known Revit degradation after ~30 cycles
/// (Autodesk forum; restart is the only cure) — so the migration is
/// cancellable and resumable instead.</item>
/// <item>SQLite writes are committed in batches of
/// <see cref="CommitBatchSize"/> files inside a single transaction each
/// (I-14: DELETE journal + busy_timeout, no WAL — the database may live
/// on an SMB share).</item>
/// </list>
/// </remarks>
internal sealed class CatalogHashRecalculationService : ICatalogHashRecalculationService
{
    /// <summary>Files processed per single SQLite transaction.</summary>
    internal const int CommitBatchSize = 10;

    private readonly LocalCatalogDatabase _database;
    private readonly StoragePathResolver _pathResolver;
    private readonly IFamilyContentHasher _contentHasher;
    private readonly IFamilyMigrationExtractor _extractor;
    private readonly IFamilyCatalogProvider _catalogProvider;
    private readonly IWritableFamilyCatalogProvider _writableProvider;

    public CatalogHashRecalculationService(
        LocalCatalogDatabase database,
        StoragePathResolver pathResolver,
        IFamilyContentHasher contentHasher,
        IFamilyMigrationExtractor extractor,
        IFamilyCatalogProvider catalogProvider,
        IWritableFamilyCatalogProvider writableProvider)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _contentHasher = contentHasher ?? throw new ArgumentNullException(nameof(contentHasher));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
        _writableProvider = writableProvider ?? throw new ArgumentNullException(nameof(writableProvider));
    }

    /// <summary>One loadable (item, label) group to recalculate.</summary>
    private sealed record PendingGroup(
        string CatalogItemId,
        string ItemName,
        string? CurrentVersionLabel,
        string VersionLabel,
        string RelativePath,
        string FileName,
        IReadOnlyList<string> AllVersionIds);

    public async Task<int> CountPendingAsync(int currentRevitMajorVersion, CancellationToken ct = default)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        // Loadable groups processable in the running Revit.
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COUNT(*) FROM (
                    SELECT cv.catalog_item_id, cv.version_label
                    FROM catalog_versions cv
                    JOIN catalog_items ci ON ci.id = cv.catalog_item_id
                    JOIN family_files ff ON ff.id = cv.file_id
                    WHERE ci.family_source = 'loadable'
                      AND (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (2, -1))
                      AND cv.revit_major_version <= @maxRevit
                    GROUP BY cv.catalog_item_id, cv.version_label
                )
                """;
            cmd.Parameters.Add(new SqliteParameter("@maxRevit", currentRevitMajorVersion));
            var loadableObj = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var loadable = loadableObj is long l ? (int)l : 0;

            using var sysCmd = connection.CreateCommand();
            sysCmd.CommandText = """
                SELECT COUNT(*)
                FROM catalog_versions cv
                JOIN catalog_items ci ON ci.id = cv.catalog_item_id
                WHERE ci.family_source = 'system'
                  AND (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (2, -1))
                """;
            var systemObj = await sysCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            var system = systemObj is long s ? (int)s : 0;

            return loadable + system;
        }
    }

    public async Task<CatalogHashRecalculationResult> RecalculateAsync(
        int currentRevitMajorVersion,
        IProgress<CatalogHashRecalculationProgress>? progress,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("HashRecalc",
            ("Method", nameof(RecalculateAsync)),
            ("RevitVersion", currentRevitMajorVersion));

        if (ct.IsCancellationRequested)
        {
            SmartConLogger.Info("Migration cancelled before start — nothing was modified");
            return new CatalogHashRecalculationResult(
                UpdatedCount: 0,
                SystemRelabeledCount: 0,
                NewerRevitCount: 0,
                MissingFiles: Array.Empty<HashRecalculationMissingFile>(),
                FailedFiles: Array.Empty<HashRecalculationFailedFile>(),
                WasCancelled: true);
        }

        // Step 1: system rows — cheap re-flag, no file I/O (see remarks).
        var systemRelabeled = await ReflagSystemRowsAsync(ct).ConfigureAwait(false);
        SmartConLogger.Info($"System rows re-flagged to hash format v2: {systemRelabeled}");

        // Step 2: load pending loadable groups + count newer-Revit-only groups.
        var groups = await LoadPendingGroupsAsync(currentRevitMajorVersion, ct).ConfigureAwait(false);
        var newerRevitCount = await CountNewerRevitOnlyGroupsAsync(currentRevitMajorVersion, ct).ConfigureAwait(false);
        SmartConLogger.Info(
            $"Pending loadable groups: {groups.Count} (processable), {newerRevitCount} (newer Revit only — left pending)");

        var missing = new List<HashRecalculationMissingFile>();
        var failed = new List<HashRecalculationFailedFile>();
        var updatedCount = 0;
        var wasCancelled = false;

        var pendingWrites = new List<(IReadOnlyList<string> VersionIds, string? Hash, string ItemId, string Label, string? CurrentLabel)>();

        for (var i = 0; i < groups.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                wasCancelled = true;
                SmartConLogger.Info(
                    $"Migration cancelled by user at {i} of {groups.Count} — committed batches stay, " +
                    "the rest will be offered again on the next prompt");
                break;
            }

            var group = groups[i];
            var absolutePath = Path.Combine(_pathResolver.GetDatabaseRoot(), group.RelativePath);
            progress?.Report(new CatalogHashRecalculationProgress(i + 1, groups.Count, group.FileName));

            if (!File.Exists(absolutePath))
            {
                SmartConLogger.Warn(
                    $"Managed file not found: '{group.FileName}' (item '{group.ItemName}', {group.VersionLabel}) " +
                    $"[Action: decide on the summary screen — purge the catalog row or keep it if the drive is temporarily unavailable]");
                missing.Add(new HashRecalculationMissingFile(
                    group.CatalogItemId, group.ItemName, group.VersionLabel, group.FileName));
                continue;
            }

            FamilyMigrationExtractResult extract;
            try
            {
                extract = await _extractor.ExtractLoadableAsync(absolutePath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                wasCancelled = true;
                SmartConLogger.Info("Migration cancelled by user (extract aborted) — committing processed batch");
                break;
            }
            catch (Exception ex)
            {
                extract = FamilyMigrationExtractResult.Fail($"{ex.GetType().Name}: {ex.Message}");
            }

            if (!extract.Success || extract.LoadableSnapshot is null)
            {
                var error = extract.ErrorMessage ?? "unknown extraction failure";
                SmartConLogger.Warn(
                    $"Hash recalculation failed for '{group.FileName}' (item '{group.ItemName}', {group.VersionLabel}): {error} " +
                    $"[Action: the version is marked as skipped (-1) and will not be retried; re-import the family to restore dedup]");
                failed.Add(new HashRecalculationFailedFile(
                    group.ItemName, group.VersionLabel, group.FileName, error));
                pendingWrites.Add((group.AllVersionIds, null, group.CatalogItemId, group.VersionLabel, group.CurrentVersionLabel));
            }
            else
            {
                var hash = _contentHasher.ComputeForLoadable(extract.LoadableSnapshot);
                pendingWrites.Add((group.AllVersionIds, hash?.HexString, group.CatalogItemId, group.VersionLabel, group.CurrentVersionLabel));
                if (hash is not null)
                    updatedCount += group.AllVersionIds.Count;
            }

            // Commit in batches so a cancel/crash never loses more than
            // CommitBatchSize files of work (and never holds a long lock).
            // The batch itself always commits atomically (None token) —
            // cancellation is handled BETWEEN files, so the user never
            // sees a cancelled batch reported as a file-read failure.
            if (pendingWrites.Count >= CommitBatchSize)
            {
                await CommitBatchAsync(pendingWrites, CancellationToken.None).ConfigureAwait(false);
                pendingWrites.Clear();
            }
        }

        if (pendingWrites.Count > 0)
        {
            await CommitBatchAsync(pendingWrites, CancellationToken.None).ConfigureAwait(false);
        }

        SmartConLogger.Info(
            $"Migration finished: updated={updatedCount}, systemReflagged={systemRelabeled}, " +
            $"missing={missing.Count}, failed={failed.Count}, newerRevitPending={newerRevitCount}, " +
            $"cancelled={wasCancelled}");

        return new CatalogHashRecalculationResult(
            UpdatedCount: updatedCount,
            SystemRelabeledCount: systemRelabeled,
            NewerRevitCount: newerRevitCount,
            MissingFiles: missing,
            FailedFiles: failed,
            WasCancelled: wasCancelled);
    }

    public async Task<(int DeletedItems, int DeletedVersions)> PurgeMissingAsync(
        IReadOnlyList<HashRecalculationMissingFile> missing,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("HashRecalc",
            ("Method", nameof(PurgeMissingAsync)),
            ("Count", missing.Count));

        var deletedItems = 0;
        var deletedVersions = 0;

        foreach (var itemGroup in missing.GroupBy(m => m.CatalogItemId))
        {
            ct.ThrowIfCancellationRequested();
            var itemId = itemGroup.Key;
            var missingLabels = itemGroup.Select(g => g.VersionLabel).ToHashSet(StringComparer.Ordinal);

            var versions = await _catalogProvider.GetVersionsAsync(itemId, ct).ConfigureAwait(false);
            var remainingLabels = versions
                .Select(v => v.VersionLabel)
                .Where(l => !missingLabels.Contains(l))
                .Distinct(StringComparer.Ordinal)
                .ToList();

            if (remainingLabels.Count == 0)
            {
                // Every version of this item is missing → delete the whole
                // catalog item (FK CASCADE cleans versions/types/attributes).
                SmartConLogger.Info(
                    $"Purging catalog item {itemId} ('{itemGroup.First().ItemName}') — all versions missing");
                var deleted = await _writableProvider.DeleteItemAsync(itemId, ct).ConfigureAwait(false);
                if (deleted)
                {
                    deletedItems++;
                    deletedVersions += itemGroup.Count();
                }
                continue;
            }

            // Some versions remain. If the ACTIVE version is being purged,
            // switch the pointer to the newest remaining version first —
            // SetActiveVersionAsync also re-syncs the item's content hash
            // and (Issue #126) the item name to the new active file name.
            var item = await _catalogProvider.GetItemAsync(itemId, ct).ConfigureAwait(false);
            if (item?.CurrentVersionLabel is not null && missingLabels.Contains(item.CurrentVersionLabel))
            {
                var newestRemaining = versions
                    .Where(v => !missingLabels.Contains(v.VersionLabel))
                    .OrderByDescending(v => v.PublishedAtUtc)
                    .Select(v => v.VersionLabel)
                    .First();
                SmartConLogger.Info(
                    $"Active version '{item.CurrentVersionLabel}' of item {itemId} is being purged — " +
                    $"switching active to '{newestRemaining}'");
                var setResult = await _writableProvider
                    .SetActiveVersionAsync(itemId, newestRemaining, ct)
                    .ConfigureAwait(false);
                if (!setResult.Success)
                {
                    SmartConLogger.Warn(
                        $"Failed to switch active version for item {itemId} before purge: {setResult.ErrorMessage} " +
                        $"[Action: skipping purge for this item — re-run the migration after fixing the catalog]");
                    continue;
                }
            }

            foreach (var label in itemGroup.Select(g => g.VersionLabel).Distinct(StringComparer.Ordinal))
            {
                var delResult = await _writableProvider.DeleteVersionAsync(itemId, label, ct).ConfigureAwait(false);
                if (delResult.Success)
                {
                    // One label may have several Revit variants — count the
                    // actual deleted rows, not the labels.
                    deletedVersions += Math.Max(1, delResult.VersionsDeleted);
                }
                else
                {
                    SmartConLogger.Warn(
                        $"Failed to delete version '{label}' of item {itemId}: {delResult.ErrorMessage} " +
                        $"[Action: see prior log lines; the row can be deleted manually from the properties dialog]");
                }
            }
        }

        SmartConLogger.Info($"Purge finished: deletedItems={deletedItems}, deletedVersions={deletedVersions}");
        return (deletedItems, deletedVersions);
    }

    private async Task<int> ReflagSystemRowsAsync(CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        try
        {
            int versions;
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE catalog_versions SET hash_format_version = 2
                    WHERE (hash_format_version IS NULL OR hash_format_version NOT IN (2, -1))
                      AND catalog_item_id IN (SELECT id FROM catalog_items WHERE family_source = 'system')
                    """;
                versions = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = """
                    UPDATE catalog_items SET hash_format_version = 2
                    WHERE family_source = 'system'
                      AND (hash_format_version IS NULL OR hash_format_version NOT IN (2, -1))
                    """;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            tx.Commit();
            return versions;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    private async Task<List<PendingGroup>> LoadPendingGroupsAsync(int currentRevitMajorVersion, CancellationToken ct)
    {
        var groups = new List<PendingGroup>();
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        // All variants of each pending group, ordered so the FIRST row of a
        // group is the openable variant with the highest revit_major_version.
        cmd.CommandText = """
            SELECT ci.id AS itemId, ci.name AS itemName, ci.current_version_label AS currentLabel,
                   cv.id AS versionId, cv.version_label AS versionLabel, cv.revit_major_version AS revit,
                   ff.relative_path AS relPath, ff.file_name AS fileName
            FROM catalog_versions cv
            JOIN catalog_items ci ON ci.id = cv.catalog_item_id
            JOIN family_files ff ON ff.id = cv.file_id
            WHERE ci.family_source = 'loadable'
              AND (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (2, -1))
            ORDER BY ci.id, cv.version_label, cv.revit_major_version DESC
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        string? currentKey = null;
        string? itemId = null, itemName = null, currentLabel = null, label = null;
        string? openableRelPath = null, openableFileName = null;
        var versionIds = new List<string>();

        void FlushGroup()
        {
            if (currentKey is null) return;
            if (openableRelPath is null)
            {
                // No variant openable in the running Revit — newer-Revit-only
                // group, counted separately; left fully pending.
                currentKey = null;
                return;
            }
            groups.Add(new PendingGroup(
                itemId!, itemName!, currentLabel, label!,
                openableRelPath, openableFileName!,
                versionIds.ToArray()));
            currentKey = null;
        }

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var rowItemId = reader.GetString(reader.GetOrdinal("itemId"));
            var rowLabel = reader.GetString(reader.GetOrdinal("versionLabel"));
            var key = rowItemId + "|" + rowLabel;

            if (key != currentKey)
            {
                FlushGroup();
                currentKey = key;
                itemId = rowItemId;
                itemName = reader.GetString(reader.GetOrdinal("itemName"));
                currentLabel = reader.IsDBNull(reader.GetOrdinal("currentLabel"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("currentLabel"));
                label = rowLabel;
                openableRelPath = null;
                openableFileName = null;
                versionIds = new List<string>();
            }

            versionIds.Add(reader.GetString(reader.GetOrdinal("versionId")));

            var revit = reader.GetInt32(reader.GetOrdinal("revit"));
            if (openableRelPath is null && revit <= currentRevitMajorVersion)
            {
                openableRelPath = reader.GetString(reader.GetOrdinal("relPath"));
                openableFileName = reader.GetString(reader.GetOrdinal("fileName"));
            }
        }
        FlushGroup();

        return groups;
    }

    private async Task<int> CountNewerRevitOnlyGroupsAsync(int currentRevitMajorVersion, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM (
                SELECT cv.catalog_item_id, cv.version_label
                FROM catalog_versions cv
                JOIN catalog_items ci ON ci.id = cv.catalog_item_id
                WHERE ci.family_source = 'loadable'
                  AND (cv.hash_format_version IS NULL OR cv.hash_format_version NOT IN (2, -1))
                GROUP BY cv.catalog_item_id, cv.version_label
                HAVING MAX(cv.revit_major_version) > @maxRevit
                  AND SUM(CASE WHEN cv.revit_major_version <= @maxRevit THEN 1 ELSE 0 END) = 0
            )
            """;
        cmd.Parameters.Add(new SqliteParameter("@maxRevit", currentRevitMajorVersion));
        var result = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return result is long l ? (int)l : 0;
    }

    private async Task CommitBatchAsync(
        List<(IReadOnlyList<string> VersionIds, string? Hash, string ItemId, string Label, string? CurrentLabel)> writes,
        CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var tx = connection.BeginTransaction();
        try
        {
            foreach (var (versionIds, hash, itemId, label, currentLabel) in writes)
            {
                // One UPDATE for ALL Revit variants of the group.
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    var idParams = new string[versionIds.Count];
                    for (var p = 0; p < versionIds.Count; p++)
                    {
                        idParams[p] = "@vid" + p;
                        cmd.Parameters.Add(new SqliteParameter(idParams[p], versionIds[p]));
                    }

                    if (hash is not null)
                    {
                        cmd.CommandText = $"""
                            UPDATE catalog_versions
                            SET content_hash = @hash, hash_format_version = 2
                            WHERE id IN ({string.Join(", ", idParams)})
                            """;
                        cmd.Parameters.Add(new SqliteParameter("@hash", hash));
                    }
                    else
                    {
                        // Unreadable file — permanently skipped (never retried,
                        // excluded from the pending count).
                        cmd.CommandText = $"""
                            UPDATE catalog_versions
                            SET hash_format_version = -1
                            WHERE id IN ({string.Join(", ", idParams)})
                            """;
                    }
                    await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                // Sync the item's denormalized hash when the processed label
                // is the ACTIVE one (same rule as SetActiveVersionAsync).
                if (hash is not null && string.Equals(label, currentLabel, StringComparison.Ordinal))
                {
                    using var itemCmd = connection.CreateCommand();
                    itemCmd.Transaction = tx;
                    itemCmd.CommandText = """
                        UPDATE catalog_items
                        SET content_hash = @hash, hash_format_version = 2, updated_at_utc = @now
                        WHERE id = @itemId
                        """;
                    itemCmd.Parameters.Add(new SqliteParameter("@hash", hash));
                    itemCmd.Parameters.Add(new SqliteParameter("@now", DateTimeOffset.UtcNow.ToString("o")));
                    itemCmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
                    await itemCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }
            }

            tx.Commit();
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }
}
