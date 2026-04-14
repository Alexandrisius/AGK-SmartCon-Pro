using Autodesk.Revit.DB;
using SmartCon.Core;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Encapsulates the document, transaction session, and session context for a connect operation.
/// </summary>
public sealed class ConnectOperationContext
{
    public required Document Doc { get; init; }
    public required ITransactionGroupSession GroupSession { get; init; }
    public required PipeConnectSessionContext Session { get; init; }
    public required VirtualCtcStore VirtualCtcStore { get; init; }
}

/// <summary>
/// Result of pre-connect validation with optional updated dynamic connector and reducer flag.
/// </summary>
public record ValidateResult(ConnectorProxy? ActiveDynamic, bool NeedsPrimaryReducer);

/// <summary>
/// Result of fitting sizing with the resolved secondary connector and updated dynamic connector.
/// </summary>
public record SizeFittingResult(ConnectorProxy? FitConn2, ConnectorProxy? ActiveDynamic);

/// <summary>
/// Executes the final connect operation including validation, sizing, and Revit connector linking.
/// </summary>
public sealed class ConnectExecutor
{
    private readonly IConnectorService _connSvc;
    private readonly ITransformService _transformSvc;
    private readonly IParameterResolver _paramResolver;
    private readonly IFittingInsertService _fittingInsertSvc;
    private readonly INetworkMover _networkMover;
    private readonly IFittingMappingRepository _mappingRepo;
    private readonly FittingCtcManager _ctcManager;

    public ConnectExecutor(
        IConnectorService connSvc,
        ITransformService transformSvc,
        IParameterResolver paramResolver,
        IFittingInsertService fittingInsertSvc,
        INetworkMover networkMover,
        IFittingMappingRepository mappingRepo,
        FittingCtcManager ctcManager)
    {
        _connSvc = connSvc;
        _transformSvc = transformSvc;
        _paramResolver = paramResolver;
        _fittingInsertSvc = fittingInsertSvc;
        _networkMover = networkMover;
        _mappingRepo = mappingRepo;
        _ctcManager = ctcManager;
    }

    public ValidateResult ValidateAndFixBeforeConnect(
        ConnectOperationContext context,
        ConnectorProxy? activeDynamic,
        ElementId? currentFittingId,
        ElementId? primaryReducerId,
        bool userManuallyChangedSize)
    {
        const double radiusEps = Tolerance.RadiusFt;
        const double positionEpsFt = Tolerance.PositionRelaxedMm * MmToFeet;
        const double angleEpsDeg = Tolerance.AngleDeg;

        SmartConLogger.DebugSection("ValidateAndFixBeforeConnect");

        ConnectorProxy? updatedDynamic = activeDynamic;
        bool needsPrimaryReducer = false;

        context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_FinalAdjustment"), doc =>
        {
            var staticConn = context.Session.StaticConnector;
            var dyn = updatedDynamic ?? context.Session.DynamicConnector;
            var dynFresh = _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;

            if (currentFittingId is not null)
            {
                ValidateFittingBranch(doc, staticConn, currentFittingId, ref dynFresh, ref updatedDynamic,
                    dyn, positionEpsFt, radiusEps, angleEpsDeg);
            }
            else if (primaryReducerId is not null)
            {
                ValidateReducerBranch(doc, staticConn, primaryReducerId, ref dynFresh, ref updatedDynamic,
                    positionEpsFt, radiusEps);
            }
            else
            {
                ValidateDirectBranch(doc, staticConn, ref dynFresh, ref updatedDynamic,
                    ref needsPrimaryReducer, context, positionEpsFt, radiusEps, angleEpsDeg,
                    userManuallyChangedSize);
            }

            doc.Regenerate();
        });

