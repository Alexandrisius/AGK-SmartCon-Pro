using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

internal sealed partial class StaleDetector
{
    /// <summary>
    /// Refines the marker-based verdict whenever the marker alone cannot
    /// speak (#180 + #218): the marker is missing entirely, matches the
    /// current version (a local edit would stay invisible to marker-only
    /// checks), or points at a DIFFERENT catalog id. The id-mismatch case is
    /// split by a catalog lookup:
    /// <list type="bullet">
    /// <item><b>Orphaned</b> (#218): the referenced id no longer exists in
    /// the catalog (the item was deleted and re-imported under a new id).
    /// The marker cannot testify about the version — the embedded content is
    /// proven against the current version file instead, and on a match the
    /// marker is HEALED with the re-resolved id (Info level, no «corrupted»
    /// scare).</item>
    /// <item><b>Foreign</b>: the id belongs to another live catalog item —
    /// genuinely corrupted ES data. Warn + the stale verdict stands.</item>
    /// </list>
    /// Content-proof outcomes: <c>true</c> → not stale (missing/orphaned
    /// marker healed, best-effort); <c>false</c> → <see cref="StaleReason.ContentDrift"/>
    /// for a loaded family («Обновить» restores the catalog content), the
    /// no-marker verdict stands otherwise; <c>null</c> (indeterminate) → the
    /// marker-based verdict stands.
    /// </summary>
    private async Task<StaleReason> RefineReasonByContentAsync(
        FamilyCatalogItem item,
        string familyName,
        FamilyVersion? loaded,
        StaleReason reason,
        Document doc,
        int targetRevit,
        IDictionary<string, EmbeddedContentVerifier.FileProof> fileProofCache,
        CancellationToken ct)
    {
        // #218: orphan-vs-foreign classification of an id mismatch. The
        // lookup is cheap (one indexed SQLite read) and runs only for the
        // already-rare mismatch — never on the happy path.
        var markerOrphaned = false;
        if (loaded is not null
            && !string.IsNullOrEmpty(loaded.CatalogItemId)
            && !string.Equals(loaded.CatalogItemId, item.Id, StringComparison.Ordinal))
        {
            bool? referencedExists;
            try
            {
                referencedExists = await _catalog.GetItemAsync(loaded.CatalogItemId!, ct)
                    .ConfigureAwait(false) is not null;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                referencedExists = null;
                SmartConLogger.Warn(
                    $"CheckEmbedded[{item.Id}]: failed to look up the marker's CatalogItemId in the catalog: " +
                    $"{ex.GetType().Name}: {ex.Message} " +
                    "[Action: orphan-резолв пропущен, маркер считается чужим — проверьте доступность БД каталога]");
            }

            if (referencedExists == false)
            {
                markerOrphaned = true;
                SmartConLogger.Info(
                    $"CheckEmbedded[{item.Id}]: '{familyName}' marker CatalogItemId='{loaded.CatalogItemId}' " +
                    "no longer exists in the catalog (the item was re-imported under a new id) — " +
                    "re-resolving by content");
            }
            else
            {
                SmartConLogger.Warn(
                    $"CheckEmbedded[{item.Id}]: '{familyName}' ES marker CatalogItemId='{loaded.CatalogItemId}' " +
                    $"does not match catalog id '{item.Id}'. " +
                    "[Action: ES data is corrupted for this family; treat as stale]");
            }
        }

        var canContentVerify = _fileResolver is not null
            && _snapshotExtractor is not null
            && _contentHasher is not null;
        var markerCannotSpeak = reason == StaleReason.NoEntityStorage
            || reason == StaleReason.None
            || markerOrphaned;
        if (!markerCannotSpeak || !canContentVerify)
        {
            // #249 (follow-up, manual test): a VersionMismatch marker is a
            // valid FAMILY-level verdict ("an older version is embedded"),
            // but it must NOT paint every type stale — the per-type answer
            // is computable from the catalog alone: per-type hashes of the
            // marker's version vs the current one. No document opens, no
            // EditFamily — immune to the open-editor guard that made the
            // embedded proof (and with it the per-type map) unavailable
            // right after "Import Active File".
            if (reason == StaleReason.VersionMismatch
                && loaded?.VersionLabel is not null
                && item.CurrentVersionLabel is not null
                && _contentHashAnalytics is not null)
            {
                StoreLoadableTypeStaleMap(item.Id,
                    await ComputeDbPerTypeStaleAsync(
                        item.Id, loaded.VersionLabel!, item.CurrentVersionLabel, ct)
                        .ConfigureAwait(false));
            }
            else
            {
                // No per-type proof without a content verification — clear any
                // stale map from a previous check so the tree falls back to the
                // family-level (leaf-scoped) dot (#249, Phase 2).
                ClearLoadableTypeStaleMap(item.Id);
            }
            return reason;
        }

        var verification = await ContentVerifyEmbeddedAsync(
            item, familyName, doc, targetRevit, fileProofCache, ct).ConfigureAwait(true);
        var verdict = verification.Verdict;

        if (verdict == true)
        {
            // A content match clears the per-type drift too — the tree
            // falls back to the (now non-stale) leaf verdict.
            ClearLoadableTypeStaleMap(item.Id);
            if (loaded is null || markerOrphaned)
            {
                SmartConLogger.Debug(
                    $"CheckEmbedded: '{familyName}' {(loaded is null ? "has no version marker" : "has an orphaned marker")} " +
                    $"but content matches {item.CurrentVersionLabel} — not stale (marker healed)");
                await HealMarkerBestEffortAsync(item, familyName, targetRevit, ct).ConfigureAwait(true);
            }
            else
            {
                SmartConLogger.Debug(
                    $"CheckEmbedded: '{familyName}' marker matches {item.CurrentVersionLabel}, " +
                    "content re-verify=matches — not stale");
            }
            return StaleReason.None;
        }

        if (verdict == false)
        {
            // #249 (Phase 2): keep the per-type drift map for the tree;
            // without a proof the entry is cleared (leaf-scoped fallback).
            StoreLoadableTypeStaleMap(item.Id, verification.PerTypeStale);
            if (loaded is null)
            {
                SmartConLogger.Debug(
                    $"CheckEmbedded: '{familyName}' has no version marker, " +
                    $"current={item.CurrentVersionLabel}, contentVerify=differs — verdict stays stale");
                return reason;
            }

            // Marker == current, but the content was edited locally (#180);
            // or an orphaned marker whose content is genuinely older than
            // the current version (#218). «Обновить» restores the catalog
            // content in both cases.
            var changedTypes = verification.PerTypeStale?.Count(kv => kv.Value) ?? 0;
            SmartConLogger.Debug(
                $"CheckEmbedded: '{familyName}' {(markerOrphaned ? "orphaned marker" : $"marker matches {item.CurrentVersionLabel}")} " +
                $"but the content DIFFERS from the current catalog version — stale (ContentDrift, {changedTypes} changed type(s))");
            return StaleReason.ContentDrift;
        }

        ClearLoadableTypeStaleMap(item.Id);
        SmartConLogger.Debug(
            $"CheckEmbedded: '{familyName}' content verify indeterminate " +
            $"— marker-based verdict stands ({reason})");
        return reason;
    }

