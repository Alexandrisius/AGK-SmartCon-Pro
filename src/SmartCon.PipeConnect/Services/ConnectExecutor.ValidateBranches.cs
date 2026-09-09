using Autodesk.Revit.DB;
using SmartCon.Core;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Compatibility;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

public sealed partial class ConnectExecutor
{
    private void ValidateFittingPlusReducerBranch(
        Document doc,
        ConnectorProxy staticConn,
        ElementId fittingId,
        ElementId reducerId,
        ref ConnectorProxy dynFresh,
        ref ConnectorProxy? updatedDynamic,
        double positionEpsFt,
        double radiusEps)
    {
        ValidateCompositeBranch(
            doc,
            staticConn,
            fittingId,
            reducerId,
            ref dynFresh,
            ref updatedDynamic,
            positionEpsFt,
            radiusEps,
            "Fitting+Reducer branch",
            false,
            "fitting.conn1",
            true,
            "reducer.conn1",
            "reducer.conn2");
    }

    private void ValidateReducerFittingBranch(
        Document doc,
        ConnectorProxy staticConn,
        ElementId fittingId,
        ElementId reducerId,
        ref ConnectorProxy dynFresh,
        ref ConnectorProxy? updatedDynamic,
        double positionEpsFt,
        double radiusEps)
    {
        ValidateCompositeBranch(
            doc,
            staticConn,
            reducerId,
            fittingId,
            ref dynFresh,
            ref updatedDynamic,
            positionEpsFt,
            radiusEps,
            "ReducerFitting branch: static ↔ reducer ↔ fitting ↔ dynamic",
            true,
            "reducer.conn1",
            false,
            "fitting.conn1",
            "fitting.conn2");
    }

