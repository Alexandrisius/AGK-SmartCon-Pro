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
public sealed partial class ConnectExecutor
{
    private readonly IConnectorService _connSvc;
    private readonly ITransformService _transformSvc;
    private readonly IAlignmentService _alignmentSvc;
    private readonly IParameterResolver _paramResolver;
    private readonly IFittingInsertService _fittingInsertSvc;
    private readonly INetworkMover _networkMover;
    private readonly IFittingMappingRepository _mappingRepo;
    private readonly FittingCtcManager _ctcManager;

    public ConnectExecutor(
        IConnectorService connSvc,
        ITransformService transformSvc,
        IAlignmentService alignmentSvc,
        IParameterResolver paramResolver,
        IFittingInsertService fittingInsertSvc,
        INetworkMover networkMover,
        IFittingMappingRepository mappingRepo,
        FittingCtcManager ctcManager)
    {
        _connSvc = connSvc;
        _transformSvc = transformSvc;
        _alignmentSvc = alignmentSvc;
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
    /// <param name="lockNetwork">"Блокировать" is on: the dynamic's DN is frozen —
    /// a radius mismatch becomes a reducer request, never a resize (issue #167).</param>
    /// <returns>Validation result with updated dynamic connector and reducer flag.</returns>
    public ValidateResult ValidateAndFixBeforeConnect(
        ConnectOperationContext context,
        ConnectorProxy? activeDynamic,
        ElementId? currentFittingId,
        ElementId? primaryReducerId,
        bool userManuallyChangedSize,
        ChainTopology topology = ChainTopology.Direct,
        bool lockNetwork = false)
    {
        using var _scope = SmartConLogger.BeginScope("Connect",
            ("Method", "ValidateAndFixBeforeConnect"),
            ("Topology", topology.ToString()));

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
                    dyn, context.Session.ParamTargetRadius, positionEpsFt, radiusEps, angleEpsDeg, userManuallyChangedSize,
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
                    userManuallyChangedSize, lockNetwork);
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
        using var _scope = SmartConLogger.BeginScope("Connect",
            ("Method", "ExecuteConnectTo"),
            ("Topology", topology.ToString()));

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

    private static string DescribeConnector(string label, ConnectorProxy connector)
        => $"{label}({connector.OwnerElementId.GetValue()}:{connector.ConnectorIndex})";

    private static string BuildConnectLog(string leftLabel, ConnectorProxy left, string rightLabel, ConnectorProxy right)
        => $"{DescribeConnector(leftLabel, left)} ↔ {DescribeConnector(rightLabel, right)}";

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
            SmartConLogger.Info($"ReducerFitting branch: reducer={reducerId.GetValue()}, fitting={fittingId.GetValue()}");
            ExecuteCompositeConnectTo(doc, staticConn, dyn, reducerId, fittingId, "reducer", "fitting");
            return;
        }

        SmartConLogger.Info($"Fitting+Reducer branch: fitting={fittingId.GetValue()}, reducer={reducerId.GetValue()}");
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
            SmartConLogger.Warn($"offset by {posErr * FeetToMm:F2} mm — correcting " +
                $"[Action: проверьте итоговое положение элемента после соединения]");
            _transformSvc.MoveElement(doc, elementId, targetOrigin - conn.OriginVec3);
            doc.Regenerate();
        }
    }

    private void CheckRadiusMismatch(ConnectorProxy conn, ConnectorProxy target, double radiusEps, string label)
    {
        double err = System.Math.Abs(conn.Radius - target.Radius);
        SmartConLogger.Debug($"{label} R={conn.Radius * FeetToMm:F2}mm, target R={target.Radius * FeetToMm:F2}mm, Δ={err * FeetToMm:F2}mm");
        if (err > radiusEps)
            SmartConLogger.Warn($"MISMATCH: {label} radius mismatch (Δ={err * FeetToMm:F2}mm) " +
                $"[Action: проверьте размеры коннекторов после соединения — при необходимости добавьте переходник]");
    }

    private void CorrectDynamicPosition(
        Document doc, ref ConnectorProxy dynFresh, ref ConnectorProxy? updatedDynamic,
        ConnectorProxy target, double positionEpsFt)
    {
        var posErr = VectorUtils.DistanceTo(dynFresh.OriginVec3, target.OriginVec3);
        if (posErr > positionEpsFt)
        {
            SmartConLogger.Warn($"dynamic offset by {posErr * FeetToMm:F2} mm — correcting " +
                $"[Action: проверьте итоговое положение элемента после соединения]");
            PipeAbsorptionApplier.MoveOrAbsorb(
                doc, _transformSvc, dynFresh.OwnerElementId, dynFresh.OriginVec3,
                target.OriginVec3 - dynFresh.OriginVec3);
            doc.Regenerate();
            dynFresh = _connSvc.RefreshConnector(doc, dynFresh.OwnerElementId, dynFresh.ConnectorIndex) ?? dynFresh;
            updatedDynamic = dynFresh;
        }
    }

}
