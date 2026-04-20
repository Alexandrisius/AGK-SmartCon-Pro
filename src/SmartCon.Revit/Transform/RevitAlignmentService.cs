using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Compatibility;

using static SmartCon.Core.Units;

namespace SmartCon.Revit.Transform;

public sealed class RevitAlignmentService(
    IConnectorService connSvc,
    ITransformService transformSvc) : IAlignmentService
{
    public void ApplyAlignment(
        Document doc,
        ElementId elementId,
        ConnectorProxy alignTarget,
        ConnectorProxy currentElement)
    {
        var alignResult = ConnectorAligner.ComputeAlignment(
            alignTarget.OriginVec3, alignTarget.BasisZVec3, alignTarget.BasisXVec3,
            currentElement.OriginVec3, currentElement.BasisZVec3, currentElement.BasisXVec3);

        ApplyCore(doc, elementId, alignResult, alignTarget.OriginVec3, currentElement.ConnectorIndex);
    }

    public void ApplyAlignment(
        Document doc,
        ElementId elementId,
        AlignmentResult alignResult,
        int positionCorrectionConnIdx = -1)
    {
        ApplyCore(doc, elementId, alignResult, alignResult.RotationCenter, positionCorrectionConnIdx);
    }

    private void ApplyCore(
        Document doc,
        ElementId elementId,
        AlignmentResult alignResult,
        Vec3 positionTarget,
        int connIdx)
    {
        if (!VectorUtils.IsZero(alignResult.InitialOffset))
            transformSvc.MoveElement(doc, elementId, alignResult.InitialOffset);

        if (alignResult.BasisZRotation is { } bzRot)
            transformSvc.RotateElement(doc, elementId,
                alignResult.RotationCenter, bzRot.Axis, bzRot.AngleRadians);

        if (alignResult.BasisXSnap is { } bxSnap)
            transformSvc.RotateElement(doc, elementId,
                alignResult.RotationCenter, bxSnap.Axis, bxSnap.AngleRadians);

        doc.Regenerate();

        if (connIdx >= 0)
        {
            var refreshed = connSvc.RefreshConnector(doc, elementId, connIdx);
            if (refreshed is not null)
            {
                var correction = positionTarget - refreshed.OriginVec3;
                if (!VectorUtils.IsZero(correction))
                    transformSvc.MoveElement(doc, elementId, correction);
            }
        }

        doc.Regenerate();
    }
}