    private void ValidateFittingBranch(
        Document doc,
        ConnectorProxy staticConn,
        ElementId fittingId,
        ref ConnectorProxy dynFresh,
        ref ConnectorProxy? updatedDynamic,
        ConnectorProxy originalDyn,
        double? planTargetRadius,
        double positionEpsFt,
        double radiusEps,
        double angleEpsDeg,
        bool userManuallyChangedSize,
        ref bool needsPrimaryReducer)
    {
        using var _scope = SmartConLogger.BeginScope("Validate",
            ("Method", "ValidateFittingBranch"),
            ("FittingId", fittingId.GetValue()));

        var dynTypeCode = dynFresh.ConnectionTypeCode.IsDefined
            ? dynFresh.ConnectionTypeCode
            : originalDyn.ConnectionTypeCode;
        var (fc1, fc2) = ResolveSides(doc, fittingId, staticConn, dynTypeCode);

        if (fc1 is not null)
        {
            CorrectElementPosition(doc, fittingId, fc1, staticConn.OriginVec3, positionEpsFt);
            CheckRadiusMismatch(fc1, staticConn, radiusEps, "fc1");
        }

        if (fc2 is not null)
        {
            double r2Err = System.Math.Abs(fc2.Radius - dynFresh.Radius);
            SmartConLogger.Debug($"fc2 R={fc2.Radius * FeetToMm:F2}mm, dyn R={dynFresh.Radius * FeetToMm:F2}mm, Δ={r2Err * FeetToMm:F2}mm");
            if (r2Err > radiusEps)
            {
                if (userManuallyChangedSize)
                {
                    SmartConLogger.Warn($"User changed size manually, fc2↔dynamic Δ={r2Err * FeetToMm:F2}mm — reducer needed " +
                        $"[Action: будет вставлен переходник между фитингом и dynamic-элементом]");
                    needsPrimaryReducer = true;
                }
                else
                {
                    // See #138: цель ресайза — план сессии (уже проверен по lookup-таблице
                    // с учётом constraints остальных коннекторов). Прямая запись fc2.Radius
                    // могла создать комбинацию DN, отсутствующую в таблице, и сломать семейство.
                    double targetRadius = planTargetRadius ?? fc2.Radius;
                    SmartConLogger.Warn($"Mismatch fc2↔dynamic Δ={r2Err * FeetToMm:F2}mm — trying to adjust dynamic " +
                        $"(target={targetRadius * FeetToMm:F2}mm{(planTargetRadius is null ? "" : ", from session plan")}) " +
                        $"[Action: если корректировка не удастся, будет вставлен переходник]");
                    bool fixed1 = _paramResolver.TrySetConnectorRadius(
                        doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex, targetRadius);
                    doc.Regenerate();
                    if (fixed1)
                    {
                        dynFresh = _connSvc.RefreshConnector(doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex) ?? dynFresh;
                        updatedDynamic = dynFresh;
                        SmartConLogger.Debug($"→ dynamic adjusted to {dynFresh.Radius * FeetToMm:F2}mm");

                        double verifyDelta = System.Math.Abs(dynFresh.Radius - fc2.Radius);
                        if (verifyDelta > radiusEps)
                        {
                            SmartConLogger.Warn($"Actual radius ({dynFresh.Radius * FeetToMm:F2}mm) ≠ fc2 ({fc2.Radius * FeetToMm:F2}mm) — reducer needed. " +
                                $"[Action: будет вставлен переходник между фитингом и dynamic-элементом]");
                            needsPrimaryReducer = true;
                        }
                    }
                    else
                    {
                        SmartConLogger.Warn("Dynamic adjustment failed — reducer needed. " +
                            $"[Action: будет вставлен переходник между фитингом и dynamic-элементом]");
                        needsPrimaryReducer = true;
                    }
                }
            }

            CorrectDynamicPosition(doc, ref dynFresh, ref updatedDynamic, fc2, positionEpsFt);

            double angleZ = VectorUtils.AngleBetween(fc2.BasisZVec3, dynFresh.BasisZVec3);
            double antiParallelErr = System.Math.Abs(angleZ - System.Math.PI) * 180.0 / System.Math.PI;
            SmartConLogger.Debug($"BasisZ: angle fc2↔dyn={angleZ * 180 / System.Math.PI:F1}° (ideal=180°, dev={antiParallelErr:F1}°)");
            if (antiParallelErr > angleEpsDeg)
                SmartConLogger.Warn($"WARNING: BasisZ not anti-parallel (dev. {antiParallelErr:F1}°) — connection may fail [Action: проверьте ориентацию коннекторов — возможно потребуется ручной поворот элемента]");
        }
    }

    private void ValidateReducerBranch(
        Document doc,
        ConnectorProxy staticConn,
        ElementId reducerId,
        ref ConnectorProxy dynFresh,
        ref ConnectorProxy? updatedDynamic,
        double positionEpsFt,
        double radiusEps)
    {
        ValidateSingleIntermediateBranch(
            doc,
            staticConn,
            reducerId,
            ref dynFresh,
            ref updatedDynamic,
            positionEpsFt,
            radiusEps,
            "reducer.conn1",
            "reducer.conn2");
    }

    private void ValidateSingleIntermediateBranch(
        Document doc,
        ConnectorProxy staticConn,
        ElementId elementId,
        ref ConnectorProxy dynFresh,
        ref ConnectorProxy? updatedDynamic,
        double positionEpsFt,
        double radiusEps,
        string firstLabel,
        string secondLabel)
    {
        var dynTypeCode = ResolveDynamicTypeCode(dynFresh, dynFresh);
        var (conn1, conn2) = ResolveSides(doc, elementId, staticConn, dynTypeCode);

        if (conn1 is not null)
        {
            CorrectElementPosition(doc, elementId, conn1, staticConn.OriginVec3, positionEpsFt);
            CheckRadiusMismatch(conn1, staticConn, radiusEps, firstLabel);
        }

        if (conn2 is null)
            return;

        CorrectDynamicPosition(doc, ref dynFresh, ref updatedDynamic, conn2, positionEpsFt);
        CheckRadiusMismatch(conn2, dynFresh, radiusEps, secondLabel);
    }

