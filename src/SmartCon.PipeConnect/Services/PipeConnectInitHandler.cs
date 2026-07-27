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

/// <summary>
/// Outcome of the initial disconnect+align: the refreshed active dynamic plus
/// absorb undo data when the root dynamic is a pipe whose alignment was absorbed
/// into its own geometry (ADR-052 addendum). The undo data lets the "Блокировать"
/// toggle revert the absorb and apply the rigid move instead (issue #165).
/// </summary>
public sealed record InitAlignmentOutcome
{
    /// <summary>Refreshed active dynamic connector after align (null on failure).</summary>
    public ConnectorProxy? ActiveDynamic { get; init; }

    /// <summary>Whether the initial offset was absorbed into the pipe geometry (not a rigid move).</summary>
    public bool AbsorbApplied { get; init; }

    /// <summary>Location curve endpoint 0 BEFORE absorb (straight pipe only).</summary>
    public XYZ? PipeStart { get; init; }

    /// <summary>Location curve endpoint 1 BEFORE absorb (straight pipe only).</summary>
    public XYZ? PipeEnd { get; init; }

    /// <summary>Point path BEFORE absorb (flex pipe only).</summary>
    public IReadOnlyList<XYZ>? FlexPoints { get; init; }

    /// <summary>Alignment offset the pipe followed (== AlignResult.InitialOffset).</summary>
    public Vec3 RigidOffset { get; init; }
}

