using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services.Actualization;

/// <summary>
/// Default <see cref="ICatalogActualizationService"/> (ADR-054). See the
/// interface for the contract. Files are opened ONE AT A TIME (open →
/// extract → close): documents are never held open, and the known Revit
/// degradation after ~30 open/close cycles is handled by making the run
/// cancellable/resumable instead (restart resumes from the stop point —
/// every task clears its own detection when its artifact is written).
/// Tasks commit their own writes per group (short transactions, I-14).
/// </summary>
internal sealed class CatalogActualizationService : ICatalogActualizationService
{
    private readonly LocalCatalogDatabase _database;
    private readonly StoragePathResolver _pathResolver;
    private readonly IFamilyMigrationExtractor _extractor;
    private readonly IFamilyCatalogProvider _catalogProvider;
    private readonly IWritableFamilyCatalogProvider _writableProvider;
    private readonly IFamilyDependencyRepository _dependencyRepository;
    private readonly IReadOnlyList<IDatabaseActualizationTask> _tasks;

    public CatalogActualizationService(
        LocalCatalogDatabase database,
        StoragePathResolver pathResolver,
        IFamilyMigrationExtractor extractor,
        IFamilyCatalogProvider catalogProvider,
        IWritableFamilyCatalogProvider writableProvider,
        IFamilyDependencyRepository dependencyRepository,
        IEnumerable<IDatabaseActualizationTask> tasks)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _pathResolver = pathResolver ?? throw new ArgumentNullException(nameof(pathResolver));
        _extractor = extractor ?? throw new ArgumentNullException(nameof(extractor));
        _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
        _writableProvider = writableProvider ?? throw new ArgumentNullException(nameof(writableProvider));
        _dependencyRepository = dependencyRepository ?? throw new ArgumentNullException(nameof(dependencyRepository));
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(tasks);
#else
        if (tasks is null) throw new ArgumentNullException(nameof(tasks));
#endif
        _tasks = tasks.OrderBy(t => t.Order).ToList();
    }

    public async Task<DatabasePendingBreakdown> CountPendingBreakdownAsync(
        int revitMajorVersion, CancellationToken ct = default)
    {
        var critical = 0;
        var optional = 0;
        var newerCritical = 0;
        var newerOptional = 0;
        var newerCriticalRequired = 0;
        var newerOptionalRequired = 0;
        foreach (var task in _tasks)
        {
            try
            {
                var pending = await task.CountPendingAsync(revitMajorVersion, ct).ConfigureAwait(false);
                var newer = await task.GetNewerOnlyPendingAsync(revitMajorVersion, ct).ConfigureAwait(false);
                if (task.IsCritical)
                {
                    critical += pending;
                    newerCritical += newer.Count;
                    newerCriticalRequired = Math.Max(newerCriticalRequired, newer.RequiredRevitVersion);
                }
                else
                {
                    optional += pending;
                    newerOptional += newer.Count;
                    newerOptionalRequired = Math.Max(newerOptionalRequired, newer.RequiredRevitVersion);
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Actualization task '{task.Id}': pending check failed: {ex.Message} " +
                    $"[Action: проверьте лог smartcon.log; задача будет повторно проверена при следующем переключении базы]");
            }
        }
        return new DatabasePendingBreakdown(critical, optional, newerCritical, newerOptional, newerCriticalRequired, newerOptionalRequired);
    }

    public async Task<DatabaseMigrationResult> RunAllPendingAsync(
        int revitMajorVersion,
        IProgress<DatabaseMigrationProgress>? progress,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("DbActualize",
            ("Method", nameof(RunAllPendingAsync)),
            ("RevitVersion", revitMajorVersion));

        if (ct.IsCancellationRequested)
        {
            SmartConLogger.Info("Actualization cancelled before start — nothing was modified");
            return DatabaseMigrationResult.Cancelled;
        }

        // Step 1: file-free passes (e.g. system-family hash re-flag).
        var fileFreeUpdated = 0;
        foreach (var task in _tasks)
        {
            try
            {
                fileFreeUpdated += await task.RunFileFreePassAsync(revitMajorVersion, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Actualization task '{task.Id}': file-free pass failed: {ex.Message} " +
                    $"[Action: задача продолжит файл-фазу; повторите «Обновить базу»]");
            }
        }
        if (fileFreeUpdated > 0)
            SmartConLogger.Info($"File-free passes updated {fileFreeUpdated} record(s)");

        // Step 2: per-task detections → union of pending group keys.
        var pendingByTask = new Dictionary<IDatabaseActualizationTask, IReadOnlyCollection<string>>();
        var unionKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in _tasks)
        {
            try
            {
                var keys = await task.LoadPendingGroupKeysAsync(revitMajorVersion, ct).ConfigureAwait(false);
                pendingByTask[task] = keys;
                unionKeys.UnionWith(keys);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Actualization task '{task.Id}': detection failed: {ex.Message} " +
                    $"[Action: задача пропущена в этом запуске; повторите «Обновить базу»]");
            }
        }

        // Step 3: load group rows for the union (loadable and system groups
        // are loaded once and intersected in memory — rows are small, and the
        // key-set filter beats a per-task IN query).
        var allGroups = await LoadAllGroupsAsync(ct).ConfigureAwait(false);
        var processable = new List<(ActualizationGroup Group, ActualizationVariant Openable)>();
        var newerRevitCount = 0;
        foreach (var group in allGroups)
        {
            if (!unionKeys.Contains(group.Key)) continue;
            // Variants are ordered revit DESC — first openable is the
            // highest Revit version the running Revit can open.
            var openable = group.Variants.FirstOrDefault(v => v.RevitMajorVersion <= revitMajorVersion);
            if (openable is null)
                newerRevitCount++;
            else
                processable.Add((group, openable));
        }
        SmartConLogger.Info(
            $"Pending groups: {processable.Count} (processable), {newerRevitCount} (newer Revit only — left pending), " +
            $"tasks with detections: {pendingByTask.Count}");

        var missing = new List<HashRecalculationMissingFile>();
        var failed = new List<HashRecalculationFailedFile>();
        var updatedCount = 0;
        var wasCancelled = false;

        for (var i = 0; i < processable.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                wasCancelled = true;
                SmartConLogger.Info(
                    $"Actualization cancelled by user at {i} of {processable.Count} — committed work stays, " +
                    "the rest will be offered again on the next run");
                break;
            }

            var (group, openable) = processable[i];
            var pendingTasks = TasksPendingOn(pendingByTask, group.Key);
            var absolutePath = Path.Combine(_pathResolver.GetDatabaseRoot(), openable.RelativePath);
            progress?.Report(new DatabaseMigrationProgress(i + 1, processable.Count, openable.FileName));

            if (!File.Exists(absolutePath))
            {
                SmartConLogger.Warn(
                    $"Managed file not found: '{openable.FileName}' (item '{group.ItemName}', {group.VersionLabel}) " +
                    $"[Action: восстановите файл или удалите запись каталога через purge на экране сводки]");
                missing.Add(new HashRecalculationMissingFile(
                    group.CatalogItemId, group.ItemName, group.VersionLabel, openable.FileName));
                foreach (var task in pendingTasks)
                {
                    await SafeHandleFailureAsync(task, group, ActualizationFailureKind.MissingFile, ct)
                        .ConfigureAwait(false);
                }
                continue;
            }

            var extractionTasks = pendingTasks.Where(t => t.RequiresExtraction).ToList();
            var extractionFailed = false;
            FamilyActualizationContext context;

            if (extractionTasks.Count > 0)
            {
                FamilyMigrationExtractResult extract;
                try
                {
                    // Managed-storage convention: system families are staged as
                    // .rvt projects (category-only extraction — the engine's one
                    // open per group stays), loadable families as .rfa.
                    extract = openable.RelativePath.EndsWith(".rvt", StringComparison.OrdinalIgnoreCase)
                        ? await _extractor.ExtractSystemCategoryAsync(absolutePath, ct).ConfigureAwait(false)
                        : await _extractor.ExtractLoadableWithGeometryAsync(absolutePath, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    wasCancelled = true;
                    SmartConLogger.Info("Actualization cancelled by user (extract aborted)");
                    break;
                }
                catch (Exception ex)
                {
                    extract = FamilyMigrationExtractResult.Fail($"{ex.GetType().Name}: {ex.Message}");
                }

                if (!extract.Success || extract.LoadableSnapshot is null)
                {
                    extractionFailed = true;
                    var error = extract.ErrorMessage ?? "unknown extraction failure";
                    SmartConLogger.Warn(
                        $"Extraction failed for '{openable.FileName}' (item '{group.ItemName}', {group.VersionLabel}): {error} " +
                        $"[Action: семья будет предложена снова при следующем запуске; при повторении — переимпортируйте её]");
                    failed.Add(new HashRecalculationFailedFile(
                        group.ItemName, group.VersionLabel, openable.FileName, error));
                    // Only extraction-based tasks are coupled to the failure;
                    // file-level tasks (RequiresExtraction=false) never read
                    // the snapshot and must not receive a terminal marker for
                    // a subsystem they never used (audit B3 finding).
                    foreach (var task in extractionTasks)
                    {
                        await SafeHandleFailureAsync(task, group, ActualizationFailureKind.ExtractionFailed, ct)
                            .ConfigureAwait(false);
                    }
                    if (extractionTasks.Count == pendingTasks.Count) continue;
                    context = FamilyActualizationContext.WithoutExtraction(group, openable, absolutePath);
                }
                else
                {
                    context = new FamilyActualizationContext(
                        group, openable, absolutePath,
                        extract.LoadableSnapshot, extract.Geometry, extract.SystemSnapshot,
                        extract.SharedNestedSnapshots, extract.SharedNestedSubtrees);
                }
            }
            else
            {
                // File-level-only group (e.g. mini-project-marker-v1): the
                // task owns open/save itself — the engine must not open the
                // file at all (no double open, the cheapest possible pass).
                context = FamilyActualizationContext.WithoutExtraction(group, openable, absolutePath);
            }

            var groupFailed = false;
            foreach (var task in pendingTasks)
            {
                // Extraction-based tasks cannot apply without their snapshot
                // (they were already failure-notified above).
                if (extractionFailed && task.RequiresExtraction) continue;
                try
                {
                    await task.ApplyAsync(context, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    wasCancelled = true;
                    SmartConLogger.Info("Actualization cancelled by user (apply aborted) — committed work stays");
                    break;
                }
                catch (Exception ex)
                {
                    // A failed task leaves its own artifact unwritten → its
                    // detection still fires → the next run resumes exactly
                    // here. Other tasks of the same family are unaffected.
                    groupFailed = true;
                    SmartConLogger.Warn(
                        $"Actualization task '{task.Id}' failed for '{openable.FileName}': {ex.GetType().Name}: {ex.Message} " +
                        $"[Action: повторите «Обновить базу» — задача продолжится с места остановки]");
                }
            }
            if (wasCancelled) break;
            if (!groupFailed) updatedCount++;
        }

        SmartConLogger.Info(
            $"Actualization finished: updated={updatedCount} (+{fileFreeUpdated} file-free), " +
            $"missing={missing.Count}, failed={failed.Count}, newerRevitPending={newerRevitCount}, cancelled={wasCancelled}");

        return new DatabaseMigrationResult(
            UpdatedCount: updatedCount + fileFreeUpdated,
            NewerRevitCount: newerRevitCount,
            MissingFiles: missing,
            FailedFiles: failed,
            WasCancelled: wasCancelled);
    }

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

    private async Task<HashSet<string>> LoadMarkedKeysAsync(CancellationToken ct)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT cv.catalog_item_id, cv.version_label
            FROM catalog_versions cv
            JOIN catalog_items ci ON ci.id = cv.catalog_item_id
            WHERE cv.hash_format_version = @marker
              AND ci.family_source IN ('loadable', 'system')
            """;
        cmd.Parameters.Add(new SqliteParameter("@marker", FamilyContentHashFormat.RecalculationMissing));
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            keys.Add(reader.GetString(0) + "|" + reader.GetString(1));
        }
        return keys;
    }

    private List<IDatabaseActualizationTask> TasksPendingOn(
        Dictionary<IDatabaseActualizationTask, IReadOnlyCollection<string>> pendingByTask, string groupKey)
    {
        var result = new List<IDatabaseActualizationTask>();
        foreach (var task in _tasks)
        {
            if (pendingByTask.TryGetValue(task, out var keys) && keys.Contains(groupKey))
                result.Add(task);
        }
        return result;
    }

    private async Task SafeHandleFailureAsync(
        IDatabaseActualizationTask task,
        ActualizationGroup group,
        ActualizationFailureKind kind,
        CancellationToken ct)
    {
        try
        {
            await task.HandleGroupFailureAsync(group, kind, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Actualization task '{task.Id}': failure handling failed for '{group.ItemName}': {ex.Message} " +
                $"[Action: задача останется pending и будет предложена снова]");
        }
    }

    /// <summary>
    /// All loadable and system (item, label) groups with ALL their variants,
    /// ordered so the FIRST variant of a group is the one saved in the
    /// highest Revit version. System groups are inert for tasks whose
    /// detection is loadable-scoped — they are extracted only when a task
    /// (e.g. revit-category) keys them as pending.
    /// </summary>
    private async Task<List<ActualizationGroup>> LoadAllGroupsAsync(CancellationToken ct)
    {
        var groups = new List<ActualizationGroup>();
        using var connection = _database.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT ci.id AS itemId, ci.name AS itemName, ci.current_version_label AS currentLabel,
                   cv.version_label AS versionLabel,
                   cv.id AS versionId, cv.file_id AS fileId, cv.revit_major_version AS revit,
                   ff.relative_path AS relPath, ff.file_name AS fileName
            FROM catalog_versions cv
            JOIN catalog_items ci ON ci.id = cv.catalog_item_id
            JOIN family_files ff ON ff.id = cv.file_id
            WHERE ci.family_source IN ('loadable', 'system')
            ORDER BY ci.id, cv.version_label, cv.revit_major_version DESC
            """;
        using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        string? currentKey = null;
        string? itemId = null, itemName = null, currentLabel = null, label = null;
        var variants = new List<ActualizationVariant>();

        void FlushGroup()
        {
            if (currentKey is null) return;
            groups.Add(new ActualizationGroup(
                itemId!, itemName!, label!,
                string.Equals(label, currentLabel, StringComparison.Ordinal),
                variants.ToArray()));
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
                variants = new List<ActualizationVariant>();
            }

            variants.Add(new ActualizationVariant(
                reader.GetString(reader.GetOrdinal("versionId")),
                reader.GetString(reader.GetOrdinal("fileId")),
                reader.GetInt32(reader.GetOrdinal("revit")),
                reader.GetString(reader.GetOrdinal("relPath")),
                reader.GetString(reader.GetOrdinal("fileName"))));
        }
        FlushGroup();

        return groups;
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