    private void ValidateCompositeBranch(
        Document doc,
        ConnectorProxy staticConn,
        ElementId firstElementId,
        ElementId secondElementId,
        ref ConnectorProxy dynFresh,
        ref ConnectorProxy? updatedDynamic,
        double positionEpsFt,
        double radiusEps,
        string branchLog,
        bool checkFirstRadius,
        string firstRadiusLabel,
        bool checkSecondRadius,
        string secondRadiusLabel,
        string dynamicLabel)
    {
        using var _scope = SmartConLogger.BeginScope("Validate",
            ("Method", "ValidateCompositeBranch"),
            ("FirstId", firstElementId.GetValue()),
            ("SecondId", secondElementId.GetValue()));

        SmartConLogger.Info(branchLog);

        var dynTypeCode = ResolveDynamicTypeCode(dynFresh, dynFresh);
        var (firstConn1, firstConn2) = ResolveSides(doc, firstElementId, staticConn, dynTypeCode);

        if (firstConn1 is not null)
        {
            CorrectElementPosition(doc, firstElementId, firstConn1, staticConn.OriginVec3, positionEpsFt);
            if (checkFirstRadius)
                CheckRadiusMismatch(firstConn1, staticConn, radiusEps, firstRadiusLabel);
        }

        if (firstConn2 is null)
            return;

        var (secondConn1, secondConn2) = ResolveSides(doc, secondElementId, firstConn2, dynTypeCode);

        if (secondConn1 is not null)
        {
            CorrectElementPosition(doc, secondElementId, secondConn1, firstConn2.OriginVec3, positionEpsFt);
            if (checkSecondRadius)
                CheckRadiusMismatch(secondConn1, firstConn2, radiusEps, secondRadiusLabel);
        }

        if (secondConn2 is null)
            return;

        CorrectDynamicPosition(doc, ref dynFresh, ref updatedDynamic, secondConn2, positionEpsFt);
        CheckRadiusMismatch(secondConn2, dynFresh, radiusEps, dynamicLabel);
    }