    /// <summary>
    /// #249 (Phase 2): stores the per-type drift map of a loadable item
    /// (a null proof clears the entry → the tree falls back to the
    /// family-level dot). Threading: same discipline as
    /// <see cref="_systemTypeStaleByType"/> — mutated under
    /// <see cref="_cacheLock"/>.
    /// </summary>
    private void StoreLoadableTypeStaleMap(string catalogItemId, IReadOnlyDictionary<string, bool>? perTypeStale)
    {
        lock (_cacheLock)
        {
            if (perTypeStale is null)
            {
                _loadableTypeStaleByType.Remove(catalogItemId);
            }
            else
            {
                // net48: Dictionary has no IReadOnlyDictionary ctor —
                // project via LINQ instead.
                _loadableTypeStaleByType[catalogItemId] = perTypeStale
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// #249 (follow-up, manual test): per-type drift map for a VersionMismatch
    /// verdict, computed PURELY from the catalog DB — per-type content hashes
    /// of the embedded (marker) version vs the current version. Types whose
    /// hashes match are NOT stale (their embedded content equals the current
    /// version's, the family-level verdict notwithstanding); types removed
    /// in the current version are stale; types added in the current version
    /// are not (the project cannot have them). <c>null</c> when either
    /// version's analytics are pending — the tree then keeps the pre-fix
    /// leaf-scoped fallback.
    /// <para>
    /// Round-5 fix: per-type hashes track per-type VALUES only — a change in
    /// a SHARED section (GEOM, DEF, CONN, …) affects EVERY type, and a
    /// values-only map showed "0 stale" while the family genuinely needed a
    /// reload (geometry dots vanished). The section hashes of the two
    /// versions are compared too: any changed section outside the per-type
    /// ones (<c>TYPES</c>/<c>VALUES</c>) marks every loaded type stale.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyDictionary<string, bool>?> ComputeDbPerTypeStaleAsync(
        string catalogItemId,
        string fromVersionLabel,
        string currentVersionLabel,
        CancellationToken ct)
    {
        try
        {
            var from = await _contentHashAnalytics!.GetTypeHashesAsync(catalogItemId, fromVersionLabel, ct)
                .ConfigureAwait(false);
            var to = await _contentHashAnalytics.GetTypeHashesAsync(catalogItemId, currentVersionLabel, ct)
                .ConfigureAwait(false);
            if (from is null || to is null)
            {
                SmartConLogger.Debug(
                    $"CheckEmbedded[{catalogItemId}]: per-type analytics pending for " +
                    $"{fromVersionLabel} or {currentVersionLabel} — leaf-scoped stale fallback");
                return null;
            }

            // Shared-section rule (round 5): a change outside the per-type
            // sections makes EVERY loaded type stale.
            var sharedChangedSections = await ComputeChangedSharedSectionsAsync(
                catalogItemId, fromVersionLabel, currentVersionLabel, ct).ConfigureAwait(false);

            var toByKey = new Dictionary<string, FamilyTypeHashEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in to)
            {
                toByKey[entry.TypeIdentityKey] = entry;
            }

            var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in from)
            {
                map[entry.TypeName] = sharedChangedSections.Count > 0
                    || !toByKey.TryGetValue(entry.TypeIdentityKey, out var other)
                    || !string.Equals(entry.HashHex, other.HashHex, StringComparison.OrdinalIgnoreCase);
            }
            foreach (var entry in to)
            {
                // New in the current version — the project cannot carry it.
                // (net48: Dictionary has no TryAdd.)
                if (!map.ContainsKey(entry.TypeName))
                {
                    map[entry.TypeName] = false;
                }
            }

            SmartConLogger.Debug(
                $"CheckEmbedded[{catalogItemId}]: DB per-type drift between {fromVersionLabel} and " +
                $"{currentVersionLabel}: {map.Count(kv => kv.Value)} stale of {map.Count} type(s)" +
                (sharedChangedSections.Count > 0
                    ? $" (shared sections changed: {string.Join(", ", sharedChangedSections)})"
                    : string.Empty));
            return map;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SmartConLogger.Warn(
                $"CheckEmbedded[{catalogItemId}]: DB per-type drift computation failed: {ex.Message} " +
                "[Action: per-type индикация отключена для этого семейства до следующей «Проверить»; повторите проверку]");
            return null;
        }
    }

