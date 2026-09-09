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

    /// <summary>
    /// Restore an element's full state (size/symbol for pipes and family instances,
    /// location curve or flex path, position and orientation) from a snapshot taken
    /// by <see cref="CaptureSnapshot"/>. Does not restore connections — the caller
    /// handles reconnects. Public for the "Блокировать" root baseline restore (#167).
    /// </summary>
    public void RestoreElementFromSnapshot(Document doc, ElementId elemId, ElementSnapshot snapshot)
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
    public ElementSnapshot CaptureSnapshot(Document doc, ElementId elemId, ConnectionGraph? graph)
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
            Connections = graph?.GetOriginalConnections(elemId) ?? [],
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

        // Restore verification: residual error between the restored basis and the
        // snapshot — catches cumulative drift from repeated rollback/attach cycles
        // (sign noise at ~180° in the signed-angle branch above).
        currentT = fi.GetTransform();
        var finBX = new Vec3(currentT.BasisX.X, currentT.BasisX.Y, currentT.BasisX.Z);
        var finBZ = new Vec3(currentT.BasisZ.X, currentT.BasisZ.Y, currentT.BasisZ.Z);
        double errBX = VectorUtils.AngleBetween(finBX, snapBX) * 180.0 / System.Math.PI;
        double errBZ = VectorUtils.AngleBetween(finBZ, snapBZ) * 180.0 / System.Math.PI;
        if (errBX > 0.1 || errBZ > 0.1)
        {
            SmartConLogger.Warn($"   c. FI: restore rotation residual for {elemId.GetValue()}: " +
                $"errBX={errBX:F3}°, errBZ={errBZ:F3}° (angleBX was {angleBX * 180.0 / System.Math.PI:F2}°) " +
                $"[Action: сообщите разработчикам — накопление ошибки восстановления вращения]");
        }
        else
        {
            SmartConLogger.Debug($"   c. FI: restore rotation residual: errBX={errBX:F3}°, errBZ={errBZ:F3}°");
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
}
