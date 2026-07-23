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

#pragma warning disable CS9113
/// <summary>
/// Handles increment/decrement of chain depth in the PipeConnect editor.
/// IncrementLevel: disconnect, resize, align, and reconnect child elements.
/// DecrementLevel: rollback to snapshot state, delete inserted reducers.
/// </summary>
public sealed class ChainOperationHandler(
    IConnectorService connSvc,
    ITransformService transformSvc,
    IParameterResolver paramResolver,
    IFittingInsertService fittingInsertSvc,
    INetworkMover networkMover,
    IAlignmentService alignmentSvc,
    IDynamicSizeResolver sizeResolver)
{
#pragma warning restore CS9113
    /// <summary>Describes the edge from an element to its parent in the chain graph.</summary>
    public record struct ParentEdge(ElementId ParentId, int ParentConnIdx, int ElemConnIdx);

    /// <summary>
    /// Process the next chain level: snapshot elements, disconnect, resize, align, reconnect.
    /// Inserts reducers when radius adjustment fails.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="groupSession">Active transaction group session.</param>
    /// <param name="graph">Chain graph with element levels.</param>
    /// <param name="snapshotStore">Store for element snapshots (for rollback).</param>
    /// <param name="warmedElementIds">Set of already-warmed element IDs.</param>
    /// <param name="nextLevel">BFS level index to process.</param>
    /// <returns>
    /// True when at least one element of the level actually changed (moved, rotated,
    /// resized, reducer inserted, or absorbed by pipe length). False = idle level —
    /// only disconnect/reconnect churn; callers may auto-skip such levels.
    /// </returns>
    public bool IncrementLevel(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        HashSet<long> warmedElementIds,
        int nextLevel)
    {
        using var _scope = SmartConLogger.BeginScope("Chain+",
            ("Method", "IncrementLevel"),
            ("Level", nextLevel));

        var levelElements = graph.Levels[nextLevel];

        SmartConLogger.Debug($"═══ LEVEL {nextLevel} ═══ ({levelElements.Count} elements)");

        WarmDepsForLevel(doc, levelElements, warmedElementIds);

        foreach (var elemId in levelElements)
            SaveSnapshotForLevelElement(doc, elemId, graph, snapshotStore);

        var transitionOptions = PrefetchTransitionOptions(doc, graph, nextLevel, levelElements);

        bool anyWork = false;
        groupSession.RunInTransaction(string.Format(LocalizationService.GetString("Tx_ChainLevel"), nextLevel), doc =>
        {
            int elemIndex = 0;
            foreach (var elemId in levelElements)
            {
                elemIndex++;
                anyWork |= ProcessIncrementElement(doc, graph, snapshotStore, nextLevel, elemId, elemIndex, levelElements.Count, transitionOptions);
            }

            doc.Regenerate();
        });

        SmartConLogger.Debug($"═══ LEVEL {nextLevel} DONE ═══ (anyWork={anyWork})");
        return anyWork;
    }

    /// <summary>
    /// Pre-fetch available size configurations for FamilyInstance elements that
    /// mismatch their parent radius. Must run OUTSIDE a transaction
    /// (GetAvailableFamilySizes uses EditFamily, which requires IsModifiable == false).
    /// Used by the TRANSITION strategy inside the level transaction (ADR-053).
    /// </summary>
    private Dictionary<long, IReadOnlyList<FamilySizeOption>> PrefetchTransitionOptions(
        Document doc,
        ConnectionGraph graph,
        int nextLevel,
        IReadOnlyList<ElementId> levelElements)
    {
        var result = new Dictionary<long, IReadOnlyList<FamilySizeOption>>();
        foreach (var elemId in levelElements)
        {
            if (doc.GetElement(elemId) is not FamilyInstance)
                continue;

            var edge = FindEdgeToParent(elemId, nextLevel, graph);
            if (edge is null)
                continue;

            var parentProxy = connSvc.RefreshConnector(doc, edge.Value.ParentId, edge.Value.ParentConnIdx);
            var elemProxy = connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);
            if (parentProxy is null || elemProxy is null)
                continue;

            if (System.Math.Abs(parentProxy.Radius - elemProxy.Radius) <= 1e-5)
                continue;

            try
            {
                var options = sizeResolver.GetAvailableFamilySizes(doc, elemId, edge.Value.ElemConnIdx);
                if (options.Count > 0)
                {
                    result[elemId.GetValue()] = options;
                    SmartConLogger.Debug($"  Prefetch: elemId={elemId.GetValue()}, {options.Count} size options");
                }
            }
            catch (Exception exPrefetch)
            {
                SmartConLogger.Warn($"  Prefetch sizes failed for {elemId.GetValue()}: {exPrefetch.Message} " +
                    $"[Action: переходная конфигурация недоступна, будет классический resize]");
            }
        }
        return result;
    }

    /// <summary>
    /// Rollback a chain level: restore elements to their snapshot state,
    /// delete inserted reducers, and reconnect original connections.
    /// Per-level absorption (ADR-052) touches only the level's own elements,
    /// so rollback restores exactly this level — symmetric with increment.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="groupSession">Active transaction group session.</param>
    /// <param name="graph">Chain graph.</param>
    /// <param name="snapshotStore">Snapshot store with saved element states.</param>
    /// <param name="currentDepth">BFS level to roll back.</param>
    public void DecrementLevel(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        int currentDepth)
    {
        using var _scope = SmartConLogger.BeginScope("Chain-",
            ("Method", "DecrementLevel"),
            ("Level", currentDepth));

        var levelElements = graph.Levels[currentDepth];

        SmartConLogger.Debug($"═══ ROLLBACK LEVEL {currentDepth} ═══ ({levelElements.Count} elements)");

        groupSession.RunInTransaction(string.Format(LocalizationService.GetString("Tx_ChainRollback"), currentDepth), doc =>
        {
            foreach (var elemId in levelElements)
                RollbackElement(doc, graph, snapshotStore, currentDepth, elemId);

            doc.Regenerate();
        });
    }

    /// <summary>
    /// Try to "seal" the chain early (ADR-052): when the displacement is already
    /// fully absorbed and the remaining downstream needs no resize work, the
    /// boundary edges (currentDepth → currentDepth+1) are reconnected and the
    /// rest of the network is left completely untouched — no per-level churn.
    /// Returns true when the chain is sealed (caller disables further increments).
    /// A level is "quiet" when every boundary child has zero alignment offset,
    /// no required rotation and a matching radius, and every deeper piping edge
    /// has matching radii (a resize deeper would require classic processing).
    /// </summary>
    public bool TrySealQuietChain(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        int currentDepth)
    {
        if (currentDepth + 1 >= graph.Levels.Count)
            return true;

        var levelOf = new Dictionary<long, int>();
        for (int level = 0; level < graph.Levels.Count; level++)
            foreach (var id in graph.Levels[level])
                levelOf[id.GetValue()] = level;

        var boundaryEdges = new List<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)>();

        foreach (var childId in graph.Levels[currentDepth + 1])
        {
            var edge = FindEdgeToParent(childId, currentDepth + 1, graph);
            if (edge is null)
                return false;

            var parentProxy = connSvc.RefreshConnector(doc, edge.Value.ParentId, edge.Value.ParentConnIdx);
            var childProxy = connSvc.RefreshConnector(doc, childId, edge.Value.ElemConnIdx);
            if (parentProxy is null || childProxy is null)
                continue;

            if (parentProxy.Domain != Domain.DomainPiping || childProxy.Domain != Domain.DomainPiping)
                continue;

            double radiusDelta = System.Math.Abs(parentProxy.Radius - childProxy.Radius);
            if (radiusDelta > 1e-5)
                return false;

            var alignResult = ConnectorAligner.ComputeAlignment(
                parentProxy.OriginVec3, parentProxy.BasisZVec3, parentProxy.BasisXVec3,
                childProxy.OriginVec3, childProxy.BasisZVec3, childProxy.BasisXVec3);

            if (!VectorUtils.IsZero(alignResult.InitialOffset)
                || alignResult.BasisZRotation is not null
                || alignResult.BasisXSnap is not null)
                return false;

            boundaryEdges.Add((edge.Value.ParentId, edge.Value.ParentConnIdx, childId, edge.Value.ElemConnIdx));
        }

        foreach (var edge in graph.Edges)
        {
            if (!levelOf.TryGetValue(edge.FromElementId.GetValue(), out int fromLevel)
                || !levelOf.TryGetValue(edge.ToElementId.GetValue(), out int toLevel)
                || fromLevel <= currentDepth
                || toLevel <= currentDepth)
                continue;

            var fromProxy = connSvc.RefreshConnector(doc, edge.FromElementId, edge.FromConnectorIndex);
            var toProxy = connSvc.RefreshConnector(doc, edge.ToElementId, edge.ToConnectorIndex);
            if (fromProxy is null || toProxy is null)
                continue;

            if (fromProxy.Domain != Domain.DomainPiping || toProxy.Domain != Domain.DomainPiping)
                continue;

            double radiusDelta = System.Math.Abs(fromProxy.Radius - toProxy.Radius);
            if (radiusDelta > 1e-5)
            {
                SmartConLogger.Debug($"Seal: radius mismatch at deeper edge " +
                    $"{edge.FromElementId.GetValue()}↔{edge.ToElementId.GetValue()} " +
                    $"({radiusDelta * FeetToMm:F2}mm) → classic flow");
                return false;
            }
        }

        groupSession.RunInTransaction(LocalizationService.GetString("Tx_ChainSeal"), doc =>
        {
            foreach (var (parentId, parentConnIdx, childId, childConnIdx) in boundaryEdges)
            {
                try
                {
                    connSvc.ConnectTo(doc, parentId, parentConnIdx, childId, childConnIdx);
                }
                catch (Exception exConn)
                {
                    SmartConLogger.Warn($"Seal: ConnectTo {parentId.GetValue()}↔{childId.GetValue()} " +
                        $"failed: {exConn.Message} [Action: подключите границу сети вручную]");
                }
            }
            doc.Regenerate();
        });

        SmartConLogger.Info($"Seal: chain sealed at level {currentDepth}, " +
            $"boundary edges={boundaryEdges.Count}, deeper levels untouched");
        return true;
    }

    private void SaveSnapshotForLevelElement(
        Document doc,
        ElementId elemId,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore)
    {
        var snapshot = CaptureSnapshot(doc, elemId, graph);
        snapshotStore.Save(snapshot);
        SmartConLogger.Debug($"  Snapshot: elemId={elemId.GetValue()}, " +
            $"isMepCurve={snapshot.IsMepCurve}, " +
            $"R={snapshot.ConnectorRadius * FeetToMm:F2}mm (DN{System.Math.Round(snapshot.ConnectorRadius * 2.0 * FeetToMm)}), " +
            $"symbolId={snapshot.FamilySymbolId?.GetValue()}, connections={snapshot.Connections.Count}");
    }

    private bool ProcessIncrementElement(
        Document doc,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        int nextLevel,
        ElementId elemId,
        int elemIndex,
        int levelCount,
        IReadOnlyDictionary<long, IReadOnlyList<FamilySizeOption>> transitionOptions)
    {
        LogIncrementElementHeader(doc, elemId, elemIndex, levelCount);

        DisconnectElementConnections(doc, elemId);

        var edge = FindEdgeToParent(elemId, nextLevel, graph);
        if (edge is null)
        {
            SmartConLogger.Warn($"b. Edge to parent NOT FOUND → skip");
            return false;
        }

        SmartConLogger.Debug($"b. Edge: parent={edge.Value.ParentId.GetValue()} " +
            $"parentConnIdx={edge.Value.ParentConnIdx}, elemConnIdx={edge.Value.ElemConnIdx}");

        var parentProxy = connSvc.RefreshConnector(doc, edge.Value.ParentId, edge.Value.ParentConnIdx);
        if (parentProxy is null)
        {
            SmartConLogger.Warn($"parentProxy=NULL → skip");
            return false;
        }

        SmartConLogger.Debug($"parent: R={parentProxy.Radius * FeetToMm:F2}mm " +
            $"(DN{System.Math.Round(parentProxy.Radius * 2.0 * FeetToMm)}) " +
            $"origin=({parentProxy.Origin.X:F4},{parentProxy.Origin.Y:F4},{parentProxy.Origin.Z:F4})");

        var (reducerId, sizeChanged) = AdjustElementSize(doc, graph, snapshotStore, elemId, edge.Value, parentProxy, transitionOptions);

        parentProxy = connSvc.RefreshConnector(doc, edge.Value.ParentId, edge.Value.ParentConnIdx);
        var elemProxyForAlign = connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);
        var alignTarget = ResolveAlignTarget(doc, reducerId, parentProxy);

        AlignmentResult? alignResult = null;
        if (alignTarget is not null && elemProxyForAlign is not null)
        {
            alignResult = ConnectorAligner.ComputeAlignment(
                alignTarget.OriginVec3, alignTarget.BasisZVec3, alignTarget.BasisXVec3,
                elemProxyForAlign.OriginVec3, elemProxyForAlign.BasisZVec3, elemProxyForAlign.BasisXVec3);
        }

        bool alignmentChanged = alignResult is not null
            && (!VectorUtils.IsZero(alignResult.InitialOffset)
                || alignResult.BasisZRotation is not null
                || alignResult.BasisXSnap is not null);

        if (!TryAlignPipeByLength(doc, elemId, elemProxyForAlign, alignResult))
            AlignElement(doc, elemId, elemProxyForAlign, alignResult);
        ReconnectIncrementElement(doc, elemId, edge.Value, parentProxy, reducerId);

        SmartConLogger.Debug($"── Element {elemId.GetValue()} ready ──");
        return sizeChanged || reducerId is not null || alignmentChanged;
    }

    private void LogIncrementElementHeader(Document doc, ElementId elemId, int elemIndex, int levelCount)
    {
        var elemRaw = doc.GetElement(elemId);
        string elemName = elemRaw?.Name ?? "?";
        string elemType = elemRaw?.GetType().Name ?? "?";
        SmartConLogger.Debug($"  ── Element {elemIndex}/{levelCount}: " +
            $"id={elemId.GetValue()} '{elemName}' ({elemType}) ──");
    }

    private void DisconnectElementConnections(Document doc, ElementId elemId)
    {
        var allConns = connSvc.GetAllConnectors(doc, elemId);
        int disconnected = 0;
        foreach (var c in allConns)
        {
            if (!c.IsFree)
            {
                SmartConLogger.Debug($"a. Disconnect connIdx={c.ConnectorIndex} " +
                    $"(R={c.Radius * FeetToMm:F2}mm)");

                connSvc.DisconnectAllFromConnector(doc, elemId, c.ConnectorIndex);
                disconnected++;
            }
        }

        SmartConLogger.Debug($"a. Disconnect done: {disconnected} connections broken, всего коннекторов={allConns.Count}");
    }

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
            SmartConLogger.Warn($"    c.3 Reducer not found in mapping!");
        }

        return reducerId;
    }

    private ConnectorProxy? ResolveAlignTarget(Document doc, ElementId? reducerId, ConnectorProxy? parentProxy)
    {
        ConnectorProxy? alignTarget = parentProxy;

        if (reducerId is not null)
        {
            var rConns = connSvc.GetAllFreeConnectors(doc, reducerId);
            if (rConns.Count >= 2 && parentProxy is not null)
            {
                var rConn1 = rConns
                    .OrderBy(rc => VectorUtils.DistanceTo(rc.OriginVec3, parentProxy.OriginVec3))
                    .First();
                alignTarget = rConns.FirstOrDefault(rc => rc.ConnectorIndex != rConn1.ConnectorIndex);
                SmartConLogger.Debug($"    d. Align target = reducer conn2 " +
                    $"(R={alignTarget?.Radius * FeetToMm:F2}mm, " +
                    $"origin=({alignTarget?.Origin.X:F4},{alignTarget?.Origin.Y:F4},{alignTarget?.Origin.Z:F4}))");
            }
        }

        return alignTarget;
    }

    private void AlignElement(Document doc, ElementId elemId, ConnectorProxy? elemProxyForAlign, AlignmentResult? alignResult)
    {
        if (alignResult is null || elemProxyForAlign is null)
            return;

        SmartConLogger.Debug($"    d. Align: offset={VectorUtils.Length(alignResult.InitialOffset) * FeetToMm:F1}mm");

        alignmentSvc.ApplyAlignment(doc, elemId, alignResult, elemProxyForAlign.ConnectorIndex);
    }

    /// <summary>
    /// Per-level displacement absorption (ADR-052): when the element being aligned
    /// is itself a straight pipe or a flex pipe and its alignment is a pure
    /// translation, the pipe changes its length/path instead of moving as a rigid
    /// body (PipeAbsorptionApplier). The remainder propagates to the next level
    /// through the classic flow.
    /// Returns true when the pipe was aligned by geometry change; false → caller
    /// falls back to the classic rigid <see cref="AlignElement"/>.
    /// </summary>
    private bool TryAlignPipeByLength(
        Document doc,
        ElementId elemId,
        ConnectorProxy? elemProxyForAlign,
        AlignmentResult? alignResult)
    {
        if (alignResult is null || elemProxyForAlign is null)
            return false;

        if (alignResult.BasisZRotation is not null || alignResult.BasisXSnap is not null)
        {
            SmartConLogger.Debug($"    d. Absorb: rotation required → rigid align");
            return false;
        }

        var offset = alignResult.InitialOffset;
        if (VectorUtils.IsZero(offset))
            return false;

        return PipeAbsorptionApplier.TryApply(doc, elemId, elemProxyForAlign.OriginVec3, offset);
    }

    private void ReconnectIncrementElement(
        Document doc,
        ElementId elemId,
        ParentEdge edge,
        ConnectorProxy? parentProxy,
        ElementId? reducerId)
    {
        if (reducerId is not null && parentProxy is not null)
        {
            SmartConLogger.Debug($"    e. ConnectTo via reducer id={reducerId.GetValue()}");
            var rConnsForConnect = connSvc.GetAllFreeConnectors(doc, reducerId);
            SmartConLogger.Debug($"    e. Reducer free conns: {rConnsForConnect.Count}");
            var rConn1 = rConnsForConnect
                .OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, parentProxy.OriginVec3))
                .FirstOrDefault();
            var rConn2 = rConnsForConnect.FirstOrDefault(c => c.ConnectorIndex != (rConn1?.ConnectorIndex ?? -1));

            if (rConn1 is not null)
            {
                SmartConLogger.Debug($"    e. ConnectTo: parent({edge.ParentId.GetValue()}:{edge.ParentConnIdx}) ↔ reducer({reducerId.GetValue()}:{rConn1.ConnectorIndex})");
                connSvc.ConnectTo(doc, edge.ParentId, edge.ParentConnIdx,
                    reducerId, rConn1.ConnectorIndex);
            }
            if (rConn2 is not null)
            {
                SmartConLogger.Debug($"    e. ConnectTo: reducer({reducerId.GetValue()}:{rConn2.ConnectorIndex}) ↔ elem({elemId.GetValue()}:{edge.ElemConnIdx})");
                connSvc.ConnectTo(doc, reducerId, rConn2.ConnectorIndex,
                    elemId, edge.ElemConnIdx);
            }

            return;
        }

        SmartConLogger.Debug($"    e. ConnectTo direct: parent({edge.ParentId.GetValue()}:{edge.ParentConnIdx}) ↔ elem({elemId.GetValue()}:{edge.ElemConnIdx})");
        connSvc.ConnectTo(doc, edge.ParentId, edge.ParentConnIdx,
            elemId, edge.ElemConnIdx);
    }

    private void RollbackElement(
        Document doc,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        int currentDepth,
        ElementId elemId)
    {
        var elemRaw = doc.GetElement(elemId);
        SmartConLogger.Debug($" ── Element id={elemId.GetValue()} '{elemRaw?.Name}' ({elemRaw?.GetType().Name}) ──");

        DisconnectElementConnections(doc, elemId);
        DeleteTrackedReducers(doc, snapshotStore, elemId);

        var snapshot = snapshotStore.Get(elemId);
        if (snapshot is null)
        {
            SmartConLogger.Warn($"   c. Snapshot not found → skip");
            return;
        }

        RestoreElementFromSnapshot(doc, elemId, snapshot);
        ReconnectSnapshotConnections(doc, elemId, snapshot, currentDepth - 1, graph);
    }

    private void DeleteTrackedReducers(Document doc, NetworkSnapshotStore snapshotStore, ElementId elemId)
    {
        var reducers = snapshotStore.GetReducers(elemId);
        SmartConLogger.Debug($"   b. Reducers to delete: {reducers.Count}");
        foreach (var reducerId in reducers)
        {
            SmartConLogger.Debug($"   b. Deleting reducer id={reducerId.GetValue()}");
            var rConns = connSvc.GetAllConnectors(doc, reducerId);
            foreach (var rc in rConns)
            {
                if (!rc.IsFree)
                    connSvc.DisconnectAllFromConnector(doc, reducerId, rc.ConnectorIndex);
            }
            fittingInsertSvc.DeleteElement(doc, reducerId);
        }
    }

    private void RestoreElementFromSnapshot(Document doc, ElementId elemId, ElementSnapshot snapshot)
    {
        var elem = doc.GetElement(elemId);
        SmartConLogger.Debug($"   c. Restoring: isMepCurve={snapshot.IsMepCurve}, " +
            $"snapR={snapshot.ConnectorRadius * FeetToMm:F2}mm (DN{System.Math.Round(snapshot.ConnectorRadius * 2.0 * FeetToMm)}), " +
            $"symbolId={snapshot.FamilySymbolId?.GetValue()}");

        if (elem is MEPCurve mc)
            RestoreMepCurve(doc, elemId, mc, snapshot);
        else if (elem is FamilyInstance fi && snapshot.FiOrigin is not null)
            RestoreFamilyInstance(doc, elemId, fi, snapshot);
    }

    /// <summary>
    /// Pre-warm parameter dependency cache for elements at the given level.
    /// Avoids repeated cold cache lookups during IncrementLevel.
    /// </summary>
    public void WarmDepsForLevel(
        Document doc,
        IReadOnlyList<ElementId> levelElements,
        HashSet<long> warmedElementIds)
    {
        int warmedCount = 0;
        foreach (var elemId in levelElements)
        {
            if (!warmedElementIds.Add(elemId.GetValue()))
                continue;

            var elem = doc.GetElement(elemId);
            if (elem is null) continue;

            var cm = elem switch
            {
                FamilyInstance fi => fi.MEPModel?.ConnectorManager,
                MEPCurve mc => mc.ConnectorManager,
                _ => null
            };
            if (cm is null) continue;

            foreach (Connector c in cm.Connectors)
            {
                if (c.ConnectorType == ConnectorType.Curve) continue;
                paramResolver.GetConnectorRadiusDependencies(doc, elemId, c.Id);
            }
            warmedCount++;
        }
        if (warmedCount > 0)
            SmartConLogger.Debug($"WarmDeps: warmed {warmedCount} elements for level");
    }

    /// <summary>Capture a full snapshot of an element's state for rollback.</summary>
    public ElementSnapshot CaptureSnapshot(Document doc, ElementId elemId, ConnectionGraph graph)
    {
        var elem = doc.GetElement(elemId);
        bool isMepCurve = elem is MEPCurve;

        XYZ? fiOrigin = null, fiBasisX = null, fiBasisY = null, fiBasisZ = null;
        XYZ? curveStart = null, curveEnd = null;
        ElementId? familySymbolId = null;

        if (elem is FamilyInstance fi)
        {
            var t = fi.GetTransform();
            fiOrigin = t.Origin;
            fiBasisX = t.BasisX;
            fiBasisY = t.BasisY;
            fiBasisZ = t.BasisZ;
            familySymbolId = fi.Symbol.Id;
        }

        if (elem is MEPCurve mc && mc.Location is LocationCurve lc && lc.Curve is Line line)
        {
            curveStart = line.GetEndPoint(0);
            curveEnd = line.GetEndPoint(1);
        }

        IReadOnlyList<XYZ>? flexPoints = null;
        if (elem is FlexPipe flexPipe)
        {
            var pts = flexPipe.Points;
            var copy = new List<XYZ>(pts.Count);
            copy.AddRange(pts);
            flexPoints = copy;
        }

        double connRadius = 0;
        XYZ? firstConnOrigin = null;
        int firstConnIdx = -1;
        var connRadiiDict = new Dictionary<int, double>();
        var conns = connSvc.GetAllConnectors(doc, elemId);
        if (conns.Count > 0)
        {
            connRadius = conns[0].Radius;
            firstConnOrigin = conns[0].Origin;
            firstConnIdx = conns[0].ConnectorIndex;
        }
        foreach (var c in conns)
            connRadiiDict[c.ConnectorIndex] = c.Radius;

        return new ElementSnapshot
        {
            ElementId = elemId,
            IsMepCurve = isMepCurve,
            FiOrigin = fiOrigin,
            FiBasisX = fiBasisX,
            FiBasisY = fiBasisY,
            FiBasisZ = fiBasisZ,
            CurveStart = curveStart,
            CurveEnd = curveEnd,
            FlexPoints = flexPoints,
            FirstConnectorOrigin = firstConnOrigin,
            FirstConnectorIndex = firstConnIdx,
            ConnectorRadius = connRadius,
            ConnectorRadii = connRadiiDict,
            FamilySymbolId = familySymbolId,
            Connections = graph.GetOriginalConnections(elemId),
        };
    }

    private void RestoreMepCurve(Document doc, ElementId elemId, MEPCurve mc, ElementSnapshot snapshot)
    {
        var diamParam = mc.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
        if (diamParam is not null && !diamParam.IsReadOnly)
        {
            double targetDiam = snapshot.ConnectorRadius * 2.0;
            SmartConLogger.Debug($"   c. MEPCurve: restore diameter={targetDiam * FeetToMm:F2}mm");
            diamParam.Set(targetDiam);
        }
        else
        {
            SmartConLogger.Debug($"   c. MEPCurve: TrySetConnectorRadius fallback...");
            var conns = connSvc.GetAllConnectors(doc, elemId);
            if (conns.Count > 0)
                paramResolver.TrySetConnectorRadius(doc, elemId, conns[0].ConnectorIndex, snapshot.ConnectorRadius);
        }
        doc.Regenerate();

        if (snapshot.FlexPoints is not null && mc is FlexPipe restoreFlex)
        {
            SmartConLogger.Debug($"   c. FlexPipe: restore {snapshot.FlexPoints.Count} points");
            try
            {
                restoreFlex.Points = new List<XYZ>(snapshot.FlexPoints);
            }
            catch (Exception exFlex)
            {
                SmartConLogger.Warn($"   c. FlexPipe: restore points failed: {exFlex.Message} " +
                    $"[Action: проверьте форму гибкой трубы и восстановите вручную]");
            }
        }
        else if (snapshot.CurveStart is not null && snapshot.CurveEnd is not null
            && mc.Location is LocationCurve lc && lc.Curve is Line)
        {
            SmartConLogger.Debug($"   c. MEPCurve: restore curve " +
                $"({snapshot.CurveStart.X:F4},{snapshot.CurveStart.Y:F4},{snapshot.CurveStart.Z:F4}) → " +
                $"({snapshot.CurveEnd.X:F4},{snapshot.CurveEnd.Y:F4},{snapshot.CurveEnd.Z:F4})");
            try { lc.Curve = Line.CreateBound(snapshot.CurveStart, snapshot.CurveEnd); }
            catch (Exception exCurve) { SmartConLogger.Warn($"   c. MEPCurve: Line.CreateBound failed: {exCurve.Message} [Action: откат цепочки частичен — проверьте геометрию трубы/гибкой трубы вручную]"); }
        }
        else if (snapshot.FirstConnectorOrigin is not null)
        {
            var currentConn = connSvc.RefreshConnector(doc, elemId, snapshot.FirstConnectorIndex);
            if (currentConn is not null)
            {
                var offset = new Vec3(
                    snapshot.FirstConnectorOrigin.X - currentConn.Origin.X,
                    snapshot.FirstConnectorOrigin.Y - currentConn.Origin.Y,
                    snapshot.FirstConnectorOrigin.Z - currentConn.Origin.Z);
                if (!VectorUtils.IsZero(offset))
                {
                    SmartConLogger.Debug($"   c. MEPCurve(FlexPipe): MoveElement to snap connector, " +
                        $"dist={VectorUtils.Length(offset) * FeetToMm:F2}mm");
                    transformSvc.MoveElement(doc, elemId, offset);
                }
            }
        }
        else
        {
            SmartConLogger.Debug($"   c. MEPCurve: skip restore (no position data)");
        }
        doc.Regenerate();
    }

    private void RestoreFamilyInstance(Document doc, ElementId elemId, FamilyInstance fi, ElementSnapshot snapshot)
    {
        if (snapshot.FamilySymbolId is not null && fi.Symbol.Id != snapshot.FamilySymbolId)
        {
            SmartConLogger.Debug($"   c. FI: ChangeTypeId {fi.Symbol.Id.GetValue()} → {snapshot.FamilySymbolId.GetValue()}");
            fi.ChangeTypeId(snapshot.FamilySymbolId);
        }

        var fiConns = connSvc.GetAllConnectors(doc, elemId);
        foreach (var fc in fiConns)
        {
            double targetR = snapshot.ConnectorRadii.TryGetValue(fc.ConnectorIndex, out var snapR)
                ? snapR : snapshot.ConnectorRadius;
            double delta = System.Math.Abs(fc.Radius - targetR);
            if (delta > 1e-5)
            {
                SmartConLogger.Debug($"   c. FI: TrySetConnectorRadius(connIdx={fc.ConnectorIndex}, " +
                    $"current={fc.Radius * FeetToMm:F2}mm → target={targetR * FeetToMm:F2}mm)");
                paramResolver.TrySetConnectorRadius(doc, elemId, fc.ConnectorIndex, targetR);
            }
        }
        doc.Regenerate();

        if (fi.Location is LocationPoint lp)
        {
            SmartConLogger.Debug($"   c. FI: set Point=({snapshot.FiOrigin!.X:F4},{snapshot.FiOrigin.Y:F4},{snapshot.FiOrigin.Z:F4})");
            lp.Point = snapshot.FiOrigin;
        }
        doc.Regenerate();

        RestoreFamilyInstanceRotation(doc, elemId, fi, snapshot);

        if (fi.Location is LocationPoint lp2)
        {
            var correction = new Vec3(
                snapshot.FiOrigin!.X - lp2.Point.X,
                snapshot.FiOrigin.Y - lp2.Point.Y,
                snapshot.FiOrigin.Z - lp2.Point.Z);
            if (!VectorUtils.IsZero(correction))
            {
                SmartConLogger.Debug($"   c. FI: final correction={VectorUtils.Length(correction) * FeetToMm:F2}mm");
                transformSvc.MoveElement(doc, elemId, correction);
            }
            doc.Regenerate();
        }
    }

    private void RestoreFamilyInstanceRotation(Document doc, ElementId elemId, FamilyInstance fi, ElementSnapshot snapshot)
    {
        var currentT = fi.GetTransform();
        var curBZ = new Vec3(currentT.BasisZ.X, currentT.BasisZ.Y, currentT.BasisZ.Z);
        var snapBZ = new Vec3(snapshot.FiBasisZ!.X, snapshot.FiBasisZ.Y, snapshot.FiBasisZ.Z);
        var origin = new Vec3(snapshot.FiOrigin!.X, snapshot.FiOrigin.Y, snapshot.FiOrigin.Z);

        double angleBZ = VectorUtils.AngleBetween(curBZ, snapBZ);
        if (angleBZ > 1e-6 && angleBZ < System.Math.PI - 1e-6)
        {
            var axisBZ = VectorUtils.CrossProduct(curBZ, snapBZ);
            double axisLen = VectorUtils.Length(axisBZ);
            if (axisLen > 1e-10)
            {
                axisBZ = new Vec3(axisBZ.X / axisLen, axisBZ.Y / axisLen, axisBZ.Z / axisLen);
                SmartConLogger.Debug($"   c. FI: RotBZ angle={angleBZ * 180 / System.Math.PI:F2}°");
                transformSvc.RotateElement(doc, elemId, origin, axisBZ, angleBZ);
                doc.Regenerate();
            }
        }
        else if (angleBZ >= System.Math.PI - 1e-6)
        {
            var perpAxis = System.Math.Abs(curBZ.Z) < 0.9
                ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
            SmartConLogger.Debug($"   c. FI: RotBZ 180° (antiparallel)");
            transformSvc.RotateElement(doc, elemId, origin, perpAxis, System.Math.PI);
            doc.Regenerate();
        }

        currentT = fi.GetTransform();
        var curBX = new Vec3(currentT.BasisX.X, currentT.BasisX.Y, currentT.BasisX.Z);
        var snapBX = new Vec3(snapshot.FiBasisX!.X, snapshot.FiBasisX.Y, snapshot.FiBasisX.Z);
        double angleBX = VectorUtils.AngleBetween(curBX, snapBX);
        if (angleBX > 1e-4)
        {
            var rotAxis = new Vec3(currentT.BasisZ.X, currentT.BasisZ.Y, currentT.BasisZ.Z);
            var cross = VectorUtils.CrossProduct(curBX, snapBX);
            double dot = cross.X * rotAxis.X + cross.Y * rotAxis.Y + cross.Z * rotAxis.Z;
            double signedAngle = dot >= 0 ? angleBX : -angleBX;
            SmartConLogger.Debug($"   c. FI: RotBX angle={signedAngle * 180 / System.Math.PI:F2}°");
            transformSvc.RotateElement(doc, elemId, origin, rotAxis, signedAngle);
            doc.Regenerate();
        }
    }

    private void ReconnectSnapshotConnections(
        Document doc, ElementId elemId, ElementSnapshot snapshot, int maxLevel, ConnectionGraph graph)
    {
        SmartConLogger.Debug($"   d. Restoring connections: {snapshot.Connections.Count} records");
        foreach (var connRecord in snapshot.Connections)
        {
            var neighborId = connRecord.NeighborElementId;
            bool inChain = IsInCurrentChain(neighborId, maxLevel, graph);
            SmartConLogger.Debug($"   d. connRecord: this={connRecord.ThisElementId.GetValue()}:{connRecord.ThisConnectorIndex} " +
                $"↔ neighbor={neighborId.GetValue()}:{connRecord.NeighborConnectorIndex}, inChain={inChain}");
            if (inChain) continue;

            var neighborConn = connSvc.RefreshConnector(doc, neighborId, connRecord.NeighborConnectorIndex);
            if (neighborConn is null)
            {
                SmartConLogger.Warn($"   d. neighborConn=null → skip");
                continue;
            }
            if (!neighborConn.IsFree)
            {
                SmartConLogger.Debug($"   d. neighbor busy → disconnect first");
                connSvc.DisconnectAllFromConnector(doc, neighborId, connRecord.NeighborConnectorIndex);
            }

            try
            {
                connSvc.ConnectTo(doc,
                    connRecord.ThisElementId, connRecord.ThisConnectorIndex,
                    connRecord.NeighborElementId, connRecord.NeighborConnectorIndex);
                SmartConLogger.Debug($"   d. ConnectTo OK");
            }
            catch (Exception exConn) { SmartConLogger.Warn($"   d. ConnectTo FAILED: {exConn.Message} [Action: элементы цепочки не соединены — проверьте расстояние и размеры коннекторов]"); }
        }
    }

    /// <summary>Find the edge connecting an element to its parent in the previous BFS level.</summary>
    public static ParentEdge? FindEdgeToParent(ElementId elemId, int level, ConnectionGraph graph)
    {
        var comparer = ElementIdEqualityComparer.Instance;
        var parentLevel = graph.Levels[level - 1];
        var parentIds = new HashSet<ElementId>(parentLevel, comparer);

        foreach (var edge in graph.Edges)
        {
            if (comparer.Equals(edge.ToElementId, elemId) && parentIds.Contains(edge.FromElementId))
                return new ParentEdge(edge.FromElementId, edge.FromConnectorIndex, edge.ToConnectorIndex);
            if (comparer.Equals(edge.FromElementId, elemId) && parentIds.Contains(edge.ToElementId))
                return new ParentEdge(edge.ToElementId, edge.ToConnectorIndex, edge.FromConnectorIndex);
        }
        return null;
    }

    /// <summary>Check whether an element belongs to the current chain (up to maxLevel).</summary>
    public static bool IsInCurrentChain(ElementId elemId, int maxLevel, ConnectionGraph graph)
    {
        var comparer = ElementIdEqualityComparer.Instance;
        for (int level = 0; level <= maxLevel && level < graph.Levels.Count; level++)
        {
            foreach (var id in graph.Levels[level])
            {
                if (comparer.Equals(id, elemId))
                    return true;
            }
        }
        return false;
    }
}