    /// <summary>
    /// Section names whose hashes differ between two versions of the item,
    /// excluding the per-type sections (<c>TYPES</c>/<c>VALUES</c> — those
    /// are answered by the per-type hash comparison). Empty when the
    /// section analytics are pending for either version (the values-level
    /// answer then stands alone) or when nothing shared changed.
    /// </summary>
    private async Task<IReadOnlyList<string>> ComputeChangedSharedSectionsAsync(
        string catalogItemId,
        string fromVersionLabel,
        string currentVersionLabel,
        CancellationToken ct)
    {
        var fromSections = await _contentHashAnalytics!.GetSectionHashesAsync(catalogItemId, fromVersionLabel, ct)
            .ConfigureAwait(false);
        var toSections = await _contentHashAnalytics.GetSectionHashesAsync(catalogItemId, currentVersionLabel, ct)
            .ConfigureAwait(false);
        if (fromSections is null || toSections is null)
        {
            return Array.Empty<string>();
        }

        var changed = new List<string>();
        foreach (var key in fromSections.Keys.Concat(toSections.Keys).Distinct(StringComparer.Ordinal))
        {
            if (string.Equals(key, FamilyContentSectionNames.Types, StringComparison.Ordinal)
                || string.Equals(key, FamilyContentSectionNames.Values, StringComparison.Ordinal))
            {
                continue;
            }
            if (!fromSections.TryGetValue(key, out var a)
                || !toSections.TryGetValue(key, out var b)
                || !string.Equals(a, b, StringComparison.Ordinal))
            {
                changed.Add(key);
            }
        }
        return changed;
    }

    private void ClearLoadableTypeStaleMap(string catalogItemId)
    {
        lock (_cacheLock)
        {
            _loadableTypeStaleByType.Remove(catalogItemId);
        }
    }
}
