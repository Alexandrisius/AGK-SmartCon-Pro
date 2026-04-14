using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

public static class PipeConnectDiagnostics
{
    public static void LogConnectorState(
        Document doc,
        ConnectorProxy staticConnector,
        ConnectorProxy dynamicConnector,
        ElementId? currentFittingId,
        IConnectorService connSvc,
        string label)
    {
        try
        {
            var dynR = connSvc.RefreshConnector(doc, dynamicConnector.OwnerElementId, dynamicConnector.ConnectorIndex)
                ?? dynamicConnector;

            SmartConLogger.DebugSection($"[DIAG {label}]");

            var st = staticConnector;
            SmartConLogger.Debug($"  static: origin=({st.Origin.X:F4},{st.Origin.Y:F4},{st.Origin.Z:F4}) R={st.Radius * FeetToMm:F2}mm Z=({st.BasisZ.X:F3},{st.BasisZ.Y:F3},{st.BasisZ.Z:F3})");

            if (currentFittingId is not null)
            {
                var fConns = connSvc.GetAllFreeConnectors(doc, currentFittingId).ToList();
                foreach (var fc in fConns)
                {
                    var angleToStatic = VectorUtils.AngleBetween(fc.BasisZVec3, st.BasisZVec3) * 180.0 / System.Math.PI;
                    var angleToDyn = VectorUtils.AngleBetween(fc.BasisZVec3, dynR.BasisZVec3) * 180.0 / System.Math.PI;
                    var distToStatic = VectorUtils.DistanceTo(fc.OriginVec3, st.OriginVec3) * FeetToMm;
                    var distToDyn = VectorUtils.DistanceTo(fc.OriginVec3, dynR.OriginVec3) * FeetToMm;
                    SmartConLogger.Debug($"  fitting conn#{fc.ConnectorIndex}: origin=({fc.Origin.X:F4},{fc.Origin.Y:F4},{fc.Origin.Z:F4}) R={fc.Radius * FeetToMm:F2}mm Z=({fc.BasisZ.X:F3},{fc.BasisZ.Y:F3},{fc.BasisZ.Z:F3})");
                    SmartConLogger.Debug($"    dist→static={distToStatic:F3}mm  dist→dyn={distToDyn:F3}mm  ∠→static={angleToStatic:F1}°  ∠→dyn={angleToDyn:F1}°");
                }
            }

            var angleZ = VectorUtils.AngleBetween(st.BasisZVec3, dynR.BasisZVec3) * 180.0 / System.Math.PI;
            var distSD = VectorUtils.DistanceTo(st.OriginVec3, dynR.OriginVec3) * FeetToMm;
            SmartConLogger.Debug($"  dynamic: origin=({dynR.Origin.X:F4},{dynR.Origin.Y:F4},{dynR.Origin.Z:F4}) R={dynR.Radius * FeetToMm:F2}mm Z=({dynR.BasisZ.X:F3},{dynR.BasisZ.Y:F3},{dynR.BasisZ.Z:F3})");
            SmartConLogger.Debug($"    dist→static={distSD:F3}mm  ∠Z(static↔dyn)={angleZ:F1}°");

            var dynElem = doc.GetElement(dynR.OwnerElementId);
            if (dynElem is FamilyInstance fi)
            {
                var t = fi.GetTransform();
                var basisXAngle = System.Math.Atan2(t.BasisX.Y, t.BasisX.X) * 180.0 / System.Math.PI;
                var basisYAngle = System.Math.Atan2(t.BasisY.Y, t.BasisY.X) * 180.0 / System.Math.PI;
                SmartConLogger.Debug($"  dynamic Transform: origin=({t.Origin.X:F4},{t.Origin.Y:F4},{t.Origin.Z:F4}) BX=({t.BasisX.X:F3},{t.BasisX.Y:F3},{t.BasisX.Z:F3}) BY=({t.BasisY.X:F3},{t.BasisY.Y:F3},{t.BasisY.Z:F3}) BZ=({t.BasisZ.X:F3},{t.BasisZ.Y:F3},{t.BasisZ.Z:F3})");
                SmartConLogger.Debug($"  dynamic BasisX angle in XY={basisXAngle:F2}°  BasisY angle in XY={basisYAngle:F2}°");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"[DIAG {label}] Logging error: {ex.Message}");
        }
    }
}
