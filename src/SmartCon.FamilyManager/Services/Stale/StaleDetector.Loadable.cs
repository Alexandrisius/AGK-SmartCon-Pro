using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

internal sealed partial class StaleDetector
{
    private async Task<IReadOnlyList<StaleCheckResult>> CheckLoadableItemsAsync(
        IReadOnlyList<FamilyCatalogItem> loadableItems,
        Document doc,
        int targetRevit,
        IProgress<StaleCheckProgress>? progress,
        int extraTotal,
        CancellationToken ct)
    {
        // 2) Collect Family element ids from the active document on the Revit thread.
        // Two families with the same Name are rare but possible (e.g. two
        // loadable variants both loaded). We use a list, log a warning, and
        // return all candidates — the catalog item matches the FIRST one, but
        // the operator is told there is an ambiguity. Without this, multiple
        // matches silently overwrite each other.
        var familyIds = await _awaitable.RaiseAsync(
            _ =>
            {
                var map = new Dictionary<string, List<(string FamilyName, ElementId Id)>>(StringComparer.Ordinal);
                using var collector = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Family));
                foreach (Autodesk.Revit.DB.Family f in collector)
                {
                    if (f is null || f.Name is null) continue;
                    if (!map.TryGetValue(f.Name, out var list))
                    {
                        list = new List<(string, ElementId)>();
                        map[f.Name] = list;
                    }
                    list.Add((f.Name, f.Id));
                }
                foreach (var kvp in map)
                {
                    if (kvp.Value.Count > 1)
                    {
                        var firstId = kvp.Value[0].Id;
#if NET8_0_OR_GREATER
                        var firstIdValue = firstId.Value;
#else
#pragma warning disable CS0618 // IntegerValue is deprecated in Revit 2024; removed in 2025. Use Value when available.
                        var firstIdValue = firstId.IntegerValue;
#pragma warning restore CS0618
#endif
                        SmartConLogger.Warn(
                            $"CheckCategory: family name '{kvp.Value[0].FamilyName}' matches {kvp.Value.Count} " +
                            $"Family elements in the project; the first match (ElementId=" +
                            $"{firstIdValue}) will be used. " +
                            "[Action: rename one of the families to remove the ambiguity]");
                    }
                }
                return map;
            }, ct).ConfigureAwait(true);
        ct.ThrowIfCancellationRequested();

        // 3) Match catalog items to Revit Family elements by name (left join).
        var matched = new List<(FamilyCatalogItem Item, string FamilyName, ElementId Id)>();
        var matchCounter = new HotLoopCounter(sampleEvery: 32);
        foreach (var item in loadableItems)
        {
            if (matchCounter.ShouldLog())
            {
                SmartConLogger.Debug(
                    $"Matching {matchCounter.Count}/{loadableItems.Count}: '{item.Name}'.");
            }
            if (familyIds.TryGetValue(item.Name, out var hits) && hits.Count > 0)
            {
                matched.Add((item, hits[0].FamilyName, hits[0].Id));
            }
        }

        if (matched.Count == 0) return Array.Empty<StaleCheckResult>();

        // 4) Batch ES read on the Revit thread.
        var ids = matched.Select(m => m.Id).ToList();
        var versions = await _awaitable.RaiseAsync(
            _ => _store.ReadManyFromDocument(doc, ids),
            ct).ConfigureAwait(true);

        // 5) Compute StaleReason for each.
        var results = new List<StaleCheckResult>(matched.Count);
        var reasonCounter = new HotLoopCounter(sampleEvery: 32);
        // Content verification (#180, owner decision 2026-08-12; #218 orphan
        // markers): the check proves CONTENT whenever the marker alone cannot
        // speak — missing entirely, matching the current version (local edits
        // become ContentDrift), or pointing at an orphaned catalog id (the
        // item was re-imported under a new id; heal re-resolves it). The
        // unified FHV10 hash is a fair comparison for an embedded / loaded
        // copy: the one field a merge physically cannot transfer (parameter
        // groups) is not hashed at all, and everything else in the embedded
        // EditFamily document is byte-identical to the source file (probes
        // 2026-08-12). A label drift without id mismatch is stale WITHOUT
        // opening any document — «Обновить» reconciles it.
        // One OpenDocumentFile per version FILE per check run — duplicate
        // catalog items resolving to the same file share the cached proof
        // (family hash + per-type hashes; a cached null-field entry is a
        // cached "indeterminate").
        var fileProofCache = new Dictionary<string, EmbeddedContentVerifier.FileProof>(StringComparer.OrdinalIgnoreCase);
        // Pane progress bar feed: one report per verified family; Total
        // includes the system items queued after this phase (extraTotal) so
        // the bar spans the whole CheckCategoryAsync run.
        var progressTotal = matched.Count + extraTotal;
        var progressDone = 0;
        foreach (var (item, familyName, id) in matched)
        {
            versions.TryGetValue(id, out var loaded);
            var reason = loaded is null
                ? StaleReason.NoEntityStorage
                : SystemTypeStaleLogic.ComputeReason(
                    loaded, item.Id, item.CurrentVersionLabel, targetRevit);

            reason = await RefineReasonByContentAsync(
                item, familyName, loaded, reason, doc, targetRevit, fileProofCache, ct)
                .ConfigureAwait(true);

            results.Add(new StaleCheckResult(
                item.Id, familyName,
                item.CurrentVersionLabel,
                loaded?.VersionLabel,
                reason != StaleReason.None,
                reason));

            progressDone++;
            progress?.Report(new StaleCheckProgress(progressDone, progressTotal, familyName));

            if (reasonCounter.ShouldLog())
            {
                SmartConLogger.Debug(
                    $"Computed reasons for {reasonCounter.Count}/{matched.Count} families.");
            }
        }

        return results;
    }

    /// <summary>
    /// FHV10 content proof for an embedded nested family in a family
    /// document or project: <see cref="LoadableVerificationResult.Verdict"/>
    /// — <c>true</c> embedded content matches the current catalog version
    /// file, <c>false</c> differs, <c>null</c> indeterminate (file
    /// unresolvable, guards tripped) and the caller keeps the marker-based
    /// verdict. <see cref="LoadableVerificationResult.PerTypeStale"/> (#249,
    /// Phase 2) resolves WHICH loaded types drifted — it feeds the
    /// per-type orange dot in the tree.
    /// </summary>
    private async Task<LoadableVerificationResult> ContentVerifyEmbeddedAsync(
        FamilyCatalogItem item,
        string familyName,
        Document doc,
        int targetRevit,
        IDictionary<string, EmbeddedContentVerifier.FileProof> fileProofCache,
        CancellationToken ct)
    {
        FamilyResolvedFile resolved;
        try
        {
            resolved = await _fileResolver!.ResolveForLoadAsync(item.Id, targetRevit, ct)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SmartConLogger.Warn(
                $"CheckEmbedded[{item.Id}]: failed to resolve the current version file: {ex.GetType().Name}: {ex.Message} " +
                "[Action: контентная верификация пропущена — проверьте, что файл версии доступен на диске]");
            return new LoadableVerificationResult(null, null);
        }
        if (string.IsNullOrEmpty(resolved.AbsolutePath))
        {
            return new LoadableVerificationResult(null, null);
        }

        return await _awaitable.RaiseAsync(
            _ => EmbeddedContentVerifier.VerifyEmbeddedAgainstFileDetailed(
                doc, familyName, resolved.AbsolutePath,
                _snapshotExtractor!, _contentHasher!, $"CheckEmbedded[{item.Id}]", fileProofCache),
            ct).ConfigureAwait(true);
    }

    /// <summary>
    /// Writes the current-version marker onto an embedded family whose
    /// content was just proven current — the next check takes the fast
    /// marker path. Best-effort: the check verdict is already correct even
    /// when the write fails.
    /// </summary>
    private async Task HealMarkerBestEffortAsync(
        FamilyCatalogItem item,
        string familyName,
        int targetRevit,
        CancellationToken ct)
    {
        if (_versionWriter is null)
        {
            return;
        }
        try
        {
            await _versionWriter.WriteVersionMarkerAsync(
                item.Id, familyName, item.CurrentVersionLabel, targetRevit, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"CheckEmbedded[{item.Id}]: content verified but marker heal failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: вердикт корректен, но следующая «Проверить» снова выполнит контентную верификацию — проверьте ES-схему]");
        }
    }
}
