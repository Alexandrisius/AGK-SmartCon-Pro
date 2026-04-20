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
/// <summary>
/// Result of pre-connect validation with optional updated dynamic connector and reducer flag.
/// </summary>
/// <param name="ActiveDynamic">Refreshed dynamic connector after validation corrections.</param>
/// <param name="NeedsPrimaryReducer">Whether a reducer must be inserted due to unresolvable size mismatch.</param>
public record ValidateResult(ConnectorProxy? ActiveDynamic, bool NeedsPrimaryReducer);

/// <summary>
/// Result of fitting sizing with the resolved secondary connector and updated dynamic connector.
/// </summary>
/// <param name="FitConn2">Secondary connector of the fitting after sizing.</param>
/// <param name="ActiveDynamic">Dynamic connector after fitting size adjustments.</param>
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

    /// <summary>
    /// Validate and fix connector positions/radii before the final ConnectTo call.
    /// Handles all chain topologies (direct, fitting-only, reducer-only, fitting+reducer).
    /// </summary>
    /// <param name="context">Operation context with document, session, and group.</param>
    /// <param name="activeDynamic">Currently active dynamic connector (may be null for initial).</param>
    /// <param name="currentFittingId">Inserted fitting element, or null.</param>
    /// <param name="primaryReducerId">Inserted reducer element, or null.</param>
    /// <param name="userManuallyChangedSize">Whether the user changed the size dropdown manually.</param>
    /// <param name="topology">Chain topology determining the validation branch.</param>
    /// <returns>Validation result with updated dynamic connector and reducer flag.</returns>
    public ValidateResult ValidateAndFixBeforeConnect(
        ConnectOperationContext context,
        ConnectorProxy? activeDynamic,
        ElementId? currentFittingId,
        ElementId? primaryReducerId,
        bool userManuallyChangedSize,
        ChainTopology topology = ChainTopology.Direct)
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

            if (currentFittingId is not null && primaryReducerId is not null)
            {
                if (topology == ChainTopology.ReducerFitting)
                {
                    ValidateReducerFittingBranch(doc, staticConn, currentFittingId, primaryReducerId,
                        ref dynFresh, ref updatedDynamic, positionEpsFt, radiusEps);
                }
                else
                {
                    ValidateFittingPlusReducerBranch(doc, staticConn, currentFittingId, primaryReducerId,
                        ref dynFresh, ref updatedDynamic, positionEpsFt, radiusEps);
                }
            }
            else if (currentFittingId is not null)
            {
                ValidateFittingBranch(doc, staticConn, currentFittingId, ref dynFresh, ref updatedDynamic,
                    dyn, positionEpsFt, radiusEps, angleEpsDeg, userManuallyChangedSize,
                    ref needsPrimaryReducer);
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

    /// <summary>
    /// Execute the final ConnectTo calls for all elements in the chain.
    /// Dispatches to the correct branch based on <paramref name="topology"/>.
    /// </summary>
    /// <param name="context">Operation context.</param>
    /// <param name="activeDynamic">Dynamic connector to connect.</param>
    /// <param name="currentFittingId">Fitting element ID, or null for direct/reducer-only.</param>
    /// <param name="primaryReducerId">Reducer element ID, or null.</param>
    /// <param name="activeFittingRule">Mapping rule for the active fitting.</param>
    /// <param name="topology">Chain topology.</param>
    public void ExecuteConnectTo(
        ConnectOperationContext context,
        ConnectorProxy? activeDynamic,
        ElementId? currentFittingId,
        ElementId? primaryReducerId,
        FittingMappingRule? activeFittingRule,
        ChainTopology topology = ChainTopology.Direct)
    {
        var dyn = activeDynamic ?? context.Session.DynamicConnector;
        var staticConn = context.Session.StaticConnector;

        PipeConnectDiagnostics.LogConnectorState(
            context.Doc, staticConn, dyn, currentFittingId, _connSvc, "ДО ConnectTo");

        context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_ConnectTo"), doc =>
        {
            doc.Regenerate();

            if (currentFittingId is not null && primaryReducerId is not null)
            {
                // TODO [ChainV2]: Заменить ветви на обобщённый цикл по FittingChainPlan.Links.
                // Текущая реализация поддерживает 2 топологии: FittingReducer и ReducerFitting.
                // Будущая: обобщённый цикл для N звеньев (fitting1 ↔ fitting2 ↔ ... ↔ reducer).
                ExecuteCompositeConnectTo(doc, staticConn, dyn, currentFittingId, primaryReducerId, topology);
            }
            else if (currentFittingId is not null)
            {
                ExecuteSingleIntermediateConnectTo(doc, staticConn, dyn, currentFittingId, "fitting");
            }
            else if (primaryReducerId is not null)
            {
                ExecuteSingleIntermediateConnectTo(doc, staticConn, dyn, primaryReducerId, "reducer");
            }
            else
            {
                var dynR = RefreshDynamicConnector(doc, dyn);
                ConnectPair(doc, staticConn, dynR, "static", "dynamic");
            }

            doc.Regenerate();

            var dynAfter = activeDynamic ?? context.Session.DynamicConnector;
            PipeConnectDiagnostics.LogConnectorState(
                doc, staticConn, dynAfter, currentFittingId, _connSvc, "ПОСЛЕ ConnectTo");
        });
    }

    /// <summary>
    /// Size a fitting's connectors to match static and dynamic radii.
    /// Handles pair-mode (type parameter) and sequential-mode sizing.
    /// Optionally adjusts the dynamic element radius to match the fitting.
    /// </summary>
    /// <param name="context">Operation context.</param>
    /// <param name="activeDynamic">Current dynamic connector state.</param>
    /// <param name="fittingId">Fitting element to size.</param>
    /// <param name="fitConn2">Expected secondary connector index, or null for auto-detection.</param>
    /// <param name="activeFittingRule">Mapping rule for CTC resolution.</param>
    /// <param name="adjustDynamicToFit">Whether to adjust the dynamic element to match the fitting.</param>
    /// <param name="upstreamTarget">Upstream connector for alignment (defaults to static).</param>
    /// <returns>Sizing result with updated connectors.</returns>
    public SizeFittingResult SizeFittingConnectors(
        ConnectOperationContext context,
        ConnectorProxy? activeDynamic,
        ElementId fittingId,
        ConnectorProxy? fitConn2,
        FittingMappingRule? activeFittingRule,
        bool adjustDynamicToFit = true,
        ConnectorProxy? upstreamTarget = null)
    {
        ConnectorProxy? result = null;
        ConnectorProxy? currentDynamic = activeDynamic;

        try
        {
            var effectiveUpstream = upstreamTarget ?? context.Session.StaticConnector;
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
                    .OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, effectiveUpstream.OriginVec3))
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
                        conn1Idx, effectiveUpstream.Radius,
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
                            : effectiveUpstream.Radius;
                        _paramResolver.TrySetConnectorRadius(txDoc, fittingId, c.ConnectorIndex, targetRadius);
                    }
                    txDoc.Regenerate();
                });
            }

            var (realignFitConn2, updatedDynamic) = RealignAfterSizing(
                context, currentDynamic, fittingId, activeFittingRule, effectiveUpstream);
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
        FittingMappingRule? activeFittingRule,
        ConnectorProxy upstreamTarget)
    {
        ConnectorProxy? newFitConn2 = null;
        ConnectorProxy? currentDynamic = activeDynamic;

        try
        {
            context.GroupSession.RunInTransaction(LocalizationService.GetString("Tx_AlignAfterSize"), txDoc =>
            {
                var ctcOvr = context.VirtualCtcStore.GetOverridesForElement(fittingId);
                var dynCtc = _ctcManager.ResolveDynamicTypeFromRule(activeFittingRule, upstreamTarget.ConnectionTypeCode);

                newFitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                    txDoc, fittingId, upstreamTarget, _transformSvc, _connSvc,
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

    private static string DescribeConnector(string label, ConnectorProxy connector)
        => $"{label}({connector.OwnerElementId.GetValue()}:{connector.ConnectorIndex})";

    private static string BuildConnectLog(string leftLabel, ConnectorProxy left, string rightLabel, ConnectorProxy right)
        => $"[Connect] {DescribeConnector(leftLabel, left)} ↔ {DescribeConnector(rightLabel, right)}";

    private ConnectorProxy RefreshDynamicConnector(Document doc, ConnectorProxy dyn)
        => _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;

    private static ConnectionTypeCode ResolveDynamicTypeCode(ConnectorProxy refreshedDynamic, ConnectorProxy fallbackDynamic)
        => refreshedDynamic.ConnectionTypeCode.IsDefined
            ? refreshedDynamic.ConnectionTypeCode
            : fallbackDynamic.ConnectionTypeCode;

    private void ConnectPair(
        Document doc,
        ConnectorProxy? left,
        ConnectorProxy? right,
        string leftLabel,
        string rightLabel)
    {
        if (left is null || right is null)
            return;

        SmartConLogger.Info(BuildConnectLog(leftLabel, left, rightLabel, right));
        _connSvc.ConnectTo(
            doc,
            left.OwnerElementId,
            left.ConnectorIndex,
            right.OwnerElementId,
            right.ConnectorIndex);
    }

    private void ExecuteSingleIntermediateConnectTo(
        Document doc,
        ConnectorProxy staticConn,
        ConnectorProxy dyn,
        ElementId elementId,
        string elementLabel)
    {
        var dynR = RefreshDynamicConnector(doc, dyn);
        var dynCtc = ResolveDynamicTypeCode(dynR, dyn);
        var (conn1, conn2) = ResolveSides(doc, elementId, staticConn, dynCtc);

        ConnectPair(doc, staticConn, conn1, "static", elementLabel);
        ConnectPair(doc, conn2, dynR, $"{elementLabel}.conn2", "dynamic");
    }

    private void ExecuteCompositeConnectTo(
        Document doc,
        ConnectorProxy staticConn,
        ConnectorProxy dyn,
        ElementId fittingId,
        ElementId reducerId,
        ChainTopology topology)
    {
        if (topology == ChainTopology.ReducerFitting)
        {
            SmartConLogger.Info($"[Connect] ReducerFitting branch: reducer={reducerId.GetValue()}, fitting={fittingId.GetValue()}");
            ExecuteCompositeConnectTo(doc, staticConn, dyn, reducerId, fittingId, "reducer", "fitting");
            return;
        }

        SmartConLogger.Info($"[Connect] Fitting+Reducer branch: fitting={fittingId.GetValue()}, reducer={reducerId.GetValue()}");
        ExecuteCompositeConnectTo(doc, staticConn, dyn, fittingId, reducerId, "fitting", "reducer");
    }

    private void ExecuteCompositeConnectTo(
        Document doc,
        ConnectorProxy staticConn,
        ConnectorProxy dyn,
        ElementId firstElementId,
        ElementId secondElementId,
        string firstLabel,
        string secondLabel)
    {
        var dynR = RefreshDynamicConnector(doc, dyn);
        var dynCtc = ResolveDynamicTypeCode(dynR, dyn);
        var (firstConn1, firstConn2) = ResolveSides(doc, firstElementId, staticConn, dynCtc);

        ConnectPair(doc, staticConn, firstConn1, "static", firstLabel);

        if (firstConn2 is null)
            return;

        var (secondConn1, secondConn2) = ResolveSides(doc, secondElementId, firstConn2, dynCtc);

        ConnectPair(doc, firstConn2, secondConn1, $"{firstLabel}.conn2", secondLabel);
        ConnectPair(doc, secondConn2, dynR, $"{secondLabel}.conn2", "dynamic");
    }

    private (ConnectorProxy? Conn1, ConnectorProxy? Conn2) ResolveSides(
        Document doc, ElementId elementId, ConnectorProxy staticConn,
        ConnectionTypeCode dynTypeCode)
    {
        var conns = _connSvc.GetAllFreeConnectors(doc, elementId).ToList();
        return _ctcManager.ResolveConnectorSidesForElement(doc, elementId, conns, dynTypeCode, staticConn);
    }

    private void CorrectElementPosition(
        Document doc, ElementId elementId, ConnectorProxy conn,
        Vec3 targetOrigin, double positionEpsFt)
    {
        var posErr = VectorUtils.DistanceTo(conn.OriginVec3, targetOrigin);
        if (posErr > positionEpsFt)
        {
            SmartConLogger.Warn($"[Validate] offset by {posErr * FeetToMm:F2} mm — correcting");
            _transformSvc.MoveElement(doc, elementId, targetOrigin - conn.OriginVec3);
            doc.Regenerate();
        }
    }

    private void CheckRadiusMismatch(ConnectorProxy conn, ConnectorProxy target, double radiusEps, string label)
    {
        double err = System.Math.Abs(conn.Radius - target.Radius);
        SmartConLogger.Debug($"  {label} R={conn.Radius * FeetToMm:F2}mm, target R={target.Radius * FeetToMm:F2}mm, Δ={err * FeetToMm:F2}mm");
        if (err > radiusEps)
            SmartConLogger.Warn($"[Validate] MISMATCH: {label} radius mismatch (Δ={err * FeetToMm:F2}mm)");
    }

    private void CorrectDynamicPosition(
        Document doc, ref ConnectorProxy dynFresh, ref ConnectorProxy? updatedDynamic,
        ConnectorProxy target, double positionEpsFt)
    {
        var posErr = VectorUtils.DistanceTo(dynFresh.OriginVec3, target.OriginVec3);
        if (posErr > positionEpsFt)
        {
            SmartConLogger.Warn($"[Validate] dynamic offset by {posErr * FeetToMm:F2} mm — correcting");
            PositionCorrector.ApplyOffset(doc, _transformSvc, dynFresh.OwnerElementId, target.OriginVec3 - dynFresh.OriginVec3);
            dynFresh = _connSvc.RefreshConnector(doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex) ?? dynFresh;
            updatedDynamic = dynFresh;
        }
    }

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
            "[Validate] Fitting+Reducer branch",
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
            "[Validate] ReducerFitting branch: static ↔ reducer ↔ fitting ↔ dynamic",
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
        double positionEpsFt,
        double radiusEps,
        double angleEpsDeg,
        bool userManuallyChangedSize,
        ref bool needsPrimaryReducer)
    {
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
            SmartConLogger.Debug($"  fc2 R={fc2.Radius * FeetToMm:F2}mm, dyn R={dynFresh.Radius * FeetToMm:F2}mm, Δ={r2Err * FeetToMm:F2}mm");
            if (r2Err > radiusEps)
            {
                if (userManuallyChangedSize)
                {
                    SmartConLogger.Warn($"[Validate] User changed size manually, fc2↔dynamic Δ={r2Err * FeetToMm:F2}mm — reducer needed");
                    needsPrimaryReducer = true;
                }
                else
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
                        SmartConLogger.Warn($"[Validate] Dynamic adjustment failed — reducer needed");
                        needsPrimaryReducer = true;
                    }
                }
            }

            CorrectDynamicPosition(doc, ref dynFresh, ref updatedDynamic, fc2, positionEpsFt);

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
