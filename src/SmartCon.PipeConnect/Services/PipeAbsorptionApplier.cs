using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Services.Interfaces;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Shared applier for per-level pipe displacement absorption (ADR-052):
/// changes a straight pipe's length (LocationCurve) or a flex pipe's point path
/// (FlexPipe.Points) so the end facing entryPoint follows the offset, instead of
/// moving the element rigidly. Used by ChainOperationHandler (level increments)
/// and PipeConnectInitHandler (initial dynamic alignment for pipe dynamics).
/// </summary>
internal static class PipeAbsorptionApplier
{
    /// <summary>
    /// Move an element by offset, absorbing the offset into the element's own
    /// geometry when it is a straight pipe (Line) or a FlexPipe — the end facing
    /// entryPoint follows the offset (pipe shortens/lengthens), the far end keeps
    /// only the non-absorbed remainder, so the downstream network stays put.
    /// Falls back to rigid MoveElement for all other elements.
    /// Use everywhere the dynamic element is repositioned to make room for
    /// inserted fittings/reducers of arbitrary length (ADR-052 addendum).
    /// </summary>
    public static void MoveOrAbsorb(
        Document doc,
        ITransformService transformSvc,
        ElementId elemId,
        Vec3 entryPoint,
        Vec3 offset)
    {
        if (VectorUtils.IsZero(offset))
            return;

        if (!TryApply(doc, elemId, entryPoint, offset))
            transformSvc.MoveElement(doc, elemId, offset);
    }

    /// <summary>
    /// Try to absorb the offset into the element's own geometry.
    /// Returns true when the element is a straight pipe (Line) or a FlexPipe and
    /// the absorption was applied; false → caller falls back to rigid move.
    /// </summary>
    public static bool TryApply(Document doc, ElementId elemId, Vec3 entryPoint, Vec3 offset)
    {
        var elem = doc.GetElement(elemId);
        if (elem is FlexPipe flexPipe)
            return TryAbsorbFlexPipe(doc, elemId, flexPipe, entryPoint, offset);

        if (elem is MEPCurve mc
            && mc.Location is LocationCurve lc
            && lc.Curve is Line line)
        {
            return TryAbsorbStraightPipe(doc, elemId, entryPoint, offset, line);
        }

        return false;
    }

    private static bool TryAbsorbStraightPipe(
        Document doc,
        ElementId elemId,
        Vec3 entryPoint,
        Vec3 offset,
        Line line)
    {
        var p0 = line.GetEndPoint(0);
        var p1 = line.GetEndPoint(1);
        var op = PipeLengthAbsorber.Compute(
            elemId.GetValue(),
            new Vec3(p0.X, p0.Y, p0.Z),
            new Vec3(p1.X, p1.Y, p1.Z),
            entryPoint,
            offset,
            PipeAbsorption.MinPipeLengthFt);
        if (op is null)
            return false;

        var newStart = new XYZ(p0.X + op.StartDelta.X, p0.Y + op.StartDelta.Y, p0.Z + op.StartDelta.Z);
        var newEnd = new XYZ(p1.X + op.EndDelta.X, p1.Y + op.EndDelta.Y, p1.Z + op.EndDelta.Z);

        var mc = (MEPCurve)doc.GetElement(elemId);
        try
        {
            ((LocationCurve)mc.Location).Curve = Line.CreateBound(newStart, newEnd);
            doc.Regenerate();
            SmartConLogger.Debug($"    d. Absorb: pipe {elemId.GetValue()} " +
                $"offset={VectorUtils.Length(offset) * FeetToMm:F1}mm, " +
                $"absorbed={op.AbsorbedLengthFt * FeetToMm:F1}mm, " +
                $"newLen={newStart.DistanceTo(newEnd) * FeetToMm:F1}mm");
        }
        catch (Exception exCurve)
        {
            SmartConLogger.Warn($"    d. Absorb: set curve failed: {exCurve.Message} " +
                $"[Action: проверьте длину трубы и соединения вокруг, подключите вручную]");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Flex pipe absorption: only the endpoint facing the parent moves by the full
    /// offset via the FlexPipe.Points setter (LocationCurve.Curve assignment throws
    /// for flex elements — see ADR-052). Intermediate points are preserved exactly;
    /// Revit maintains connectivity automatically.
    /// </summary>
    private static bool TryAbsorbFlexPipe(
        Document doc,
        ElementId elemId,
        FlexPipe flexPipe,
        Vec3 entryPoint,
        Vec3 offset)
    {
        var currentPoints = flexPipe.Points;
        var vecPoints = new List<Vec3>(currentPoints.Count);
        foreach (var p in currentPoints)
            vecPoints.Add(new Vec3(p.X, p.Y, p.Z));

        var newPath = PipeLengthAbsorber.ComputeFlexPath(
            vecPoints, entryPoint, offset, PipeAbsorption.MinPipeLengthFt);
        if (newPath is null)
        {
            SmartConLogger.Debug($"    d. Absorb: flex path too short after offset → rigid align");
            return false;
        }

        var xyzPoints = new List<XYZ>(newPath.Count);
        foreach (var v in newPath)
            xyzPoints.Add(new XYZ(v.X, v.Y, v.Z));

        try
        {
            flexPipe.Points = xyzPoints;
            doc.Regenerate();
            SmartConLogger.Debug($"    d. Absorb: flexpipe {elemId.GetValue()} " +
                $"offset={VectorUtils.Length(offset) * FeetToMm:F1}mm, points={xyzPoints.Count}");
        }
        catch (Exception exFlex)
        {
            SmartConLogger.Warn($"    d. Absorb: flex points failed: {exFlex.Message} " +
                $"[Action: проверьте форму гибкой трубы и соединения вокруг, подключите вручную]");
            return false;
        }

        return true;
    }
}
