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

public sealed partial class ChainOperationHandler
{
    /// <summary>
    /// DN compensation strategy chain (ADR-053), in priority order:
    /// 1. TRANSITION — the element itself becomes a transition fitting (only the
    ///    parent-facing port changes, other ports keep their DN, downstream untouched);
    /// 2. REDUCER — insert a reducer from the CTC mapping (element fully untouched);
    /// 3. RESIZE — classic uniform resize, cascade continues to the next level,
    ///    where strategies 1–2 are tried again (cascade stops at the first
    ///    DN-absorbing element — mirrors pipe length absorption, #139).
    /// </summary>
    private (ElementId? ReducerId, bool SizeChanged) AdjustElementSize(
        Document doc,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        ElementId elemId,
        ParentEdge edge,
        ConnectorProxy parentProxy,
        IReadOnlyDictionary<long, IReadOnlyList<FamilySizeOption>> transitionOptions)
    {
        ElementId? reducerId = null;
        double targetRadius = parentProxy.Radius;
        double targetDn = System.Math.Round(targetRadius * 2.0 * FeetToMm);

        var elemRefreshed = connSvc.RefreshConnector(doc, elemId, edge.ElemConnIdx);
        double elemRadius = elemRefreshed?.Radius ?? 0;
        double elemDn = System.Math.Round(elemRadius * 2.0 * FeetToMm);
        double delta = System.Math.Abs(targetRadius - elemRadius);

        SmartConLogger.Debug($"    c. AdjustSize: target={targetRadius * FeetToMm:F2}mm (DN{targetDn}), " +
            $"elem={elemRadius * FeetToMm:F2}mm (DN{elemDn}), delta={delta * FeetToMm:F4}mm, needsAdjust={delta > 1e-5}");

        if (elemRefreshed is null || delta <= 1e-5)
        {
            SmartConLogger.Debug($"    c. Sizes match, no adjustment needed");
            doc.Regenerate();
            return (null, false);
        }

        // ── 1. TRANSITION: только порт к родителю меняет DN, остальные сохраняются ──
        if (doc.GetElement(elemId) is FamilyInstance
            && TryApplyTransitionSize(doc, elemId, edge.ElemConnIdx, targetRadius, transitionOptions))
        {
            elemRefreshed = connSvc.RefreshConnector(doc, elemId, edge.ElemConnIdx);
            if (elemRefreshed is not null && System.Math.Abs(targetRadius - elemRefreshed.Radius) <= 1e-5)
            {
                SmartConLogger.Debug($"    c. Transition verified: DN{targetDn}, downstream DN preserved");
                doc.Regenerate();
                return (null, true);
            }
            SmartConLogger.Debug($"    c. Transition verify failed → next strategy");
        }

        // ── 2. REDUCER: элемент и сеть полностью не трогаем ──
        if (doc.GetElement(elemId) is FamilyInstance && elemRefreshed is not null)
        {
            reducerId = InsertReducerForMismatch(doc, snapshotStore, elemId, parentProxy, elemRefreshed);
            if (reducerId is not null)
            {
                doc.Regenerate();
                return (reducerId, true);
            }
        }

        // ── 3. RESIZE: классический равномерный resize (каскад) ──
        SmartConLogger.Debug($"    c.3 Resize: TrySetConnectorRadius(elemId={elemId.GetValue()}, " +
            $"connIdx={edge.ElemConnIdx}, target={targetRadius * FeetToMm:F2}mm)...");
        bool setResult = paramResolver.TrySetConnectorRadius(
            doc, elemId, edge.ElemConnIdx, targetRadius);
        SmartConLogger.Debug($"    c.3 TrySetConnectorRadius → {(setResult ? "OK" : "FAILED")}");

        doc.Regenerate();

        AdjustRelatedFamilyConnectors(doc, graph, elemId, edge.ElemConnIdx, targetRadius);

        doc.Regenerate();

        LogAdjustedFamilyDiagnostics(doc, elemId);

        elemRefreshed = connSvc.RefreshConnector(doc, elemId, edge.ElemConnIdx);
        double actualRadius = elemRefreshed?.Radius ?? 0;
        double actualDn = System.Math.Round(actualRadius * 2.0 * FeetToMm);
        double verifyDelta = System.Math.Abs(targetRadius - actualRadius);

        SmartConLogger.Debug($"    c.3 Verification: actualR={actualRadius * FeetToMm:F2}mm " +
            $"(DN{actualDn}), targetR={targetRadius * FeetToMm:F2}mm (DN{targetDn}), " +
            $"delta={verifyDelta * FeetToMm:F4}mm, match={verifyDelta <= 1e-5}");

        if (elemRefreshed is not null && verifyDelta > 1e-5)
            reducerId = InsertReducerForMismatch(doc, snapshotStore, elemId, parentProxy, elemRefreshed);

        doc.Regenerate();
        return (reducerId, true);
    }

