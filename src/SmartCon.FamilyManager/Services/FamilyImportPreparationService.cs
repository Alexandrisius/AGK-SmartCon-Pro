using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Import;
using SmartCon.FamilyManager.Services.LocalCatalog;

namespace SmartCon.FamilyManager.Services;

/// <summary>
/// Coordinates Phase 1 (Prepare) of the unified import flow.
/// Opens documents, extracts snapshots, computes content hashes,
/// and runs dedup checks — all in a single pass per family.
/// Open documents are held in a dictionary so Phase 3 (Commit)
/// can SaveAs from the same document without re-opening.
/// </summary>
public sealed class FamilyImportPreparationService : IFamilyImportPreparationService
{
    private readonly IFamilyManagerAwaitableEvent _awaitableEvent;
    private readonly IFamilySnapshotExtractor _snapshotExtractor;
    private readonly IFamilyContentHasher _contentHasher;
    private readonly IContentHashDedupService _dedupService;
    private readonly IRevitContext _revitContext;
    private readonly IFamilyTypeCatalogBaker _typeCatalogBaker;
    private readonly IFamilyHealthChecker _healthChecker;
    private readonly IFamilyDependencyCollector _dependencyCollector;
    private readonly IFamilyVersionStore _versionStore;

    private readonly Dictionary<string, Document> _openedDocuments = new(StringComparer.Ordinal);

    // UC-2 (#209): the active family document is never held in
    // _openedDocuments (the prepare cleanup must NOT close the user's
    // document) — the nested queue resolves it through this override.
    private Document? _activeFamilyDoc;
    private string? _activeFamilyDocKey;

    public FamilyImportPreparationService(
        IFamilyManagerAwaitableEvent awaitableEvent,
        IFamilySnapshotExtractor snapshotExtractor,
        IFamilyContentHasher contentHasher,
        IContentHashDedupService dedupService,
        IRevitContext revitContext,
        IFamilyTypeCatalogBaker typeCatalogBaker,
        IFamilyHealthChecker healthChecker,
        IFamilyDependencyCollector dependencyCollector,
        IFamilyVersionStore versionStore)
    {
        _awaitableEvent = awaitableEvent ?? throw new ArgumentNullException(nameof(awaitableEvent));
        _snapshotExtractor = snapshotExtractor ?? throw new ArgumentNullException(nameof(snapshotExtractor));
        _contentHasher = contentHasher ?? throw new ArgumentNullException(nameof(contentHasher));
        _dedupService = dedupService ?? throw new ArgumentNullException(nameof(dedupService));
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
        _typeCatalogBaker = typeCatalogBaker ?? throw new ArgumentNullException(nameof(typeCatalogBaker));
        _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
        _dependencyCollector = dependencyCollector ?? throw new ArgumentNullException(nameof(dependencyCollector));
        _versionStore = versionStore ?? throw new ArgumentNullException(nameof(versionStore));
    }

