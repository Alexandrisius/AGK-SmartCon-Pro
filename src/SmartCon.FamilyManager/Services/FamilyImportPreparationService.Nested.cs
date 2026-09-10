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

public sealed partial class FamilyImportPreparationService
{
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
}