    /// <summary>
    /// TRANSITION strategy: apply a family configuration where only the
    /// parent-facing port changes DN and all other ports keep theirs
    /// (TransitionSizeMatcher over prefetched lookup/symbol configurations).
    /// Returns true when a configuration was applied (caller verifies the result).
    /// </summary>
    private bool TryApplyTransitionSize(
        Document doc,
        ElementId elemId,
        int primaryConnIdx,
        double targetRadius,
        IReadOnlyDictionary<long, IReadOnlyList<FamilySizeOption>> transitionOptions)
    {
        if (!transitionOptions.TryGetValue(elemId.GetValue(), out var options) || options.Count == 0)
            return false;

        var currentConns = connSvc.GetAllConnectors(doc, elemId);
        var currentRadii = new Dictionary<int, double>();
        foreach (var c in currentConns)
            currentRadii[c.ConnectorIndex] = c.Radius;

        var option = TransitionSizeMatcher.FindBestTransition(options, targetRadius, primaryConnIdx, currentRadii);
        if (option is null)
        {
            SmartConLogger.Debug($"    c.1 Transition: no config with target DN{System.Math.Round(targetRadius * 2.0 * FeetToMm)} " +
                $"→ next strategy");
            return false;
        }

        double otherDelta = TransitionSizeMatcher.OtherPortsDelta(option, primaryConnIdx, currentRadii);
        SmartConLogger.Debug($"    c.1 Transition: '{option.DisplayName}' " +
            $"(other ports delta={otherDelta * FeetToMm:F2}mm)");

        bool applied = PipeConnectSizeHandler.ApplyQueryParamsIfExists(doc, elemId, option);
        if (!applied)
        {
            foreach (var kvp in option.AllConnectorRadii)
            {
                if (!currentRadii.TryGetValue(kvp.Key, out var curR)
                    || System.Math.Abs(kvp.Value - curR) > 1e-5)
                {
                    paramResolver.TrySetConnectorRadius(doc, elemId, kvp.Key, kvp.Value);
                }
            }
        }

        doc.Regenerate();
        return true;
    }

    private void AdjustRelatedFamilyConnectors(
        Document doc,
        ConnectionGraph graph,
        ElementId elemId,
        int primaryConnectorIndex,
        double targetRadius)
    {
        var elem = doc.GetElement(elemId);
        if (elem is not FamilyInstance fiElem)
            return;

        var allElemConns = connSvc.GetAllConnectors(doc, elemId);
        SmartConLogger.Debug($"    c.2 FamilyInstance '{fiElem.Symbol?.Family?.Name}' " +
            $"symbolId={fiElem.Symbol?.Id.GetValue()}: {allElemConns.Count} коннекторов (после Regenerate):");
        foreach (var c in allElemConns)
            SmartConLogger.Debug($"      conn[{c.ConnectorIndex}]: R={c.Radius * FeetToMm:F2}mm, " +
                $"isFree={c.IsFree}");

        foreach (var c in allElemConns)
        {
            if (c.ConnectorIndex == primaryConnectorIndex)
                continue;

            double connDelta = System.Math.Abs(c.Radius - targetRadius);
            if (connDelta <= 1e-5)
            {
                SmartConLogger.Debug($"    c.2 conn[{c.ConnectorIndex}]: " +
                    $"R={c.Radius * FeetToMm:F2}mm ≈ target — уже верно, skip");
                continue;
            }

            if (!IsConnectorInGraph(graph, elemId, c.ConnectorIndex))
            {
                SmartConLogger.Debug($"    c.2 conn[{c.ConnectorIndex}]: NOT in graph, " +
                    $"R={c.Radius * FeetToMm:F2}mm ≠ target {targetRadius * FeetToMm:F2}mm — пропущен");
                continue;
            }

            // ADR-053: трогаем только порты, чей DN-параметр общий с primary
            // (физически не могут отличаться). Независимые порты сохраняют свой DN —
            // именно так семейство становится переходным вместо равномерного resize.
            if (!DnParamsShared(doc, elemId, primaryConnectorIndex, c.ConnectorIndex))
            {
                SmartConLogger.Debug($"    c.2 conn[{c.ConnectorIndex}]: independent DN param " +
                    $"(R={c.Radius * FeetToMm:F2}mm) — сохранён");
                continue;
            }

            SmartConLogger.Debug($"    c.2 TrySetConnectorRadius(connIdx={c.ConnectorIndex}, " +
                $"currentR={c.Radius * FeetToMm:F2}mm, target={targetRadius * FeetToMm:F2}mm)...");
            bool r2 = paramResolver.TrySetConnectorRadius(doc, elemId, c.ConnectorIndex, targetRadius);
            SmartConLogger.Debug($"    c.2 → {(r2 ? "OK" : "FAILED")}");
        }
    }