    /// <summary>
    /// Prepare a batch of .rfa files from disk for the batch import dialog.
    /// Opens each file, extracts a snapshot, computes a content hash,
    /// and runs a dedup check. Documents are held open for Phase 3.
    /// </summary>
    public async Task<IReadOnlyList<PreparedFamilyItem>> PrepareForFileImportAsync(
        IReadOnlyList<string> filePaths,
        CancellationToken ct = default)
    {
        if (filePaths is null || filePaths.Count == 0)
            return Array.Empty<PreparedFamilyItem>();

        LogDiagSnapshot("Prepare.entry", _openedDocuments.Count);

        // A previous UC-2 run may have left the override behind when its
        // dialog was cancelled without cleanup — never let a stale active
        // document leak into the file-based nested resolution.
        _activeFamilyDoc = null;
        _activeFamilyDocKey = null;

        using var _scope = SmartConLogger.BeginScope("FamilyPrep",
            ("Method", nameof(PrepareForFileImportAsync)),
            ("FileCount", filePaths.Count));

        SmartConLogger.Info($"Preparing {filePaths.Count} file(s) for batch import");

        var results = new List<PreparedFamilyItem>(filePaths.Count);
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var filePath in filePaths)
        {
            ct.ThrowIfCancellationRequested();

            if (!seenPaths.Add(filePath))
            {
                SmartConLogger.Warn(
                    $"Duplicate path in batch: '{Path.GetFileName(filePath)}' — skipped. " +
                    "[Action: user selected the same file twice — second occurrence is ignored]");
                continue;
            }

            try
            {
                var item = await PrepareSingleFileAsync(filePath, ct).ConfigureAwait(false);
                results.Add(item);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Prepare failed for '{Path.GetFileName(filePath)}': {ex.GetType().Name}: {ex.Message} " +
                    "[Action: file will be shown as Error in batch dialog]");
                results.Add(new PreparedFamilyItem(
                    SourcePath: filePath,
                    DisplayName: Path.GetFileNameWithoutExtension(filePath),
                    RevitMajorVersion: 0,
                    ContentHash: null,
                    LoadableSnapshot: null,
                    SystemSnapshot: null,
                    ErrorMessage: ex.Message,
                    Source: null,
                    SourceTypes: null,
                    FamilySource: "loadable"));
            }
        }

        SmartConLogger.Info(
            $"Prepare complete: {results.Count} items, " +
            $"{results.Count(r => r.ContentHash is not null)} with hash, " +
            $"{results.Count(r => r.ErrorMessage is not null)} errors, " +
            $"{_openedDocuments.Count} documents held open");

        await PrepareSharedNestedItemsAsync(results, ct).ConfigureAwait(false);
        await FinalizeLoadableHashesAsync(results, ct).ConfigureAwait(false);

        LogDiagSnapshot("Prepare.exit", _openedDocuments.Count);

        return results;
    }

    /// <summary>
    /// Prepare the active family document (.rfa in Family Editor) for import.
    /// The document is already open — no OpenDocumentFile needed. Returns the
    /// parent item FIRST, followed by its shared-nested children (E2, #209):
    /// the nested queue re-opens each shared nested from the live document
    /// (EditFamily independent copy, probe P2) exactly like the file-based
    /// flow, so the batch dialog shows the full dependency set.
    /// </summary>
    public async Task<IReadOnlyList<PreparedFamilyItem>> PrepareActiveFamilyAsync(
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyPrep",
            ("Method", nameof(PrepareActiveFamilyAsync)));

        Document activeDoc;
        try
        {
            activeDoc = await _awaitableEvent
                .RaiseAsync(app => _revitContext.GetDocument(), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed to get active document: {ex.Message}");
            throw;
        }

        if (!activeDoc.IsFamilyDocument)
            throw new InvalidOperationException("Active document is not a family document.");

        var displayName = !string.IsNullOrEmpty(activeDoc.Title)
            ? activeDoc.Title
            : "ActiveFamily";
        if (displayName.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase))
            displayName = displayName[..^4];

        SmartConLogger.Info($"Preparing active family: '{displayName}'");

        var sourcePath = activeDoc.PathName ?? $"active://{displayName}";
        _activeFamilyDoc = activeDoc;
        _activeFamilyDocKey = sourcePath;

        try
        {
            // Active document stays untouched (no type switching — the user
            // is editing it): collect accumulated warnings only.
            var healthReport = await _awaitableEvent
                .RaiseAsync(app => _healthChecker.CheckActiveFamilyDocument(activeDoc), ct)
                .ConfigureAwait(false);

            IReadOnlyList<FamilyDependencyDescriptor>? sharedNested = null;
            var snapshot = await _awaitableEvent
                .RaiseAsync(app =>
                {
                    var extracted = _snapshotExtractor.ExtractFromFamilyDocument(activeDoc);
                    // ADR-066 (E2): same Revit-thread roundtrip — shared
                    // nested families are scanned flat in the family
                    // document (probe P1).
                    sharedNested = _dependencyCollector.CollectSharedNestedDependencies(activeDoc);
                    return extracted;
                }, ct)
                .ConfigureAwait(false);

            var results = new List<PreparedFamilyItem>
            {
                new(
                    SourcePath: sourcePath,
                    DisplayName: displayName,
                    RevitMajorVersion: GetRevitMajorVersion(),
                    ContentHash: null,
                    LoadableSnapshot: snapshot,
                    SystemSnapshot: null,
                    ErrorMessage: null,
                    Source: null,
                    SourceTypes: null,
                    FamilySource: "loadable",
                    HealthReport: healthReport,
                    SharedNestedDependencies: sharedNested),
            };

            await PrepareSharedNestedItemsAsync(results, ct).ConfigureAwait(false);
            await FinalizeLoadableHashesAsync(results, ct).ConfigureAwait(false);

            SmartConLogger.Info(
                $"Active family prepared: '{displayName}' + {results.Count - 1} shared nested, " +
                $"status={results[0].Status}");

            return results;
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Prepare active family failed: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// Prepare system family categories and loadable families from the
    /// active project for the batch import dialog. System snapshots are
    /// extracted from the active project document. Loadable families are
    /// opened via EditFamily and held open for Phase 3.
    /// </summary>
    public async Task<IReadOnlyList<PreparedFamilyItem>> PrepareProjectImportAsync(
        IReadOnlyList<CategoryAnalysis> systemAnalyses,
        IReadOnlyList<LoadableFamilyInfo> loadableFamilies,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyPrep",
            ("Method", nameof(PrepareProjectImportAsync)),
            ("SystemCount", systemAnalyses.Count),
            ("LoadableCount", loadableFamilies.Count));

        _activeFamilyDoc = null;
        _activeFamilyDocKey = null;

        SmartConLogger.Info(
            $"Preparing project import: {systemAnalyses.Count} system categories, " +
            $"{loadableFamilies.Count} loadable families");

        var results = new List<PreparedFamilyItem>();

        foreach (var analysis in systemAnalyses)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var item = await PrepareSystemCategoryAsync(analysis, ct).ConfigureAwait(false);
                results.Add(item);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Prepare system category '{analysis.DisplayName}' failed: {ex.Message} " +
                    "[Action: category will be shown as Error in batch dialog]");
                results.Add(new PreparedFamilyItem(
                    SourcePath: $"system://{analysis.DisplayName}",
                    DisplayName: analysis.DisplayName,
                    RevitMajorVersion: GetRevitMajorVersion(),
                    ContentHash: null,
                    LoadableSnapshot: null,
                    SystemSnapshot: null,
                    ErrorMessage: ex.Message,
                    Source: null,
                    SourceTypes: null,
                    FamilySource: "system"));
            }
        }

        foreach (var loadable in loadableFamilies)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var item = await PrepareLoadableFromProjectAsync(loadable, ct).ConfigureAwait(false);
                results.Add(item);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Prepare loadable '{loadable.FamilyName}' failed: {ex.Message} " +
                    "[Action: family will be shown as Error in batch dialog]");
                results.Add(new PreparedFamilyItem(
                    SourcePath: $"loadable://{loadable.FamilyName}",
                    DisplayName: loadable.FamilyName,
                    RevitMajorVersion: GetRevitMajorVersion(),
                    ContentHash: null,
                    LoadableSnapshot: null,
                    SystemSnapshot: null,
                    ErrorMessage: ex.Message,
                    Source: null,
                    SourceTypes: null,
                    FamilySource: "loadable"));
            }
        }

        await PrepareDependencyItemsAsync(results, ct).ConfigureAwait(false);
        await PrepareSharedNestedItemsAsync(results, ct).ConfigureAwait(false);
        await FinalizeLoadableHashesAsync(results, ct).ConfigureAwait(false);

        SmartConLogger.Info(
            $"Project prepare complete: {results.Count} items, " +
            $"{_openedDocuments.Count} EditFamily docs held open");

        return results;
    }

    /// <summary>
    /// ADR-066 (E1): prepares dependency families discovered on system items
    /// (routing fittings) as regular loadable batch rows. A family already
    /// queued as a top-level row is NOT duplicated — it only gains the
    /// dependency links. Each new dependency row goes through the full
    /// standard pipeline (EditFamily → snapshot → FHV hash → dedup), so it
    /// arrives in the dialog with a real New/Duplicate/Existing status.
    /// </summary>
    private async Task PrepareDependencyItemsAsync(
        List<PreparedFamilyItem> results,
        CancellationToken ct)
    {
        var byUniqueId = new Dictionary<string, (FamilyDependencyDescriptor Descriptor, List<FamilyDependencyLink> Links)>(StringComparer.Ordinal);
        foreach (var systemItem in results)
        {
            if (systemItem.RoutingDependencies is null) continue;
            foreach (var descriptor in systemItem.RoutingDependencies)
            {
                if (!byUniqueId.TryGetValue(descriptor.FamilyUniqueId, out var entry))
                {
                    entry = (descriptor, new List<FamilyDependencyLink>());
                    byUniqueId.Add(descriptor.FamilyUniqueId, entry);
                }

                entry.Links.Add(new FamilyDependencyLink(
                    systemItem.SourcePath, descriptor.Kind, descriptor.PartName));
            }
        }

        if (byUniqueId.Count == 0)
        {
            return;
        }

        SmartConLogger.Info($"Preparing {byUniqueId.Count} dependency families (routing fittings)");

        foreach (var pair in byUniqueId)
        {
            ct.ThrowIfCancellationRequested();
            var uniqueId = pair.Key;
            var entry = pair.Value;
            var links = (IReadOnlyList<FamilyDependencyLink>)entry.Links;

            var topLevelIndex = results.FindIndex(r =>
                r.Source is FamilyImportSource.LoadableSource loadableSource &&
                string.Equals(loadableSource.FamilyUniqueId, uniqueId, StringComparison.Ordinal));
            if (topLevelIndex >= 0)
            {
                results[topLevelIndex] = results[topLevelIndex] with { DependencyLinks = links };
                SmartConLogger.Debug(
                    $"Dependency '{entry.Descriptor.FamilyName}' already queued as top-level row — links attached");
                continue;
            }

            try
            {
                var info = new LoadableFamilyInfo(
                    entry.Descriptor.FamilyName,
                    uniqueId,
                    entry.Descriptor.CategoryName ?? string.Empty,
                    TypeCount: 0);
                var item = await PrepareLoadableFromProjectAsync(info, ct).ConfigureAwait(false);
                results.Add(item with { DependencyLinks = links });
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Prepare dependency '{entry.Descriptor.FamilyName}' failed: {ex.Message} " +
                    "[Action: семейство будет показано как Error в batch-диалоге; родительская категория будет импортирована без связи на него]");
                results.Add(new PreparedFamilyItem(
                    SourcePath: $"loadable://{entry.Descriptor.FamilyName}",
                    DisplayName: entry.Descriptor.FamilyName,
                    RevitMajorVersion: GetRevitMajorVersion(),
                    ContentHash: null,
                    LoadableSnapshot: null,
                    SystemSnapshot: null,
                    ErrorMessage: ex.Message,
                    Source: null,
                    SourceTypes: null,
                    FamilySource: "loadable",
                    DependencyLinks: links));
            }
        }
    }

    /// <summary>
    /// ADR-066 (E2, #209): prepares shared-nested dependencies of loadable
    /// items as regular loadable batch rows. The nested family is re-opened
    /// from the PARENT'S held-open family document via EditFamily (an
    /// independent copy — survives the parent's later Close, probe P3) and
    /// held open under a synthetic <c>"nested://{FamilyName}"</c> key, so
    /// Phase 3 stages it from the held document exactly like any other row —
    /// no temp files. The scan is flat (probe P1): every nesting level is
    /// visible in the top parent's family document, so the queue terminates
    /// without a cycle guard; the claimed-set (normalized family name)
    /// collapses duplicates when several parents embed the same nested
    /// family — later parents just gain a link to the first row.
    /// </summary>
    private async Task PrepareSharedNestedItemsAsync(
        List<PreparedFamilyItem> results,
        CancellationToken ct)
    {
        var claimedByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.FamilySource == "loadable" && r.ErrorMessage is null)
            {
                // Two DIFFERENT top-level families with the same normalized
                // name in one batch: last wins (indexer assignment) — a
                // nested link then attaches to the last row. The collision
                // is a pathological batch (Revit forbids same-name families
                // within one document); accepted heuristic, not worth a
                // dialog-level conflict resolver.
                claimedByName[FamilyNameNormalizer.Normalize(r.DisplayName)] = i;
            }
        }

        var queue = new Queue<(int ParentIndex, FamilyDependencyDescriptor Descriptor)>();
        for (var i = 0; i < results.Count; i++)
        {
            EnqueueNestedOf(results, queue, i);
        }

        if (queue.Count == 0)
        {
            return;
        }

        SmartConLogger.Info($"Preparing shared nested dependencies: {queue.Count} candidate(s)");

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var (parentIndex, descriptor) = queue.Dequeue();
            var parent = results[parentIndex];
            var link = new FamilyDependencyLink(parent.SourcePath, descriptor.Kind, descriptor.PartName);

            var normalized = FamilyNameNormalizer.Normalize(descriptor.FamilyName);
            if (claimedByName.TryGetValue(normalized, out var existingIndex))
            {
                results[existingIndex] = AppendDependencyLink(results[existingIndex], link);
                SmartConLogger.Debug(
                    $"Shared nested '{descriptor.FamilyName}' already queued — link attached to the existing row");
                continue;
            }

            try
            {
                var child = await PrepareNestedFromFamilyDocAsync(parent, descriptor, ct)
                    .ConfigureAwait(false);
                child = AppendDependencyLink(child, link);
                results.Add(child);
                claimedByName[normalized] = results.Count - 1;
                EnqueueNestedOf(results, queue, results.Count - 1);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Prepare shared nested '{descriptor.FamilyName}' failed: {ex.Message} " +
                    "[Action: семейство будет показано как Error в batch-диалоге; родитель будет импортирован без связи на него]");
                results.Add(AppendDependencyLink(new PreparedFamilyItem(
                    SourcePath: $"nested://{descriptor.FamilyName}",
                    DisplayName: descriptor.FamilyName,
                    RevitMajorVersion: GetRevitMajorVersion(),
                    ContentHash: null,
                    LoadableSnapshot: null,
                    SystemSnapshot: null,
                    ErrorMessage: ex.Message,
                    Source: null,
                    SourceTypes: null,
                    FamilySource: "loadable"), link));
                // Claim even on failure: a second parent embedding the same
                // broken nested links to this single Error row instead of
                // duplicating it.
                claimedByName[normalized] = results.Count - 1;
            }
        }
    }

    /// <summary>
    /// Re-opens one shared nested family from the parent's held-open family
    /// document, extracts its snapshot (and its own nested descriptors) in
    /// the same Revit-thread roundtrip, and holds the document open under
    /// the <c>"nested://{FamilyName}"</c> key for Phase 3.
    /// </summary>
    private async Task<PreparedFamilyItem> PrepareNestedFromFamilyDocAsync(
        PreparedFamilyItem parent,
        FamilyDependencyDescriptor descriptor,
        CancellationToken ct)
    {
        var childKey = $"nested://{descriptor.FamilyName}";
        FamilySnapshot? snapshot = null;
        IReadOnlyList<FamilyDependencyDescriptor>? childNested = null;
        FamilyVersion? embeddedMarker = null;

        await _awaitableEvent.RaiseAsync(app =>
        {
            // UC-2 (#209 bug-fix): the active family document is NOT in
            // _openedDocuments (it must never be closed by the prepare
            // cleanup) — resolve it through the registered override.
            var parentDoc = (parent.SourcePath == _activeFamilyDocKey ? _activeFamilyDoc : null)
                ?? GetOpenedDocument(parent.SourcePath)
                ?? throw new InvalidOperationException(
                    $"Parent family document for '{parent.DisplayName}' is not held open");

            // #209 manual-test bug: when the nested family is ALREADY open
            // as a top-level document in the Revit session, EditFamily
            // returns THAT document (with possible unsaved edits) instead of
            // an independent copy. Hashing it would fingerprint someone
            // else's live editor content — and worse, the prepare cleanup
            // could close the user's document. Detect by title among the
            // open documents and fail the row with an actionable message.
            var revitApp = parentDoc.Application;
            foreach (Document openDoc in revitApp.Documents)
            {
                if (openDoc.IsFamilyDocument
                    && string.Equals(openDoc.Title, descriptor.FamilyName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Nested family '{descriptor.FamilyName}' is open in the Family Editor — " +
                        "close it (save or discard changes) before importing the parent");
                }
            }

            var family = parentDoc.GetElement(descriptor.FamilyUniqueId) as Autodesk.Revit.DB.Family
                ?? throw new InvalidOperationException(
                    $"Nested family '{descriptor.FamilyName}' not found in the parent document by UniqueId");

            // #209 (2026-08-11): read the ES version marker left by the
            // stale-update command (written only after FHV10-verified
            // content). Identity-hash matching cannot see past parameter
            // groups (a merge never propagates them), so without the marker
            // an updated embedded family keeps matching its OLD stored
            // version and the parent stays drift-blocked forever.
            embeddedMarker = _versionStore.ReadFromLoadedFamily(parentDoc, family.Id);
            if (embeddedMarker is not null)
            {
                SmartConLogger.Info(
                    $"Nested '{descriptor.FamilyName}' carries ES marker: item={embeddedMarker.CatalogItemId}, " +
                    $"version={embeddedMarker.VersionLabel} — will prefer it over hash matching");
            }

            Document? nestedDoc = null;
            try
            {
                nestedDoc = parentDoc.EditFamily(family);
                snapshot = _snapshotExtractor.ExtractFromFamilyDocument(nestedDoc);
                childNested = _dependencyCollector.CollectSharedNestedDependencies(nestedDoc);
                _openedDocuments[childKey] = nestedDoc;
            }
            catch
            {
                try { nestedDoc?.Close(false); } catch { }
                throw;
            }
        }, ct).ConfigureAwait(false);

        // FHV8 (#209): hash + dedup are DEFERRED to FinalizeLoadableHashesAsync
        // — the composite hash needs the whole shared-nested closure.
        return new PreparedFamilyItem(
            SourcePath: childKey,
            DisplayName: descriptor.FamilyName,
            RevitMajorVersion: GetRevitMajorVersion(),
            ContentHash: null,
            LoadableSnapshot: snapshot,
            SystemSnapshot: null,
            ErrorMessage: null,
            Source: new FamilyImportSource.LoadableSource(
                FamilyName: descriptor.FamilyName,
                // UniqueId is valid in the parent's family document only —
                // it is a last-resort fallback identity for staging; the
                // primary path always resolves the held-open document by
                // the synthetic key above.
                FamilyUniqueId: descriptor.FamilyUniqueId,
                CategoryName: descriptor.CategoryName ?? string.Empty),
            SourceTypes: null,
            FamilySource: "loadable",
            SharedNestedDependencies: childNested,
            EmbeddedMarkerCatalogItemId: embeddedMarker?.CatalogItemId,
            EmbeddedMarkerVersionLabel: embeddedMarker?.VersionLabel);
    }

    private static void EnqueueNestedOf(
        List<PreparedFamilyItem> results,
        Queue<(int ParentIndex, FamilyDependencyDescriptor Descriptor)> queue,
        int parentIndex)
    {
        var nested = results[parentIndex].SharedNestedDependencies;
        if (nested is null) return;
        foreach (var descriptor in nested)
        {
            queue.Enqueue((parentIndex, descriptor));
        }
    }

    private static PreparedFamilyItem AppendDependencyLink(PreparedFamilyItem item, FamilyDependencyLink link)
    {
        var links = item.DependencyLinks is null
            ? new List<FamilyDependencyLink>()
            : new List<FamilyDependencyLink>(item.DependencyLinks);
        links.Add(link);
        return item with { DependencyLinks = links };
    }

    /// <summary>
    /// FHV8 (#209, ADR-066): final Phase-1 pass — composite content hashes
    /// and dedup for every loadable item. Deferred from the per-item
    /// preparation because a family's composite hash covers the composite
    /// hashes of its DIRECT shared-nested children
    /// (<see cref="CompositeFamilyHashComposer"/>), which are known only
    /// after the whole closure has been opened and snapshotted by
    /// <see cref="PrepareSharedNestedItemsAsync"/>. System items keep their
    /// inline hash (their canonical string has no nested section).
    /// </summary>
    private async Task FinalizeLoadableHashesAsync(
        List<PreparedFamilyItem> results, CancellationToken ct)
    {
        var snapshots = new Dictionary<string, FamilySnapshot>(StringComparer.OrdinalIgnoreCase);
        var flatSubtrees = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var itemIndexes = new List<int>();

        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            if (r.FamilySource != "loadable" || r.ErrorMessage is not null || r.LoadableSnapshot is null)
            {
                continue;
            }

            itemIndexes.Add(i);
            var normalized = FamilyNameNormalizer.Normalize(r.DisplayName);
            // Last-wins on a normalized-name collision (same heuristic as
            // the claimed-set in PrepareSharedNestedItemsAsync).
            snapshots[normalized] = r.LoadableSnapshot;
            if (r.SharedNestedDependencies is { Count: > 0 } nested)
            {
                flatSubtrees[normalized] = nested
                    .Select(d => FamilyNameNormalizer.Normalize(d.FamilyName))
                    .ToList();
            }
        }

        if (itemIndexes.Count == 0)
        {
            return;
        }

        var composer = new CompositeFamilyHashComposer(_contentHasher);
        var detailed = composer.ComposeDetailed(snapshots, flatSubtrees);
        var hashes = new Dictionary<string, FamilyContentHash?>(StringComparer.OrdinalIgnoreCase);
        var sectionsByName = new Dictionary<string, IReadOnlyList<ContentSectionHash>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in detailed)
        {
            hashes[name] = value.Hash;
            sectionsByName[name] = value.Sections;
        }

        foreach (var i in itemIndexes)
        {
            ct.ThrowIfCancellationRequested();
            var r = results[i];
            var normalized = FamilyNameNormalizer.Normalize(r.DisplayName);
            if (!hashes.TryGetValue(normalized, out var hash) || hash is null)
            {
                continue;
            }

            var dedupResult = await Task.Run(
                () => _dedupService.CheckAsync(normalized, hash, "loadable", ct: ct),
                ct).ConfigureAwait(false);

            var status = dedupResult.Status;
            var matchedVersionLabel = dedupResult.HashMatch?.MatchedVersionLabel;

            // #209 (2026-08-11): the verified ES marker on the embedded
            // family outranks identity-hash matching — a reload merge never
            // propagates parameter groups, so the hash of a correctly
            // updated nested family keeps matching its OLD stored version
            // (or nothing) and the parent would stay drift-blocked forever.
            var markerOverride = EmbeddedMarkerMatchResolver.ResolveOverride(
                r.EmbeddedMarkerCatalogItemId,
                r.EmbeddedMarkerVersionLabel,
                dedupResult.ExistingCatalogItemId,
                matchedVersionLabel);
            if (markerOverride is not null)
            {
                SmartConLogger.Info(
                    $"Embedded marker override for '{r.DisplayName}': hash match said " +
                    $"{matchedVersionLabel ?? "<none>"}, verified marker says {markerOverride.Value.MatchedVersionLabel} — using the marker");
                status = markerOverride.Value.Status;
                matchedVersionLabel = markerOverride.Value.MatchedVersionLabel;
            }

            results[i] = r with
            {
                ContentHash = hash,
                Status = status,
                ExistingCatalogItemId = dedupResult.ExistingCatalogItemId,
                ExistingVersionLabel = dedupResult.ExistingVersionLabel,
                MatchedVersionLabel = matchedVersionLabel,
                IsCrossNameDuplicate = dedupResult.IsCrossNameDuplicate,
                MatchedItemName = dedupResult.HashMatch?.MatchedItemName,
                // #180: the row must show that the version came from the
                // verified marker, not from content identity (#209 follow-up:
                // "Duplicate (v2)" on v1-group content read as a lie).
                IsMarkerResolvedVersion = markerOverride is not null,
                // #249 (Phase 2): per-type content hashes of the baked-in
                // type values — persisted to family_type_hashes at import.
                // LoadableSnapshot is non-null here (filtered at the top
                // of the loop).
                PerTypeHashes = _contentHasher.ComputePerTypeHashesForLoadable(r.LoadableSnapshot!)
                    ?.Select(kvp => FamilyTypeHashEntry.ForLoadableType(kvp.Key, kvp.Value))
                    .ToList(),
                // #249 (Phase 4): canonical sections of the SAME enriched
                // snapshot as the identity hash (composite-consistent
                // NESTEDHASH) — persisted to section_hashes/section_strings.
                Sections = sectionsByName.TryGetValue(normalized, out var sections)
                    ? sections
                    : null,
            };

            SmartConLogger.Info(
                $"Loadable prepared: '{r.DisplayName}', hash={hash.HexString}, status={status}");
        }
    }

    /// <summary>
    /// Close all documents opened during Phase 1 (Prepare).
    /// Call this when the user cancels the batch dialog.
    /// </summary>
    public async Task CloseAllPreparedDocumentsAsync(CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyPrep",
            ("Method", nameof(CloseAllPreparedDocumentsAsync)),
            ("HeldOpenCount", _openedDocuments.Count));

        SmartConLogger.Info(
            $"Closing prepared documents: heldOpen={_openedDocuments.Count} " +
            "(will also enumerate app.Documents to detect leaked handles)");

        await _awaitableEvent.RaiseAsync(app =>
        {
            LogAllOpenRevitDocuments();

            foreach (var pair in _openedDocuments)
            {
                try
                {
                    if (pair.Value is not null)
                    {
                        // Phase 27B: after staging (SaveAs + ReleaseDocument),
                        // some documents may have been invalidated by Revit.
                        // IsValidObject check prevents "The referenced object
                        // is not valid" warnings during cleanup.
                        if (!pair.Value.IsValidObject)
                        {
                            SmartConLogger.Debug(
                                $"Document '{Path.GetFileName(pair.Key)}' already invalidated by Revit — skipping Close");
                            continue;
                        }
                        pair.Value.Close(false);
                        SmartConLogger.Debug($"Closed document: {Path.GetFileName(pair.Key)}");
                    }
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"Failed to close document '{Path.GetFileName(pair.Key)}': {ex.Message} " +
                        "[Action: document may remain open — user can close manually]");
                }
            }
        }, ct).ConfigureAwait(false);

        _openedDocuments.Clear();
        // UC-2: release the active-document override WITHOUT closing — the
        // user's family document stays open and untouched.
        _activeFamilyDoc = null;
        _activeFamilyDocKey = null;
        SmartConLogger.Info("All prepared documents closed");
    }

    /// <summary>
    /// Get a document that was opened during Phase 1 and is held open
    /// for Phase 3 (SaveAs). Returns null if the path was not prepared,
    /// the entry was null, or the document has been invalidated by Revit
    /// (IsValidObject == false). Stale entries are evicted from the cache.
    /// </summary>
    public Document? GetOpenedDocument(string sourcePath)
    {
        if (_openedDocuments.TryGetValue(sourcePath, out var doc))
        {
            if (doc is null)
            {
                _openedDocuments.Remove(sourcePath);
                return null;
            }

            if (!doc.IsValidObject)
            {
                SmartConLogger.Warn(
                    $"GetOpenedDocument: held-open document for '{Path.GetFileName(sourcePath)}' " +
                    "is invalidated by Revit (IsValidObject=false) — evicting from cache " +
                    "[Action: caller will fall back to a fresh OpenDocumentFile/EditFamily]");
                _openedDocuments.Remove(sourcePath);
                return null;
            }

            SmartConLogger.Debug(
                $"GetOpenedDocument HIT: path='{Path.GetFileName(sourcePath)}', " +
                $"PathName='{(string.IsNullOrEmpty(doc.PathName) ? "<empty>" : doc.PathName)}', " +
                $"Title='{doc.Title}'");
            return doc;
        }

        SmartConLogger.Debug($"GetOpenedDocument MISS: path='{Path.GetFileName(sourcePath)}'");
        return null;
    }

    /// <summary>
    /// Remove a document from the held-open dictionary without closing it.
    /// Use this only when the document is known to be already invalidated or
    /// closed by other means (e.g. exception paths). For the normal Phase 3
    /// flow (SaveAs followed by cleanup) prefer <see cref="CloseAndRelease"/>.
    /// </summary>
    public void ReleaseDocument(string sourcePath)
    {
        _openedDocuments.Remove(sourcePath);
    }

    /// <summary>
    /// Close a held-open document with <c>Close(false)</c> and remove it from
    /// the cache. Must be called on the Revit UI thread (e.g. from inside an
    /// <c>IFamilyManagerAwaitableEvent.RaiseAsync</c> callback) because it
    /// touches <c>Document.IsValidObject</c> and <c>Document.Close</c>.
    /// Silently evicts entries that are null or already invalidated by Revit.
    /// </summary>
    public void CloseAndRelease(string sourcePath)
    {
        if (!_openedDocuments.TryGetValue(sourcePath, out var doc))
        {
            SmartConLogger.Debug($"CloseAndRelease MISS: path='{Path.GetFileName(sourcePath)}'");
            return;
        }

        _openedDocuments.Remove(sourcePath);

        if (doc is null)
            return;

        if (!doc.IsValidObject)
        {
            SmartConLogger.Debug(
                $"CloseAndRelease: document '{Path.GetFileName(sourcePath)}' already invalidated by Revit — " +
                "evicted from cache without Close");
            return;
        }

        try
        {
            doc.Close(false);
            SmartConLogger.Info(
                $"CloseAndRelease: closed held-open document '{Path.GetFileName(sourcePath)}' " +
                $"(PathName='{(string.IsNullOrEmpty(doc.PathName) ? "<empty>" : doc.PathName)}')");
        }
        catch (Autodesk.Revit.Exceptions.InvalidObjectException)
        {
            SmartConLogger.Debug(
                $"CloseAndRelease: document '{Path.GetFileName(sourcePath)}' already closed by Revit " +
                "(InvalidObjectException after SaveAs-overwrite) — no leak");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"CloseAndRelease: failed to close document '{Path.GetFileName(sourcePath)}': {ex.Message} " +
                "[Action: document may remain open in Revit — user can close it manually]");
        }
    }

    /// <summary>
    /// Enumerate every open document in the Revit session and log it with
    /// its title, path, validity and family-document flag. Documents whose
    /// path contains <paramref name="filterId"/> are tagged so callers (e.g.
    /// <c>DeleteFamilyAsync</c> catching <c>IOException</c>) can identify
    /// which open document holds the lock on a managed path. Marshalled via
    /// <c>IFamilyManagerAwaitableEvent</c>, so safe to call from any thread.
    /// </summary>
    public async Task LogOpenRevitDocumentsStateAsync(
        string contextTag,
        string? filterId = null,
        CancellationToken ct = default)
    {
        using var _scope = SmartConLogger.BeginScope("FamilyPrep",
            ("Method", nameof(LogOpenRevitDocumentsStateAsync)),
            ("ContextTag", contextTag),
            ("FilterId", filterId ?? "<none>"));

        await _awaitableEvent.RaiseAsync(_ =>
        {
            LogAllOpenRevitDocuments(filterId);
        }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Synchronous helper that walks <c>Application.Documents</c> and writes
    /// one structured <c>Info</c> line per open document. Must be called on
    /// the Revit UI thread. Best-effort: any per-document access failure is
    /// logged and skipped so a single corrupted document does not hide the
    /// rest of the session state.
    /// </summary>
    private void LogAllOpenRevitDocuments(string? filterId = null)
    {
        try
        {
            var activeDoc = _revitContext.GetDocument();
            if (activeDoc is null)
            {
                SmartConLogger.Info("LogAllOpenRevitDocuments: no active document (Revit context is null)");
                return;
            }

            var revitApp = activeDoc.Application;
            var allDocs = revitApp.Documents.OfType<Document>().ToList();
            var heldKeys = new HashSet<string>(_openedDocuments.Keys, StringComparer.Ordinal);

            var lines = new List<string>(allDocs.Count);
            var matchCount = 0;

            foreach (var d in allDocs)
            {
                string title;
                try { title = d.Title; }
                catch (Exception ex) { title = $"<title-threw:{ex.GetType().Name}>"; }

                string path;
                try { path = d.PathName ?? string.Empty; }
                catch (Exception ex) { path = $"<path-threw:{ex.GetType().Name}>"; }

                bool isValid;
                try { isValid = d.IsValidObject; }
                catch (Exception ex) { isValid = false; title += $"(IsValidObject-threw:{ex.GetType().Name})"; }

                bool isFamilyDoc;
                try { isFamilyDoc = d.IsFamilyDocument; }
                catch { isFamilyDoc = false; }

                var heldFlag = heldKeys.Contains(path) ? " [HELD-OPEN]" : string.Empty;

                var filterFlag = string.Empty;
                if (ContainsOrdinalIgnoreCase(path, filterId))
                {
                    filterFlag = " [MATCHES-FILTER]";
                    matchCount++;
                }

                var pathDisplay = string.IsNullOrEmpty(path) ? "<empty>" : path;
                lines.Add(
                    $"  title='{title}', path='{pathDisplay}', valid={isValid}, " +
                    $"isFamilyDoc={isFamilyDoc}{heldFlag}{filterFlag}");
            }

            SmartConLogger.Info(
                $"app.Documents.Size={allDocs.Count}, heldOpenInCache={_openedDocuments.Count}, " +
                $"filterMatches={matchCount}. Open docs:\n{string.Join("\n", lines)}");
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"LogAllOpenRevitDocuments failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private async Task<PreparedFamilyItem> PrepareSingleFileAsync(
        string filePath, CancellationToken ct)
    {
        var fileName = Path.GetFileNameWithoutExtension(filePath);
        SmartConLogger.Debug($"Preparing file: {Path.GetFileName(filePath)}");

        Document? doc = null;
        FamilySnapshot? snapshot = null;
        IReadOnlyList<FamilyGeometryPerType>? geometryPerType = null;
        FamilyHealthReport? healthReport = null;
        IReadOnlyList<FamilyDependencyDescriptor>? sharedNested = null;

        try
        {
            var openSw = Stopwatch.StartNew();
            doc = await _awaitableEvent
                .RaiseAsync(app =>
                {
                    var activeDoc = _revitContext.GetDocument();
                    return activeDoc.Application.OpenDocumentFile(filePath);
                }, ct)
                .ConfigureAwait(false);
            openSw.Stop();

            if (doc is null)
                throw new InvalidOperationException("OpenDocumentFile returned null");

            if (openSw.ElapsedMilliseconds > 2000)
            {
                SmartConLogger.Warn(
                    $"OpenDocumentFile slow: {openSw.ElapsedMilliseconds}ms for '{Path.GetFileName(filePath)}' " +
                    $"(heldOpen={_openedDocuments.Count}) " +
                    "[Action: known Revit degradation after 30+ opens; consider splitting batch into sub-batches of 20]");
            }
            else
            {
                SmartConLogger.Debug(
                    $"OpenDocumentFile: {openSw.ElapsedMilliseconds}ms for '{Path.GetFileName(filePath)}' " +
                    $"(heldOpen={_openedDocuments.Count})");
            }

            if (!doc.IsFamilyDocument)
                throw new InvalidOperationException("File is not a family document");

            // Phase 27B: bake Type Catalog (.txt) into the held-open family
            // document BEFORE extracting the snapshot. This way the snapshot
            // contains the baked types and the content hash is computed over
            // the baked content — eliminating the re-open that BakeAsync
            // performed in Commit. The document is already open; baker only
            // runs a transaction + regenerate, no save/close.
            var sidecarPath = Path.ChangeExtension(filePath, ".txt");
            if (File.Exists(sidecarPath))
            {
                using var _bakeScope = SmartConLogger.BeginScope("Sidecar",
                    ("Method", "PrepareBake"),
                    ("File", Path.GetFileName(sidecarPath)));

                SmartConLogger.Debug(
                    $"PrepareBake: .txt sidecar found for '{Path.GetFileName(filePath)}' — " +
                    "reading content");

                var catalogContent = await Task.Run(
                    () => LocalFamilyImportService.ReadTypeCatalogWithEncodingFallback(sidecarPath),
                    ct).ConfigureAwait(false);

                if (catalogContent.Length == 0)
                {
                    SmartConLogger.Warn(
                        $"PrepareBake: Type Catalog file is empty: '{Path.GetFileName(sidecarPath)}' " +
                        "[Action: verify the .txt content — snapshot will use raw .rfa types]");
                }
                else
                {
                    var parseResult = TypeCatalogParser.Parse(catalogContent);
                    if (!parseResult.HasEntries)
                    {
                        SmartConLogger.Warn(
                            $"PrepareBake: parsed 0 entries from '{Path.GetFileName(sidecarPath)}' " +
                            "[Action: verify the Type Catalog format — snapshot will use raw .rfa types]");
                    }
                    else
                    {
                        SmartConLogger.Debug(
                            $"PrepareBake: baking {parseResult.Entries.Count} type(s) " +
                            "into held-open document (no re-open)");

                        var bakeResult = await _typeCatalogBaker
                            .BakeInExistingDocumentAsync(doc, parseResult, ct)
                            .ConfigureAwait(false);

                        if (!bakeResult.Success)
                        {
                            SmartConLogger.Warn(
                                $"PrepareBake: bake failed — {bakeResult.ErrorMessage} " +
                                "[Action: verify the .rfa and .txt are compatible — snapshot will use raw .rfa types]");
                        }
                        else
                        {
                            SmartConLogger.Info(
                                $"PrepareBake: baked {bakeResult.BakedTypeCount} type(s) " +
                                "into held-open document — snapshot will contain baked types");
                        }
                    }
                }
            }
            else
            {
                SmartConLogger.Debug(
                    $"PrepareBake: no .txt sidecar for '{Path.GetFileName(filePath)}' — " +
                    "snapshot from raw .rfa");
            }

            // Import health check: switch every type with Regenerate and
            // collect system errors/warnings (rolled back — document stays
            // unmodified). Runs AFTER the bake-in so baked types are
            // checked too, BEFORE the snapshot so a corrupt family is
            // flagged in the same single open.
            try
            {
                healthReport = await _awaitableEvent
                    .RaiseAsync(app => _healthChecker.CheckFamilyDocument(doc, ct), ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception healthEx)
            {
                SmartConLogger.Warn(
                    $"Health check failed for '{Path.GetFileName(filePath)}': {healthEx.GetType().Name}: {healthEx.Message} " +
                    "[Action: файл будет показан в диалоге без данных о системных ошибках — проверьте его вручную в Revit]");
            }

            // Phase: snapshot + hashing only. 3D geometry extraction is
            // DEFERRED to the post-confirmation geometry pipeline
            // (FamilyGeometryPipeline.RunAsync → IFamilyGeometryExtractor.ExtractAsync)
            // so it runs on a freshly-opened managed .rfa AFTER the user
            // confirms the batch dialog, not on a held-open document BEFORE.
            // Rationale (white-dialog bug): ExtractGeometryPerType uses
            // Transaction + RollBack on a held-open family document,
            // which on net48 R2019-2024 leaves the WPF render thread in a
            // zombie state — the following ShowDialog blocks ~9s waiting
            // for paint (white window). Deferring extraction (passing null)
            // keeps Prepare fast and the dialog responsive.
            var familySnapshot = await _awaitableEvent
                .RaiseAsync(app =>
                {
                    var extracted = _snapshotExtractor.ExtractFromFamilyDocument(doc);
                    // ADR-066 (E2): same Revit-thread roundtrip — shared
                    // nested families are scanned flat in the parent family
                    // document (probe P1), no recursion needed downstream.
                    sharedNested = _dependencyCollector.CollectSharedNestedDependencies(doc);
                    return extracted;
                }, ct)
                .ConfigureAwait(false);
            snapshot = familySnapshot;
            // geometryPerType stays null → pipeline extracts post-confirm.

            _openedDocuments[filePath] = doc;
            SmartConLogger.Debug($"Document held open: {Path.GetFileName(filePath)}");

            LogDiagSnapshot("Prepare.perFile", _openedDocuments.Count);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"PrepareSingleFile cleanup: {ex.Message}");
            if (doc is not null)
            {
                try { doc.Close(false); } catch { }
            }
            throw;
        }

        // FHV8 (#209): hash + dedup are DEFERRED to FinalizeLoadableHashesAsync
        // — the composite hash needs the whole shared-nested closure, which
        // is only complete after PrepareSharedNestedItemsAsync ran.
        return new PreparedFamilyItem(
            SourcePath: filePath,
            DisplayName: fileName,
            RevitMajorVersion: GetRevitMajorVersion(),
            ContentHash: null,
            LoadableSnapshot: snapshot,
            SystemSnapshot: null,
            ErrorMessage: null,
            Source: null,
            SourceTypes: null,
            FamilySource: "loadable",
            GeometryPerType: geometryPerType,
            HealthReport: healthReport,
            SharedNestedDependencies: sharedNested);
    }

    private async Task<PreparedFamilyItem> PrepareSystemCategoryAsync(
        CategoryAnalysis analysis, CancellationToken ct)
    {
        SmartConLogger.Debug($"Preparing system category: {analysis.DisplayName}");

        var typeUniqueIds = analysis.Types.Select(t => t.UniqueId).ToList();
        var builtInCategory = analysis.Category;

        IReadOnlyList<FamilyDependencyDescriptor>? routingDependencies = null;
        var snapshot = await _awaitableEvent
            .RaiseAsync(app =>
            {
                var activeDoc = _revitContext.GetDocument();
                var extracted = _snapshotExtractor.ExtractFromProject(
                    activeDoc, typeUniqueIds, builtInCategory);
                // ADR-066 (E1): same Revit-thread roundtrip — routing rules
                // resolve to live families whose identities (UniqueId) drive
                // the dependency auto-import below.
                routingDependencies = _dependencyCollector.CollectRoutingDependencies(activeDoc, extracted);
                return extracted;
            }, ct)
            .ConfigureAwait(false);

        var hash = _contentHasher.ComputeForSystem(snapshot);
        // #249 (Phase 2): per-type content hashes — the DB writer persists
        // them to family_type_hashes without re-opening the staged file.
        var perTypeHashes = _contentHasher.ComputePerTypeHashesForSystem(snapshot)
            ?.Select(FamilyTypeHashEntry.ForSystemType)
            .ToList();
        // #249 (Phase 4): canonical sections — persisted to
        // section_hashes/section_strings at import.
        var systemSections = _contentHasher.ComputeSectionsForSystem(snapshot);
        var displayName = analysis.DisplayName;
        var normalizedName = FamilyNameNormalizer.Normalize(displayName);

        var dedupResult = await Task.Run(
            () => _dedupService.CheckAsync(normalizedName, hash, "system", (int)builtInCategory, ct),
            ct).ConfigureAwait(false);

        SmartConLogger.Info(
            $"System category prepared: '{displayName}', hash={hash?.HexString ?? "null"}, " +
            $"status={dedupResult.Status}");

        var sourceTypes = analysis.Types
            .Select(t => new FamilySourceTypeInfo(t.UniqueId, t.Name, displayName, (int)builtInCategory, t.FamilyName, t.FamilyKey))
            .ToList();

        var source = new FamilyImportSource.SystemSource(
            DisplayName: displayName,
            CategoryId: (int)builtInCategory,
            TypeUniqueIds: typeUniqueIds,
            TypeNames: analysis.Types.Select(t => t.Name).ToList(),
            // #183: parallel family-name list so the staged import persists
            // family_types.family_name and the sync matches by (family, name).
            TypeFamilyNames: analysis.Types.Select(t => t.FamilyName).ToList(),
            // #190 (ADR-064): parallel family-key list → family_types.family_key.
            TypeFamilyKeys: analysis.Types.Select(t => t.FamilyKey).ToList());

        return new PreparedFamilyItem(
            SourcePath: $"system://{displayName}",
            DisplayName: displayName,
            RevitMajorVersion: GetRevitMajorVersion(),
            ContentHash: hash,
            LoadableSnapshot: null,
            SystemSnapshot: snapshot,
            ErrorMessage: null,
            Source: source,
            SourceTypes: sourceTypes,
            FamilySource: "system",
            Status: dedupResult.Status,
            ExistingCatalogItemId: dedupResult.ExistingCatalogItemId,
            ExistingVersionLabel: dedupResult.ExistingVersionLabel,
            MatchedVersionLabel: dedupResult.HashMatch?.MatchedVersionLabel,
            IsCrossNameDuplicate: dedupResult.IsCrossNameDuplicate,
            MatchedItemName: dedupResult.HashMatch?.MatchedItemName,
            RoutingDependencies: routingDependencies,
            PerTypeHashes: perTypeHashes,
            Sections: systemSections);
    }

    private async Task<PreparedFamilyItem> PrepareLoadableFromProjectAsync(
        LoadableFamilyInfo loadable, CancellationToken ct)
    {
        SmartConLogger.Debug($"Preparing loadable from project: {loadable.FamilyName}");

        FamilySnapshot? snapshot = null;
        Document? familyDoc = null;
        IReadOnlyList<FamilyDependencyDescriptor>? sharedNested = null;
        var sourcePath = $"loadable://{loadable.FamilyName}";

        try
        {
            snapshot = await _awaitableEvent
                .RaiseAsync<FamilySnapshot>(app =>
                {
                    var activeDoc = _revitContext.GetDocument();
                    var family = activeDoc.GetElement(loadable.FamilyUniqueId) as Autodesk.Revit.DB.Family;
                    if (family is null)
                        throw new InvalidOperationException($"Family '{loadable.FamilyName}' not found by UniqueId");

                    var loadedSymbolIds = family.GetFamilySymbolIds();
                    SmartConLogger.Debug(
                        $"EditFamily IN: family.UniqueId={loadable.FamilyUniqueId}, name='{loadable.FamilyName}', " +
                        $"loadedSymbolsInProject={loadedSymbolIds.Count}, IsEditable={family.IsEditable}, " +
                        $"alreadyHeldOpen={_openedDocuments.ContainsKey(sourcePath)}");

                    familyDoc = activeDoc.EditFamily(family);

                    SmartConLogger.Debug(
                        $"EditFamily OUT: familyDoc.PathName='{(string.IsNullOrEmpty(familyDoc.PathName) ? "<empty>" : familyDoc.PathName)}', " +
                        $"Title='{familyDoc.Title}', IsValidObject={familyDoc.IsValidObject}, " +
                        $"IsFamilyDocument={familyDoc.IsFamilyDocument}");

                    var extracted = _snapshotExtractor.ExtractFromFamilyDocument(familyDoc);
                    // ADR-066 (E2): shared nested families are scanned flat
                    // in the parent family document (probe P1) — the nested
                    // preparation below re-opens them from THIS held-open
                    // document, not from the project (purge-proof).
                    sharedNested = _dependencyCollector.CollectSharedNestedDependencies(familyDoc);
                    return extracted;
                }, ct)
                .ConfigureAwait(false);

            if (familyDoc is not null)
            {
                _openedDocuments[sourcePath] = familyDoc;
                SmartConLogger.Debug($"EditFamily document held open: {loadable.FamilyName}");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"PrepareLoadableFromProject cleanup: {ex.Message}");
            if (familyDoc is not null)
            {
                try { familyDoc.Close(false); } catch { }
            }
            throw;
        }

        // FHV8 (#209): hash + dedup are DEFERRED to FinalizeLoadableHashesAsync
        // — the composite hash needs the whole shared-nested closure, which
        // is only complete after PrepareSharedNestedItemsAsync ran.
        var source = new FamilyImportSource.LoadableSource(
            FamilyName: loadable.FamilyName,
            FamilyUniqueId: loadable.FamilyUniqueId,
            CategoryName: loadable.CategoryName);

        return new PreparedFamilyItem(
            SourcePath: sourcePath,
            DisplayName: loadable.FamilyName,
            RevitMajorVersion: GetRevitMajorVersion(),
            ContentHash: null,
            LoadableSnapshot: snapshot,
            SystemSnapshot: null,
            ErrorMessage: null,
            Source: source,
            SourceTypes: null,
            FamilySource: "loadable",
            SharedNestedDependencies: sharedNested);
    }

    private int GetRevitMajorVersion()
    {
        try
        {
            var version = _revitContext.GetRevitVersion();
            return int.TryParse(version, out var v) ? v : 0;
        }
        catch
        {
            return 0;
        }
    }

    private void LogDiagSnapshot(string phase, int openedCount)
    {
        var wsMB = Process.GetCurrentProcess().WorkingSet64 / 1024 / 1024;
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        var invalid = CountInvalidOpenedDocs();
        var invalidPart = invalid > 0 ? $", invalidRCW={invalid}" : "";
        SmartConLogger.Info(
            $"[DIAG {phase}] WS={wsMB}MB, GC0={gc0}/GC1={gc1}/GC2={gc2}, " +
            $"openedDocs={openedCount}{invalidPart}");
    }

    private int CountInvalidOpenedDocs()
    {
        var invalid = 0;
        foreach (var kv in _openedDocuments)
        {
            try
            {
                _ = kv.Value.IsFamilyDocument;
            }
            catch
            {
                invalid++;
            }
        }
        return invalid;
    }

    private static bool ContainsOrdinalIgnoreCase(string haystack, string? needle)
    {
        if (string.IsNullOrEmpty(needle) || string.IsNullOrEmpty(haystack))
            return false;
#if NET8_0_OR_GREATER
        return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);
#else
        return haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }
}
