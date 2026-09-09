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
        // (transition/resize) and no pipe-length absorption. The element is only
        // rigidly aligned to its parent and reconnected. A DN mismatch is resolved
        // by a reducer from the mapping (the network DN is never changed — #167);
        // only when no reducer is mapped do we fall back to a direct as-is join.
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
                SmartConLogger.Debug($"Lock: DN mismatch (parent DN{System.Math.Round(parentProxy.Radius * 2.0 * FeetToMm)} " +
                    $"↔ element DN{System.Math.Round(elemForDnCheck.Radius * 2.0 * FeetToMm)}) — trying reducer from mapping");
                reducerId = InsertReducerForMismatch(doc, snapshotStore, elemId, parentProxy, elemForDnCheck);

                if (reducerId is null)
                {
                    SmartConLogger.Warn($"Lock: DN mismatch on attach — parent DN{System.Math.Round(parentProxy.Radius * 2.0 * FeetToMm)} " +
                        $"↔ element DN{System.Math.Round(elemForDnCheck.Radius * 2.0 * FeetToMm)} will be connected directly (no reducer in mapping). " +
                        $"[Action: добавьте переход в маппинг (Настройки → Правила) или вставьте его вручную]");
                }
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
}
