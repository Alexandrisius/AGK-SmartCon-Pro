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
public sealed partial class FamilyImportPreparationService : IFamilyImportPreparationService
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
    private readonly IMiniProjectMarker? _miniProjectMarker;
    private readonly IFamilyRoutingRuleRepository? _routingRuleRepository;

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
        IFamilyVersionStore versionStore,
        IMiniProjectMarker? miniProjectMarker = null,
        IFamilyRoutingRuleRepository? routingRuleRepository = null)
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
        // ADR-072: optional so legacy test wirings keep the pre-V34 behavior
        // (no substitution — routing rides in the mini-project).
        _miniProjectMarker = miniProjectMarker;
        _routingRuleRepository = routingRuleRepository;
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
