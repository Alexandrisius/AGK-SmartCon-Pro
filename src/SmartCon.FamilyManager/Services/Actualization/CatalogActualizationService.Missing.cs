using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

internal sealed partial class CatalogActualizationService
{
    public async Task<PurgeMissingResult> PurgeMissingAsync(
        IReadOnlyList<HashRecalculationMissingFile> missing,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("DbActualize",
            ("Method", nameof(PurgeMissingAsync)),
            ("Count", missing.Count));

        var deletedItems = 0;
        var deletedVersions = 0;
        var failedDirectories = 0;
        var resetRoutingLinks = new List<ResetRoutingLinkInfo>();
        var switchedActiveVersions = new List<SwitchedActiveVersionInfo>();

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
                // #133 (owner decision 2026-09-09): a missing fitting can
                // NEVER be loaded into routing — dependency links to it are
                // dead weight. The former E5 guard (skip) is retired: the
                // item is purged anyway, family_dependencies FK CASCADE
                // resets the parent links, and the caller warns the user
                // that project routing using this fitting became stale.
                // References are read BEFORE the delete (they vanish with it).
                var references = await _dependencyRepository
                    .GetReferencingParentsAsync(itemId, ct)
                    .ConfigureAwait(false);

                // Every version of this item is missing → delete the whole
                // catalog item (FK CASCADE cleans versions/types/attributes
                // and the dependency links).
                SmartConLogger.Info(
                    $"Purging catalog item {itemId} ('{itemGroup.First().ItemName}') — all versions missing");
                try
                {
                    var deleted = await _writableProvider.DeleteItemAsync(itemId, ct).ConfigureAwait(false);
                    if (deleted)
                    {
                        deletedItems++;
                        // All version ROWS die with the item — a label may
                        // carry several Revit variants.
                        deletedVersions += versions.Count;
                        CollectResetRoutingLinks(references, itemGroup.First().ItemName, resetRoutingLinks);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // The files are ALREADY missing/unreachable — a filesystem
                    // error must not block the catalog cleanup (that is the
                    // whole point of the purge). Delete the rows DB-only and
                    // report the stale directory for manual removal.
                    failedDirectories++;
                    SmartConLogger.Warn(
                        $"Purge: cannot delete files for item {itemId}: {ex.Message} — deleting catalog rows anyway. " +
                        $"[Action: удалите папку вручную: files\\{itemId}]");
                    var dbDeleted = await DeleteItemDbOnlyAsync(itemId, ct).ConfigureAwait(false);
                    if (dbDeleted)
                    {
                        deletedItems++;
                        deletedVersions += versions.Count;
                        CollectResetRoutingLinks(references, itemGroup.First().ItemName, resetRoutingLinks);
                    }
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
                        $"[Action: skipping purge for this item — re-run the update after fixing the catalog]");
                    continue;
                }
                switchedActiveVersions.Add(new SwitchedActiveVersionInfo(item!.Name, newestRemaining));
            }

