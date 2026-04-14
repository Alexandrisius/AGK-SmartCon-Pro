using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

public sealed class ChainOperationHandler(
    IConnectorService connSvc,
    ITransformService transformSvc,
    IParameterResolver paramResolver,
    IFittingInsertService fittingInsertSvc,
    INetworkMover networkMover)
{
    public record struct ParentEdge(ElementId ParentId, int ParentConnIdx, int ElemConnIdx);

    public void IncrementLevel(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        HashSet<long> warmedElementIds,
        int nextLevel)
    {
        var levelElements = graph.Levels[nextLevel];

        SmartConLogger.Info($"[Chain+] ═══ LEVEL {nextLevel} ═══ ({levelElements.Count} elements)");

        WarmDepsForLevel(doc, levelElements, warmedElementIds);

        foreach (var elemId in levelElements)
        {
            var snapshot = CaptureSnapshot(doc, elemId, graph);
            snapshotStore.Save(snapshot);
            SmartConLogger.Info($"[Chain+] Snapshot: elemId={elemId.Value}, " +
                $"isMepCurve={snapshot.IsMepCurve}, " +
                $"R={snapshot.ConnectorRadius * FeetToMm:F2}mm (DN{System.Math.Round(snapshot.ConnectorRadius * 2.0 * FeetToMm)}), " +
                $"symbolId={snapshot.FamilySymbolId?.Value}, connections={snapshot.Connections.Count}");
        }

        var comparer = ElementIdEqualityComparer.Instance;

        groupSession.RunInTransaction($"Цепочка: уровень {nextLevel}", doc =>
        {
            int elemIndex = 0;
            foreach (var elemId in levelElements)
            {
                elemIndex++;
                var elemRaw = doc.GetElement(elemId);
                string elemName = elemRaw?.Name ?? "?";
                string elemType = elemRaw?.GetType().Name ?? "?";
                SmartConLogger.Info($"[Chain+] ── Element {elemIndex}/{levelElements.Count}: " +
                    $"id={elemId.Value} '{elemName}' ({elemType}) ──");

                var allConns = connSvc.GetAllConnectors(doc, elemId);
                int disconnected = 0;
                foreach (var c in allConns)
                {
                    if (!c.IsFree)
                    {
                        SmartConLogger.Info($"[Chain+]   a. Disconnect connIdx={c.ConnectorIndex} " +
                            $"(R={c.Radius * FeetToMm:F2}mm)");
                        connSvc.DisconnectAllFromConnector(doc, elemId, c.ConnectorIndex);
                        disconnected++;
                    }
                }
                SmartConLogger.Info($"[Chain+]   a. Disconnect done: {disconnected} connections broken, " +
                    $"всего коннекторов={allConns.Count}");

                var edge = FindEdgeToParent(elemId, nextLevel, graph);
                if (edge is null)
                {
                    SmartConLogger.Warn($"[Chain+]   b. Edge to parent NOT FOUND → skip");
                    continue;
                }
                SmartConLogger.Info($"[Chain+]   b. Edge: parent={edge.Value.ParentId.Value} " +
                    $"parentConnIdx={edge.Value.ParentConnIdx}, elemConnIdx={edge.Value.ElemConnIdx}");

                var parentProxy = connSvc.RefreshConnector(doc, edge.Value.ParentId, edge.Value.ParentConnIdx);
                if (parentProxy is null)
                {
                    SmartConLogger.Warn($"[Chain+]   parentProxy=NULL → skip");
                    continue;
                }
                SmartConLogger.Info($"[Chain+]   parent: R={parentProxy.Radius * FeetToMm:F2}mm " +
                    $"(DN{System.Math.Round(parentProxy.Radius * 2.0 * FeetToMm)}) " +
                    $"origin=({parentProxy.Origin.X:F4},{parentProxy.Origin.Y:F4},{parentProxy.Origin.Z:F4})");

                ElementId? reducerId = null;
                double targetRadius = parentProxy.Radius;
                double targetDn = System.Math.Round(targetRadius * 2.0 * FeetToMm);

                {
                    var elemRefreshed = connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);
                    double elemRadius = elemRefreshed?.Radius ?? 0;
                    double elemDn = System.Math.Round(elemRadius * 2.0 * FeetToMm);
                    double delta = System.Math.Abs(targetRadius - elemRadius);

                    SmartConLogger.Info($"[Chain+]   c. AdjustSize: target={targetRadius * FeetToMm:F2}mm (DN{targetDn}), " +
                        $"elem={elemRadius * FeetToMm:F2}mm (DN{elemDn}), delta={delta * FeetToMm:F4}mm, needsAdjust={delta > 1e-5}");

                    if (elemRefreshed is not null && delta > 1e-5)
                    {
                        SmartConLogger.Info($"[Chain+]   c.1 TrySetConnectorRadius(elemId={elemId.Value}, " +
                            $"connIdx={edge.Value.ElemConnIdx}, target={targetRadius * FeetToMm:F2}mm)...");
                        bool setResult = paramResolver.TrySetConnectorRadius(
                            doc, elemId, edge.Value.ElemConnIdx, targetRadius);
                        SmartConLogger.Info($"[Chain+]   c.1 TrySetConnectorRadius → {(setResult ? "OK" : "FAILED")}");

                        doc.Regenerate();

                        var elem = doc.GetElement(elemId);
                        if (elem is FamilyInstance fiElem)
                        {
                            var allElemConns = connSvc.GetAllConnectors(doc, elemId);
                            SmartConLogger.Info($"[Chain+]   c.2 FamilyInstance '{fiElem.Symbol?.Family?.Name}' " +
                                $"symbolId={fiElem.Symbol?.Id.Value}: {allElemConns.Count} коннекторов (после Regenerate):");
                            foreach (var c in allElemConns)
                                SmartConLogger.Info($"[Chain+]     conn[{c.ConnectorIndex}]: R={c.Radius * FeetToMm:F2}mm, " +
                                    $"isFree={c.IsFree}");

                            foreach (var c in allElemConns)
                            {
                                if (c.ConnectorIndex == edge.Value.ElemConnIdx)
                                    continue;

                                double connDelta = System.Math.Abs(c.Radius - targetRadius);
                                if (connDelta <= 1e-5)
                                {
                                    SmartConLogger.Info($"[Chain+]   c.2 conn[{c.ConnectorIndex}]: " +
                                        $"R={c.Radius * FeetToMm:F2}mm ≈ target — уже верно, skip");
                                    continue;
                                }

                                bool inGraph = false;
                                foreach (var e in graph.Edges)
                                {
                                    if ((comparer.Equals(e.FromElementId, elemId) && e.FromConnectorIndex == c.ConnectorIndex) ||
                                        (comparer.Equals(e.ToElementId, elemId) && e.ToConnectorIndex == c.ConnectorIndex))
                                    {
                                        inGraph = true;
                                        break;
                                    }
                                }
                                if (inGraph)
                                {
                                    SmartConLogger.Info($"[Chain+]   c.2 TrySetConnectorRadius(connIdx={c.ConnectorIndex}, " +
                                        $"currentR={c.Radius * FeetToMm:F2}mm, target={targetRadius * FeetToMm:F2}mm)...");
                                    bool r2 = paramResolver.TrySetConnectorRadius(doc, elemId, c.ConnectorIndex, targetRadius);
                                    SmartConLogger.Info($"[Chain+]   c.2 → {(r2 ? "OK" : "FAILED")}");
                                }
                                else
                                {
                                    SmartConLogger.Info($"[Chain+]   c.2 conn[{c.ConnectorIndex}]: NOT in graph, " +
                                        $"R={c.Radius * FeetToMm:F2}mm ≠ target {targetRadius * FeetToMm:F2}mm — пропущен");
                                }
                            }
                        }

                        doc.Regenerate();

                        if (doc.GetElement(elemId) is FamilyInstance)
                        {
                            var diagConns = connSvc.GetAllConnectors(doc, elemId);
                            foreach (var dc in diagConns)
                                SmartConLogger.Info($"[Chain+]   c.2b diag conn[{dc.ConnectorIndex}]: " +
                                    $"R={dc.Radius * FeetToMm:F2}mm (DN{System.Math.Round(dc.Radius * 2.0 * FeetToMm)}), " +
                                    $"isFree={dc.IsFree}");
                        }

                        elemRefreshed = connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);
                        double actualRadius = elemRefreshed?.Radius ?? 0;
                        double actualDn = System.Math.Round(actualRadius * FeetToMm);
                        double verifyDelta = System.Math.Abs(targetRadius - actualRadius);

                        SmartConLogger.Info($"[Chain+]   c.3 Verification: actualR={actualRadius * FeetToMm:F2}mm " +
                            $"(DN{actualDn}), targetR={targetRadius * FeetToMm:F2}mm (DN{targetDn}), " +
                            $"delta={verifyDelta * FeetToMm:F4}mm, match={verifyDelta <= 1e-5}");

                        if (elemRefreshed is not null && verifyDelta > 1e-5)
                        {
                            SmartConLogger.Info($"[Chain+]   c.3 Adjustment failed → InsertReducer...");
                            reducerId = networkMover.InsertReducer(doc, parentProxy, elemRefreshed);
                            if (reducerId is not null)
                            {
                                SmartConLogger.Info($"[Chain+]   c.3 Reducer inserted: id={reducerId.Value}");
                                snapshotStore.TrackReducer(elemId, reducerId);
                            }
                            else
                            {
                                SmartConLogger.Warn($"[Chain+]   c.3 Reducer not found in mapping!");
                            }
                        }
                    }
                    else
                    {
                        SmartConLogger.Info($"[Chain+]   c. Sizes match, no adjustment needed");
                    }

                    doc.Regenerate();
                }

                parentProxy = connSvc.RefreshConnector(doc, edge.Value.ParentId, edge.Value.ParentConnIdx);
                var elemProxyForAlign = connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);

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
                        SmartConLogger.Info($"[Chain+]   d. Align target = reducer conn2 " +
                            $"(R={alignTarget?.Radius * FeetToMm:F2}mm, " +
                            $"origin=({alignTarget?.Origin.X:F4},{alignTarget?.Origin.Y:F4},{alignTarget?.Origin.Z:F4}))");
                    }
                }

                if (alignTarget is not null && elemProxyForAlign is not null)
                {
                    SmartConLogger.Info($"[Chain+]   d. Align: elem R={elemProxyForAlign.Radius * FeetToMm:F2}mm " +
                        $"→ target R={alignTarget.Radius * FeetToMm:F2}mm");

                    var alignResult = ConnectorAligner.ComputeAlignment(
                        alignTarget.OriginVec3, alignTarget.BasisZVec3, alignTarget.BasisXVec3,
                        elemProxyForAlign.OriginVec3, elemProxyForAlign.BasisZVec3, elemProxyForAlign.BasisXVec3);

                    if (!VectorUtils.IsZero(alignResult.InitialOffset))
                    {
                        SmartConLogger.Info($"[Chain+]   d. Move offset=({alignResult.InitialOffset.X * FeetToMm:F2}," +
                            $"{alignResult.InitialOffset.Y * FeetToMm:F2},{alignResult.InitialOffset.Z * FeetToMm:F2})mm");
                        transformSvc.MoveElement(doc, elemId, alignResult.InitialOffset);
                    }
                    if (alignResult.BasisZRotation is { } bzRot)
                    {
                        SmartConLogger.Info($"[Chain+]   d. RotateBZ angle={bzRot.AngleRadians * 180 / System.Math.PI:F2}°");
                        transformSvc.RotateElement(doc, elemId,
                            alignResult.RotationCenter, bzRot.Axis, bzRot.AngleRadians);
                    }
                    if (alignResult.BasisXSnap is { } bxSnap)
                    {
                        SmartConLogger.Info($"[Chain+]   d. RotateBX angle={bxSnap.AngleRadians * 180 / System.Math.PI:F2}°");
                        transformSvc.RotateElement(doc, elemId,
                            alignResult.RotationCenter, bxSnap.Axis, bxSnap.AngleRadians);
                    }

                    doc.Regenerate();

                    var refreshedAfterAlign = connSvc.RefreshConnector(doc, elemId, edge.Value.ElemConnIdx);
                    if (refreshedAfterAlign is not null)
                    {
                        var correction = alignTarget.OriginVec3 - refreshedAfterAlign.OriginVec3;
                        if (!VectorUtils.IsZero(correction))
                        {
                            SmartConLogger.Info($"[Chain+]   d. PosCorrection dist={VectorUtils.Length(correction) * FeetToMm:F3}mm");
                            transformSvc.MoveElement(doc, elemId, correction);
                        }
                    }
                    doc.Regenerate();
                }

                if (reducerId is not null && parentProxy is not null)
                {
                    SmartConLogger.Info($"[Chain+]   e. ConnectTo via reducer id={reducerId.Value}");
                    var rConnsForConnect = connSvc.GetAllFreeConnectors(doc, reducerId);
                    SmartConLogger.Info($"[Chain+]   e. Reducer free conns: {rConnsForConnect.Count}");
                    var rConn1 = rConnsForConnect
                        .OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, parentProxy.OriginVec3))
                        .FirstOrDefault();
                    var rConn2 = rConnsForConnect.FirstOrDefault(c => c.ConnectorIndex != (rConn1?.ConnectorIndex ?? -1));

                    if (rConn1 is not null)
                    {
                        SmartConLogger.Info($"[Chain+]   e. ConnectTo: parent({edge.Value.ParentId.Value}:{edge.Value.ParentConnIdx}) ↔ reducer({reducerId.Value}:{rConn1.ConnectorIndex})");
                        connSvc.ConnectTo(doc, edge.Value.ParentId, edge.Value.ParentConnIdx,
                            reducerId, rConn1.ConnectorIndex);
                    }
                    if (rConn2 is not null)
                    {
                        SmartConLogger.Info($"[Chain+]   e. ConnectTo: reducer({reducerId.Value}:{rConn2.ConnectorIndex}) ↔ elem({elemId.Value}:{edge.Value.ElemConnIdx})");
                        connSvc.ConnectTo(doc, reducerId, rConn2.ConnectorIndex,
                            elemId, edge.Value.ElemConnIdx);
                    }
                }
                else
                {
                    SmartConLogger.Info($"[Chain+]   e. ConnectTo direct: parent({edge.Value.ParentId.Value}:{edge.Value.ParentConnIdx}) ↔ elem({elemId.Value}:{edge.Value.ElemConnIdx})");
                    connSvc.ConnectTo(doc, edge.Value.ParentId, edge.Value.ParentConnIdx,
                        elemId, edge.Value.ElemConnIdx);
                }

                SmartConLogger.Info($"[Chain+] ── Element {elemId.Value} ready ──");
            }

            doc.Regenerate();
        });

        SmartConLogger.Info($"[Chain+] ═══ LEVEL {nextLevel} DONE ═══");
    }

    public void DecrementLevel(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectionGraph graph,
        NetworkSnapshotStore snapshotStore,
        int currentDepth)
    {
        var levelElements = graph.Levels[currentDepth];

        SmartConLogger.Info($"[Chain−] ═══ ROLLBACK LEVEL {currentDepth} ═══ ({levelElements.Count} elements)");

        groupSession.RunInTransaction($"Цепочка: откат уровня {currentDepth}", doc =>
        {
            foreach (var elemId in levelElements)
            {
                var elemRaw = doc.GetElement(elemId);
                SmartConLogger.Info($"[Chain−] ── Element id={elemId.Value} '{elemRaw?.Name}' ({elemRaw?.GetType().Name}) ──");

                var allConns = connSvc.GetAllConnectors(doc, elemId);
                int disconnected = 0;
                foreach (var c in allConns)
                {
                    if (!c.IsFree)
                    {
                        connSvc.DisconnectAllFromConnector(doc, elemId, c.ConnectorIndex);
                        disconnected++;
                    }
                }
                SmartConLogger.Info($"[Chain−]   a. Disconnect: {disconnected} connections broken");

                var reducers = snapshotStore.GetReducers(elemId);
                SmartConLogger.Info($"[Chain−]   b. Reducers to delete: {reducers.Count}");
                foreach (var reducerId in reducers)
                {
                    SmartConLogger.Info($"[Chain−]   b. Deleting reducer id={reducerId.Value}");
                    var rConns = connSvc.GetAllConnectors(doc, reducerId);
                    foreach (var rc in rConns)
                    {
                        if (!rc.IsFree)
                            connSvc.DisconnectAllFromConnector(doc, reducerId, rc.ConnectorIndex);
                    }
                    fittingInsertSvc.DeleteElement(doc, reducerId);
                }

                var snapshot = snapshotStore.Get(elemId);
                if (snapshot is null)
                {
                    SmartConLogger.Warn($"[Chain−]   c. Snapshot not found → skip");
                    continue;
                }
                var elem = doc.GetElement(elemId);
                SmartConLogger.Info($"[Chain−]   c. Restoring: isMepCurve={snapshot.IsMepCurve}, " +
                    $"snapR={snapshot.ConnectorRadius * FeetToMm:F2}mm (DN{System.Math.Round(snapshot.ConnectorRadius * 2.0 * FeetToMm)}), " +
                    $"symbolId={snapshot.FamilySymbolId?.Value}");

                if (elem is MEPCurve mc)
                {
                    var diamParam = mc.get_Parameter(BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                    if (diamParam is not null && !diamParam.IsReadOnly)
                    {
                        double targetDiam = snapshot.ConnectorRadius * 2.0;
                        SmartConLogger.Info($"[Chain−]   c. MEPCurve: restore diameter={targetDiam * FeetToMm:F2}mm");
                        diamParam.Set(targetDiam);
                    }
                    else
                    {
                        SmartConLogger.Info($"[Chain−]   c. MEPCurve: TrySetConnectorRadius fallback...");
                        var conns = connSvc.GetAllConnectors(doc, elemId);
                        if (conns.Count > 0)
                            paramResolver.TrySetConnectorRadius(doc, elemId, conns[0].ConnectorIndex, snapshot.ConnectorRadius);
                    }
                    doc.Regenerate();

                    if (snapshot.CurveStart is not null && snapshot.CurveEnd is not null
                        && mc.Location is LocationCurve lc && lc.Curve is Line)
                    {
                        SmartConLogger.Info($"[Chain−]   c. MEPCurve: restore curve " +
                            $"({snapshot.CurveStart.X:F4},{snapshot.CurveStart.Y:F4},{snapshot.CurveStart.Z:F4}) → " +
                            $"({snapshot.CurveEnd.X:F4},{snapshot.CurveEnd.Y:F4},{snapshot.CurveEnd.Z:F4})");
                        try
                        {
                            lc.Curve = Line.CreateBound(snapshot.CurveStart, snapshot.CurveEnd);
                        }
                        catch (Exception exCurve)
                        {
                            SmartConLogger.Warn($"[Chain−]   c. MEPCurve: Line.CreateBound failed: {exCurve.Message}");
                        }
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
                                SmartConLogger.Info($"[Chain−]   c. MEPCurve(FlexPipe): MoveElement to snap connector, " +
                                    $"dist={VectorUtils.Length(offset) * FeetToMm:F2}mm");
                                transformSvc.MoveElement(doc, elemId, offset);
                            }
                        }
                    }
                    else
                    {
                        SmartConLogger.Info($"[Chain−]   c. MEPCurve: skip restore (no position data)");
                    }
                    doc.Regenerate();
                }
                else if (elem is FamilyInstance fi && snapshot.FiOrigin is not null)
                {
                    if (snapshot.FamilySymbolId is not null && fi.Symbol.Id != snapshot.FamilySymbolId)
                    {
                        SmartConLogger.Info($"[Chain−]   c. FI: ChangeTypeId {fi.Symbol.Id.Value} → {snapshot.FamilySymbolId.Value}");
                        fi.ChangeTypeId(snapshot.FamilySymbolId);
                    }

                    var fiConns = connSvc.GetAllConnectors(doc, elemId);
                    foreach (var fc in fiConns)
                    {
                        double targetR = snapshot.ConnectorRadii.TryGetValue(fc.ConnectorIndex, out var snapR)
                            ? snapR
                            : snapshot.ConnectorRadius;
                        double delta = System.Math.Abs(fc.Radius - targetR);
                        if (delta > 1e-5)
                        {
                            SmartConLogger.Info($"[Chain−]   c. FI: TrySetConnectorRadius(connIdx={fc.ConnectorIndex}, " +
                                $"current={fc.Radius * FeetToMm:F2}mm → target={targetR * FeetToMm:F2}mm)");
                            paramResolver.TrySetConnectorRadius(doc, elemId, fc.ConnectorIndex, targetR);
                        }
                    }
                    doc.Regenerate();

                    if (fi.Location is LocationPoint lp)
                    {
                        SmartConLogger.Info($"[Chain−]   c. FI: set Point=({snapshot.FiOrigin.X:F4},{snapshot.FiOrigin.Y:F4},{snapshot.FiOrigin.Z:F4})");
                        lp.Point = snapshot.FiOrigin;
                    }
                    doc.Regenerate();

                    var currentT = fi.GetTransform();
                    var curBZ = new Vec3(currentT.BasisZ.X, currentT.BasisZ.Y, currentT.BasisZ.Z);
                    var snapBZ = new Vec3(snapshot.FiBasisZ!.X, snapshot.FiBasisZ.Y, snapshot.FiBasisZ.Z);
                    SmartConLogger.Info($"[Chain−]   c. FI: curBZ=({curBZ.X:F3},{curBZ.Y:F3},{curBZ.Z:F3}), " +
                        $"snapBZ=({snapBZ.X:F3},{snapBZ.Y:F3},{snapBZ.Z:F3})");

                    double angleBZ = VectorUtils.AngleBetween(curBZ, snapBZ);
                    if (angleBZ > 1e-6 && angleBZ < System.Math.PI - 1e-6)
                    {
                        var axisBZ = VectorUtils.CrossProduct(curBZ, snapBZ);
                        double axisLen = VectorUtils.Length(axisBZ);
                        if (axisLen > 1e-10)
                        {
                            axisBZ = new Vec3(axisBZ.X / axisLen, axisBZ.Y / axisLen, axisBZ.Z / axisLen);
                            SmartConLogger.Info($"[Chain−]   c. FI: RotBZ angle={angleBZ * 180 / System.Math.PI:F2}°");
                            transformSvc.RotateElement(doc, elemId,
                                new Vec3(snapshot.FiOrigin.X, snapshot.FiOrigin.Y, snapshot.FiOrigin.Z),
                                axisBZ, angleBZ);
                            doc.Regenerate();
                        }
                    }
                    else if (angleBZ >= System.Math.PI - 1e-6)
                    {
                        var perpAxis = System.Math.Abs(curBZ.Z) < 0.9
                            ? new Vec3(0, 0, 1) : new Vec3(1, 0, 0);
                        SmartConLogger.Info($"[Chain−]   c. FI: RotBZ 180° (antiparallel)");
                        transformSvc.RotateElement(doc, elemId,
                            new Vec3(snapshot.FiOrigin.X, snapshot.FiOrigin.Y, snapshot.FiOrigin.Z),
                            perpAxis, System.Math.PI);
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
                        SmartConLogger.Info($"[Chain−]   c. FI: RotBX angle={signedAngle * 180 / System.Math.PI:F2}°");
                        transformSvc.RotateElement(doc, elemId,
                            new Vec3(snapshot.FiOrigin.X, snapshot.FiOrigin.Y, snapshot.FiOrigin.Z),
                            rotAxis, signedAngle);
                        doc.Regenerate();
                    }

                    if (fi.Location is LocationPoint lp2)
                    {
                        var correction = new Vec3(
                            snapshot.FiOrigin.X - lp2.Point.X,
                            snapshot.FiOrigin.Y - lp2.Point.Y,
                            snapshot.FiOrigin.Z - lp2.Point.Z);
                        if (!VectorUtils.IsZero(correction))
                        {
                            SmartConLogger.Info($"[Chain−]   c. FI: final correction={VectorUtils.Length(correction) * FeetToMm:F2}mm");
                            transformSvc.MoveElement(doc, elemId, correction);
                        }
                        doc.Regenerate();
                    }
                }

                SmartConLogger.Info($"[Chain−]   d. Restoring connections: {snapshot.Connections.Count} records");
                foreach (var connRecord in snapshot.Connections)
                {
                    var neighborId = connRecord.NeighborElementId;
                    bool inChain = IsInCurrentChain(neighborId, currentDepth - 1, graph);
                    SmartConLogger.Info($"[Chain−]   d. connRecord: this={connRecord.ThisElementId.Value}:{connRecord.ThisConnectorIndex} " +
                        $"↔ neighbor={neighborId.Value}:{connRecord.NeighborConnectorIndex}, inChain={inChain}");
                    if (inChain) continue;

                    var neighborConn = connSvc.RefreshConnector(doc, neighborId, connRecord.NeighborConnectorIndex);
                    if (neighborConn is null)
                    {
                        SmartConLogger.Warn($"[Chain−]   d. neighborConn=null → skip");
                        continue;
                    }
                    if (!neighborConn.IsFree)
                    {
                        SmartConLogger.Info($"[Chain−]   d. neighbor busy → disconnect first");
                        connSvc.DisconnectAllFromConnector(doc, neighborId, connRecord.NeighborConnectorIndex);
                    }

                    try
                    {
                        connSvc.ConnectTo(doc,
                            connRecord.ThisElementId, connRecord.ThisConnectorIndex,
                            connRecord.NeighborElementId, connRecord.NeighborConnectorIndex);
                        SmartConLogger.Info($"[Chain−]   d. ConnectTo OK");
                    }
                    catch (Exception exConn)
                    {
                        SmartConLogger.Warn($"[Chain−]   d. ConnectTo FAILED: {exConn.Message}");
                    }
                }
            }

            doc.Regenerate();
        });
    }

    public void WarmDepsForLevel(
        Document doc,
        IReadOnlyList<ElementId> levelElements,
        HashSet<long> warmedElementIds)
    {
        int warmedCount = 0;
        foreach (var elemId in levelElements)
        {
            if (!warmedElementIds.Add(elemId.Value))
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
            SmartConLogger.Info($"[Chain] WarmDeps: warmed {warmedCount} elements for level");
    }

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
            FirstConnectorOrigin = firstConnOrigin,
            FirstConnectorIndex = firstConnIdx,
            ConnectorRadius = connRadius,
            ConnectorRadii = connRadiiDict,
            FamilySymbolId = familySymbolId,
            Connections = graph.GetOriginalConnections(elemId),
        };
    }

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