/// <summary>
/// Handles initialization for PipeConnect sessions: disconnect, alignment, and sizing adjustments.
/// </summary>
public sealed class PipeConnectInitHandler(
    IConnectorService connSvc,
    ITransformService transformSvc,
    IParameterResolver paramResolver,
    FittingCtcManager ctcManager)
{
    public InitAlignmentOutcome DisconnectAndAlign(
        Document doc,
        PipeConnectSessionContext ctx,
        ITransactionGroupSession groupSession)
    {
        using var _scope = SmartConLogger.BeginScope("Init",
            ("Method", "DisconnectAndAlign"),
            ("DynId", ctx.DynamicConnector.OwnerElementId.GetValue()));

        DisconnectDynamic(groupSession, ctx);
        var align = AlignDynamic(groupSession, ctx);
        var activeDynamic = ctcManager.RefreshWithCtcOverride(
            doc, ctx.DynamicConnector.OwnerElementId, ctx.DynamicConnector.ConnectorIndex);

        return new InitAlignmentOutcome
        {
            ActiveDynamic = activeDynamic,
            AbsorbApplied = align.AbsorbApplied,
            PipeStart = align.PipeStart,
            PipeEnd = align.PipeEnd,
            FlexPoints = align.FlexPoints,
            RigidOffset = ctx.AlignResult.InitialOffset,
        };
    }

    public ConnectorProxy? RunDirectConnectSizing(
        Document doc,
        PipeConnectSessionContext ctx,
        ITransactionGroupSession groupSession,
        double targetRadius,
        IReadOnlyList<FamilySizeOption> availableSizes)
    {
        using var _scope = SmartConLogger.BeginScope("Init",
            ("Method", "RunDirectConnectSizing"),
            ("DynId", ctx.DynamicConnector.OwnerElementId.GetValue()),
            ("TargetRadius", targetRadius));

        var dynId = ctx.DynamicConnector.OwnerElementId;
        var dynIdx = ctx.DynamicConnector.ConnectorIndex;

        groupSession.RunInTransaction(LocalizationService.GetString("Tx_AdjustSize"), d =>
        {
            var bestMatch = PipeConnectSizeHandler.FindBestOptionForRadius(
                connSvc, d, ctx, availableSizes, targetRadius, dynIdx);
            bool appliedViaQP = bestMatch is not null
                && PipeConnectSizeHandler.ApplyQueryParamsIfExists(d, dynId, bestMatch);

            if (!appliedViaQP)
            {
                SmartConLogger.Info("SizeAdj: Query params not available, fallback to TrySetConnectorRadius for all connectors");
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
                    SmartConLogger.Info($"SizeAdj: Position correction after size change: " +
                        $"dist={VectorUtils.Length(posCorrection) * FeetToMm:F3}mm");
                    PipeAbsorptionApplier.MoveOrAbsorb(
                        d, transformSvc, dynId, refreshedAfterSize.OriginVec3, posCorrection);
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
        using var _scope = SmartConLogger.BeginScope("Init",
            ("Method", "DisconnectDynamic"),
            ("DynId", ctx.DynamicConnector.OwnerElementId.GetValue()));

        groupSession.RunInTransaction(LocalizationService.GetString("Tx_Disconnect"), doc =>
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

    private sealed record AlignInfo(bool AbsorbApplied, XYZ? PipeStart, XYZ? PipeEnd, IReadOnlyList<XYZ>? FlexPoints);

    private AlignInfo AlignDynamic(
        ITransactionGroupSession groupSession,
        PipeConnectSessionContext ctx)
    {
        using var _scope = SmartConLogger.BeginScope("Init",
            ("Method", "AlignDynamic"),
            ("DynId", ctx.DynamicConnector.OwnerElementId.GetValue()));

        var alignResult = ctx.AlignResult;
        bool absorbApplied = false;
        XYZ? pipeStart = null, pipeEnd = null;
        IReadOnlyList<XYZ>? flexPoints = null;

        groupSession.RunInTransaction(LocalizationService.GetString("Tx_Align"), doc =>
        {
            var dynId = ctx.DynamicConnector.OwnerElementId;

            SmartConLogger.Info($"Align: START dynId={dynId.GetValue()} " +
                $"origin=({ctx.DynamicConnector.Origin.X:F4},{ctx.DynamicConnector.Origin.Y:F4},{ctx.DynamicConnector.Origin.Z:F4}) " +
                $"BZ=({ctx.DynamicConnector.BasisZ.X:F3},{ctx.DynamicConnector.BasisZ.Y:F3},{ctx.DynamicConnector.BasisZ.Z:F3})");

            if (!VectorUtils.IsZero(alignResult.InitialOffset))
            {
                // ADR-052 addendum: если динамик — прямая/гибкая труба и выравнивание
                // чисто поступательное, меняем её геометрию (длину/путь) вместо
                // жёсткого сдвига — дальний конец остаётся на сети, сеть не дёргается.
                bool pureTranslation = alignResult.BasisZRotation is null && alignResult.BasisXSnap is null;

                // Snapshot the pipe geometry BEFORE absorb so the "Блокировать" toggle
                // can revert it into a rigid move later (issue #165).
                if (pureTranslation)
                {
                    var dynElem = doc.GetElement(dynId);
                    if (dynElem is FlexPipe flex)
                    {
                        flexPoints = [.. flex.Points];
                    }
                    else if (dynElem is MEPCurve mc
                        && mc.Location is LocationCurve lc
                        && lc.Curve is Line line)
                    {
                        pipeStart = line.GetEndPoint(0);
                        pipeEnd = line.GetEndPoint(1);
                    }
                }

                absorbApplied = pureTranslation
                    && PipeAbsorptionApplier.TryApply(
                        doc, dynId, ctx.DynamicConnector.OriginVec3, alignResult.InitialOffset);

                if (!absorbApplied)
                {
                    SmartConLogger.Info($"Align: Move offset=({alignResult.InitialOffset.X * FeetToMm:F2}," +
                        $"{alignResult.InitialOffset.Y * FeetToMm:F2},{alignResult.InitialOffset.Z * FeetToMm:F2})mm");
                    transformSvc.MoveElement(doc, dynId, alignResult.InitialOffset);
                }
                else
                {
                    SmartConLogger.Info($"Align: Absorbed by pipe geometry " +
                        $"offset={VectorUtils.Length(alignResult.InitialOffset) * FeetToMm:F1}mm (network untouched)");
                }
            }

            if (alignResult.BasisZRotation is { } bzRot)
            {
                SmartConLogger.Info($"Align: RotateBasisZ angle={bzRot.AngleRadians * 180 / System.Math.PI:F2}° " +
                    $"axis=({bzRot.Axis.X:F3},{bzRot.Axis.Y:F3},{bzRot.Axis.Z:F3})");
                transformSvc.RotateElement(doc, dynId,
                    alignResult.RotationCenter, bzRot.Axis, bzRot.AngleRadians);
            }

            if (alignResult.BasisXSnap is { } bxSnap)
            {
                SmartConLogger.Info($"Align: RotateBasisXSnap angle={bxSnap.AngleRadians * 180 / System.Math.PI:F2}° " +
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

                SmartConLogger.Info($"Align: GlobalYSnap check: elemBasisY=({elemBasisY.X:F3},{elemBasisY.Y:F3},{elemBasisY.Z:F3}) " +
                    $"staticBZ=({staticBZ.X:F3},{staticBZ.Y:F3},{staticBZ.Z:F3})");

                var globalYSnap = ConnectorAligner.ComputeGlobalYAlignmentSnap(
                    staticBZ, elemBasisY, alignResult.RotationCenter);

                if (globalYSnap is not null)
                {
                    SmartConLogger.Info($"Align: GlobalYSnap APPLY angle={globalYSnap.AngleRadians * 180 / System.Math.PI:F2}°");
                    transformSvc.RotateElement(doc, dynId,
                        alignResult.RotationCenter, globalYSnap.Axis, globalYSnap.AngleRadians);
                    doc.Regenerate();

                    var tAfter = fiForSnap.GetTransform();
                    var byAngle = System.Math.Atan2(tAfter.BasisY.Y, tAfter.BasisY.X) * 180.0 / System.Math.PI;
                    SmartConLogger.Info($"Align: GlobalYSnap DONE: BasisY ugol v XY={byAngle:F2}°");
                }
                else
                {
                    SmartConLogger.Info("Align: GlobalYSnap: skipped (BasisZ ∥ Y or delta≈0)");
                }
            }

            var refreshed = connSvc.RefreshConnector(
                doc, dynId, ctx.DynamicConnector.ConnectorIndex);
            if (refreshed is not null)
            {
                var correction = ctx.StaticConnector.OriginVec3 - refreshed.OriginVec3;
                if (!VectorUtils.IsZero(correction))
                {
                    SmartConLogger.Info($"Align: PositionCorrection dist={VectorUtils.Length(correction) * FeetToMm:F3}mm");
                    transformSvc.MoveElement(doc, dynId, correction);
                }
            }

            doc.Regenerate();

            var refreshedFinal = connSvc.RefreshConnector(doc, dynId, ctx.DynamicConnector.ConnectorIndex);
            if (refreshedFinal is not null)
            {
                var distToStatic = VectorUtils.DistanceTo(refreshedFinal.OriginVec3, ctx.StaticConnector.OriginVec3);
                SmartConLogger.Info($"Align: END: dynOrigin=({refreshedFinal.Origin.X:F4},{refreshedFinal.Origin.Y:F4},{refreshedFinal.Origin.Z:F4}) " +
                    $"distToStatic={distToStatic * FeetToMm:F3}mm");
            }
        });

        return new AlignInfo(absorbApplied, pipeStart, pipeEnd, flexPoints);
    }
}
