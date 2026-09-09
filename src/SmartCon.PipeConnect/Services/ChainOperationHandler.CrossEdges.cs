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
}