    /// <summary>
    /// True when two connectors of the same element are driven by the same DN
    /// parameter (changing one inevitably changes the other). Compared via
    /// ParameterDependency names from GetConnectorRadiusDependencies.
    /// Unknown dependencies → conservative true (shared).
    /// </summary>
    private bool DnParamsShared(Document doc, ElementId elemId, int connIdxA, int connIdxB)
    {
        var depsA = paramResolver.GetConnectorRadiusDependencies(doc, elemId, connIdxA);
        var depsB = paramResolver.GetConnectorRadiusDependencies(doc, elemId, connIdxB);
        if (depsA.Count == 0 || depsB.Count == 0)
            return true;

        var depA = depsA[0];
        var depB = depsB[0];

        if (depA.BuiltIn is not null || depB.BuiltIn is not null)
            return depA.BuiltIn == depB.BuiltIn;

        string? keyA = depA.DirectParamName ?? depA.RootParamName;
        string? keyB = depB.DirectParamName ?? depB.RootParamName;
        if (keyA is null || keyB is null)
            return true;

        return string.Equals(keyA, keyB, StringComparison.Ordinal);
    }

    private bool IsConnectorInGraph(ConnectionGraph graph, ElementId elemId, int connectorIndex)
    {
        var comparer = ElementIdEqualityComparer.Instance;
        foreach (var e in graph.Edges)
        {
            if ((comparer.Equals(e.FromElementId, elemId) && e.FromConnectorIndex == connectorIndex) ||
                (comparer.Equals(e.ToElementId, elemId) && e.ToConnectorIndex == connectorIndex))
            {
                return true;
            }
        }

        return false;
    }

    private void LogAdjustedFamilyDiagnostics(Document doc, ElementId elemId)
    {
        if (doc.GetElement(elemId) is not FamilyInstance)
            return;

        var diagConns = connSvc.GetAllConnectors(doc, elemId);
        foreach (var dc in diagConns)
            SmartConLogger.Debug($"    c.2b diag conn[{dc.ConnectorIndex}]: " +
                $"R={dc.Radius * FeetToMm:F2}mm (DN{System.Math.Round(dc.Radius * 2.0 * FeetToMm)}), " +
                $"isFree={dc.IsFree}");
    }

    private ElementId? InsertReducerForMismatch(
        Document doc,
        NetworkSnapshotStore snapshotStore,
        ElementId elemId,
        ConnectorProxy parentProxy,
        ConnectorProxy elemProxy)
    {
        SmartConLogger.Debug($"    c.3 Adjustment failed → InsertReducer...");
        var reducerId = networkMover.InsertReducer(doc, parentProxy, elemProxy);
        if (reducerId is not null)
        {
            SmartConLogger.Debug($"    c.3 Reducer inserted: id={reducerId.GetValue()}");
            snapshotStore.TrackReducer(elemId, reducerId);
        }
        else
        {
            SmartConLogger.Warn($"    c.3 Reducer not found in mapping! " +
                $"[Action: добавьте семейство переходника в маппинг (Настройки → Правила) — иначе элементы с разными DN соединятся напрямую]");
        }

        return reducerId;
    }
}
