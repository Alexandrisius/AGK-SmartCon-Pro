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
/// Handles element-wise attach/detach of chain elements in the PipeConnect editor.
/// AttachSingleElement: snapshot, disconnect, resize, align, reconnect one element.
/// DetachSingleElement: rollback one element to its snapshot state, delete inserted reducers.
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
    /// Maximum connector gap (mm) for cross-edge restoration: beyond this the loop
    /// did not close after alignment and ConnectTo could drag elements silently.
    /// </summary>
    private const double CrossEdgeMaxGapMm = 2.0;

    /// <summary>Result of attaching a single chain element (element-wise chain mode).</summary>
    /// <param name="DidWork">
    /// True when the element actually changed (moved, rotated, resized, reducer
    /// inserted, or absorbed by pipe length). False = idle element — only
    /// disconnect/reconnect churn.
    /// </param>
    /// <param name="Edge">
    /// The parent edge used for the reconnect (parent connector ↔ element connector),
    /// or null when no parent edge was found and the element was skipped.
    /// </param>
    public readonly record struct SingleElementResult(bool DidWork, ParentEdge? Edge);

    /// <summary>
    /// Attach a single chain element (element-wise chain mode): snapshot, disconnect,
    /// resize, align, reconnect to its parent. Inserts a reducer when radius
    /// adjustment fails. The element's parent must already be attached — the
    /// flattened queue order (see <see cref="ConnectionGraph.GetElementQueue"/>)
    /// guarantees this.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="groupSession">Active transaction group session.</param>
    /// <param name="graph">Chain graph with element levels.</param>
    /// <param name="snapshotStore">Store for element snapshots (for rollback).</param>
    /// <param name="warmedElementIds">Set of already-warmed element IDs.</param>
    /// <param name="entry">Queue entry: element + its BFS level.</param>
    /// <param name="attachedElementIds">
    /// Element ids (numeric) already attached in this session (root + queue[1..current]).
    /// Used to restore cross-edges (network loops): a loop connection to an already
    /// attached neighbor is re-established right after the parent reconnect;
    /// loop neighbors attached later restore the same edge from their own attach.
    /// </param>
    /// <param name="lockNetwork">
    /// User's "Блокировать" toggle: attach the element strictly as-is — no size
    /// adjustment (no transition/reducer/resize) and no pipe-length absorption.
    /// The element is only aligned rigidly to its parent and reconnected.
    /// </param>
    public SingleElementResult AttachSingleElement(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        HashSet<long> warmedElementIds,
        ChainQueueEntry entry,
        IReadOnlyCollection<long> attachedElementIds,
        bool lockNetwork = false)
    {
        using var _scope = SmartConLogger.BeginScope("Chain+",
            ("Method", "AttachSingleElement"),
            ("ElementId", entry.ElementId.GetValue()),
            ("Level", entry.Level),
            ("Lock", lockNetwork));

        var singleElement = new[] { entry.ElementId };

        WarmDepsForLevel(doc, singleElement, warmedElementIds);
        SaveSnapshotForLevelElement(doc, entry.ElementId, graph, snapshotStore);

        var transitionOptions = lockNetwork
            ? new Dictionary<long, IReadOnlyList<FamilySizeOption>>()
            : PrefetchTransitionOptions(doc, graph, entry.Level, singleElement);

        var result = new SingleElementResult(false, null);
        groupSession.RunInTransaction(
            string.Format(LocalizationService.GetString("Tx_ChainElement"), entry.ElementId.GetValue()), doc =>
        {
            bool didWork = ProcessIncrementElement(
                doc, graph, snapshotStore, entry.Level, entry.ElementId,
                elemIndex: 1, levelCount: 1, transitionOptions, attachedElementIds, lockNetwork, out var edge);

            result = new SingleElementResult(didWork, edge);
            doc.Regenerate();
        });

        SmartConLogger.Debug($"═══ ELEMENT {entry.ElementId.GetValue()} DONE ═══ (didWork={result.DidWork})");
        return result;
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
    /// Detach a single chain element (element-wise chain mode): restore it to its
    /// snapshot state, delete inserted reducers, and reconnect original connections.
    /// Elements must be detached in strict LIFO order (queue tail first) so the
    /// parent edge is still intact when a child is rolled back.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="groupSession">Active transaction group session.</param>
    /// <param name="graph">Chain graph.</param>
    /// <param name="snapshotStore">Snapshot store with saved element states.</param>
    /// <param name="entry">Queue entry: element + its BFS level.</param>
    public void DetachSingleElement(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        ChainQueueEntry entry)
    {
        using var _scope = SmartConLogger.BeginScope("Chain-",
            ("Method", "DetachSingleElement"),
            ("ElementId", entry.ElementId.GetValue()),
            ("Level", entry.Level));

        groupSession.RunInTransaction(
            string.Format(LocalizationService.GetString("Tx_ChainRollbackElement"), entry.ElementId.GetValue()), doc =>
        {
            RollbackElement(doc, graph, snapshotStore, entry.Level, entry.ElementId);
            doc.Regenerate();
        });
    }

    /// <summary>
    /// Try to "seal" the chain early (ADR-052): when the displacement is already
    /// fully absorbed and the remaining downstream needs no resize work, the
    /// boundary edges (currentDepth → currentDepth+1) are reconnected and the
    /// rest of the network is left completely untouched — no per-element churn.
    /// Returns true when the chain is sealed (caller disables further increments).
    /// A level is "quiet" when every boundary child has zero alignment offset,
    /// no required rotation and a matching radius, and every deeper piping edge
    /// has matching radii (a resize deeper would require classic processing).
    /// On partial ConnectTo failure every edge connected so far is disconnected
    /// again inside the same transaction — a failed seal never leaves the
    /// network half-sealed.
    /// </summary>
    /// <param name="sealedEdges">
    /// On success: the boundary edges connected by this seal (caller stores them
    /// so the seal can be torn down later — see UnsealIfSealed in the editor VM).
    /// Empty on failure.
    /// </param>
    public bool TrySealQuietChain(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        int currentDepth,
        out IReadOnlyList<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)> sealedEdges)
    {
        sealedEdges = [];
        if (currentDepth + 1 >= graph.Levels.Count)
            return true;

        // Per-call connector cache: quiet checks read every deeper edge once —
        // without caching each RefreshConnector re-walks the ConnectorManager.
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

        foreach (var childId in graph.Levels[currentDepth + 1])
        {
            var edge = FindEdgeToParent(childId, currentDepth + 1, graph);
            if (edge is null)
                return false;

            var parentProxy = RefreshCached(edge.Value.ParentId, edge.Value.ParentConnIdx);
            var childProxy = RefreshCached(childId, edge.Value.ElemConnIdx);
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

            var fromProxy = RefreshCached(edge.FromElementId, edge.FromConnectorIndex);
            var toProxy = RefreshCached(edge.ToElementId, edge.ToConnectorIndex);
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

        bool allConnected = true;
        var sealedCrossEdges = new List<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)>();
        groupSession.RunInTransaction(LocalizationService.GetString("Tx_ChainSeal"), doc =>
        {
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
                    SmartConLogger.Warn($"Seal: ConnectTo {parentId.GetValue()}↔{childId.GetValue()} " +
                        $"failed: {exConn.Message} [Action: граница будет подключена поэлементным обходом]");
                    break;
                }
            }

            if (!allConnected)
            {
                // Roll back the partially sealed boundary — a failed seal must
                // never leave the network half-sealed (caller falls back to
                // element-wise traversal with visible per-element errors).
                foreach (var (_, _, childId, childConnIdx) in connected)
                {
                    try { connSvc.DisconnectAllFromConnector(doc, childId, childConnIdx); }
                    catch (Exception exDisc)
                    {
                        SmartConLogger.Warn($"Seal rollback: DisconnectAllFromConnector {childId.GetValue()}:{childConnIdx} " +
                            $"failed: {exDisc.Message} [Action: граница может остаться частично подключённой — проверьте соединения]");
                    }
                }
                connected.Clear();
            }
            else
            {
                // Restore cross-edges (network loops) between the sealed boundary
                // and the attached part: they were torn by the classic attach of
                // the attached element, and the sealed child never passes through
                // AttachSingleElement — without this the loop stays torn (bug:
                // pipe left detached from a tee after seal compensation).
                RestoreCrossEdgesForBoundary(doc, graph, currentDepth, levelOf, sealedCrossEdges);
            }
            doc.Regenerate();
        });

        if (!allConnected)
        {
            SmartConLogger.Warn("Seal: some boundary edges failed — falling back to element-wise traversal " +
                "[Action: продолжайте подключение кнопкой «+» или «Подключить всё» — сбойный элемент покажет ошибку]");
            return false;
        }

        sealedEdges = boundaryEdges.Concat(sealedCrossEdges).ToList();
        SmartConLogger.Info($"Seal: chain sealed at level {currentDepth}, " +
            $"boundary edges={boundaryEdges.Count}, cross-edges restored={sealedCrossEdges.Count}, deeper levels untouched");
        return true;
    }

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
        IReadOnlyDictionary<long, IReadOnlyList<FamilySizeOption>> transitionOptions,
        IReadOnlyCollection<long> attachedElementIds,
        bool lockNetwork,
        out ParentEdge? processedEdge)
    {
        LogIncrementElementHeader(doc, elemId, elemIndex, levelCount);

        DisconnectElementConnections(doc, elemId);

        var edge = FindEdgeToParent(elemId, nextLevel, graph);
        if (edge is null)
        {
            SmartConLogger.Warn($"b. Edge to parent NOT FOUND → element left disconnected " +
                $"[Action: элемент отсоединён от сети — откатите его кнопкой «−» и проверьте граф соединений]");
            processedEdge = null;
            return false;
        }

        SmartConLogger.Debug($"b. Edge: parent={edge.Value.ParentId.GetValue()} " +
            $"parentConnIdx={edge.Value.ParentConnIdx}, elemConnIdx={edge.Value.ElemConnIdx}");

        var parentProxy = connSvc.RefreshConnector(doc, edge.Value.ParentId, edge.Value.ParentConnIdx);
        if (parentProxy is null)
        {
            SmartConLogger.Warn($"parentProxy=NULL → element left disconnected " +
                $"[Action: элемент отсоединён от сети — откатите его кнопкой «−» и проверьте коннектор родителя]");
            processedEdge = null;
            return false;
        }

        SmartConLogger.Debug($"parent: R={parentProxy.Radius * FeetToMm:F2}mm " +
            $"(DN{System.Math.Round(parentProxy.Radius * 2.0 * FeetToMm)}) " +
            $"origin=({parentProxy.Origin.X:F4},{parentProxy.Origin.Y:F4},{parentProxy.Origin.Z:F4})");

        // LockNetwork ("Блокировать"): attach strictly as-is — no size adjustment
        // (transition/reducer/resize) and no pipe-length absorption. The element
        // is only rigidly aligned to its parent and reconnected.
        ElementId? reducerId = null;
        bool sizeChanged = false;
        if (!lockNetwork)
        {
            (reducerId, sizeChanged) = AdjustElementSize(doc, graph, snapshotStore, elemId, edge.Value, parentProxy, transitionOptions);
            parentProxy = connSvc.RefreshConnector(doc, edge.Value.ParentId, edge.Value.ParentConnIdx);
        }
        else
        {
            var elemForDnCheck = connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);
            if (elemForDnCheck is not null
                && System.Math.Abs(parentProxy.Radius - elemForDnCheck.Radius) > 1e-5)
            {
                SmartConLogger.Warn($"Lock: DN mismatch on attach — parent DN{System.Math.Round(parentProxy.Radius * 2.0 * FeetToMm)} " +
                    $"↔ element DN{System.Math.Round(elemForDnCheck.Radius * 2.0 * FeetToMm)} will be connected directly (no reducer by design). " +
                    $"[Action: если требуется переход — снимите «Блокировать» или добавьте переход вручную]");
            }
        }

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

        if (lockNetwork)
            AlignElement(doc, elemId, elemProxyForAlign, alignResult);
        else if (!TryAlignPipeByLength(doc, elemId, elemProxyForAlign, alignResult))
            AlignElement(doc, elemId, elemProxyForAlign, alignResult);
        ReconnectIncrementElement(doc, elemId, edge.Value, parentProxy, reducerId);

        // processedEdge signals "element is physically reconnected to its parent" —
        // callers rely on it to decide whether the queue depth may advance.
        processedEdge = edge;

        RestoreCrossEdgesToAttached(doc, graph, snapshotStore, elemId, edge.Value, attachedElementIds);

        SmartConLogger.Debug($"── Element {elemId.GetValue()} ready ──");
        return sizeChanged || reducerId is not null || alignmentChanged;
    }

    /// <summary>
    /// Restore cross-edges (network loops) between the sealed remainder and the
    /// attached part after a rigid-body rotation (lock-network mode): attached
    /// part does not rotate, so loop edges crossing the boundary may drift apart.
    /// Best-effort: only edges whose connectors still meet within the gap tolerance
    /// are re-established; others are logged for manual review.
    /// </summary>
    public void RestoreCrossEdgesAfterRigidRotation(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        int currentDepth)
    {
        if (currentDepth + 1 >= graph.Levels.Count)
            return;

        var levelOf = new Dictionary<long, int>();
        for (int level = 0; level < graph.Levels.Count; level++)
            foreach (var id in graph.Levels[level])
                levelOf[id.GetValue()] = level;

        var restored = new List<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)>();
        groupSession.RunInTransaction(LocalizationService.GetString("Tx_RestoreCrossEdges"), doc =>
        {
            RestoreCrossEdgesForBoundary(doc, graph, currentDepth, levelOf, restored);
            doc.Regenerate();
        });

        if (restored.Count > 0)
            SmartConLogger.Info($"Rigid rotation: {restored.Count} cross-edge(s) restored after body rotation");
    }

    /// <summary>Info about a reducer inserted while restoring a cross-edge (for tracking/cleanup).</summary>
    private readonly record struct CrossEdgeReducerInfo(
        ElementId FittingId, int FittingConnIdx, ElementId ReducerId, int ReducerConn2Idx);

    /// <summary>
    /// Restore one cross-edge (network loop) torn during attach. Both elements of
    /// the loop are already fixed by their parents — the loop can only be closed
    /// without moving them, so the strategies are, in order:
    /// 1. direct ConnectTo when radii match and the gap is within tolerance;
    /// 2. pipe-length absorption (ADR-052) when radii match but the gap is larger —
    ///    the pipe in the pair changes its length to close the loop;
    /// 3. reducer from the mapping on DN mismatch (cascade may give loop ends
    ///    different DN when one branch went through reducer/transition, ADR-053):
    ///    the reducer is attached to the fitting (fixed) side, the pipe absorbs
    ///    the remaining gap to the reducer's free connector;
    /// 4. otherwise — Warn with an actionable message (rare, manual fix needed).
    /// </summary>
    /// <param name="insertedReducer">
    /// Set when strategy 3 inserted a reducer — the CALLER is responsible for
    /// tracking it (snapshotStore.TrackReducer on attach, sealed edges on seal/rigid).
    /// </param>
    private bool TryRestoreCrossEdge(
        Document doc,
        ElementId thisId,
        ConnectionRecord rec,
        string logTag,
        out CrossEdgeReducerInfo? insertedReducer)
    {
        insertedReducer = null;
        var thisConn = connSvc.RefreshConnector(doc, thisId, rec.ThisConnectorIndex);
        var neighborConn = connSvc.RefreshConnector(doc, rec.NeighborElementId, rec.NeighborConnectorIndex);
        if (thisConn is null || neighborConn is null)
        {
            SmartConLogger.Debug($"  {logTag} cross-edge {thisId.GetValue()}↔{rec.NeighborElementId.GetValue()}: connector refresh failed — skip");
            return false;
        }

        if (!thisConn.IsFree || !neighborConn.IsFree)
        {
            SmartConLogger.Debug($"  {logTag} cross-edge {thisId.GetValue()}:{rec.ThisConnectorIndex}↔{rec.NeighborElementId.GetValue()}:{rec.NeighborConnectorIndex}: " +
                $"connector busy (this.Free={thisConn.IsFree}, neighbor.Free={neighborConn.IsFree}) — skip");
            return false;
        }

        double gapMm = VectorUtils.DistanceTo(thisConn.OriginVec3, neighborConn.OriginVec3) * FeetToMm;
        bool radiusMatch = System.Math.Abs(thisConn.Radius - neighborConn.Radius) <= 1e-5;

        // ── 1+2: radii match → direct, or absorb the gap through the pipe in the pair ──
        if (radiusMatch)
        {
            if (gapMm > CrossEdgeMaxGapMm
                && !TryAbsorbCrossGap(doc, thisId, thisConn, rec.NeighborElementId, neighborConn, logTag))
            {
                SmartConLogger.Warn($"  {logTag} cross-edge {thisId.GetValue()}:{rec.ThisConnectorIndex} ↔ " +
                    $"{rec.NeighborElementId.GetValue()}:{rec.NeighborConnectorIndex} not restored: connector gap {gapMm:F1}mm " +
                    $"could not be absorbed (no pipe in pair or absorption failed). " +
                    $"[Action: петля сети не замкнулась — соедините элементы вручную]");
                return false;
            }

            return TryConnectCrossEdge(doc, thisId, rec.ThisConnectorIndex, rec.NeighborElementId, rec.NeighborConnectorIndex, logTag);
        }

        // ── 3: DN mismatch (cascade gave loop ends different DN) → reducer + absorb ──
        bool thisIsPipe = doc.GetElement(thisId) is MEPCurve or FlexPipe;
        bool neighborIsPipe = doc.GetElement(rec.NeighborElementId) is MEPCurve or FlexPipe;

        if (!thisIsPipe && !neighborIsPipe)
        {
            SmartConLogger.Warn($"  {logTag} cross-edge {thisId.GetValue()}:{rec.ThisConnectorIndex} ↔ " +
                $"{rec.NeighborElementId.GetValue()}:{rec.NeighborConnectorIndex} not restored: DN mismatch " +
                $"(DN{System.Math.Round(thisConn.Radius * 2.0 * FeetToMm)} ↔ DN{System.Math.Round(neighborConn.Radius * 2.0 * FeetToMm)}) " +
                $"and no pipe in pair to absorb the gap. " +
                $"[Action: вставьте переход в месте петли вручную]");
            return false;
        }

        // Reducer goes to the FIXED (non-pipe) side; the pipe absorbs the remaining gap.
        var fittingId = thisIsPipe ? rec.NeighborElementId : thisId;
        var fittingConn = thisIsPipe ? neighborConn : thisConn;
        var pipeId = thisIsPipe ? thisId : rec.NeighborElementId;
        var pipeConn = thisIsPipe ? thisConn : neighborConn;

        var reducerId = networkMover.InsertReducer(doc, fittingConn, pipeConn);
        if (reducerId is null)
        {
            SmartConLogger.Warn($"  {logTag} cross-edge {thisId.GetValue()}↔{rec.NeighborElementId.GetValue()}: " +
                $"DN mismatch (DN{System.Math.Round(thisConn.Radius * 2.0 * FeetToMm)} ↔ DN{System.Math.Round(neighborConn.Radius * 2.0 * FeetToMm)}) " +
                $"but reducer not found in mapping. [Action: добавьте переход в маппинг (Настройки → Правила) или вставьте его вручную в месте петли]");
            return false;
        }

        var reducerConns = connSvc.GetAllFreeConnectors(doc, reducerId);
        var reducerConn2 = reducerConns
            .OrderByDescending(c => VectorUtils.DistanceTo(c.OriginVec3, fittingConn.OriginVec3))
            .FirstOrDefault();
        if (reducerConn2 is null)
        {
            SmartConLogger.Warn($"  {logTag} cross-edge: reducer {reducerId.GetValue()} has no free connector " +
                $"[Action: проверьте семейство переходника и соедините петлю вручную]");
            return false;
        }

        var pipeFresh = connSvc.RefreshConnector(doc, pipeId, pipeConn.ConnectorIndex) ?? pipeConn;
        var absorbOffset = reducerConn2.OriginVec3 - pipeFresh.OriginVec3;
        if (!VectorUtils.IsZero(absorbOffset)
            && !PipeAbsorptionApplier.TryApply(doc, pipeId, pipeFresh.OriginVec3, absorbOffset))
        {
            SmartConLogger.Warn($"  {logTag} cross-edge: pipe {pipeId.GetValue()} failed to absorb " +
                $"{VectorUtils.Length(absorbOffset) * FeetToMm:F1}mm to the reducer. " +
                $"[Action: петля сети не замкнулась — соедините элементы вручную]");
            DeleteOrphanedReducer(doc, reducerId, logTag);
            return false;
        }
        doc.Regenerate();

        try
        {
            connSvc.ConnectTo(doc, reducerId, reducerConn2.ConnectorIndex, pipeId, pipeConn.ConnectorIndex);
            insertedReducer = new CrossEdgeReducerInfo(
                fittingId, fittingConn.ConnectorIndex, reducerId, reducerConn2.ConnectorIndex);
            SmartConLogger.Info($"  {logTag} cross-edge restored via reducer {reducerId.GetValue()}: " +
                $"{fittingId.GetValue()} ↔ reducer ↔ {pipeId.GetValue()}:{pipeConn.ConnectorIndex}");
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"  {logTag} cross-edge ConnectTo reducer↔pipe failed: {ex.Message} " +
                $"[Action: петля сети не замкнулась — проверьте соединение элементов вручную]");
            DeleteOrphanedReducer(doc, reducerId, logTag);
            return false;
        }
    }

    /// <summary>Delete a reducer that was inserted but never connected (failed absorb/connect) — no orphans in the model.</summary>
    private void DeleteOrphanedReducer(Document doc, ElementId reducerId, string logTag)
    {
        try
        {
            foreach (var conn in connSvc.GetAllConnectors(doc, reducerId))
            {
                if (!conn.IsFree)
                    connSvc.DisconnectAllFromConnector(doc, reducerId, conn.ConnectorIndex);
            }
            fittingInsertSvc.DeleteElement(doc, reducerId);
            SmartConLogger.Info($"  {logTag} orphaned reducer {reducerId.GetValue()} deleted after failed cross-edge restore");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"  {logTag} failed to delete orphaned reducer {reducerId.GetValue()}: {ex.Message} " +
                $"[Action: удалите неиспользуемый переход из модели вручную]");
        }
    }

    /// <summary>Absorb a cross-edge gap through the pipe in the pair (either side may be the pipe).</summary>
    private bool TryAbsorbCrossGap(
        Document doc,
        ElementId thisId,
        ConnectorProxy thisConn,
        ElementId neighborId,
        ConnectorProxy neighborConn,
        string logTag)
    {
        ElementId pipeId;
        ConnectorProxy pipeConn;
        Vec3 offset;

        if (doc.GetElement(thisId) is MEPCurve or FlexPipe)
        {
            pipeId = thisId;
            pipeConn = thisConn;
            offset = neighborConn.OriginVec3 - thisConn.OriginVec3;
        }
        else if (doc.GetElement(neighborId) is MEPCurve or FlexPipe)
        {
            pipeId = neighborId;
            pipeConn = neighborConn;
            offset = thisConn.OriginVec3 - neighborConn.OriginVec3;
        }
        else
        {
            return false;
        }

        bool absorbed = PipeAbsorptionApplier.TryApply(doc, pipeId, pipeConn.OriginVec3, offset);
        if (absorbed)
            SmartConLogger.Debug($"  {logTag} cross-edge: pipe {pipeId.GetValue()} absorbed {VectorUtils.Length(offset) * FeetToMm:F1}mm");
        return absorbed;
    }

    private bool TryConnectCrossEdge(
        Document doc,
        ElementId thisId, int thisConnIdx,
        ElementId neighborId, int neighborConnIdx,
        string logTag)
    {
        try
        {
            connSvc.ConnectTo(doc, thisId, thisConnIdx, neighborId, neighborConnIdx);
            SmartConLogger.Info($"  {logTag} cross-edge restored: {thisId.GetValue()}:{thisConnIdx} ↔ {neighborId.GetValue()}:{neighborConnIdx}");
            return true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"  {logTag} cross-edge restore failed {thisId.GetValue()}:{thisConnIdx} ↔ " +
                $"{neighborId.GetValue()}:{neighborConnIdx}: {ex.Message} " +
                $"[Action: петля сети не замкнулась — проверьте соединение элементов вручную]");
            return false;
        }
    }

    /// <summary>
    /// Restore cross-edges (network loops) between ANY remainder element
    /// (levels &gt; currentDepth) and the attached part (levels 0..currentDepth).
    /// Called inside the seal/rigid transaction; successfully restored edges are
    /// collected so the caller can report them as part of the seal (and unseal
    /// them later together with the boundary).
    /// </summary>
    private void RestoreCrossEdgesForBoundary(
        Document doc,
        ConnectionGraph graph,
        int currentDepth,
        IReadOnlyDictionary<long, int> levelOf,
        List<(ElementId ParentId, int ParentConnIdx, ElementId ChildId, int ChildConnIdx)> restoredEdges)
    {
        var comparer = ElementIdEqualityComparer.Instance;
        for (int level = currentDepth + 1; level < graph.Levels.Count; level++)
        {
            foreach (var childId in graph.Levels[level])
            {
            foreach (var rec in graph.GetOriginalConnections(childId))
            {
                if (comparer.Equals(rec.NeighborElementId, childId))
                    continue;

                if (!levelOf.TryGetValue(rec.NeighborElementId.GetValue(), out int neighborLevel)
                    || neighborLevel > currentDepth)
                    continue;

                if (TryRestoreCrossEdge(doc, childId, rec, "seal", out var reducerInfo))
                {
                    if (reducerInfo is { } ri)
                    {
                        // Reducer inserted on the loop — track it via the sealed edges
                        // so UnsealIfSealed deletes it (it is not a graph node).
                        restoredEdges.Add((ri.FittingId, ri.FittingConnIdx, ri.ReducerId, ri.ReducerConn2Idx));
                    }
                    else
                    {
                        restoredEdges.Add((childId, rec.ThisConnectorIndex, rec.NeighborElementId, rec.NeighborConnectorIndex));
                    }
                }
            }
            }
        }
    }

    /// <summary>
    /// Restore cross-edges (network loops) torn by DisconnectElementConnections:
    /// the original loop connection is re-established when the loop neighbor is
    /// already attached. Loop neighbors attached later in the queue restore the
    /// same edge from their own attach (symmetric coverage).
    /// </summary>
    private void RestoreCrossEdgesToAttached(
        Document doc,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        ElementId elemId,
        ParentEdge parentEdge,
        IReadOnlyCollection<long> attachedElementIds)
    {
        var comparer = ElementIdEqualityComparer.Instance;
        foreach (var rec in graph.GetOriginalConnections(elemId))
        {
            if (comparer.Equals(rec.NeighborElementId, elemId))
                continue;

            if (comparer.Equals(rec.NeighborElementId, parentEdge.ParentId)
                && rec.ThisConnectorIndex == parentEdge.ElemConnIdx
                && rec.NeighborConnectorIndex == parentEdge.ParentConnIdx)
                continue;

            if (!attachedElementIds.Contains(rec.NeighborElementId.GetValue()))
                continue;

            if (TryRestoreCrossEdge(doc, elemId, rec, "attach", out var reducerInfo)
                && reducerInfo is { } ri)
            {
                // Reducer inserted on the loop — track it for rollback on detach.
                snapshotStore.TrackReducer(elemId, ri.ReducerId);
            }
        }
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
            SmartConLogger.Warn($"    c.3 Reducer not found in mapping! " +
                $"[Action: добавьте семейство переходника в маппинг (Настройки → Правила) — иначе элементы с разными DN соединятся напрямую]");
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
            SmartConLogger.Warn($"   c. Snapshot not found → skip restore " +
                $"[Action: элемент останется в текущем состоянии — проверьте его положение и соединения вручную]");
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
    /// Pre-warm parameter dependency cache for the given elements.
    /// Avoids repeated cold cache lookups during AttachSingleElement.
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
                SmartConLogger.Warn($"   d. neighborConn=null → skip " +
                    $"[Action: исходное соединение не восстановлено — проверьте соединения элемента вручную]");
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
