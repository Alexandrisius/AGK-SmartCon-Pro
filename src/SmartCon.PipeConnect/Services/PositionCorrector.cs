using Autodesk.Revit.DB;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Corrects element positions after connect operations by applying offsets and snapping connectors to targets.
/// </summary>
public static class PositionCorrector
{
    public static void SnapToTarget(
        Document doc,
        IConnectorService connSvc,
        ITransformService transformSvc,
        ElementId elementId,
        int connIdx,
        Vec3 targetOrigin,
        bool regenerate = true)
    {
        var refreshed = connSvc.RefreshConnector(doc, elementId, connIdx);
        if (refreshed is null) return;

        var offset = targetOrigin - refreshed.OriginVec3;
        if (!VectorUtils.IsZero(offset))
            transformSvc.MoveElement(doc, elementId, offset);

        if (regenerate)
            doc.Regenerate();
    }

    public static void ApplyOffset(
        Document doc,
        ITransformService transformSvc,
        ElementId elementId,
        Vec3 offset,
        bool regenerate = true)
    {
        if (!VectorUtils.IsZero(offset))
            transformSvc.MoveElement(doc, elementId, offset);

        if (regenerate)
            doc.Regenerate();
    }

    public static ConnectorProxy? ValidateAndSnap(
        Document doc,
        IConnectorService connSvc,
        ITransformService transformSvc,
        ConnectorProxy current,
        Vec3 targetOrigin,
        double epsilon,
        bool regenerate = true)
    {
        var dist = VectorUtils.DistanceTo(current.OriginVec3, targetOrigin);
        if (dist <= epsilon) return current;

        var correction = targetOrigin - current.OriginVec3;
        transformSvc.MoveElement(doc, current.OwnerElementId, correction);

        if (regenerate)
            doc.Regenerate();

        return connSvc.RefreshConnector(doc, current.OwnerElementId, current.ConnectorIndex) ?? current;
    }
}
