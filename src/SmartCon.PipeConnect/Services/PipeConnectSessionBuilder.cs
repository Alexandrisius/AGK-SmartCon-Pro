using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Compatibility;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Extracts S1–S5 analysis from PipeConnectCommand into a reusable builder.
/// Receives services through constructor. Returns a fully populated
/// PipeConnectSessionContext or null (user cancelled / no connectors).
/// </summary>
public sealed class PipeConnectSessionBuilder(
    IElementSelectionService selectionSvc,
    IConnectorService connectorSvc,
    IFittingMappingRepository mappingRepo,
    IFamilyConnectorService familyConnSvc,
    ITransactionService txService,
    IDialogService dialogSvc,
    IParameterResolver paramResolver,
    ILookupTableService lookupSvc,
    IFittingMapper fittingMapper,
    IElementChainIterator chainIterator)
{
    private sealed record ParameterResolutionPlan(
        bool Skip,
        double TargetRadius,
        bool ExpectNeedsAdapter,
        string? WarningMessage,
        IReadOnlyList<LookupColumnConstraint> LookupConstraints
    );

    /// <summary>
    /// Runs S1 → S1.1 → S2 → S2.1 → S3 → S4 → S5 → S6 (chain graph).
    /// Returns null when the user cancels at any selection step.
    /// </summary>
    public PipeConnectSessionContext? BuildSession(Document doc)
    {
        // ── S1: Dynamic element ──────────────────────────────────────────────
        var dynamicPick = selectionSvc.PickElementWithFreeConnector(
            LocalizationService.GetString("Pick_FirstElement"));
        if (dynamicPick is null) return null;

        var dynamicProxy = connectorSvc.GetNearestFreeConnector(
            doc, dynamicPick.Value.ElementId, dynamicPick.Value.ClickPoint);
        if (dynamicProxy is null)
        {
            dialogSvc.ShowWarning("SmartCon", LocalizationService.GetString("Msg_NoConnectorsFirst"));
            return null;
        }

        var virtualCtcStore = new VirtualCtcStore();

        // ── S1.1: Dynamic connector type ─────────────────────────────────────
        if (!IsKnownTypeCode(dynamicProxy.ConnectionTypeCode))
        {
            var result = EnsureTypeCode(doc, dynamicProxy, virtualCtcStore);
            if (result is null) return null;
            dynamicProxy = connectorSvc.GetNearestFreeConnector(
                doc, dynamicProxy.OwnerElementId, dynamicProxy.Origin) ?? dynamicProxy;
            dynamicProxy = dynamicProxy with { ConnectionTypeCode = new ConnectionTypeCode(result.Code) };
        }

        // ── S2: Static element ───────────────────────────────────────────────
        var staticPick = selectionSvc.PickElementWithFreeConnector(
            LocalizationService.GetString("Pick_SecondElement"),
            excludeElementId: dynamicPick.Value.ElementId);
        if (staticPick is null) return null;

        var staticProxy = connectorSvc.GetNearestFreeConnector(
            doc, staticPick.Value.ElementId, staticPick.Value.ClickPoint);
        if (staticProxy is null)
        {
            dialogSvc.ShowWarning("SmartCon", LocalizationService.GetString("Msg_NoConnectorsSecond"));
            return null;
        }

        // ── S2.1: Static connector type ──────────────────────────────────────
        if (!IsKnownTypeCode(staticProxy.ConnectionTypeCode))
        {
            var result = EnsureTypeCode(doc, staticProxy, virtualCtcStore);
            if (result is null) return null;
            staticProxy = connectorSvc.GetNearestFreeConnector(
                doc, staticProxy.OwnerElementId, staticProxy.Origin) ?? staticProxy;
            staticProxy = staticProxy with { ConnectionTypeCode = new ConnectionTypeCode(result.Code) };
        }

        // ── S3: alignment (pure math) ────────────────────────────────────────
        var alignResult = ConnectorAligner.ComputeAlignment(
            staticProxy.OriginVec3, staticProxy.BasisZVec3, staticProxy.BasisXVec3,
            dynamicProxy.OriginVec3, dynamicProxy.BasisZVec3, dynamicProxy.BasisXVec3);

        // ── S4: parameter resolution (outside TransactionGroup) ──────────────
        var plan = BuildResolutionPlan(doc, dynamicProxy, staticProxy.Radius);

        // ── S5: fitting mapping ──────────────────────────────────────────────
        var proposed = fittingMapper.GetMappings(
            staticProxy.ConnectionTypeCode, dynamicProxy.ConnectionTypeCode);

        if (proposed.Count == 0 &&
            staticProxy.ConnectionTypeCode.IsDefined &&
            dynamicProxy.ConnectionTypeCode.IsDefined)
        {
            proposed = fittingMapper.FindShortestFittingPath(
                staticProxy.ConnectionTypeCode, dynamicProxy.ConnectionTypeCode);
        }

        // ── S6: chain graph ──────────────────────────────────────────────────
        var stopAt = new HashSet<ElementId>(new ElementIdEqualityComparer())
        {
            staticPick.Value.ElementId
        };
        var chainGraph = chainIterator.BuildGraph(doc, dynamicPick.Value.ElementId, stopAt);
        SmartConLogger.Info($"[Chain] Graph: {chainGraph.TotalChainElements} elements, " +
            $"{chainGraph.MaxLevel} уровней");

        return new PipeConnectSessionContext
        {
            StaticConnector = staticProxy,
            DynamicConnector = dynamicProxy,
            AlignResult = alignResult,
            ParamTargetRadius = plan.Skip ? null : plan.TargetRadius,
            ParamExpectNeedsAdapter = plan.ExpectNeedsAdapter,
            ProposedFittings = proposed.ToList(),
            ChainGraph = chainGraph,
            LookupConstraints = plan.LookupConstraints,
            VirtualCtcStore = virtualCtcStore,
        };
    }

    // ── Private helpers ────────────────────────────────────────────────────────

    private bool IsKnownTypeCode(ConnectionTypeCode code)
    {
        if (!code.IsDefined) return false;
        var types = mappingRepo.GetConnectorTypes();
        return types.Any(t => t.Code == code.Value);
    }

    private ConnectorTypeDefinition? EnsureTypeCode(
        Document doc,
        ConnectorProxy proxy,
        VirtualCtcStore virtualCtcStore)
    {
        var types = mappingRepo.GetConnectorTypes();
        if (types.Count == 0)
        {
            dialogSvc.ShowWarning("SmartCon", LocalizationService.GetString("Msg_ConfigureTypes"));
            return null;
        }

        var selected = dialogSvc.ShowMiniTypeSelector(types);
        if (selected is null) return null;

        var element = doc.GetElement(proxy.OwnerElementId);

        if (element is MEPCurve or FlexPipe)
        {
            txService.RunInTransaction("SetConnectorType", txDoc =>
            {
                familyConnSvc.SetConnectorTypeCode(
                    txDoc, proxy.OwnerElementId, proxy.ConnectorIndex, selected);
            });
        }
        else
        {
            var ctc = new ConnectionTypeCode(selected.Code);
            virtualCtcStore.Set(proxy.OwnerElementId, proxy.ConnectorIndex, ctc, selected);
            SmartConLogger.Info($"[CTC] Virtual CTC for {proxy.OwnerElementId.GetValue()}:{proxy.ConnectorIndex} = {selected.Code}.{selected.Name}");
        }

        return selected;
    }

    private ParameterResolutionPlan BuildResolutionPlan(
        Document doc,
        ConnectorProxy dynamicProxy,
        double staticRadius)
    {
        const double eps = 1e-6;

        SmartConLogger.DebugSection("BuildResolutionPlan (S4)");
        SmartConLogger.Debug($"  dynamic: elementId={dynamicProxy.OwnerElementId.GetValue()}, connIdx={dynamicProxy.ConnectorIndex}, radius={dynamicProxy.Radius:F6} ft ({dynamicProxy.Radius * FeetToMm:F2} mm)");
        SmartConLogger.Debug($"  staticRadius={staticRadius:F6} ft ({staticRadius * FeetToMm:F2} mm)");
        SmartConLogger.Debug($"  delta={System.Math.Abs(staticRadius - dynamicProxy.Radius):F6} ft");

        var dynId = dynamicProxy.OwnerElementId;
        var connIdx = dynamicProxy.ConnectorIndex;
        var element = doc.GetElement(dynId);
        SmartConLogger.Debug($"  element='{element?.Name}' ({element?.GetType().Name})");

        if (element is Autodesk.Revit.DB.FamilyInstance)
        {
            SmartConLogger.Debug("  → Warming cache for ALL free FamilyInstance connectors...");
            var allFreeConns = connectorSvc.GetAllFreeConnectors(doc, dynId);
            foreach (var c in allFreeConns)
            {
                SmartConLogger.Debug($"    connIdx={c.ConnectorIndex}, radius={c.Radius * FeetToMm:F2}mm");
                paramResolver.GetConnectorRadiusDependencies(doc, dynId, c.ConnectorIndex);
            }
        }

        var lookupConstraints = BuildMultiColumnConstraints(
            doc, dynId, connIdx);
        if (lookupConstraints.Count > 0)
        {
            var constraintStr = string.Join(", ", lookupConstraints.Select(c => $"{c.ParameterName}={c.ValueMm:F0}mm"));
            SmartConLogger.Debug($"  Multi-column constraints: [{constraintStr}]");
            SmartConLogger.Info($"[S4] [MultiCol] constraints: [{constraintStr}]");
        }
        else
        {
            SmartConLogger.Info($"[S4] [MultiCol] constraints: [] (no other connectors with dep)");
        }

        if (System.Math.Abs(staticRadius - dynamicProxy.Radius) < eps)
        {
            SmartConLogger.Debug("  → Radii match (< eps) → Plan(Skip=true)");
            SmartConLogger.Info($"[S4] Radii match, S4 skipped (constraints={lookupConstraints.Count})");
            return new ParameterResolutionPlan(Skip: true, TargetRadius: staticRadius,
                ExpectNeedsAdapter: false, WarningMessage: null, LookupConstraints: lookupConstraints);
        }

        double staticDn = System.Math.Round(staticRadius * 2.0 * FeetToMm);
        double dynDn = System.Math.Round(dynamicProxy.Radius * 2.0 * FeetToMm);
        SmartConLogger.Debug($"  static=DN{staticDn}, dynamic=DN{dynDn} — need to adjust");

        if (element is MEPCurve or Autodesk.Revit.DB.Plumbing.FlexPipe)
        {
            SmartConLogger.Debug("  → MEPCurve/FlexPipe: direct write RBS_PIPE_DIAMETER_PARAM → Plan(Skip=false, target=staticRadius)");
            SmartConLogger.Info($"[S4] MEPCurve DN{dynDn} → DN{staticDn}: direct write");
            return new ParameterResolutionPlan(Skip: false, TargetRadius: staticRadius,
                ExpectNeedsAdapter: false, WarningMessage: null, LookupConstraints: []);
        }

        SmartConLogger.Debug("  → GetConnectorRadiusDependencies...");
        var deps = paramResolver.GetConnectorRadiusDependencies(doc, dynId, connIdx);
        var dep = deps.Count > 0 ? deps[0] : null;
        SmartConLogger.Debug($"  deps.Count={deps.Count}, dep={(dep is null ? "null" : $"IsInstance={dep.IsInstance}, Formula='{dep.Formula}', DirectParamName='{dep.DirectParamName}', IsDiameter={dep.IsDiameter}")}");

        SmartConLogger.Debug("  → HasLookupTable...");
        bool hasTable = lookupSvc.HasLookupTable(doc, dynId, connIdx);
        SmartConLogger.Debug($"  hasTable={hasTable}");

        if (hasTable)
        {
            SmartConLogger.Debug("  → ConnectorRadiusExistsInTable...");
            bool exactMatch = lookupSvc.ConnectorRadiusExistsInTable(doc, dynId, connIdx, staticRadius, lookupConstraints);
            SmartConLogger.Debug($"  exactMatch={exactMatch}");

            if (exactMatch)
            {
                SmartConLogger.Debug("  → Exact match in table → Plan(Skip=false, target=staticRadius)");
                SmartConLogger.Info($"[S4] LookupTable: DN{staticDn} found exactly (constraints={lookupConstraints.Count})");
                return new ParameterResolutionPlan(Skip: false, TargetRadius: staticRadius,
                    ExpectNeedsAdapter: false, WarningMessage: null, LookupConstraints: lookupConstraints);
            }

            SmartConLogger.Debug("  → GetNearestAvailableRadius (with constraints)...");
            double nearest = lookupSvc.GetNearestAvailableRadius(doc, dynId, connIdx, staticRadius, lookupConstraints);
            double nearestDn = System.Math.Round(nearest * 2.0 * FeetToMm);
            SmartConLogger.Debug($"  nearest={nearest:F6} ft = DN{nearestDn} (with constraints)");

            if (lookupConstraints.Count > 0)
            {
                SmartConLogger.Debug("  → Pass 2: search WITHOUT constraints...");
                double nearestUnconstrained = lookupSvc.GetNearestAvailableRadius(doc, dynId, connIdx, staticRadius, constraints: null);
                double nearestUncDn = System.Math.Round(nearestUnconstrained * 2.0 * FeetToMm);
                SmartConLogger.Debug($"  nearestUnconstrained={nearestUnconstrained:F6} ft = DN{nearestUncDn}");

                double deltaConstrained = System.Math.Abs(nearest - staticRadius);
                double deltaUnconstrained = System.Math.Abs(nearestUnconstrained - staticRadius);
                SmartConLogger.Debug($"  delta_constrained={deltaConstrained * FeetToMm:F2}mm, delta_unconstrained={deltaUnconstrained * FeetToMm:F2}mm");

                if (deltaUnconstrained < deltaConstrained - eps)
                {
                    SmartConLogger.Debug($"  → Pass 2 BETTER: using unconstrained result DN{nearestUncDn}");
                    SmartConLogger.Info($"[S4] Pass 2 (unconstrained): DN{staticDn} → nearest=DN{nearestUncDn} (other connectors will change)");

                    bool exactUnc = deltaUnconstrained < eps;
                    return new ParameterResolutionPlan(
                        Skip: false, TargetRadius: nearestUnconstrained,
                        ExpectNeedsAdapter: !exactUnc,
                        WarningMessage: exactUnc
                            ? null
                            : $"Размер DN{staticDn} не found exactly. Ближайший DN{nearestUncDn} (other connectors will change).",
                        LookupConstraints: []);
                }
            }

            SmartConLogger.Debug($"  → Pass 1 result: DN{nearestDn} (constraints={lookupConstraints.Count})");
            SmartConLogger.Warn($"[S4] LookupTable: DN{staticDn} not found, nearest=DN{nearestDn} (NeedsAdapter)");
            return new ParameterResolutionPlan(
                Skip: false, TargetRadius: nearest,
                ExpectNeedsAdapter: true,
                WarningMessage: $"Размер DN{staticDn} отсутствует в таблице. Будет выбран DN{nearestDn}, нужен переходник.",
                LookupConstraints: lookupConstraints);
        }

        if (dep is null)
        {
            SmartConLogger.Debug("  → No table and no dep → Plan(NeedsAdapter=true, warning)");
            SmartConLogger.Warn($"[S4] No table, dep=null — S4 failed (NeedsAdapter)");
            return new ParameterResolutionPlan(
                Skip: false, TargetRadius: staticRadius,
                ExpectNeedsAdapter: true,
                WarningMessage: "Не удалось определить параметр размера. Будет вставлен переходник если настроен в маппинге.",
                LookupConstraints: lookupConstraints);
        }

        bool expectAdapter = !dep.IsInstance && dep.Formula is null;
        SmartConLogger.Debug($"  → Dep found: IsInstance={dep.IsInstance}, Formula='{dep.Formula}' → Plan(target=staticRadius, ExpectNeedsAdapter={expectAdapter})");
        SmartConLogger.Info($"[S4] dep found: IsInstance={dep.IsInstance}, Formula='{dep.Formula}', DirectParamName='{dep.DirectParamName}', IsDiameter={dep.IsDiameter}");
        return new ParameterResolutionPlan(Skip: false, TargetRadius: staticRadius,
            ExpectNeedsAdapter: expectAdapter,
            WarningMessage: null,
            LookupConstraints: lookupConstraints);
    }

    private List<LookupColumnConstraint> BuildMultiColumnConstraints(
        Document doc,
        ElementId elementId,
        int currentConnectorIndex)
    {
        var constraints = new List<LookupColumnConstraint>();

        var element = doc.GetElement(elementId);
        if (element is not FamilyInstance)
        {
            SmartConLogger.Debug($"  [MultiCol] element is not FamilyInstance → constraints=[]");
            return constraints;
        }

        var allConns = connectorSvc.GetAllConnectors(doc, elementId);
        SmartConLogger.Debug($"  [MultiCol] BuildMultiColumnConstraints: elementId={elementId.GetValue()}, currentConn={currentConnectorIndex}, allConns={allConns.Count}");
        SmartConLogger.Info($"[S4] [MultiCol] allConns={allConns.Count} for elementId={elementId.GetValue()}, currentConn={currentConnectorIndex}");

        if (allConns.Count <= 1)
        {
            SmartConLogger.Debug($"  [MultiCol] Only 1 connector → constraints=[] (single-port element)");
            return constraints;
        }

        foreach (var conn in allConns)
        {
            if (conn.ConnectorIndex == currentConnectorIndex)
            {
                SmartConLogger.Debug($"    conn[{conn.ConnectorIndex}]: SKIP (current connector)");
                continue;
            }

            var deps = paramResolver.GetConnectorRadiusDependencies(doc, elementId, conn.ConnectorIndex);
            if (deps.Count == 0)
            {
                SmartConLogger.Debug($"    conn[{conn.ConnectorIndex}]: deps=0, radius={conn.Radius * FeetToMm:F2}mm → SKIP (no dep)");
                continue;
            }

            var dep = deps[0];
            var paramName = dep.RootParamName ?? dep.DirectParamName;
            SmartConLogger.Debug($"    conn[{conn.ConnectorIndex}]: RootParam='{dep.RootParamName}', DirectParam='{dep.DirectParamName}', Formula='{dep.Formula}', radius={conn.Radius * FeetToMm:F2}mm");

            if (paramName is null)
            {
                SmartConLogger.Debug($"    conn[{conn.ConnectorIndex}]: paramName=null → SKIP");
                continue;
            }

            var valueMm = System.Math.Round(conn.Radius * 2.0 * FeetToMm);
            constraints.Add(new LookupColumnConstraint(conn.ConnectorIndex, paramName, valueMm));
            SmartConLogger.Debug($"    conn[{conn.ConnectorIndex}]: → CONSTRAINT: param='{paramName}', DN={valueMm}mm");
        }

        SmartConLogger.Debug($"  [MultiCol] Total constraints: {constraints.Count}");
        return constraints;
    }
}
