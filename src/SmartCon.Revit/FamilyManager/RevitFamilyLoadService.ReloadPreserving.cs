using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilyLoadService
{
    /// <summary>
    /// Reloads an already-loaded family while preserving the set of loaded
    /// family symbols/types. See <see cref="IFamilyLoadService.ReloadFamilyPreservingLoadedTypesAsync"/>
    /// and Issue #101 for rationale.
    /// </summary>
    public async Task<FamilyLoadResult> ReloadFamilyPreservingLoadedTypesAsync(
        FamilyResolvedFile file,
        bool overwriteParameterValues,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        IReadOnlyList<TypeParameterOverwriteOperation>? overwriteOperations = null,
        CancellationToken ct = default)
    {
        var doc = _revitContext.GetDocument();
        if (doc is null)
            return new FamilyLoadResult(false, null, null, "No active document", FamilyLoadStatus.Failed);

        var normalizedPath = Path.GetFullPath(file.AbsolutePath);
        var fileName = Path.GetFileName(normalizedPath);
        using var _scope = SmartConLogger.BeginScope("FamilyReloadPreserve",
            ("Method", nameof(ReloadFamilyPreservingLoadedTypesAsync)),
            ("FilePath", fileName),
            ("OverwriteParameterValues", overwriteParameterValues));

        if (!File.Exists(normalizedPath))
        {
            SmartConLogger.Info("File not found");
            return new FamilyLoadResult(false, null, null, $"File not found: {normalizedPath}", FamilyLoadStatus.Failed);
        }

        // #209 (manual-test bug): in a FAMILY document the preserve-types
        // path below is a SILENT NO-OP for an already-nested shared family —
        // LoadFamilySymbol fires OnFamilyFound and returns true, yet the
        // embedded definition is never replaced (the catalog markers then
        // lied about the embedded version). The documented way to reload a
        // nested definition is plain LoadFamily with the IFamilyLoadOptions
        // overload (overwrite). The caller (StaleFamilyUpdater) post-verifies
        // the embedded content hash against the catalog target version
        // before writing any marker.
        if (doc.IsFamilyDocument)
        {
            return ReloadNestedInFamilyDocumentCore(
                doc, normalizedPath, SafeFileName.GetBaseName(normalizedPath),
                overwriteParameterValues, preOpenedSourceDocProvider: null);
        }

        // Resolve shared-nested names once (REVIT-198137 fallback for the
        // per-type OnSharedFamilyFound callbacks). Same path as a regular
        // family load so the dialog shows the real nested name in Revit
        // 2023 / 2024 < 24.3.0.13. Caller-provided names take priority:
        // callers that block synchronously inside an ExternalEvent callback
        // (Stale Update, .GetAwaiter().GetResult()) pre-resolve them so this
        // method contains no asynchronous gap on the Revit main thread.
        IReadOnlyList<string>? resolvedNestedNames = nestedSharedNames;
        var catalogItemId = file.CatalogItemId;
        if (resolvedNestedNames is null && !string.IsNullOrEmpty(catalogItemId) && _nestedSharedRepository is not null)
        {
            try
            {
                resolvedNestedNames = await _nestedSharedRepository
                    .GetNamesForCurrentVersionAsync(catalogItemId!, ct)
                    .ConfigureAwait(true);
                if (resolvedNestedNames is { Count: > 0 })
                {
                    SmartConLogger.Info(
                        $"Loaded {resolvedNestedNames.Count} nested names from catalog DB for '{catalogItemId}' — " +
                        "will use as fallback if Revit API returns null (REVIT-198137)");
                }
                else
                {
                    SmartConLogger.Debug(
                        $"Catalog lookup for '{catalogItemId}': empty list (legacy catalog or no shared nested in this family)");
                }
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Failed to read nested names from catalog DB for preserve-types reload: {ex.GetType().Name}: {ex.Message} " +
                    "[Action: continuing without fallback names — dialog may show placeholder in Revit 2023/2024.2]");
            }
        }

        // Issue #101: snapshot the names of already-loaded symbols BEFORE the
        // reload. After LoadFamilySymbol for each name, the family definition
        // (geometry/parameters) is refreshed and the symbol's parameter values
        // are updated according to overwriteParameterValues, but no new types
        // are pulled in. If the family is not yet loaded OR has no loaded
        // symbols, fall back to a full LoadFamilyAsync (fresh load).
        Autodesk.Revit.DB.Family? existingFamily = null;
        var existingTypeNames = new List<string>();

        // Try the file base name first; FindExistingFamily only does an exact
        // case-insensitive name match, which is the canonical SmartCon
        // approach (managed storage keeps the family name equal to the rfa
        // base name without the .rfa extension).
        var familyNameFromPath = SafeFileName.GetBaseName(normalizedPath);

        try
        {
            existingFamily = FindExistingFamily(doc, familyNameFromPath);

            if (existingFamily is not null)
            {
                foreach (var sid in existingFamily.GetFamilySymbolIds())
                {
                    var sym = doc.GetElement(sid) as Autodesk.Revit.DB.FamilySymbol;
                    if (sym is not null && !string.IsNullOrEmpty(sym.Name))
                        existingTypeNames.Add(sym.Name);
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"Failed to snapshot already-loaded family symbols: {ex.GetType().Name}: {ex.Message}. " +
                "[Action: falling back to full LoadFamily — no types will be preserved]");
            existingFamily = null;
            existingTypeNames.Clear();
        }

        if (existingFamily is null || existingTypeNames.Count == 0)
        {
            SmartConLogger.Info(
                $"Family '{familyNameFromPath}' not loaded or has no symbols — falling back to full LoadFamily " +
                "(fresh load, all types from .rfa)");
            var fullOptions = FamilyLoadOptions.Default with
            {
                PreferredName = familyNameFromPath,
                OverwriteParameterValues = overwriteParameterValues,
            };
            var freshFile = new FamilyResolvedFile(
                normalizedPath, file.CatalogItemId, file.VersionId, file.VersionLabel);
            return await LoadFamilyAsync(freshFile, fullOptions, onStatusMessage, onSharedDecision, resolvedNestedNames, ct)
                .ConfigureAwait(true);
        }

        SmartConLogger.Info(
            $"Family '{existingFamily.Name}' found in project with {existingTypeNames.Count} loaded symbol(s): " +
            $"[{string.Join(", ", existingTypeNames)}]. Reloading each symbol (preserve-types, Issue #101).");

        // I-05: capture the family name as a string BEFORE the reload transaction.
        // When the .rfa has changed, doc.LoadFamilySymbol triggers OnFamilyFound
        // and Revit REPLACES the Family element with a new one (new ElementId).
        // The `existingFamily` reference becomes invalid (InvalidObjectException
        // on any property access) right after the first successful symbol reload.
        // Observed in smartcon.log (Issue #101 follow-up): the first Update
        // attempt threw InvalidObjectException at `existingFamily.Name` after
        // "Symbol 'Ф160' reloaded successfully"; the second attempt succeeded
        // only because the family was already current (OnFamilyFound did not
        // fire, so the Family element was not replaced).
        var displayFamilyName = existingFamily.Name;

        var loadOptions = new RevitFamilyLoadOptions(
            overwriteParameterValues, onStatusMessage, onSharedDecision, resolvedNestedNames);

        var reloadedCount = 0;

        try
        {
            // Reload each symbol. Revit re-reads the family definition on each
            // call when the .rfa has changed (OnFamilyFound fires "only when
            // the family is both loaded and changed" per revitapidocs.com/2026).
            //
            // #239 (probe-proven, 2026-08-23): ONLY THE FIRST call performs the
            // real definition merge — and its parameter-value overwrite lands
            // on the requested symbol alone. The 2nd..Nth calls return true
            // yet are no-ops: the definition is already current, no merge, no
            // parameter overwrite. The catalog post-pass (overwriteOperations,
            // applied below in this same group) is what lands the values on
            // every type.
            //
            // Multiple symbols = multiple definition reloads (API limitation,
            // change-request REVIT-68222); a single TransactionGroup wraps
            // the whole batch so the user gets one Undo entry.
            //
            // NOTE: after the first LoadFamilySymbol that triggers a definition
            // reload, `existingFamily` is invalid — do NOT touch it below.
            //
            // I-03: the group is created via ITransactionService.BeginGroupSession,
            // never `new TransactionGroup` directly. Assimilate runs only when
            // at least one symbol reload committed; on exception the session's
            // Dispose rolls the whole batch back (all-or-nothing per family).
            using var groupSession = _transactionService.BeginGroupSession("SmartCon: Reload Family (Preserve Types)");

            foreach (var typeName in existingTypeNames)
            {
                if (ct.IsCancellationRequested) break;

                var symbolOk = false;
                Autodesk.Revit.DB.FamilySymbol? localSymbol = null;

                groupSession.RunInTransaction("Reload Family Symbol", _ =>
                {
                    symbolOk = doc.LoadFamilySymbol(
                        normalizedPath, typeName, loadOptions, out localSymbol);
                });

                if (symbolOk)
                {
                    reloadedCount++;
                    SmartConLogger.Info($"Symbol '{typeName}' reloaded successfully");
                }
                else
                {
                    // A false return means the family is already current for
                    // this symbol. NOTE (#239 probe): the 2nd..Nth calls can
                    // also return TRUE while being no-ops — the return value
                    // alone never proves a parameter overwrite happened.
                    reloadedCount++;
                    SmartConLogger.Info(
                        $"Symbol '{typeName}' reload returned false (family already current for this " +
                        "symbol — likely 2nd+ type after first reload). Treating as success.");
                }
            }

            if (reloadedCount == 0)
            {
                // Nothing reloaded (cancelled before the first symbol). Skip
                // Assimilate — the session Dispose rolls the empty group back —
                // and report failure so the caller does NOT persist a fresh
                // version marker for a family that was never touched.
                var errorMessage = $"Failed to reload any of the {existingTypeNames.Count} type(s) (cancelled or rejected)";
                SmartConLogger.Info(errorMessage);
                return new FamilyLoadResult(false, displayFamilyName, null, errorMessage, FamilyLoadStatus.Failed);
            }

            // Issue #239: land the catalog's per-type parameter values on ALL
            // loaded symbols (the merge above overwrote only the first one).
            // Runs inside this same TransactionGroup — one Undo entry, and a
            // rollback covers the post-pass too.
            if (overwriteParameterValues && overwriteOperations is { Count: > 0 })
            {
                ApplyOverwriteOperations(doc, displayFamilyName, overwriteOperations, groupSession);
            }

            groupSession.Assimilate();

            var msg = $"Family '{displayFamilyName}' updated preserving {reloadedCount}/{existingTypeNames.Count} loaded type(s)";
            SmartConLogger.Info(msg);

            return new FamilyLoadResult(
                true, displayFamilyName, msg, null, FamilyLoadStatus.Updated);
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"ReloadFamilyPreservingLoadedTypesAsync exception: {ex.GetType().Name}: {ex.Message}");
            return new FamilyLoadResult(false, displayFamilyName, null, ex.Message, FamilyLoadStatus.Failed);
        }
    }

    /// <summary>
    /// Issue #239 post-pass: applies the catalog's per-type parameter values
    /// to every loaded symbol of the reloaded family, inside the reload's
    /// TransactionGroup. Revit's per-symbol <c>LoadFamilySymbol</c> merge
    /// overwrites parameter values only for the FIRST requested symbol — the
    /// 2nd..Nth calls return true but are no-ops (probe-proven 2026-08-23).
    /// The values come from the catalog DB (extracted attribute values of the
    /// target version) — no extra file open. Per-parameter failures are
    /// isolated (try/catch); the caller's content post-verify arbitrates the
    /// final state. Logging is captured into locals and emitted AFTER the
    /// transaction (file I/O inside a transaction callback is a known WPF
    /// freeze factor in this service).
    /// </summary>
    private void ApplyOverwriteOperations(
        Document doc,
        string familyName,
        IReadOnlyList<TypeParameterOverwriteOperation> operations,
        ITransactionGroupSession groupSession)
    {
        var applied = 0;
        var skipped = 0;
        var failed = 0;
        var unresolvedElements = new List<string>();
        var failureSamples = new List<string>();

        groupSession.RunInTransaction("Overwrite Type Parameter Values", _ =>
        {
            // Re-collect AFTER the reload: the merge may have replaced the
            // Family element with a new one (I-05).
            var family = FindExistingFamily(doc, familyName);
            if (family is null)
            {
                failed = operations.Count;
                failureSamples.Add($"family '{familyName}' not found after reload");
                return;
            }

            var symbolsByName = new Dictionary<string, Autodesk.Revit.DB.FamilySymbol>(StringComparer.Ordinal);
            foreach (var sid in family.GetFamilySymbolIds())
            {
                if (doc.GetElement(sid) is Autodesk.Revit.DB.FamilySymbol sym
                    && !string.IsNullOrEmpty(sym.Name)
                    && !symbolsByName.ContainsKey(sym.Name))
                {
                    symbolsByName[sym.Name] = sym;
                }
            }

            // Lazy name → ElementId map for ElementId parameters. Material is
            // the realistic per-type ElementId parameter; values referencing
            // other element classes fail the name lookup and are reported in
            // the post-transaction Warn.
            Dictionary<string, ElementId>? materialsByName = null;

            foreach (var op in operations)
            {
                if (!symbolsByName.TryGetValue(op.TypeName, out var symbol))
                {
                    skipped++;
                    continue;
                }

                var param = symbol.LookupParameter(op.ParameterName);
                if (param is null || param.IsReadOnly)
                {
                    skipped++;
                    continue;
                }

                try
                {
                    bool ok;
                    switch (op.Kind)
                    {
                        case TypeParameterOverwriteKind.SetDouble:
                            ok = op.ValueNumber.HasValue && param.Set(op.ValueNumber.Value);
                            break;
                        case TypeParameterOverwriteKind.SetInteger:
                            ok = op.ValueNumber.HasValue && param.Set((int)op.ValueNumber.Value);
                            break;
                        case TypeParameterOverwriteKind.SetString:
                            ok = param.Set(op.ValueText ?? string.Empty);
                            break;
                        default: // ResolveElementByName
                            materialsByName ??= BuildMaterialNameMap(doc);
                            if (op.ValueText is not null
                                && materialsByName.TryGetValue(op.ValueText, out var materialId))
                            {
                                ok = param.Set(materialId);
                            }
                            else if (op.ValueText is not null
                                && TryResolveByNameAnyClass(doc, op.ValueText, out var elementId))
                            {
                                ok = param.Set(elementId);
                            }
                            else
                            {
                                ok = false;
                                if (unresolvedElements.Count < 5)
                                    unresolvedElements.Add($"{op.TypeName}.{op.ParameterName}='{op.ValueText}'");
                            }
                            break;
                    }

                    if (ok) applied++;
                    else skipped++;
                }
                catch (Exception ex)
                {
                    failed++;
                    if (failureSamples.Count < 5)
                        failureSamples.Add($"{op.TypeName}.{op.ParameterName}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // Recompute formula-driven parameters before the group commits so
            // the caller's content post-verify sees the regenerated state.
            doc.Regenerate();
        });

        SmartConLogger.Info(
            $"Overwrite post-pass (#239): applied={applied}, skipped={skipped}, failed={failed} " +
            $"of {operations.Count} operation(s)");

        if (unresolvedElements.Count > 0)
        {
            SmartConLogger.Warn(
                $"Overwrite post-pass (#239): ElementId value(s) not resolvable by name in the project: " +
                $"{string.Join(", ", unresolvedElements)}. " +
                "[Action: load the referenced element (e.g. the material) into the project, or update the family via the editor]");
        }

        if (failureSamples.Count > 0)
        {
            SmartConLogger.Info($"Overwrite post-pass (#239) failure samples: {string.Join(" | ", failureSamples)}");
        }
    }

    private static Dictionary<string, ElementId> BuildMaterialNameMap(Document doc)
    {
        var map = new Dictionary<string, ElementId>(StringComparer.Ordinal);
        foreach (var material in new FilteredElementCollector(doc)
            .OfClass(typeof(Material))
            .Cast<Material>())
        {
            if (!string.IsNullOrEmpty(material.Name) && !map.ContainsKey(material.Name))
                map[material.Name] = material.Id;
        }

        return map;
    }

    /// <summary>
    /// Best-effort ElementId resolution by element name for the #239
    /// post-pass. ElementId parameters in MEP families reference more than
    /// materials — electrical load classifications, fill patterns, and
    /// other non-ElementType elements (manual test 2026-08-23: the ADSK
    /// fan's «Классификация нагрузок» = 'ОВК' is an
    /// ElectricalLoadClassification and the material map missed it).
    /// Fallback LINQ name search across all non-ElementType element classes;
    /// matches are ordered Material-first so a cross-class name collision
    /// resolves to the most common per-type reference kind. References to
    /// ElementType targets (e.g. nested family types) stay unresolved
    /// (best-effort — the post-verify arbitrates).
    /// </summary>
    private static bool TryResolveByNameAnyClass(Document doc, string name, out ElementId elementId)
    {
        var best = new FilteredElementCollector(doc)
            .WhereElementIsNotElementType()
            .Where(e => string.Equals(e.Name, name, StringComparison.Ordinal))
            .OrderBy(e => e is Material ? 0 : 1)
            .FirstOrDefault();
        elementId = best?.Id ?? ElementId.InvalidElementId;
        return best is not null;
    }
}
