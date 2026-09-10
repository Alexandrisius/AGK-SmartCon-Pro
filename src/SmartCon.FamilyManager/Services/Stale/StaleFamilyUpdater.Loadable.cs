using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

internal sealed partial class StaleFamilyUpdater
{
    private async Task<StaleFamilyUpdateResult> UpdateFamilyCoreAsync(
        string catalogItemId,
        bool overwriteParameterValues,
        string? fromVersionLabel,
        CancellationToken ct)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(catalogItemId);
#else
        if (catalogItemId is null) throw new ArgumentNullException(nameof(catalogItemId));
#endif
        // Note: caller (UpdateFamilyAsync or UpdateBatchAsync) already opened BeginScope.
        try
        {
            var targetRevit = ResolveTargetRevit(catalogItemId);

            // Issue #104: system catalog items are synchronized from the
            // mini-project (parameter/structure/routing data written into the
            // existing project types), never re-loaded like .rfa families.
            // The ES marker is written by the synchronizer inside its
            // per-type transaction. overwriteParameterValues is not
            // applicable — the catalog reference always overwrites.
            if (_catalog is not null && _typeRepository is not null && _systemSyncOrchestrator is not null)
            {
                var item = await _catalog.GetItemAsync(catalogItemId, ct).ConfigureAwait(true);
                if (item is not null && item.FamilySource == "system")
                {
                    return await UpdateSystemFamilyCoreAsync(item, targetRevit, ct).ConfigureAwait(true);
                }
            }

            var resolved = await _fileResolver
                .ResolveForLoadAsync(catalogItemId, targetRevit, ct)
                .ConfigureAwait(true);
            if (string.IsNullOrEmpty(resolved.AbsolutePath))
            {
                SmartConLogger.Warn(
                    $"UpdateFamily[{catalogItemId}]: fileResolver returned empty path. " +
                    "[Action: catalog item is not available on disk for the current Revit " +
                    "version; the family will be skipped and the next Check will mark it " +
                    "stale again]");
                return StaleFamilyUpdateResult.Failure(null);
            }

            // Pre-resolve shared-nested names BEFORE entering the ExternalEvent
            // callback. The callback below blocks the Revit main thread with
            // .GetAwaiter().GetResult(), which is only safe when every await
            // inside completes synchronously. Resolving the SQLite lookup here
            // (true async caller context) removes the only asynchronous gap
            // from ReloadFamilyPreservingLoadedTypesAsync — a latent deadlock
            // if Microsoft.Data.Sqlite ever yields asynchronously.
            IReadOnlyList<string>? nestedNames = null;
            if (_nestedSharedRepository is not null)
            {
                try
                {
                    nestedNames = await _nestedSharedRepository
                        .GetNamesForCurrentVersionAsync(catalogItemId, ct)
                        .ConfigureAwait(true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SmartConLogger.Warn(
                        $"UpdateFamily[{catalogItemId}]: failed to pre-resolve nested names: " +
                        $"{ex.GetType().Name}: {ex.Message}. " +
                        "[Action: continuing without fallback names — dialog may show placeholder in Revit 2023/2024.2]");
                }
            }

            var canVerify = _snapshotExtractor is not null && _contentHasher is not null;

            // #209 optimization: when the load service is source-aware, the
            // whole FAMILY-document cycle (pre-verify → poke+merge → failed-
            // reload arbitration → post-verify) runs in ONE Revit-thread
            // pass sharing a single OpenDocumentFile of the resolved file
            // (the legacy path below opens it up to 3 times). Returns
            // NotApplicable only for a project document — the legacy path
            // below then runs unchanged.
            if (canVerify && _loadService is IFamilyLoadServiceSourceAware sourceAware)
            {
                var orchestrated = await _awaitable.RaiseAsync(
                    _ => UpdateInFamilyDocumentOnRevitThread(
                        catalogItemId, resolved, sourceAware, overwriteParameterValues),
                    ct).ConfigureAwait(true);
                if (orchestrated.Kind != FamilyDocumentUpdateKind.NotApplicable)
                {
                    if (orchestrated.Kind == FamilyDocumentUpdateKind.Failed)
                    {
                        return StaleFamilyUpdateResult.Failure(orchestrated.FamilyName);
                    }
                    await WriteMarkerBestEffortAsync(catalogItemId, resolved, orchestrated.FamilyName, targetRevit, ct)
                        .ConfigureAwait(true);
                    var alreadyCurrent = orchestrated.Kind is FamilyDocumentUpdateKind.PreVerified
                        or FamilyDocumentUpdateKind.Arbitrated;
                    return StaleFamilyUpdateResult.SuccessWithoutReport(orchestrated.FamilyName, alreadyCurrent);
                }
            }

            // #209 round-3 + #222: pre-verify BEFORE any reload, uniformly in
            // family documents and PROJECTS (the content proof applies to a
            // loaded project family the same way — EditFamily extraction,
            // see StaleCheckContentFallbackTests). Case covered: the previous
            // batch already reloaded the family (a retry's LoadFamily then
            // returns false for "unchanged" and used to be reported as a
            // hard failure). If the embedded content already equals the
            // catalog target, the update is a no-op success — write the
            // marker without touching Revit.
            // (Legacy path — used when the load service is not source-aware.)
            if (canVerify
                && StaleUpdateVerificationPolicy.ShouldSkipReload(
                    await VerifyEmbeddedMatchesResolvedFileAsync(
                        catalogItemId, resolved, null, ct, isPostReload: false)
                    .ConfigureAwait(true)))
            {
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: embedded content already matches catalog " +
                    $"{resolved.VersionLabel} — reload skipped, writing marker only");
                var embeddedName = System.IO.Path.GetFileNameWithoutExtension(resolved.AbsolutePath);
                await WriteMarkerBestEffortAsync(catalogItemId, resolved, embeddedName, targetRevit, ct)
                    .ConfigureAwait(true);
                return StaleFamilyUpdateResult.SuccessWithoutReport(embeddedName, contentAlreadyCurrent: true);
            }

            // Issue #101: Stale Update must reload the family while preserving
            // the set of types currently loaded in the project. A plain
            // LoadFamily pulls in EVERY type defined in the .rfa (could be 50),
            // even if the user originally loaded only one via LoadFamilySymbol.
            // ReloadFamilyPreservingLoadedTypesAsync snapshots the loaded
            // symbols first and calls LoadFamilySymbol per type. When the
            // family is not loaded yet (no symbols to preserve) it falls back
            // to a full LoadFamilyAsync internally.
            //
            // Issue #239: the per-symbol merge overwrites parameter values
            // only for the FIRST reloaded symbol (probe-proven). Build the
            // overwrite post-pass plan from the catalog — per-type parameter
            // values of the TARGET version, read from SQLite (zero extra file
            // opens) — and pass it down: the load service applies it to every
            // loaded symbol inside the reload's TransactionGroup.
            IReadOnlyList<TypeParameterOverwriteOperation>? overwriteOperations = null;
            if (overwriteParameterValues
                && resolved.VersionId is not null
                && _attributeValueRepository is not null
                && _typeRepository is not null)
            {
                try
                {
                    var targetValues = await _attributeValueRepository
                        .GetValuesForItemAsync(catalogItemId, resolved.VersionId, ct)
                        .ConfigureAwait(true);
                    var targetTypes = await _typeRepository
                        .GetTypesForItemVersionAsync(catalogItemId, resolved.VersionId, ct)
                        .ConfigureAwait(true);
                    overwriteOperations = TypeParameterOverwritePlanner.Plan(
                        targetValues, ToTypeNameMap(targetTypes));
                    SmartConLogger.Info(
                        $"UpdateFamily[{catalogItemId}]: overwrite post-pass plan — " +
                        $"{overwriteOperations.Count} operation(s) from {targetValues.Count} catalog value(s)");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    SmartConLogger.Warn(
                        $"UpdateFamily[{catalogItemId}]: failed to build the overwrite post-pass plan: " +
                        $"{ex.GetType().Name}: {ex.Message}. " +
                        "[Action: update falls back to Revit's merge only — multi-type families may fail " +
                        "post-verify and stay stale; check the catalog DB availability]");
                    overwriteOperations = null;
                }
            }

            var result = await _awaitable.RaiseAsync(
                _ => _loadService.ReloadFamilyPreservingLoadedTypesAsync(
                    resolved,
                    overwriteParameterValues,
                    onStatusMessage: null,
                    onSharedDecision: req => _dialogService.ShowSharedFamiliesLoadModeDialog(req),
                    nestedSharedNames: nestedNames,
                    overwriteOperations: overwriteOperations,
                    ct: ct).GetAwaiter().GetResult(),
                ct).ConfigureAwait(true);

            if (!result.Success)
            {
                // LoadFamily also reports failure when the file content is
                // UNCHANGED versus what is loaded (callbacks never fire) —
                // indistinguishable from a real rejection at this point, so
                // let the content verification arbitrate (uniformly in family
                // documents and projects, #222). A rejected reload whose
                // content genuinely differs must never be masked as success.
                if (canVerify
                    && StaleUpdateVerificationPolicy.ShouldAcceptFailedReload(
                        await VerifyEmbeddedMatchesResolvedFileAsync(
                            catalogItemId, resolved, result.FamilyName, ct)
                        .ConfigureAwait(true)))
                {
                    SmartConLogger.Info(
                        $"UpdateFamily[{catalogItemId}]: reload reported failure but embedded content " +
                        $"matches catalog {resolved.VersionLabel} — treating as already up-to-date");
                    await WriteMarkerBestEffortAsync(catalogItemId, resolved, result.FamilyName, targetRevit, ct)
                        .ConfigureAwait(true);
                    return StaleFamilyUpdateResult.SuccessWithoutReport(result.FamilyName, contentAlreadyCurrent: true);
                }
                return StaleFamilyUpdateResult.Failure(result.FamilyName);
            }

            // #209 (manual-test bug) + #222: a reload can be a silent no-op
            // while markers claim the new version (family document: Revit's
            // changedness wall; project: LoadFamilySymbol skipped types whose
            // definition did not change while OTHERS failed to land). Verify
            // the embedded content hash equals the catalog target version
            // BEFORE writing any marker — uniformly in family documents and
            // projects. A failed verification = failed update (no marker,
            // the family stays stale and the next Check offers it again).
            if (canVerify)
            {
                var verified = await VerifyEmbeddedMatchesResolvedFileAsync(
                        catalogItemId, resolved, result.FamilyName, ct)
                    .ConfigureAwait(true);
                if (StaleUpdateVerificationPolicy.ShouldFailSuccessfulReload(verified))
                {
                    return StaleFamilyUpdateResult.Failure(result.FamilyName);
                }
            }

            await WriteMarkerBestEffortAsync(catalogItemId, resolved, result.FamilyName, targetRevit, ct)
                .ConfigureAwait(true);

            // #222: per-type change report for the single-update UX («успешно»
            // must not read as «мой параметр обновился» when the changes
            // landed in types the project does not have loaded).
            var report = await BuildTypeChangeReportAsync(
                catalogItemId, result.FamilyName, resolved, fromVersionLabel, targetRevit, ct)
                .ConfigureAwait(true);
            return new StaleFamilyUpdateResult(
                true, result.FamilyName, report.Loaded, report.NotLoaded, report.Available, ContentAlreadyCurrent: false);
        }
        catch (OperationCanceledException)
        {
            // OCE is a normal control flow (caller requested cancellation) -
            // do not treat as a failure and do not pollute the log with a Warn.
            // Re-raise so the caller's CancellationToken is honoured.
            throw;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: failed: {ex.Message}. " +
                "[Action: family skipped, batch continues]");
            return StaleFamilyUpdateResult.Failure(null);
        }
    }

    /// <summary>
    /// #222: per-type change report for the single-update UX — which catalog
    /// types actually changed values between the version that WAS loaded
    /// (<paramref name="fromVersionLabel"/>, from the stale snapshot / ES
    /// marker) and the version updated TO, split by the set of types
    /// currently loaded in the project. <c>Available == false</c> when the
    /// diff cannot be computed honestly (unknown from-version, missing
    /// extraction rows for either version, optional dependency absent) —
    /// the caller then shows the plain success message.
    /// </summary>
    private async Task<(IReadOnlyList<string> Loaded, IReadOnlyList<string> NotLoaded, bool Available)>
        BuildTypeChangeReportAsync(
            string catalogItemId,
            string? familyName,
            FamilyResolvedFile resolved,
            string? fromVersionLabel,
            int targetRevit,
            CancellationToken ct)
    {
        if (fromVersionLabel is null
            || resolved.VersionId is null
            || _catalog is null
            || _typeRepository is null
            || _attributeValueRepository is null)
        {
            return ([], [], false);
        }

        if (string.Equals(fromVersionLabel, resolved.VersionLabel, StringComparison.OrdinalIgnoreCase))
        {
            // Updating to the same version that was loaded — an honest,
            // empty diff ("type values unchanged"), not a missing report.
            return ([], [], true);
        }

        FamilyCatalogVersion? fromVersion;
        IReadOnlyList<ExtractedAttributeValue> fromValues;
        IReadOnlyList<ExtractedAttributeValue> toValues;
        IReadOnlyList<FamilyTypeDescriptor> fromTypes;
        IReadOnlyList<FamilyTypeDescriptor> toTypes;
        try
        {
            fromVersion = await _catalog
                .GetVersionByLabelAsync(catalogItemId, fromVersionLabel, targetRevit, ct)
                .ConfigureAwait(true);
            if (fromVersion is null)
            {
                SmartConLogger.Info(
                    $"UpdateFamily[{catalogItemId}]: type-change report unavailable — " +
                    $"the previously loaded version '{fromVersionLabel}' is not in the catalog");
                return ([], [], false);
            }

            fromValues = await _attributeValueRepository
                .GetValuesForItemAsync(catalogItemId, fromVersion.Id, ct)
                .ConfigureAwait(true);
            toValues = await _attributeValueRepository
                .GetValuesForItemAsync(catalogItemId, resolved.VersionId, ct)
                .ConfigureAwait(true);
            fromTypes = await _typeRepository
                .GetTypesForItemVersionAsync(catalogItemId, fromVersion.Id, ct)
                .ConfigureAwait(true);
            toTypes = await _typeRepository
                .GetTypesForItemVersionAsync(catalogItemId, resolved.VersionId, ct)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            SmartConLogger.Warn(
                $"UpdateFamily[{catalogItemId}]: type-change report failed: {ex.GetType().Name}: {ex.Message} " +
                "[Action: отчёт об изменённых типах пропущен — сама семья обновлена корректно; проверьте доступность БД каталога]");
            return ([], [], false);
        }

        if (fromValues.Count == 0 || toValues.Count == 0)
        {
            SmartConLogger.Info(
                $"UpdateFamily[{catalogItemId}]: type-change report unavailable — " +
                "extracted attribute values are missing for one of the versions " +
                "(re-run the attribute extraction for the catalog)");
            return ([], [], false);
        }

        var changed = CatalogVersionTypeDiffLogic.ComputeChangedTypeNames(
            fromValues, ToTypeNameMap(fromTypes), toValues, ToTypeNameMap(toTypes));

        IReadOnlyList<string> loadedTypeNames = [];
        if (_familySearchService is not null && !string.IsNullOrEmpty(familyName))
        {
            loadedTypeNames = await _awaitable.RaiseAsync(
                _ => _familySearchService.GetFamilyTypeNames(familyName!),
                ct).ConfigureAwait(true);
        }

        var loadedSet = new HashSet<string>(loadedTypeNames, StringComparer.OrdinalIgnoreCase);
        var loaded = changed.Where(t => loadedSet.Contains(t)).ToList();
        var notLoaded = changed.Where(t => !loadedSet.Contains(t)).ToList();
        SmartConLogger.Info(
            $"UpdateFamily[{catalogItemId}]: type-change report — {changed.Count} changed type(s) " +
            $"({loaded.Count} loaded, {notLoaded.Count} not loaded) between " +
            $"{fromVersionLabel} and {resolved.VersionLabel}");
        return (loaded, notLoaded, true);
    }

    private static Dictionary<string, string> ToTypeNameMap(IReadOnlyList<FamilyTypeDescriptor> types)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var type in types)
        {
            if (!map.ContainsKey(type.Id))
            {
                map[type.Id] = type.Name;
            }
        }
        return map;
    }
}
