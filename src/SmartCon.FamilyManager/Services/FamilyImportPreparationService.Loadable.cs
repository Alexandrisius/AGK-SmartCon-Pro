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

public sealed partial class FamilyImportPreparationService
{
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
        foreach (var kvp in detailed)
        {
            hashes[kvp.Key] = kvp.Value.Hash;
            sectionsByName[kvp.Key] = kvp.Value.Sections;
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
            using (var _openMs = SmartConLogger.Measure("PrepSingle.OpenDocumentFile"))
            {
                doc = await _awaitableEvent
                    .RaiseAsync(app =>
                    {
                        var activeDoc = _revitContext.GetDocument();
                        return activeDoc.Application.OpenDocumentFile(filePath);
                    }, ct)
                    .ConfigureAwait(false);

                if (doc is null)
                    throw new InvalidOperationException("OpenDocumentFile returned null");

                if (_openMs.GetElapsedMilliseconds() > 2000)
                {
                    SmartConLogger.Warn(
                        $"OpenDocumentFile slow: {(long)_openMs.GetElapsedMilliseconds()}ms for '{Path.GetFileName(filePath)}' " +
                        $"(heldOpen={_openedDocuments.Count}) " +
                        "[Action: known Revit degradation after 30+ opens; consider splitting batch into sub-batches of 20]");
                }
                else
                {
                    SmartConLogger.Debug(
                        $"OpenDocumentFile: {(long)_openMs.GetElapsedMilliseconds()}ms for '{Path.GetFileName(filePath)}' " +
                        $"(heldOpen={_openedDocuments.Count})");
                }
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
}