    private void ValidateDirectBranch(
        Document doc,
        ConnectorProxy staticConn,
        ref ConnectorProxy dynFresh,
        ref ConnectorProxy? updatedDynamic,
        ref bool needsPrimaryReducer,
        ConnectOperationContext context,
        double positionEpsFt,
        double radiusEps,
        double angleEpsDeg,
        bool userManuallyChangedSize,
        bool lockNetwork)
    {
        using var _scope = SmartConLogger.BeginScope("Validate",
            ("Method", "ValidateDirectBranch"));

        double rErr = System.Math.Abs(staticConn.Radius - dynFresh.Radius);
        SmartConLogger.Debug($"direct: static R={staticConn.Radius * FeetToMm:F2}mm, dyn R={dynFresh.Radius * FeetToMm:F2}mm, Δ={rErr * FeetToMm:F2}mm");
        if (rErr > radiusEps)
        {
            if (lockNetwork || userManuallyChangedSize)
            {
                SmartConLogger.Warn($"{(lockNetwork ? "LockNetwork: DN frozen" : "User manually changed size")} (Δ={rErr * FeetToMm:F2}mm) → reducer needed [Action: добавьте редуктор в mapping или верните размер динамического элемента]");
                needsPrimaryReducer = true;
            }
            else
            {
                // See #138: цель ресайза — план сессии (проверен по lookup-таблице
                // с constraints остальных коннекторов), а не «сырой» радиус static.
                double targetRadius = context.Session.ParamTargetRadius ?? staticConn.Radius;
                SmartConLogger.Warn($"Direct: mismatch Δ={rErr * FeetToMm:F2}mm — trying to adjust dynamic " +
                    $"(target={targetRadius * FeetToMm:F2}mm{(context.Session.ParamTargetRadius is null ? "" : ", from session plan")}) " +
                    $"[Action: если корректировка не удастся, будет вставлен переходник]");
                bool fixed2 = _paramResolver.TrySetConnectorRadius(
                    doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex, targetRadius);
                doc.Regenerate();

                if (fixed2)
                {
                    dynFresh = _connSvc.RefreshConnector(doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex) ?? dynFresh;
                    double verifyDelta = System.Math.Abs(dynFresh.Radius - staticConn.Radius);
                    SmartConLogger.Debug($"→ verify: actual R={dynFresh.Radius * FeetToMm:F2}mm, Δ={verifyDelta * FeetToMm:F2}mm");

                    if (verifyDelta > radiusEps)
                    {
                        SmartConLogger.Warn($"Actual radius ({dynFresh.Radius * FeetToMm:F2}mm) ≠ static ({staticConn.Radius * FeetToMm:F2}mm) — falling back to nearest, reducer needed [Action: проверьте mapping редукторов для этой пары размеров]");
                        needsPrimaryReducer = true;

                        if (context.Session.ParamTargetRadius is { } bestRadius)
                        {
                            _paramResolver.TrySetConnectorRadius(
                                doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex, bestRadius);
                            doc.Regenerate();
                        }

                        dynFresh = _connSvc.RefreshConnector(doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex) ?? dynFresh;
                    }
                }
                else
                {
                    SmartConLogger.Warn("TrySetConnectorRadius returned false — reducer needed " +
                        "[Action: будет вставлен переходник; если его нет в mapping, добавьте семейство (Настройки → Правила)]");
                    needsPrimaryReducer = true;
                }
            }

            updatedDynamic = dynFresh;
        }

        var posErrD = VectorUtils.DistanceTo(dynFresh.OriginVec3, staticConn.OriginVec3);
        if (posErrD > positionEpsFt)
        {
            PipeAbsorptionApplier.MoveOrAbsorb(
                doc, _transformSvc, dynFresh.OwnerElementId, dynFresh.OriginVec3,
                staticConn.OriginVec3 - dynFresh.OriginVec3);
            doc.Regenerate();
        }

        double angleZD = VectorUtils.AngleBetween(staticConn.BasisZVec3, dynFresh.BasisZVec3);
        double antiErrD = System.Math.Abs(angleZD - System.Math.PI) * 180.0 / System.Math.PI;
        if (antiErrD > angleEpsDeg)
        {
            // ConnectTo on non-anti-parallel connectors makes Revit auto-orient the fitting
            // to an unpredictable pose (tee jumped to the branch port on Connect). The method
            // is ValidateAndFix — so fix: re-align dynamic to static (same alignment as the
            // initial/cycle alignment) instead of only warning.
            SmartConLogger.Warn($"BasisZ not anti-parallel (dev. {antiErrD:F1}°) — re-aligning dynamic to static before ConnectTo " +
                $"[Action: элемент перевыравнен автоматически; проверьте итоговое положение и ориентацию коннекторов]");

            var reAlign = ConnectorAligner.ComputeAlignment(
                staticConn.OriginVec3, staticConn.BasisZVec3, staticConn.BasisXVec3,
                dynFresh.OriginVec3, dynFresh.BasisZVec3, dynFresh.BasisXVec3);
            _alignmentSvc.ApplyAlignment(doc, dynFresh.OwnerElementId, reAlign, dynFresh.ConnectorIndex);

            dynFresh = _connSvc.RefreshConnector(doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex) ?? dynFresh;
            updatedDynamic = dynFresh;

            double verifyAngle = VectorUtils.AngleBetween(staticConn.BasisZVec3, dynFresh.BasisZVec3);
            double verifyDev = System.Math.Abs(verifyAngle - System.Math.PI) * 180.0 / System.Math.PI;
            SmartConLogger.Info($"Re-align done: dev. now {verifyDev:F1}°, " +
                $"dist={VectorUtils.DistanceTo(dynFresh.OriginVec3, staticConn.OriginVec3) * FeetToMm:F1}mm");
        }
    }

    // ...
}
