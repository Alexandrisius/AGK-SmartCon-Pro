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
    /// <summary>Outcome of a rigid remainder move attempt (ConnectAll fast path 2).</summary>
    public enum RigidMoveOutcome
    {
        /// <summary>Preconditions not met — caller falls back to element-wise traversal.</summary>
        NotApplicable,
        /// <summary>Remainder moved as one body and boundary connected directly.</summary>
        Connected,
        /// <summary>Remainder moved as one body and boundary connected via an inserted reducer.</summary>
        ConnectedViaReducer,
        /// <summary>
        /// Diameter mismatch and no reducer in mapping: the remainder was placed
        /// with a 100 mm gap along the parent's BasisZ (visual marker for the
        /// missing transition) and intentionally left NOT connected.
        /// </summary>
        Gapped,
    }

    /// <summary>
    /// Maximum per-edge offset spread (mm) across a multi-element boundary for a
    /// rigid move: all boundary children must need the SAME translation vector,
    /// otherwise one MoveElements call cannot align every edge.
    /// </summary>
    private const double RigidMoveOffsetSpreadMm = 1.0;

    /// <summary>Gap (mm) left along the parent's BasisZ when a reducer is needed but missing.</summary>
    private const double ReducerMissingGapMm = 100.0;

    /// <summary>
    /// Rigid move fast path for ConnectAll: the whole remainder (levels &gt; currentDepth)
    /// is translated with ONE MoveElements call (plus, when the network is locked,
    /// ONE rigid rotation) and the boundary is reconnected — the "engineer drags a
    /// whole node" case, no per-element churn even for 100k-element networks.
    /// Applicability:
    /// - every boundary edge needs the SAME offset (±1 mm);
    /// - a required rotation is allowed ONLY when the network is locked and the
    ///   boundary is single-element (the remainder is then rotated as one body);
    /// - when NOT locked, a remainder containing linear elements (pipes/flex) is
    ///   refused — displacement must be absorbed element-wise (ADR-052), not by
    ///   dragging the whole network;
    /// - when NOT locked, the deeper network must have consistent radii.
    /// DN mismatch on a single-element boundary: insert a reducer from the mapping;
    /// if none, place the network with a 100 mm gap and leave it disconnected
    /// (<see cref="RigidMoveOutcome.Gapped"/>).
    /// </summary>
    /// <param name="lockNetwork">
    /// User's "Блокировать" toggle: skip the pipe check and the deeper-radius check —
    /// the network is moved (and rotated, if needed) strictly as one body.
    /// </param>
    public RigidMoveOutcome TryRigidMoveRemainder(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        int currentDepth,
        bool lockNetwork,
        out IReadOnlyList<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)> sealedEdges)
    {
        using var _scope = SmartConLogger.BeginScope("Chain+",
            ("Method", "TryRigidMoveRemainder"),
            ("Level", currentDepth),
            ("Lock", lockNetwork));

        sealedEdges = [];
        if (currentDepth + 1 >= graph.Levels.Count)
            return RigidMoveOutcome.NotApplicable;

        var proxyCache = new Dictionary<(long, int), ConnectorProxy?>();
        ConnectorProxy? RefreshCached(ElementId id, int idx)
        {
            var key = (id.GetValue(), idx);
            if (!proxyCache.TryGetValue(key, out var proxy))
            {
                proxy = connSvc.RefreshConnector(doc, id, idx);
                proxyCache[key] = proxy;
            }
            return proxy;
        }

        var levelOf = new Dictionary<long, int>();
        for (int level = 0; level < graph.Levels.Count; level++)
            foreach (var id in graph.Levels[level])
                levelOf[id.GetValue()] = level;

        var boundaryEdges = new List<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)>();
        ConnectorProxy? firstParentProxy = null;
        ConnectorProxy? firstChildProxy = null;
        AlignmentResult? firstAlign = null;
        Vec3? moveOffset = null;
        bool needsRotation = false;
        bool radiusMismatch = false;

        foreach (var childId in graph.Levels[currentDepth + 1])
        {
            var edge = FindEdgeToParent(childId, currentDepth + 1, graph);
            if (edge is null)
                return RigidMoveOutcome.NotApplicable;

            var parentProxy = RefreshCached(edge.Value.ParentId, edge.Value.ParentConnIdx);
            var childProxy = RefreshCached(childId, edge.Value.ElemConnIdx);
            if (parentProxy is null || childProxy is null)
                continue;

            if (parentProxy.Domain != Domain.DomainPiping || childProxy.Domain != Domain.DomainPiping)
                continue;

            var alignResult = ConnectorAligner.ComputeAlignment(
                parentProxy.OriginVec3, parentProxy.BasisZVec3, parentProxy.BasisXVec3,
                childProxy.OriginVec3, childProxy.BasisZVec3, childProxy.BasisXVec3);

            bool edgeNeedsRotation = alignResult.BasisZRotation is not null || alignResult.BasisXSnap is not null;

            if (firstAlign is null)
            {
                firstAlign = alignResult;
                moveOffset = alignResult.InitialOffset;
                firstParentProxy = parentProxy;
                firstChildProxy = childProxy;
                needsRotation = edgeNeedsRotation;
            }
            else
            {
                if (VectorUtils.DistanceTo(alignResult.InitialOffset, moveOffset!.Value) * FeetToMm > RigidMoveOffsetSpreadMm)
                {
                    SmartConLogger.Debug($"RigidMove: boundary offsets differ → element-wise flow");
                    return RigidMoveOutcome.NotApplicable;
                }
                // A multi-element boundary requiring rotation cannot be rigid-rotated as one body.
                if (edgeNeedsRotation || needsRotation)
                {
                    SmartConLogger.Debug("RigidMove: multi-element boundary with rotation → element-wise flow");
                    return RigidMoveOutcome.NotApplicable;
                }
            }

            if (System.Math.Abs(parentProxy.Radius - childProxy.Radius) > 1e-5)
                radiusMismatch = true;

            boundaryEdges.Add((edge.Value.ParentId, edge.Value.ParentConnIdx, childId, edge.Value.ElemConnIdx));
        }

        if (boundaryEdges.Count == 0 || firstParentProxy is null || firstChildProxy is null)
            return RigidMoveOutcome.NotApplicable;

        // Rotation is a lock-only rigid operation: without the lock the element-wise
        // flow handles it (and may absorb part of it through pipe lengths).
        if (needsRotation && !lockNetwork)
        {
            SmartConLogger.Debug("RigidMove: rotation required but network not locked → element-wise flow");
            return RigidMoveOutcome.NotApplicable;
        }

        if (radiusMismatch && boundaryEdges.Count > 1)
            return RigidMoveOutcome.NotApplicable;

        var remainderIds = new List<ElementId>();
        for (int level = currentDepth + 1; level < graph.Levels.Count; level++)
            remainderIds.AddRange(graph.Levels[level]);

        // Not locked: a remainder with linear elements must be traversed element-wise
        // so the displacement is absorbed by pipe lengths (ADR-052), not dragged.
        if (!lockNetwork && remainderIds.Any(id => graph.ContainsMepCurve(id)))
        {
            SmartConLogger.Debug("RigidMove: remainder contains pipes/flex — element-wise compensation");
            return RigidMoveOutcome.NotApplicable;
        }

        // Radius consistency of the deeper network (skipped when the user locked it).
        if (!lockNetwork)
        {
            foreach (var edge in graph.Edges)
            {
                if (!levelOf.TryGetValue(edge.FromElementId.GetValue(), out int fromLevel)
                    || !levelOf.TryGetValue(edge.ToElementId.GetValue(), out int toLevel)
                    || fromLevel <= currentDepth
                    || toLevel <= currentDepth)
                    continue;

                var fromProxy = RefreshCached(edge.FromElementId, edge.FromConnectorIndex);
                var toProxy = RefreshCached(edge.ToElementId, edge.ToConnectorIndex);
                if (fromProxy is null || toProxy is null)
                    continue;

                if (fromProxy.Domain != Domain.DomainPiping || toProxy.Domain != Domain.DomainPiping)
                    continue;

                if (System.Math.Abs(fromProxy.Radius - toProxy.Radius) > 1e-5)
                {
                    SmartConLogger.Debug($"RigidMove: radius mismatch at deeper edge " +
                        $"{edge.FromElementId.GetValue()}↔{edge.ToElementId.GetValue()} → element-wise flow");
                    return RigidMoveOutcome.NotApplicable;
                }
            }
        }

        var outcome = RigidMoveOutcome.Connected;
        bool allConnected = true;
        var sealedCrossEdges = new List<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)>();
        var connectedEdges = new List<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)>();

        groupSession.RunInTransaction(LocalizationService.GetString("Tx_ChainRigidMove"), doc =>
        {
            if (radiusMismatch)
            {
                // Boundary DN mismatch (e.g. root dynamic was resized): try a reducer
                // from the mapping; without one, place the network with a 100 mm gap.
                var freshParent = connSvc.RefreshConnector(doc, firstParentProxy.OwnerElementId, firstParentProxy.ConnectorIndex)
                    ?? firstParentProxy;
                var freshChild = connSvc.RefreshConnector(doc, firstChildProxy.OwnerElementId, firstChildProxy.ConnectorIndex)
                    ?? firstChildProxy;

                var reducerId = networkMover.InsertReducer(doc, freshParent, freshChild,
                    directConnectRules: null);
                if (reducerId is not null)
                {
                    var reducerConns = connSvc.GetAllFreeConnectors(doc, reducerId);
                    var rConn1 = reducerConns
                        .OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, freshParent.OriginVec3))
                        .FirstOrDefault();
                    var rConn2 = reducerConns.FirstOrDefault(c => c.ConnectorIndex != (rConn1?.ConnectorIndex ?? -1));

                    if (rConn1 is not null && rConn2 is not null)
                    {
                        var (pId, pIdx, cId, cIdx) = boundaryEdges[0];

                        // parent ↔ reducer.conn1 (InsertReducer only aligns — it never connects)
                        connSvc.ConnectTo(doc, pId, pIdx, reducerId, rConn1.ConnectorIndex);

                        var offset2 = rConn2.OriginVec3 - freshChild.OriginVec3;
                        if (!VectorUtils.IsZero(offset2))
                            transformSvc.MoveElements(doc, remainderIds, offset2);
                        doc.Regenerate();

                        // reducer.conn2 ↔ child
                        connSvc.ConnectTo(doc, reducerId, rConn2.ConnectorIndex, cId, cIdx);

                        connectedEdges.Add((pId, pIdx, reducerId, rConn1.ConnectorIndex));
                        connectedEdges.Add((reducerId, rConn2.ConnectorIndex, cId, cIdx));
                        outcome = RigidMoveOutcome.ConnectedViaReducer;
                        SmartConLogger.Info($"RigidMove: boundary DN mismatch — reducer {reducerId.GetValue()} inserted " +
                            $"(parent↔conn{rConn1.ConnectorIndex}, conn{rConn2.ConnectorIndex}↔child), remainder moved as one body");
                    }
                    else
                    {
                        allConnected = false;
                        SmartConLogger.Warn("RigidMove: reducer has no free second connector — cannot connect. " +
                            "[Action: проверьте семейство переходника и подключите границу вручную]");
                    }
                }
                else
                {
                    // No reducer in mapping: place the network with a 100 mm gap
                    // along the parent's BasisZ — a visual marker for the transition
                    // the engineer must insert manually. NOT connected by design.
                    var gapTarget = freshParent.OriginVec3 + freshParent.BasisZVec3 * (ReducerMissingGapMm * MmToFeet);
                    var gapOffset = gapTarget - freshChild.OriginVec3;
                    if (!VectorUtils.IsZero(gapOffset))
                        transformSvc.MoveElements(doc, remainderIds, gapOffset);
                    doc.Regenerate();
                    outcome = RigidMoveOutcome.Gapped;
                    SmartConLogger.Warn($"RigidMove: reducer not found in mapping — network placed with {ReducerMissingGapMm}mm gap, NOT connected. " +
                        $"[Action: добавьте переход в маппинг (Настройки → Правила) или вставьте его вручную в месте зазора]");
                }
                return;
            }

            // Rigid rotate (lock only): rotate the whole remainder as one body by the
            // boundary alignment, then recompute the translation from fresh proxies.
            if (needsRotation && firstAlign is not null)
            {
                if (firstAlign.BasisZRotation is { } bz)
                    transformSvc.RotateElements(doc, remainderIds, firstAlign.RotationCenter, bz.Axis, bz.AngleRadians);
                if (firstAlign.BasisXSnap is { } bx)
                    transformSvc.RotateElements(doc, remainderIds, firstAlign.RotationCenter, bx.Axis, bx.AngleRadians);
                doc.Regenerate();

                var childAfterRotation = connSvc.RefreshConnector(doc, firstChildProxy.OwnerElementId, firstChildProxy.ConnectorIndex);
                if (childAfterRotation is not null)
                    moveOffset = firstParentProxy.OriginVec3 - childAfterRotation.OriginVec3;
            }

            if (moveOffset is not null && !VectorUtils.IsZero(moveOffset.Value))
                transformSvc.MoveElements(doc, remainderIds, moveOffset.Value);
            doc.Regenerate();

            var connected = new List<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)>();
            foreach (var (parentId, parentConnIdx, childId, childConnIdx) in boundaryEdges)
            {
                try
                {
                    connSvc.ConnectTo(doc, parentId, parentConnIdx, childId, childConnIdx);
                    connected.Add((parentId, parentConnIdx, childId, childConnIdx));
                }
                catch (Exception exConn)
                {
                    allConnected = false;
                    SmartConLogger.Warn($"RigidMove: ConnectTo {parentId.GetValue()}↔{childId.GetValue()} " +
                        $"failed: {exConn.Message} [Action: граница будет подключена поэлементным обходом]");
                    break;
                }
            }

            if (!allConnected)
            {
                // Roll back the partially connected boundary (same discipline as seal) —
                // a failed rigid move must not leave busy connectors for the fallback.
                foreach (var (_, _, childId, childConnIdx) in connected)
                {
                    try { connSvc.DisconnectAllFromConnector(doc, childId, childConnIdx); }
                    catch (Exception exDisc)
                    {
                        SmartConLogger.Warn($"RigidMove rollback: disconnect {childId.GetValue()}:{childConnIdx} " +
                            $"failed: {exDisc.Message} [Action: проверьте соединения границы вручную]");
                    }
                }
            }
            else
            {
                RestoreCrossEdgesForBoundary(doc, graph, currentDepth, levelOf, sealedCrossEdges);
            }
        });

        if (!allConnected)
        {
            SmartConLogger.Warn("RigidMove: boundary connect failed after move — falling back to element-wise traversal " +
                "[Action: продолжайте подключение кнопкой «+» — сбойный элемент покажет ошибку]");
            return RigidMoveOutcome.NotApplicable;
        }

        if (outcome == RigidMoveOutcome.Connected)
            connectedEdges = boundaryEdges.Concat(sealedCrossEdges).ToList();

        sealedEdges = connectedEdges;
        SmartConLogger.Info($"RigidMove: outcome={outcome}, remainder={remainderIds.Count} elements moved as one body, " +
            $"boundary edges={boundaryEdges.Count}, cross-edges restored={sealedCrossEdges.Count}, rotated={needsRotation}");
        return outcome;
    }
}