            foreach (var label in itemGroup.Select(g => g.VersionLabel).Distinct(StringComparer.Ordinal))
            {
                try
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    failedDirectories++;
                    SmartConLogger.Warn(
                        $"Purge: cannot delete files for version '{label}' of item {itemId}: {ex.Message} — deleting catalog rows anyway. " +
                        $"[Action: удалите папку вручную: files\\{itemId}\\{label}]");
                    var dbDeleted = await DeleteVersionDbOnlyAsync(itemId, label, ct).ConfigureAwait(false);
                    deletedVersions += dbDeleted;
                }
            }
        }

        SmartConLogger.Info(
            $"Purge finished: deletedItems={deletedItems}, deletedVersions={deletedVersions}, " +
            $"failedDirectories={failedDirectories}, resetRoutingLinks={resetRoutingLinks.Count}, " +
            $"switchedActiveVersions={switchedActiveVersions.Count}");

        // #249 (Phase 5): garbage sweep of the shared CAS preview pool —
        // crashed imports and historical bugs leave pool files with zero
        // references; the purge pass is the natural storage-hygiene
        // moment to collect them.
        try
        {
            await LocalCatalog.SharedPreviewPoolCleanup
                .SweepOrphanedPoolFilesAsync(_database, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"CAS pool sweep skipped: {ex.Message}");
        }

        return new PurgeMissingResult(
            deletedItems, deletedVersions, failedDirectories, resetRoutingLinks, switchedActiveVersions);
    }

    /// <summary>
    /// Records the reset routing links as name pairs (child fitting × parent
    /// family) — deduplicated, the UI lists them so the user sees WHICH
    /// families lost WHICH fitting.
    /// </summary>
    private static void CollectResetRoutingLinks(
        IReadOnlyList<FamilyDependencyReference> references,
        string purgedItemName,
        List<ResetRoutingLinkInfo> target)
    {
        foreach (var reference in references)
        {
            var link = new ResetRoutingLinkInfo(purgedItemName, reference.ParentName);
            if (!target.Contains(link)) target.Add(link);
        }

        if (references.Count > 0)
        {
            SmartConLogger.Warn(
                $"Purge: routing links to '{purgedItemName}' were reset at " +
                $"{string.Join("; ", references.Select(r => $"'{r.ParentName}' ({r.VersionLabel})").Distinct())}. " +
                $"[Action: если фитинг использовался в трассировке проекта — трассировка стала stale: замените фитинг в окне свойств семейства или удалите его из проекта]");
        }
    }

    public async Task<IReadOnlyList<MissingRecordCandidate>> LoadMissingRecordCandidatesAsync(CancellationToken ct = default)
    {
        // One row per (item, version label); variants are ordered by Revit
        // DESC so the FIRST row of a label carries the highest-Revit variant
        // (same convention as LoadAllGroupsAsync).
        var candidates = new List<MissingRecordCandidate>();
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ci.id AS itemId, ci.name AS itemName,
                   cv.version_label AS versionLabel, cv.revit_major_version AS revit,
                   ff.relative_path AS relPath, ff.file_name AS fileName
            FROM catalog_versions cv
            JOIN catalog_items ci ON ci.id = cv.catalog_item_id
            JOIN family_files ff ON ff.id = cv.file_id
            WHERE cv.hash_format_version = @marker
              AND ci.family_source IN ('loadable', 'system')
            ORDER BY ci.name COLLATE NOCASE, ci.id, cv.version_label, cv.revit_major_version DESC
            """;
        cmd.Parameters.Add(new SqliteParameter("@marker", FamilyContentHashFormat.RecalculationMissing));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var seenKeys = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var itemId = reader.GetString(reader.GetOrdinal("itemId"));
            var label = reader.GetString(reader.GetOrdinal("versionLabel"));
            if (!seenKeys.Add(itemId + "|" + label)) continue;
            candidates.Add(new MissingRecordCandidate(
                itemId,
                reader.GetString(reader.GetOrdinal("itemName")),
                label,
                reader.GetString(reader.GetOrdinal("fileName")),
                reader.GetString(reader.GetOrdinal("relPath")),
                reader.GetInt32(reader.GetOrdinal("revit")),
                MissingRecordReason.MarkedMissing));
        }

        SmartConLogger.Debug($"Missing-record candidates loaded: {candidates.Count}");
        return candidates;
    }

    public async Task<int> ScanForMissingFilesAsync(
        IProgress<MissingRecordScanProgress>? progress,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("DbActualize",
            ("Method", nameof(ScanForMissingFilesAsync)));

        var groups = await LoadAllGroupsAsync(ct).ConfigureAwait(false);
        var markedKeys = await LoadMarkedKeysAsync(ct).ConfigureAwait(false);
        var root = _pathResolver.GetDatabaseRoot();

        // File.Exists is synchronous I/O (can hang for tens of seconds on an
        // unreachable SMB share) — keep it off the caller's thread. Progress
        // is reported for EVERY group (the dialog shows "X of Y — file"),
        // and each discovered candidate rides along with its report so the
        // dialog fills the list incrementally — an interrupted scan keeps
        // everything found before the stop.
        var foundCount = await Task.Run(() =>
        {
            var found = 0;
            for (var i = 0; i < groups.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var group = groups[i];
                var top = group.Variants[0];
                MissingRecordCandidate? candidate = null;
                if (markedKeys.Contains(group.Key))
                {
                    // Already marked -2 by a migration run — always a
                    // candidate (cheap list), same row the SQL pass built.
                    candidate = new MissingRecordCandidate(
                        group.CatalogItemId, group.ItemName, group.VersionLabel,
                        top.FileName, top.RelativePath, top.RevitMajorVersion,
                        MissingRecordReason.MarkedMissing);
                }
                else
                {
                    // A label is a candidate only when EVERY variant file is
                    // absent — a single surviving variant keeps the record
                    // working in its Revit version and must not be purged
                    // (DeleteVersionAsync removes all variants of the label).
                    var anyExists = false;
                    foreach (var variant in group.Variants)
                    {
                        if (File.Exists(Path.Combine(root, variant.RelativePath)))
                        {
                            anyExists = true;
                            break;
                        }
                    }
                    if (!anyExists && group.Variants.Count > 0)
                    {
                        candidate = new MissingRecordCandidate(
                            group.CatalogItemId, group.ItemName, group.VersionLabel,
                            top.FileName, top.RelativePath, top.RevitMajorVersion,
                            MissingRecordReason.FileMissing);
                    }
                }
                if (candidate is not null) found++;
                progress?.Report(new MissingRecordScanProgress(i + 1, groups.Count, top.FileName, candidate));
            }
            return found;
        }, ct).ConfigureAwait(false);

        SmartConLogger.Info(
            $"Disk scan finished: groups={groups.Count}, missing={foundCount} " +
            $"(marked -2: {markedKeys.Count})");
        return foundCount;
    }

    /// <summary>
    /// DB-only fallback for purge when the managed directory is unreachable
    /// (network drive permissions): deletes the catalog item row and lets the
    /// FK cascade clean versions/types/attributes/files. The stale directory
    /// is left on disk for manual removal.
    /// </summary>
    private async Task<bool> DeleteItemDbOnlyAsync(string itemId, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA foreign_keys = ON";
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        // #249 (Phase 5): capture the item's CAS pool paths before the
        // CASCADE delete — refcount cleanup runs after the row is gone.
        var capturedPoolPaths = await CapturePoolPathsAsync(connection, itemId, null, ct)
            .ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM catalog_items WHERE id = @id";
        cmd.Parameters.Add(new SqliteParameter("@id", itemId));
        var deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false) > 0;
        foreach (var poolPath in capturedPoolPaths)
        {
            await LocalCatalog.SharedPreviewPoolCleanup
                .DeletePoolFileIfOrphanedAsync(_database, poolPath, ct)
                .ConfigureAwait(false);
        }
        return deleted;
    }

    private async Task<int> DeleteVersionDbOnlyAsync(string itemId, string label, CancellationToken ct)
    {
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using (var pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = "PRAGMA foreign_keys = ON";
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        // #249 (Phase 5): the label's family_assets rows are bound by
        // (item, label) with no FK to catalog_versions — deleting only
        // the version row would orphan them (pre-existing gap, now also
        // breaking CAS refcounting). Capture pool paths, delete the asset
        // rows, then the version row; refcount-clean the pool after.
        var capturedPoolPaths = await CapturePoolPathsAsync(connection, itemId, label, ct)
            .ConfigureAwait(false);
        using (var assetsCmd = connection.CreateCommand())
        {
            assetsCmd.CommandText = "DELETE FROM family_assets WHERE catalog_item_id = @id AND version_label = @label";
            assetsCmd.Parameters.Add(new SqliteParameter("@id", itemId));
            assetsCmd.Parameters.Add(new SqliteParameter("@label", label));
            await assetsCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        int deleted;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "DELETE FROM catalog_versions WHERE catalog_item_id = @id AND version_label = @label";
            cmd.Parameters.Add(new SqliteParameter("@id", itemId));
            cmd.Parameters.Add(new SqliteParameter("@label", label));
            deleted = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
        foreach (var poolPath in capturedPoolPaths)
        {
            await LocalCatalog.SharedPreviewPoolCleanup
                .DeletePoolFileIfOrphanedAsync(_database, poolPath, ct)
                .ConfigureAwait(false);
        }
        return deleted;
    }

    /// <summary>
    /// #249 (Phase 5): reads the CAS pool paths referenced by an item's
    /// (optionally one label's) <c>family_assets</c> rows — captured
    /// BEFORE the rows are deleted so refcount cleanup can run after.
    /// </summary>
    private static async Task<List<string>> CapturePoolPathsAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        string itemId, string? label, CancellationToken ct)
    {
        var paths = new List<string>();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = label is null
            ? """
                SELECT relative_path FROM family_assets
                WHERE catalog_item_id = @id AND relative_path LIKE @poolPrefix
                """
            : """
                SELECT relative_path FROM family_assets
                WHERE catalog_item_id = @id AND version_label = @label
                  AND relative_path LIKE @poolPrefix
                """;
        cmd.Parameters.Add(new SqliteParameter("@id", itemId));
        if (label is not null)
        {
            cmd.Parameters.Add(new SqliteParameter("@label", label));
        }
        cmd.Parameters.Add(new SqliteParameter("@poolPrefix",
            LocalCatalog.StoragePathResolver.SharedPreviewPoolRelativePrefix + "%"));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                paths.Add(reader.GetString(0));
            }
        }
        return paths;
    }
}
