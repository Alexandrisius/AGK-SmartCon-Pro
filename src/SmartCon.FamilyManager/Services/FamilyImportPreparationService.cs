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

    private readonly Dictionary<string, Document> _openedDocuments = new(StringComparer.Ordinal);

    public FamilyImportPreparationService(
        IFamilyManagerAwaitableEvent awaitableEvent,
        IFamilySnapshotExtractor snapshotExtractor,
        IFamilyContentHasher contentHasher,
        IContentHashDedupService dedupService,
        IRevitContext revitContext,
        IFamilyTypeCatalogBaker typeCatalogBaker,
        IFamilyHealthChecker healthChecker)
    {
        _awaitableEvent = awaitableEvent ?? throw new ArgumentNullException(nameof(awaitableEvent));
        _snapshotExtractor = snapshotExtractor ?? throw new ArgumentNullException(nameof(snapshotExtractor));
        _contentHasher = contentHasher ?? throw new ArgumentNullException(nameof(contentHasher));
        _dedupService = dedupService ?? throw new ArgumentNullException(nameof(dedupService));
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
        _typeCatalogBaker = typeCatalogBaker ?? throw new ArgumentNullException(nameof(typeCatalogBaker));
        _healthChecker = healthChecker ?? throw new ArgumentNullException(nameof(healthChecker));
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

        LogDiagSnapshot("Prepare.exit", _openedDocuments.Count);

        return results;
    }

    /// <summary>
    /// Prepare the active family document (.rfa in Family Editor) for import.
    /// The document is already open — no OpenDocumentFile needed.
    /// </summary>
    public async Task<PreparedFamilyItem> PrepareActiveFamilyAsync(
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

        try
        {
            // Active document stays untouched (no type switching — the user
            // is editing it): collect accumulated warnings only.
            var healthReport = await _awaitableEvent
                .RaiseAsync(app => _healthChecker.CheckActiveFamilyDocument(activeDoc), ct)
                .ConfigureAwait(false);

            var snapshot = await _awaitableEvent
                .RaiseAsync(app => _snapshotExtractor.ExtractFromFamilyDocument(activeDoc), ct)
                .ConfigureAwait(false);

            var hash = _contentHasher.ComputeForLoadable(snapshot);
            var normalizedName = FamilyNameNormalizer.Normalize(displayName);

            var dedupResult = await Task.Run(
                () => _dedupService.CheckAsync(normalizedName, hash, "loadable", ct: ct),
                ct).ConfigureAwait(false);

            SmartConLogger.Info(
                $"Active family prepared: hash={hash?.HexString ?? "null"}, " +
                $"status={dedupResult.Status}");

            return new PreparedFamilyItem(
                SourcePath: activeDoc.PathName ?? $"active://{displayName}",
                DisplayName: displayName,
                RevitMajorVersion: GetRevitMajorVersion(),
                ContentHash: hash,
                LoadableSnapshot: snapshot,
                SystemSnapshot: null,
                ErrorMessage: null,
                Source: null,
                SourceTypes: null,
                FamilySource: "loadable",
                Status: dedupResult.Status,
                ExistingCatalogItemId: dedupResult.ExistingCatalogItemId,
                ExistingVersionLabel: dedupResult.ExistingVersionLabel,
                MatchedVersionLabel: dedupResult.HashMatch?.MatchedVersionLabel,
                IsCrossNameDuplicate: dedupResult.IsCrossNameDuplicate,
                MatchedItemName: dedupResult.HashMatch?.MatchedItemName,
                HealthReport: healthReport);
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

        SmartConLogger.Info(
            $"Project prepare complete: {results.Count} items, " +
            $"{_openedDocuments.Count} EditFamily docs held open");

        return results;
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
                .RaiseAsync(app => _snapshotExtractor.ExtractFromFamilyDocument(doc), ct)
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

        var hash = _contentHasher.ComputeForLoadable(snapshot!);
        var normalizedName = FamilyNameNormalizer.Normalize(fileName);

        var dedupResult = await Task.Run(
            () => _dedupService.CheckAsync(normalizedName, hash, "loadable", ct: ct),
            ct).ConfigureAwait(false);

        SmartConLogger.Info(
            $"File prepared: '{fileName}', hash={hash?.HexString ?? "null"}, " +
            $"status={dedupResult.Status}");

        return new PreparedFamilyItem(
            SourcePath: filePath,
            DisplayName: fileName,
            RevitMajorVersion: GetRevitMajorVersion(),
            ContentHash: hash,
            LoadableSnapshot: snapshot,
            SystemSnapshot: null,
            ErrorMessage: null,
            Source: null,
            SourceTypes: null,
            FamilySource: "loadable",
            Status: dedupResult.Status,
            ExistingCatalogItemId: dedupResult.ExistingCatalogItemId,
            ExistingVersionLabel: dedupResult.ExistingVersionLabel,
            MatchedVersionLabel: dedupResult.HashMatch?.MatchedVersionLabel,
            GeometryPerType: geometryPerType,
            IsCrossNameDuplicate: dedupResult.IsCrossNameDuplicate,
            MatchedItemName: dedupResult.HashMatch?.MatchedItemName,
            HealthReport: healthReport);
    }

    private async Task<PreparedFamilyItem> PrepareSystemCategoryAsync(
        CategoryAnalysis analysis, CancellationToken ct)
    {
        SmartConLogger.Debug($"Preparing system category: {analysis.DisplayName}");

        var typeUniqueIds = analysis.Types.Select(t => t.UniqueId).ToList();
        var builtInCategory = analysis.Category;

        var snapshot = await _awaitableEvent
            .RaiseAsync(app =>
            {
                var activeDoc = _revitContext.GetDocument();
                return _snapshotExtractor.ExtractFromProject(
                    activeDoc, typeUniqueIds, builtInCategory);
            }, ct)
            .ConfigureAwait(false);

        var hash = _contentHasher.ComputeForSystem(snapshot);
        var displayName = analysis.DisplayName;
        var normalizedName = FamilyNameNormalizer.Normalize(displayName);

        var dedupResult = await Task.Run(
            () => _dedupService.CheckAsync(normalizedName, hash, "system", (int)builtInCategory, ct),
            ct).ConfigureAwait(false);

        SmartConLogger.Info(
            $"System category prepared: '{displayName}', hash={hash?.HexString ?? "null"}, " +
            $"status={dedupResult.Status}");

        var sourceTypes = analysis.Types
            .Select(t => new FamilySourceTypeInfo(t.UniqueId, t.Name, displayName, (int)builtInCategory, t.FamilyName))
            .ToList();

        var source = new FamilyImportSource.SystemSource(
            DisplayName: displayName,
            CategoryId: (int)builtInCategory,
            TypeUniqueIds: typeUniqueIds,
            TypeNames: analysis.Types.Select(t => t.Name).ToList(),
            // #183: parallel family-name list so the staged import persists
            // family_types.family_name and the sync matches by (family, name).
            TypeFamilyNames: analysis.Types.Select(t => t.FamilyName).ToList());

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
            MatchedItemName: dedupResult.HashMatch?.MatchedItemName);
    }

    private async Task<PreparedFamilyItem> PrepareLoadableFromProjectAsync(
        LoadableFamilyInfo loadable, CancellationToken ct)
    {
        SmartConLogger.Debug($"Preparing loadable from project: {loadable.FamilyName}");

        FamilySnapshot? snapshot = null;
        Document? familyDoc = null;
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

                    return _snapshotExtractor.ExtractFromFamilyDocument(familyDoc);
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

        var hash = _contentHasher.ComputeForLoadable(snapshot!);
        var normalizedName = FamilyNameNormalizer.Normalize(loadable.FamilyName);

        var dedupResult = await Task.Run(
            () => _dedupService.CheckAsync(normalizedName, hash, "loadable", ct: ct),
            ct).ConfigureAwait(false);

        SmartConLogger.Info(
            $"Loadable from project prepared: '{loadable.FamilyName}', " +
            $"hash={hash?.HexString ?? "null"}, status={dedupResult.Status}");

        var source = new FamilyImportSource.LoadableSource(
            FamilyName: loadable.FamilyName,
            FamilyUniqueId: loadable.FamilyUniqueId,
            CategoryName: loadable.CategoryName);

        return new PreparedFamilyItem(
            SourcePath: sourcePath,
            DisplayName: loadable.FamilyName,
            RevitMajorVersion: GetRevitMajorVersion(),
            ContentHash: hash,
            LoadableSnapshot: snapshot,
            SystemSnapshot: null,
            ErrorMessage: null,
            Source: source,
            SourceTypes: null,
            FamilySource: "loadable",
            Status: dedupResult.Status,
            ExistingCatalogItemId: dedupResult.ExistingCatalogItemId,
            ExistingVersionLabel: dedupResult.ExistingVersionLabel,
            MatchedVersionLabel: dedupResult.HashMatch?.MatchedVersionLabel,
            IsCrossNameDuplicate: dedupResult.IsCrossNameDuplicate,
            MatchedItemName: dedupResult.HashMatch?.MatchedItemName);
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
