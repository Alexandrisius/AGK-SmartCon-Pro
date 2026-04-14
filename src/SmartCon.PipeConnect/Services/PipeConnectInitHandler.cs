using Autodesk.Revit.DB;
using SmartCon.Core;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

public sealed class PipeConnectInitHandler(
    IConnectorService connSvc,
    ITransformService transformSvc,
    IParameterResolver paramResolver,
    FittingCtcManager ctcManager)
{
    public ConnectorProxy? DisconnectAndAlign(
        Document doc,
        PipeConnectSessionContext ctx,
        ITransactionGroupSession groupSession)
    {
        DisconnectDynamic(groupSession, ctx);
        AlignDynamic(groupSession, ctx);
        return ctcManager.RefreshWithCtcOverride(
            doc, ctx.DynamicConnector.OwnerElementId, ctx.DynamicConnector.ConnectorIndex);
    }

    public ConnectorProxy? RunDirectConnectSizing(
        Document doc,
        PipeConnectSessionContext ctx,
        ITransactionGroupSession groupSession,
        double targetRadius,
        IReadOnlyList<FamilySizeOption> availableSizes)
    {
        var dynId = ctx.DynamicConnector.OwnerElementId;
        var dynIdx = ctx.DynamicConnector.ConnectorIndex;

        groupSession.RunInTransaction("PipeConnect — Подгонка размера", d =>
        {
            var bestMatch = PipeConnectSizeHandler.FindBestOptionForRadius(
                connSvc, d, ctx, availableSizes, targetRadius, dynIdx);
            bool appliedViaQP = bestMatch is not null
                && PipeConnectSizeHandler.ApplyQueryParamsIfExists(d, dynId, bestMatch);

            if (!appliedViaQP)
            {
                SmartConLogger.Info("[SizeAdj] Query params not available, fallback to TrySetConnectorRadius for all connectors");
                if (bestMatch is not null)
                {
                    foreach (var kvp in bestMatch.AllConnectorRadii)
                        paramResolver.TrySetConnectorRadius(d, dynId, kvp.Key, kvp.Value);
                }
                else
                {
                    paramResolver.TrySetConnectorRadius(d, dynId, dynIdx, targetRadius);
                }
            }
            d.Regenerate();

            var refreshedAfterSize = connSvc.RefreshConnector(d, dynId, dynIdx);
            if (refreshedAfterSize is not null)
            {
                var posCorrection = ctx.StaticConnector.OriginVec3 - refreshedAfterSize.OriginVec3;
                if (!VectorUtils.IsZero(posCorrection))
                {
                    SmartConLogger.Info($"[SizeAdj] Position correction after size change: " +
                        $"dist={VectorUtils.Length(posCorrection) * FeetToMm:F3}mm");
                    transformSvc.MoveElement(d, dynId, posCorrection);
                    d.Regenerate();
                }
            }
        });

        return ctcManager.RefreshWithCtcOverride(doc, dynId, dynIdx);
    }

    private void DisconnectDynamic(
        ITransactionGroupSession groupSession,
        PipeConnectSessionContext ctx)
    {
        groupSession.RunInTransaction("PipeConnect — Отсоединение", doc =>
        {
            var dynId = ctx.DynamicConnector.OwnerElementId;
            var allConns = connSvc.GetAllConnectors(doc, dynId);
            foreach (var c in allConns)
            {
                if (!c.IsFree)
                    connSvc.DisconnectAllFromConnector(doc, dynId, c.ConnectorIndex);
            }
            doc.Regenerate();
        });
    }

    private void AlignDynamic(
        ITransactionGroupSession groupSession,
        PipeConnectSessionContext ctx)
    {
        var alignResult = ctx.AlignResult;
        groupSession.RunInTransaction("PipeConnect — Выравнивание", doc =>
        {
            var dynId = ctx.DynamicConnector.OwnerElementId;

            SmartConLogger.Info($"[Align] START dynId={dynId.Value} " +
                $"origin=({ctx.DynamicConnector.Origin.X:F4},{ctx.DynamicConnector.Origin.Y:F4},{ctx.DynamicConnector.Origin.Z:F4}) " +
                $"BZ=({ctx.DynamicConnector.BasisZ.X:F3},{ctx.DynamicConnector.BasisZ.Y:F3},{ctx.DynamicConnector.BasisZ.Z:F3})");

            if (!VectorUtils.IsZero(alignResult.InitialOffset))
            {
                SmartConLogger.Info($"[Align] Move offset=({alignResult.InitialOffset.X * FeetToMm:F2}," +
                    $"{alignResult.InitialOffset.Y * FeetToMm:F2},{alignResult.InitialOffset.Z * FeetToMm:F2})mm");
                transformSvc.MoveElement(doc, dynId, alignResult.InitialOffset);
            }

            if (alignResult.BasisZRotation is { } bzRot)
            {
                SmartConLogger.Info($"[Align] RotateBasisZ angle={bzRot.AngleRadians * 180 / System.Math.PI:F2}° " +
                    $"axis=({bzRot.Axis.X:F3},{bzRot.Axis.Y:F3},{bzRot.Axis.Z:F3})");
                transformSvc.RotateElement(doc, dynId,
                    alignResult.RotationCenter, bzRot.Axis, bzRot.AngleRadians);
            }

            if (alignResult.BasisXSnap is { } bxSnap)
            {
                SmartConLogger.Info($"[Align] RotateBasisXSnap angle={bxSnap.AngleRadians * 180 / System.Math.PI:F2}° " +
                    $"axis=({bxSnap.Axis.X:F3},{bxSnap.Axis.Y:F3},{bxSnap.Axis.Z:F3})");
                transformSvc.RotateElement(doc, dynId,
                    alignResult.RotationCenter, bxSnap.Axis, bxSnap.AngleRadians);
            }

            doc.Regenerate();

            var dynElemRaw = doc.GetElement(dynId);
            if (dynElemRaw is FamilyInstance fiForSnap)
            {
                var t = fiForSnap.GetTransform();
                var elemBasisY = new Vec3(t.BasisY.X, t.BasisY.Y, t.BasisY.Z);
                var staticBZ = ctx.StaticConnector.BasisZVec3;

                SmartConLogger.Info($"[Align] GlobalYSnap check: elemBasisY=({elemBasisY.X:F3},{elemBasisY.Y:F3},{elemBasisY.Z:F3}) " +
                    $"staticBZ=({staticBZ.X:F3},{staticBZ.Y:F3},{staticBZ.Z:F3})");

                var globalYSnap = ConnectorAligner.ComputeGlobalYAlignmentSnap(
                    staticBZ, elemBasisY, alignResult.RotationCenter);

                if (globalYSnap is not null)
                {
                    SmartConLogger.Info($"[Align] GlobalYSnap APPLY angle={globalYSnap.AngleRadians * 180 / System.Math.PI:F2}°");
                    transformSvc.RotateElement(doc, dynId,
                        alignResult.RotationCenter, globalYSnap.Axis, globalYSnap.AngleRadians);
                    doc.Regenerate();

                    var tAfter = fiForSnap.GetTransform();
                    var byAngle = System.Math.Atan2(tAfter.BasisY.Y, tAfter.BasisY.X) * 180.0 / System.Math.PI;
                    SmartConLogger.Info($"[Align] GlobalYSnap DONE: BasisY ugol v XY={byAngle:F2}°");
                }
                else
                {
                    SmartConLogger.Info("[Align] GlobalYSnap: skipped (BasisZ ∥ Y or delta≈0)");
                }
            }

            var refreshed = connSvc.RefreshConnector(
                doc, dynId, ctx.DynamicConnector.ConnectorIndex);
            if (refreshed is not null)
            {
                var correction = ctx.StaticConnector.OriginVec3 - refreshed.OriginVec3;
                if (!VectorUtils.IsZero(correction))
                {
                    SmartConLogger.Info($"[Align] PositionCorrection dist={VectorUtils.Length(correction) * FeetToMm:F3}mm");
                    transformSvc.MoveElement(doc, dynId, correction);
                }
            }

            doc.Regenerate();

            var refreshedFinal = connSvc.RefreshConnector(doc, dynId, ctx.DynamicConnector.ConnectorIndex);
            if (refreshedFinal is not null)
            {
                var distToStatic = VectorUtils.DistanceTo(refreshedFinal.OriginVec3, ctx.StaticConnector.OriginVec3);
                SmartConLogger.Info($"[Align] END: dynOrigin=({refreshedFinal.Origin.X:F4},{refreshedFinal.Origin.Y:F4},{refreshedFinal.Origin.Z:F4}) " +
                    $"distToStatic={distToStatic * FeetToMm:F3}mm");
            }
        });
    }
}
