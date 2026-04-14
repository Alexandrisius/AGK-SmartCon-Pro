using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.Services;

public sealed class PipeConnectRotationHandler(
    ITransformService transformSvc)
{
    public void ExecuteRotation(
        Document doc,
        ITransactionGroupSession groupSession,
        PipeConnectSessionContext ctx,
        ConnectorProxy? activeDynamic,
        ElementId? fittingId,
        ElementId? reducerId,
        ConnectionGraph? chainGraph,
        NetworkSnapshotStore snapshotStore,
        int chainDepth,
        int angleDeg)
    {
        var dynId = ctx.DynamicConnector.OwnerElementId;
        SmartConLogger.Info($"[Rotate] START angle={angleDeg}°, dynId={dynId.Value}, fitting={fittingId?.Value}, reducer={reducerId?.Value}");

        groupSession.RunInTransaction("PipeConnect — Поворот", d =>
        {
            var axisOrigin = ctx.StaticConnector.OriginVec3;
            var axisDir = ctx.StaticConnector.BasisZVec3;
            var radians = angleDeg * System.Math.PI / 180.0;

            var idsToRotate = new List<ElementId> { dynId };
            if (fittingId is not null)
                idsToRotate.Add(fittingId);
            if (reducerId is not null)
                idsToRotate.Add(reducerId);

            if (chainGraph is not null && chainDepth > 0)
            {
                for (int level = 1; level <= chainDepth && level < chainGraph.Levels.Count; level++)
                {
                    foreach (var elemId in chainGraph.Levels[level])
                    {
                        idsToRotate.Add(elemId);
                        foreach (var rId in snapshotStore.GetReducers(elemId))
                            idsToRotate.Add(rId);
                    }
                }
            }

            var activeIdx = activeDynamic?.ConnectorIndex
                         ?? ctx.DynamicConnector.ConnectorIndex;
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
                        if (refId is not null && refId != dynId)
                            idsToRotate.Add(refId);
                    }
                }
            }

            SmartConLogger.Debug($"[Rotate] Rotating {idsToRotate.Count} elements");
            transformSvc.RotateElements(d, idsToRotate, axisOrigin, axisDir, radians);
            d.Regenerate();

            var dynElemForSnap = d.GetElement(dynId);
            if (dynElemForSnap is FamilyInstance fiForSnap)
            {
                var t = fiForSnap.GetTransform();
                var elemBasisY = new Vec3(t.BasisY.X, t.BasisY.Y, t.BasisY.Z);
                var staticBZ = ctx.StaticConnector.BasisZVec3;
                var globalYSnap = ConnectorAligner.ComputeGlobalYAlignmentSnap(
                    staticBZ, elemBasisY, axisOrigin);
                if (globalYSnap is not null)
                {
                    SmartConLogger.Debug("[Rotate] GlobalYSnap applied");
                    transformSvc.RotateElement(d, dynId,
                        axisOrigin, globalYSnap.Axis, globalYSnap.AngleRadians);
                }
                else
                {
                    SmartConLogger.Debug("[Rotate] GlobalYSnap skipped (null)");
                }
            }

            d.Regenerate();
        });

        SmartConLogger.Info($"[Rotate] DONE angle={angleDeg}°");
    }
}
