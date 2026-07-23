using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Compatibility;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Handles rotation of the active dynamic element during PipeConnect sessions.
/// In element-wise chain mode the active dynamic is always the queue tail, so
/// the rotation set is just the dynamic itself plus the fitting/reducer of its
/// connection point (children are attached later, already aligned to the
/// rotated connector). The rotation axis is the BasisZ of the parent connector
/// the dynamic is attached to (its "local static").
/// </summary>
public sealed class PipeConnectRotationHandler(
    ITransformService transformSvc)
{
    /// <summary>
    /// Rotate the active dynamic (and its connection point fitting/reducer)
    /// around the BasisZ axis of <paramref name="rotationAxisConnector"/>.
    /// </summary>
    /// <param name="doc">Active Revit document.</param>
    /// <param name="groupSession">Active transaction group session.</param>
    /// <param name="activeDynamic">Connector of the active dynamic element (must not be null).</param>
    /// <param name="rotationAxisConnector">
    /// Parent connector the dynamic is attached to — provides the rotation axis
    /// (Origin + BasisZ). For the root connection point this is the static connector.
    /// </param>
    /// <param name="fittingId">Fitting of the active connection point, if any.</param>
    /// <param name="reducerId">Reducer of the active connection point, if any.</param>
    /// <param name="angleDeg">Rotation angle in degrees (positive = counterclockwise).</param>
    public void ExecuteRotation(
        Document doc,
        ITransactionGroupSession groupSession,
        ConnectorProxy activeDynamic,
        ConnectorProxy rotationAxisConnector,
        ElementId? fittingId,
        ElementId? reducerId,
        int angleDeg)
    {
        var dynId = activeDynamic.OwnerElementId;
        using var _scope = SmartConLogger.BeginScope("Rotate",
            ("DynId", dynId.GetValue()),
            ("Angle", angleDeg),
            ("AxisOwner", rotationAxisConnector.OwnerElementId.GetValue()));
        SmartConLogger.Info($"START fitting={fittingId?.GetValue()}, reducer={reducerId?.GetValue()}");

        groupSession.RunInTransaction(LocalizationService.GetString("Tx_Rotate"), d =>
        {
            var axisOrigin = rotationAxisConnector.OriginVec3;
            var axisDir = rotationAxisConnector.BasisZVec3;
            var radians = angleDeg * System.Math.PI / 180.0;

            var idsToRotate = new List<ElementId> { dynId };
            if (fittingId is not null)
                idsToRotate.Add(fittingId);
            if (reducerId is not null)
                idsToRotate.Add(reducerId);

            // Elements still attached to OTHER connectors of the dynamic rotate with it
            // (rigid-body semantics — matches Revit UI behaviour).
            var activeIdx = activeDynamic.ConnectorIndex;
            var dynElem = d.GetElement(dynId);
            ConnectorManager? cm = dynElem switch
            {
                FamilyInstance fi => fi.MEPModel?.ConnectorManager,
                MEPCurve mc => mc.ConnectorManager,
                _ => null
            };
            if (cm is not null)
            {
                foreach (Connector c in cm.Connectors)
                {
                    if (c.ConnectorType == ConnectorType.Curve) continue;
                    if ((int)c.Id == activeIdx) continue;
                    if (!c.IsConnected) continue;
                    foreach (Connector refConn in c.AllRefs)
                    {
                        var refId = refConn.Owner?.Id;
                        if (refId is not null && refId != dynId && !idsToRotate.Contains(refId))
                            idsToRotate.Add(refId);
                    }
                }
            }

            SmartConLogger.Debug($"Rotating {idsToRotate.Count} elements");
            transformSvc.RotateElements(d, idsToRotate, axisOrigin, axisDir, radians);
            d.Regenerate();

            var dynElemForSnap = d.GetElement(dynId);
            if (dynElemForSnap is FamilyInstance fiForSnap)
            {
                var t = fiForSnap.GetTransform();
                var elemBasisY = new Vec3(t.BasisY.X, t.BasisY.Y, t.BasisY.Z);
                var globalYSnap = ConnectorAligner.ComputeGlobalYAlignmentSnap(
                    axisDir, elemBasisY, axisOrigin);
                if (globalYSnap is not null)
                {
                    SmartConLogger.Debug("GlobalYSnap applied");
                    transformSvc.RotateElement(d, dynId,
                        axisOrigin, globalYSnap.Axis, globalYSnap.AngleRadians);
                }
                else
                {
                    SmartConLogger.Debug("GlobalYSnap skipped (null)");
                }
            }

            d.Regenerate();
        });

        SmartConLogger.Info($"DONE angle={angleDeg}°");
    }
}