        return new ValidateResult(updatedDynamic, needsPrimaryReducer);
    }

    public void ExecuteConnectTo(
        ConnectOperationContext context,
        ConnectorProxy? activeDynamic,
        ElementId? currentFittingId,
        ElementId? primaryReducerId,
        FittingMappingRule? activeFittingRule)
    {
        var dyn = activeDynamic ?? context.Session.DynamicConnector;
        var staticConn = context.Session.StaticConnector;

        PipeConnectDiagnostics.LogConnectorState(
            context.Doc, staticConn, dyn, currentFittingId, _connSvc, "ДО ConnectTo");

        context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_ConnectTo"), doc =>
        {
            doc.Regenerate();

            if (currentFittingId is not null)
            {
                var fConns = _connSvc.GetAllFreeConnectors(doc, currentFittingId).ToList();
                var dynR = _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;
                var dynCtc = dynR.ConnectionTypeCode.IsDefined ? dynR.ConnectionTypeCode : dyn.ConnectionTypeCode;
                var (fc1, fc2) = _ctcManager.ResolveConnectorSidesForElement(
                    doc, currentFittingId, fConns, dynCtc, staticConn);

                if (fc1 is not null)
                    _connSvc.ConnectTo(doc,
                        staticConn.OwnerElementId, staticConn.ConnectorIndex,
                        currentFittingId, fc1.ConnectorIndex);

                if (fc2 is not null)
                    _connSvc.ConnectTo(doc, currentFittingId, fc2.ConnectorIndex,
                        dynR.OwnerElementId, dynR.ConnectorIndex);
            }
            else if (primaryReducerId is not null)
            {
                var dynR = _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;
                var dynCtc = dynR.ConnectionTypeCode.IsDefined ? dynR.ConnectionTypeCode : dyn.ConnectionTypeCode;
                var rConns = _connSvc.GetAllFreeConnectors(doc, primaryReducerId).ToList();
                var (rConn1, rConn2) = _ctcManager.ResolveConnectorSidesForElement(
                    doc, primaryReducerId, rConns, dynCtc, staticConn);

                if (rConn1 is not null)
                {
                    SmartConLogger.Info($"[Connect] static({staticConn.OwnerElementId.Value}:{staticConn.ConnectorIndex}) ↔ reducer({primaryReducerId.Value}:{rConn1.ConnectorIndex})");
                    _connSvc.ConnectTo(doc,
                        staticConn.OwnerElementId, staticConn.ConnectorIndex,
                        primaryReducerId, rConn1.ConnectorIndex);
                }
                if (rConn2 is not null)
                {
                    SmartConLogger.Info($"[Connect] reducer({primaryReducerId.Value}:{rConn2.ConnectorIndex}) ↔ dynamic({dynR.OwnerElementId.Value}:{dynR.ConnectorIndex})");
                    _connSvc.ConnectTo(doc,
                        primaryReducerId, rConn2.ConnectorIndex,
                        dynR.OwnerElementId, dynR.ConnectorIndex);
                }
            }
            else
            {
                var dynR = _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;
                _connSvc.ConnectTo(doc,
                    staticConn.OwnerElementId, staticConn.ConnectorIndex,
                    dynR.OwnerElementId, dynR.ConnectorIndex);
            }

            doc.Regenerate();

            var dynAfter = activeDynamic ?? context.Session.DynamicConnector;
            PipeConnectDiagnostics.LogConnectorState(
                doc, staticConn, dynAfter, currentFittingId, _connSvc, "ПОСЛЕ ConnectTo");
        });
    }

    public SizeFittingResult SizeFittingConnectors(
        ConnectOperationContext context,
        ConnectorProxy? activeDynamic,
        ElementId fittingId,
        ConnectorProxy? fitConn2,
        FittingMappingRule? activeFittingRule,
        bool adjustDynamicToFit = true)
    {
        ConnectorProxy? result = null;
        ConnectorProxy? currentDynamic = activeDynamic;

        try
        {
            var staticConn = context.Session.StaticConnector;
            var allConns = _connSvc.GetAllFreeConnectors(context.Doc, fittingId).ToList();
            if (allConns.Count == 0) return new SizeFittingResult(null, currentDynamic);

            ConnectorProxy? resolvedConn2;
            ConnectorProxy? resolvedConn1;
            if (fitConn2 is not null && allConns.Any(c => c.ConnectorIndex == fitConn2.ConnectorIndex))
            {
                resolvedConn2 = allConns.First(c => c.ConnectorIndex == fitConn2.ConnectorIndex);
                resolvedConn1 = allConns.FirstOrDefault(c => c.ConnectorIndex != fitConn2.ConnectorIndex);
            }
            else
            {
                var ordered = allConns
                    .OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, staticConn.OriginVec3))
                    .ToList();
                resolvedConn1 = ordered.FirstOrDefault();
                resolvedConn2 = ordered.Skip(1).FirstOrDefault();
            }

            int conn1Idx = resolvedConn1?.ConnectorIndex ?? -1;
            int conn2Idx = resolvedConn2?.ConnectorIndex ?? -1;

            var depsByIdx = new Dictionary<int, IReadOnlyList<ParameterDependency>>();
            foreach (var c in allConns)
                depsByIdx[c.ConnectorIndex] = _paramResolver.GetConnectorRadiusDependencies(context.Doc, fittingId, c.ConnectorIndex);

            bool usePairMode = conn1Idx >= 0 && conn2Idx >= 0
                && depsByIdx.TryGetValue(conn1Idx, out var d1) && d1.Count > 0 && !d1[0].IsInstance
                && depsByIdx.TryGetValue(conn2Idx, out var d2) && d2.Count > 0 && !d2[0].IsInstance;

            if (usePairMode)
            {
                double currentDynRadius = currentDynamic?.Radius ?? context.Session.DynamicConnector.Radius;
                double achievedDynRadius = currentDynRadius;

                context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_FittingSize"), txDoc =>
                {
                    var (_, dynR) = _paramResolver.TrySetFittingTypeForPair(
                        txDoc, fittingId,
                        conn1Idx, staticConn.Radius,
                        conn2Idx, currentDynRadius);
                    achievedDynRadius = dynR;
                    txDoc.Regenerate();
                });

                const double eps = 1e-6;
                var dynId = context.Session.DynamicConnector.OwnerElementId;
                var dynConnIdx = context.Session.DynamicConnector.ConnectorIndex;
                double actualDynRadius = currentDynamic?.Radius ?? currentDynRadius;
                if (adjustDynamicToFit && System.Math.Abs(achievedDynRadius - actualDynRadius) > eps)
                {
                    SmartConLogger.Info($"[SizeFitting] Adjusting dynamic: {actualDynRadius * FeetToMm:F2}mm → {achievedDynRadius * FeetToMm:F2}mm");
                    context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_FitDynamicToFitting"), txDoc =>
                    {
                        _paramResolver.TrySetConnectorRadius(txDoc, dynId, dynConnIdx, achievedDynRadius);
                        txDoc.Regenerate();
                    });
                }
                else if (!adjustDynamicToFit)
                {
                    SmartConLogger.Info($"[SizeFitting] Skipping dynamic adjustment (adjustDynamicToFit=false). Actual dynamic={actualDynRadius * FeetToMm:F2}mm, fitting achieved={achievedDynRadius * FeetToMm:F2}mm");
                }
            }
            else
            {
                double currentDynRadius = currentDynamic?.Radius ?? context.Session.DynamicConnector.Radius;

                context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_FittingSize"), txDoc =>
                {
                    var sortedConns = allConns
                        .OrderBy(c =>
                        {
                            if (!depsByIdx.TryGetValue(c.ConnectorIndex, out var deps) || deps.Count == 0)
                                return 1;
                            return deps[0].IsInstance ? 1 : 0;
                        })
                        .ToList();

                    foreach (var c in sortedConns)
                    {
                        double targetRadius = c.ConnectorIndex == conn2Idx
                            ? currentDynRadius
                            : staticConn.Radius;
                        _paramResolver.TrySetConnectorRadius(txDoc, fittingId, c.ConnectorIndex, targetRadius);
                    }
                    txDoc.Regenerate();
                });
            }

            var (realignFitConn2, updatedDynamic) = RealignAfterSizing(
                context, currentDynamic, fittingId, activeFittingRule);
            currentDynamic = updatedDynamic;
            result = realignFitConn2;
        }
        catch (Exception ex) { SmartConLogger.Warn($"[SizeFitting] Best-effort error (ignored): {ex.Message}"); }

        return new SizeFittingResult(result, currentDynamic);
    }

    private (ConnectorProxy? FitConn2, ConnectorProxy? ActiveDynamic) RealignAfterSizing(
        ConnectOperationContext context,
        ConnectorProxy? activeDynamic,
        ElementId fittingId,
        FittingMappingRule? activeFittingRule)
    {
        ConnectorProxy? newFitConn2 = null;
        ConnectorProxy? currentDynamic = activeDynamic;

        try
        {
            context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_AlignAfterSize"), txDoc =>
            {
                var ctcOvr = context.VirtualCtcStore.GetOverridesForElement(fittingId);
                var staticConn = context.Session.StaticConnector;
                var dynCtc = _ctcManager.ResolveDynamicTypeFromRule(activeFittingRule, staticConn.ConnectionTypeCode);

                newFitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                    txDoc, fittingId, staticConn, _transformSvc, _connSvc,
                    dynamicTypeCode: dynCtc,
                    ctcOverrides: ctcOvr.Count > 0 ? ctcOvr : null,
                    directConnectRules: _mappingRepo.GetMappingRules());

                if (newFitConn2 is not null && currentDynamic is not null)
                {
                    var dynProxy = _connSvc.RefreshConnector(
                        txDoc, currentDynamic.OwnerElementId, currentDynamic.ConnectorIndex)
                        ?? currentDynamic;

                    var offset = newFitConn2.OriginVec3 - dynProxy.OriginVec3;
                    if (!VectorUtils.IsZero(offset))
                        _transformSvc.MoveElement(txDoc, currentDynamic.OwnerElementId, offset);

                    txDoc.Regenerate();

                    currentDynamic = _ctcManager.RefreshWithCtcOverride(
                        txDoc, currentDynamic.OwnerElementId, currentDynamic.ConnectorIndex)
                        ?? currentDynamic;
                }

                txDoc.Regenerate();
            });
        }
        catch (Exception ex) { SmartConLogger.Warn($"[RealignAfterSizing] Best-effort error (ignored): {ex.Message}"); }

        return (newFitConn2, currentDynamic);
    }

    private void ValidateFittingBranch(
        Document doc,
        ConnectorProxy staticConn,
        ElementId fittingId,
        ref ConnectorProxy dynFresh,
        ref ConnectorProxy? updatedDynamic,
        ConnectorProxy originalDyn,
        double positionEpsFt,
        double radiusEps,
        double angleEpsDeg)
    {
        var fConns = _connSvc.GetAllFreeConnectors(doc, fittingId).ToList();
        var dynTypeCode = dynFresh.ConnectionTypeCode.IsDefined
            ? dynFresh.ConnectionTypeCode
            : originalDyn.ConnectionTypeCode;
        var (fc1, fc2) = _ctcManager.ResolveConnectorSidesForElement(
            doc, fittingId, fConns, dynTypeCode, staticConn);

        if (fc1 is not null)
        {
            var posErr1 = VectorUtils.DistanceTo(fc1.OriginVec3, staticConn.OriginVec3);
            if (posErr1 > positionEpsFt)
            {
                SmartConLogger.Warn($"[Validate] fc1 offset from static by {posErr1 * FeetToMm:F2} mm — correcting");
                var correction = staticConn.OriginVec3 - fc1.OriginVec3;
                _transformSvc.MoveElement(doc, fittingId, correction);
                doc.Regenerate();
                fc1 = _connSvc.RefreshConnector(doc, fittingId, fc1.ConnectorIndex) ?? fc1;
                fc2 = fc2 is not null
                    ? _connSvc.RefreshConnector(doc, fittingId, fc2.ConnectorIndex) ?? fc2
                    : null;
            }

            double r1Err = System.Math.Abs(fc1.Radius - staticConn.Radius);
            SmartConLogger.Debug($"  fc1 R={fc1.Radius * FeetToMm:F2}mm, static R={staticConn.Radius * FeetToMm:F2}mm, Δ={r1Err * FeetToMm:F2}mm");
            if (r1Err > radiusEps)
                SmartConLogger.Warn($"[Validate] MISMATCH: fc1.Radius≠static.Radius (Δ={r1Err * FeetToMm:F2}mm). Fitting is suboptimal.");
        }

        if (fc2 is not null)
        {
            double r2Err = System.Math.Abs(fc2.Radius - dynFresh.Radius);
            SmartConLogger.Debug($"  fc2 R={fc2.Radius * FeetToMm:F2}mm, dyn R={dynFresh.Radius * FeetToMm:F2}mm, Δ={r2Err * FeetToMm:F2}mm");
            if (r2Err > radiusEps)
            {
                SmartConLogger.Warn($"[Validate] Mismatch fc2↔dynamic Δ={r2Err * FeetToMm:F2}mm — trying to adjust dynamic");
                bool fixed1 = _paramResolver.TrySetConnectorRadius(
                    doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex, fc2.Radius);
                doc.Regenerate();
                if (fixed1)
                {
                    dynFresh = _connSvc.RefreshConnector(doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex) ?? dynFresh;
                    updatedDynamic = dynFresh;
                    SmartConLogger.Debug($"  → dynamic adjusted to {dynFresh.Radius * FeetToMm:F2}mm");
                }
                else
                {
                    SmartConLogger.Warn($"[Validate] Dynamic adjustment failed — size mismatch persists in model");
                }
            }

            var posErr2 = VectorUtils.DistanceTo(dynFresh.OriginVec3, fc2.OriginVec3);
            SmartConLogger.Debug($"  position: dyn↔fc2 distance={posErr2 * FeetToMm:F2}mm");
            if (posErr2 > positionEpsFt)
            {
                SmartConLogger.Warn($"[Validate] dynamic offset from fc2 by {posErr2 * FeetToMm:F2} mm — correcting");
                PositionCorrector.ApplyOffset(doc, _transformSvc, dynFresh.OwnerElementId, fc2.OriginVec3 - dynFresh.OriginVec3);
            }

            double angleZ = VectorUtils.AngleBetween(fc2.BasisZVec3, dynFresh.BasisZVec3);
            double antiParallelErr = System.Math.Abs(angleZ - System.Math.PI) * 180.0 / System.Math.PI;
            SmartConLogger.Debug($"  BasisZ: angle fc2↔dyn={angleZ * 180 / System.Math.PI:F1}° (ideal=180°, dev={antiParallelErr:F1}°)");
            if (antiParallelErr > angleEpsDeg)
                SmartConLogger.Warn($"[Validate] WARNING: BasisZ not anti-parallel (dev. {antiParallelErr:F1}°) — connection may fail");
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
        var rConns = _connSvc.GetAllFreeConnectors(doc, reducerId).ToList();
        var dynTypeCode = dynFresh.ConnectionTypeCode.IsDefined
            ? dynFresh.ConnectionTypeCode
            : dynFresh.ConnectionTypeCode;
        var (rConn1, rConn2) = _ctcManager.ResolveConnectorSidesForElement(
            doc, reducerId, rConns, dynTypeCode, staticConn);

        if (rConn1 is not null)
        {
            var posErr1 = VectorUtils.DistanceTo(rConn1.OriginVec3, staticConn.OriginVec3);
            SmartConLogger.Debug($"  reducer.conn1↔static distance={posErr1 * FeetToMm:F2}mm");
            if (posErr1 > positionEpsFt)
            {
                SmartConLogger.Warn($"[Validate] reducer.conn1 offset from static by {posErr1 * FeetToMm:F2} mm — correcting");
                var correction = staticConn.OriginVec3 - rConn1.OriginVec3;
                _transformSvc.MoveElement(doc, reducerId, correction);
                doc.Regenerate();
                rConn1 = _connSvc.RefreshConnector(doc, reducerId, rConn1.ConnectorIndex) ?? rConn1;
                rConn2 = rConn2 is not null
                    ? _connSvc.RefreshConnector(doc, reducerId, rConn2.ConnectorIndex) ?? rConn2
                    : null;
            }

            double r1Err = System.Math.Abs(rConn1.Radius - staticConn.Radius);
            SmartConLogger.Debug($"  reducer.conn1 R={rConn1.Radius * FeetToMm:F2}mm, static R={staticConn.Radius * FeetToMm:F2}mm, Δ={r1Err * FeetToMm:F2}mm");
        }

        if (rConn2 is not null)
        {
            var posErr2 = VectorUtils.DistanceTo(dynFresh.OriginVec3, rConn2.OriginVec3);
            SmartConLogger.Debug($"  position: dyn↔reducer.conn2 distance={posErr2 * FeetToMm:F2}mm");
            if (posErr2 > positionEpsFt)
            {
                SmartConLogger.Warn($"[Validate] dynamic offset from reducer.conn2 by {posErr2 * FeetToMm:F2} mm — correcting");
                PositionCorrector.ApplyOffset(doc, _transformSvc, dynFresh.OwnerElementId, rConn2.OriginVec3 - dynFresh.OriginVec3);
                dynFresh = _connSvc.RefreshConnector(doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex) ?? dynFresh;
                updatedDynamic = dynFresh;
            }

            double r2Err = System.Math.Abs(rConn2.Radius - dynFresh.Radius);
            SmartConLogger.Debug($"  reducer.conn2 R={rConn2.Radius * FeetToMm:F2}mm, dyn R={dynFresh.Radius * FeetToMm:F2}mm, Δ={r2Err * FeetToMm:F2}mm");
        }
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
        bool userManuallyChangedSize)
    {
        double rErr = System.Math.Abs(staticConn.Radius - dynFresh.Radius);
        SmartConLogger.Debug($"  direct: static R={staticConn.Radius * FeetToMm:F2}mm, dyn R={dynFresh.Radius * FeetToMm:F2}mm, Δ={rErr * FeetToMm:F2}mm");
        if (rErr > radiusEps)
        {
            if (userManuallyChangedSize)
            {
                SmartConLogger.Warn($"[Validate] User manually changed size (Δ={rErr * FeetToMm:F2}mm) → reducer needed");
                needsPrimaryReducer = true;
            }
            else
            {
                SmartConLogger.Warn($"[Validate] Direct: mismatch Δ={rErr * FeetToMm:F2}mm — trying to adjust dynamic");
                bool fixed2 = _paramResolver.TrySetConnectorRadius(
                    doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex, staticConn.Radius);
                doc.Regenerate();

                if (fixed2)
                {
                    dynFresh = _connSvc.RefreshConnector(doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex) ?? dynFresh;
                    double verifyDelta = System.Math.Abs(dynFresh.Radius - staticConn.Radius);
                    SmartConLogger.Debug($"  → verify: actual R={dynFresh.Radius * FeetToMm:F2}mm, Δ={verifyDelta * FeetToMm:F2}mm");

                    if (verifyDelta > radiusEps)
                    {
                        SmartConLogger.Warn($"[Validate] Actual radius ({dynFresh.Radius * FeetToMm:F2}mm) ≠ static ({staticConn.Radius * FeetToMm:F2}mm) — falling back to nearest, reducer needed");
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
                    SmartConLogger.Warn($"[Validate] TrySetConnectorRadius returned false — reducer needed");
                    needsPrimaryReducer = true;
                }
            }

            updatedDynamic = dynFresh;
        }

        var posErrD = VectorUtils.DistanceTo(dynFresh.OriginVec3, staticConn.OriginVec3);
        if (posErrD > positionEpsFt)
        {
            PositionCorrector.ApplyOffset(doc, _transformSvc, dynFresh.OwnerElementId, staticConn.OriginVec3 - dynFresh.OriginVec3);
        }

        double angleZD = VectorUtils.AngleBetween(staticConn.BasisZVec3, dynFresh.BasisZVec3);
        double antiErrD = System.Math.Abs(angleZD - System.Math.PI) * 180.0 / System.Math.PI;
        if (antiErrD > angleEpsDeg)
            SmartConLogger.Warn($"[Validate] WARNING: BasisZ not anti-parallel (dev. {antiErrD:F1}°)");
    }

    // ...
}
