using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.PipeConnect.Services;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel
{
    // ── Active dynamic switching (element-wise chain mode) ────────────────────

    /// <summary>
    /// Per-connection-point fitting/reducer state. The editor fields
    /// (<see cref="_currentFittingId"/>, <see cref="_primaryReducerId"/>, …) always
    /// describe the ACTIVE connection point; on every dynamic switch the fields are
    /// captured into this slot and the slot of the newly activated point is loaded.
    /// Key = queue index of the point's dynamic element (0 = root static↔dynamic pair).
    /// </summary>
    private sealed class ConnectionPointState
    {
        public ElementId? FittingId;
        public ConnectorProxy? FittingConn2;
        public FittingMappingRule? FittingRule;
        public ElementId? ReducerId;
        public bool NeedsReducer;
        public bool ReducerVisible;
        public bool UserChangedSize;
        public string? SelectedFittingKey;
        public string? SelectedReducerKey;
    }

    private readonly Dictionary<int, ConnectionPointState> _pointStates = new();

    /// <summary>Queue index of the active connection point (== ChainDepth in steady state).</summary>
    private int _activePointIndex;

    /// <summary>Selection keys captured by LoadPointState, applied by ReevaluateFittingsForActivePoint.</summary>
    private string? _pendingFittingKey;
    private string? _pendingReducerKey;

    /// <summary>
    /// Parent connector of the active connection point — the "local static" the
    /// active dynamic is attached to. Rotation axis, fitting/reducer upstream
    /// target and reducer-detection reference are all derived from it.
    /// Null only before Init; falls back to the session static connector.
    /// </summary>
    private ConnectorProxy? _activeParentConnector;

    /// <summary>Upstream connector of the active connection point (never null after Init).</summary>
    private ConnectorProxy ActiveUpstreamConnector => _activeParentConnector ?? _ctx.StaticConnector;

    [ObservableProperty] private string _dynamicElementTitle = string.Empty;
    [ObservableProperty] private string _zeroRotationHint = string.Empty;

    /// <summary>
    /// Hook invoked after a chain element has been attached (queue tail advanced,
    /// ChainDepth already points at the new element). Makes the freshly attached
    /// element the new active dynamic: all editor commands (rotate / resize /
    /// insert fitting) target it, and the rotation axis becomes the BasisZ of the
    /// parent connector it attached to.
    /// </summary>
    private void OnChainElementAttached(ChainOperationHandler.SingleElementResult result)
    {
        if (result.Edge is null || _elementQueue is null) return;

        SaveActivePointState();

        var edge = result.Edge.Value;
        var entry = _elementQueue[ChainDepth];

        _activePointIndex = ChainDepth;
        _activeParentConnector = RefreshConnectorSafe(edge.ParentId, edge.ParentConnIdx);
        _activeDynamic = _ctcManager.RefreshWithCtcOverride(_doc, entry.ElementId, edge.ElemConnIdx)
            ?? RefreshConnectorSafe(entry.ElementId, edge.ElemConnIdx);

        LoadPointState(ChainDepth);
        RefreshAfterDynamicSwitch();

        SmartConLogger.Info($"Active dynamic → element {entry.ElementId.GetValue()} " +
            $"(queue {ChainDepth}/{TotalChainElementCount}, level {entry.Level}, " +
            $"parent={edge.ParentId.GetValue()}:{edge.ParentConnIdx})");
    }

    /// <summary>
    /// Hook invoked after a chain element has been detached (queue tail retracted,
    /// ChainDepth already decremented). Deletes the detached point's fitting/reducer
    /// from the model and returns the active dynamic to the previous queue entry
    /// (or the root dynamic when the queue is fully retracted).
    /// </summary>
    private void OnChainElementDetached(ChainQueueEntry entry, int detachedIndex)
    {
        if (_elementQueue is null) return;

        // Fields still hold the detached point's state — remove its fitting/reducer
        // from the model (the element itself was already rolled back from snapshot).
        var detachedState = CaptureCurrentPointState();
        DeleteFittingAndReducerOfPoint(detachedState);
        _pointStates.Remove(detachedIndex);

        _activePointIndex = ChainDepth;
        LoadPointState(ChainDepth);
        RefreshActiveDynamicForCurrentPoint();
        RefreshAfterDynamicSwitch();

        SmartConLogger.Info($"Active dynamic ← element {_activeDynamic?.OwnerElementId.GetValue()} " +
            $"(queue {ChainDepth}/{TotalChainElementCount})");
    }

    /// <summary>
    /// Switch the active connection point without touching the queue depth.
    /// Used by Connect to run final validation/connection on the root pair
    /// (static ↔ root dynamic) regardless of where the user stopped in the queue.
    /// </summary>
    private void SwitchToPoint(int pointIndex)
    {
        if (pointIndex == _activePointIndex) return;

        SmartConLogger.Info($"SwitchToPoint: {_activePointIndex} → {pointIndex}");
        SaveActivePointState();
        _activePointIndex = pointIndex;
        LoadPointState(pointIndex);

        if (pointIndex == 0)
        {
            _activeParentConnector = _ctx.StaticConnector;
            _activeDynamic = _ctcManager.RefreshWithCtcOverride(
                _doc, _ctx.DynamicConnector.OwnerElementId, _ctx.DynamicConnector.ConnectorIndex)
                ?? _ctx.DynamicConnector;
        }
        else
        {
            RefreshActiveDynamicForCurrentPoint();
        }

        SmartConLogger.Info($"Active point {pointIndex}: dynamic={_activeDynamic?.OwnerElementId.GetValue()}:{_activeDynamic?.ConnectorIndex}, " +
            $"parent={_activeParentConnector?.OwnerElementId.GetValue()}:{_activeParentConnector?.ConnectorIndex}, " +
            $"fitting={_currentFittingId?.GetValue()}, reducer={_primaryReducerId?.GetValue()}");
    }

    /// <summary>
    /// Resolve the active dynamic/parent connectors from the queue tail:
    /// index 0 → root pair (static ↔ dynamic), index N → parent edge of queue[N].
    /// </summary>
    private void RefreshActiveDynamicForCurrentPoint()
    {
        if (ChainDepth == 0 || _elementQueue is null || _chainGraph is null)
        {
            _activeParentConnector = _ctx.StaticConnector;
            _activeDynamic = _ctcManager.RefreshWithCtcOverride(
                _doc, _ctx.DynamicConnector.OwnerElementId, _ctx.DynamicConnector.ConnectorIndex)
                ?? _ctx.DynamicConnector;
            return;
        }

        var entry = _elementQueue[ChainDepth];
        var edge = ChainOperationHandler.FindEdgeToParent(entry.ElementId, entry.Level, _chainGraph);
        if (edge is not { } e)
        {
            SmartConLogger.Warn($"RefreshActiveDynamic: parent edge not found for element {entry.ElementId.GetValue()} " +
                $"[Action: состояние редактора может быть рассинхронизировано — откатите очередь кнопкой «−» и подключите заново]");
            return;
        }

        _activeParentConnector = RefreshConnectorSafe(e.ParentId, e.ParentConnIdx);
        _activeDynamic = _ctcManager.RefreshWithCtcOverride(_doc, entry.ElementId, e.ElemConnIdx)
            ?? RefreshConnectorSafe(entry.ElementId, e.ElemConnIdx);
    }

    /// <summary>Reload every UI projection bound to the active dynamic/point.</summary>
    private void RefreshAfterDynamicSwitch()
    {
        InitializeCycleStateForActiveDynamic();
        ReevaluateFittingsForActivePoint();
        ReloadDynamicSizesForActiveConnector();
        UpdateDynamicInfoPanel();
    }

    private static string? GetCardKey(FittingCardItem? card)
        => card?.PrimaryFitting is { } f ? $"{f.FamilyName}|{f.SymbolName}" : null;

    private ConnectionPointState CaptureCurrentPointState() => new()
    {
        FittingId = _currentFittingId,
        FittingConn2 = _activeFittingConn2,
        FittingRule = _activeFittingRule,
        ReducerId = _primaryReducerId,
        NeedsReducer = _needsPrimaryReducer,
        ReducerVisible = IsReducerVisible,
        UserChangedSize = _userManuallyChangedSize,
        SelectedFittingKey = GetCardKey(SelectedFitting),
        SelectedReducerKey = GetCardKey(SelectedReducer),
    };

    private void SaveActivePointState()
    {
        _pointStates[_activePointIndex] = CaptureCurrentPointState();
    }

    private void LoadPointState(int pointIndex)
    {
        if (_pointStates.TryGetValue(pointIndex, out var state))
        {
            _currentFittingId = state.FittingId;
            _activeFittingConn2 = state.FittingConn2;
            _activeFittingRule = state.FittingRule;
            _primaryReducerId = state.ReducerId;
            _needsPrimaryReducer = state.NeedsReducer;
            IsReducerVisible = state.ReducerVisible;
            _userManuallyChangedSize = state.UserChangedSize;
            _pendingFittingKey = state.SelectedFittingKey;
            _pendingReducerKey = state.SelectedReducerKey;
        }
        else
        {
            _currentFittingId = null;
            _activeFittingConn2 = null;
            _activeFittingRule = null;
            _primaryReducerId = null;
            _needsPrimaryReducer = false;
            IsReducerVisible = false;
            _userManuallyChangedSize = false;
            _pendingFittingKey = null;
            _pendingReducerKey = null;
        }

        InsertFittingCommand.NotifyCanExecuteChanged();
        InsertReducerCommand.NotifyCanExecuteChanged();
        ReflectFittingCtcCommand.NotifyCanExecuteChanged();
        ReflectReducerCtcCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// Delete the fitting/reducer of a detached connection point from the model.
    /// Their connectors are disconnected first so no dangling links survive.
    /// </summary>
    private void DeleteFittingAndReducerOfPoint(ConnectionPointState state)
    {
        if (state.FittingId is null && state.ReducerId is null) return;

        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_CleanupOldFitting"), doc =>
        {
            foreach (var id in new[] { state.FittingId, state.ReducerId })
            {
                if (id is null) continue;
                SmartConLogger.Info($"Deleting point fitting/reducer id={id.GetValue()}");
                DisconnectAllConnectorsOf(doc, id);
                _fittingInsertSvc.DeleteElement(doc, id);
                _virtualCtcStore.RemoveForElement(id);
            }
            doc.Regenerate();
        });
    }

    private void DisconnectAllConnectorsOf(Document doc, ElementId elementId)
    {
        foreach (var conn in _connSvc.GetAllConnectors(doc, elementId))
        {
            if (!conn.IsFree)
                _connSvc.DisconnectAllFromConnector(doc, elementId, conn.ConnectorIndex);
        }
    }

    /// <summary>
    /// Rebuild the fitting/reducer cards for the active connection point's CTC pair
    /// (parent ↔ dynamic). Unlike <see cref="ReevaluateAfterCycle"/> this never
    /// deletes or inserts fittings — it only refreshes the available choices.
    /// </summary>
    private void ReevaluateFittingsForActivePoint()
    {
        if (_activeDynamic is null) return;

        var parentCtc = GetEffectiveCtcForConnector(ActiveUpstreamConnector);
        var dynCtc = GetEffectiveCtcForConnector(_activeDynamic);

        SmartConLogger.Info($"Active point CTC pair: parent={parentCtc.Value} ↔ dynamic={dynCtc.Value}");

        var proposed = _fittingMapper.GetMappings(parentCtc, dynCtc);
        if (proposed.Count == 0 && parentCtc.IsDefined && dynCtc.IsDefined)
            proposed = _fittingMapper.FindShortestFittingPath(parentCtc, dynCtc);

        AvailableFittings.Clear();
        AvailableReducers.Clear();

        var (newFittings, newReducers) = FittingCardBuilder.Build(proposed, parentCtc, dynCtc);
        foreach (var f in newFittings) AvailableFittings.Add(f);
        foreach (var r in newReducers) AvailableReducers.Add(r);

        SelectedFitting = SelectCardByKey(AvailableFittings, _pendingFittingKey);
        SelectedReducer = SelectCardByKey(AvailableReducers, _pendingReducerKey);
        _pendingFittingKey = null;
        _pendingReducerKey = null;
    }

    /// <summary>Pick the card matching the saved key, falling back to the first card.</summary>
    private static FittingCardItem? SelectCardByKey(
        IReadOnlyList<FittingCardItem> cards, string? key)
    {
        if (key is not null)
        {
            foreach (var card in cards)
            {
                if (GetCardKey(card) == key)
                    return card;
            }
        }
        return cards.Count > 0 ? cards[0] : null;
    }

    private void InitializeCycleStateForActiveDynamic()
    {
        if (_activeDynamic is null) return;
        var conns = GetFreeConnectorsSnapshot(_activeDynamic.OwnerElementId);
        if (conns.Count > 0)
            _cycleService.State.Initialize(conns, _activeDynamic);
        CycleConnectorCommand.NotifyCanExecuteChanged();
    }

    private ConnectorProxy? RefreshConnectorSafe(ElementId elementId, int connectorIndex)
    {
        try
        {
            return _connSvc.RefreshConnector(_doc, elementId, connectorIndex);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"RefreshConnector failed for {elementId.GetValue()}:{connectorIndex}: {ex.Message} " +
                $"[Action: состояние коннектора могло устареть — продолжайте осторожно или переоткройте редактор]");
            return null;
        }
    }

    /// <summary>
    /// Update the info panel: family/symbol of the active dynamic. Queue position
    /// and DN are intentionally not shown — the status bar reports progress and
    /// the size ComboBox shows the DN.
    /// </summary>
    private void UpdateDynamicInfoPanel()
    {
        if (_activeDynamic is null)
        {
            DynamicElementTitle = string.Empty;
            return;
        }

        var elem = _doc.GetElement(_activeDynamic.OwnerElementId);
        DynamicElementTitle = ResolveElementDisplayName(elem);
        UpdateZeroRotationHint();
    }

    /// <summary>
    /// Refresh the zero-rotation tooltip: which global axis the zeroing targets
    /// and the current angle to it — so the user always knows what the button
    /// will do before pressing it.
    /// </summary>
    private void UpdateZeroRotationHint()
    {
        try
        {
            double angleDeg = ComputeCurrentAngleToReferenceDeg();
            ZeroRotationHint = string.Format(
                LocalizationService.GetString("Tip_ZeroRotation_Dynamic"),
                GetReferenceAxisName(),
                (int)System.Math.Round(angleDeg));
        }
        catch
        {
            ZeroRotationHint = string.Empty;
        }
    }

    /// <summary>Name of the upright reference axis (Z = world up, Y/X = horizontal grid fallback).</summary>
    private string GetReferenceAxisName()
    {
        var axis = ConnectorAligner.SelectUprightReference(ActiveUpstreamConnector.BasisZVec3);
        if (axis == SmartCon.Core.Math.Vec3.BasisX) return "X";
        if (axis == SmartCon.Core.Math.Vec3.BasisZ) return "Z";
        return "Y";
    }

    /// <summary>Current angle (degrees) of the free connector's BasisY to the upright reference projection.</summary>
    private double ComputeCurrentAngleToReferenceDeg()
    {
        if (_activeDynamic is null) return 0;

        var parent = ActiveUpstreamConnector;
        var axisNorm = SmartCon.Core.Math.VectorUtils.Normalize(parent.BasisZVec3);
        var referenceAxis = ConnectorAligner.SelectUprightReference(axisNorm);
        var by = _activeDynamic.BasisYVec3;

        var projTarget = referenceAxis - axisNorm * SmartCon.Core.Math.VectorUtils.DotProduct(referenceAxis, axisNorm);
        var projCurrent = by - axisNorm * SmartCon.Core.Math.VectorUtils.DotProduct(by, axisNorm);
        if (projTarget.LengthSquared < 1e-12 || projCurrent.LengthSquared < 1e-12)
            return 0;

        return SmartCon.Core.Math.VectorUtils.AngleBetween(projCurrent, projTarget) * 180.0 / System.Math.PI;
    }

    private static string ResolveElementDisplayName(Element? elem)
    {
        if (elem is FamilyInstance fi && fi.Symbol is not null)
        {
            string familyName = fi.Symbol.Family?.Name ?? string.Empty;
            string symbolName = fi.Symbol.Name ?? string.Empty;
            if (string.IsNullOrEmpty(familyName)) return symbolName;
            if (string.IsNullOrEmpty(symbolName) || string.Equals(familyName, symbolName, StringComparison.Ordinal))
                return familyName;
            return $"{familyName}: {symbolName}";
        }
        return elem?.Name ?? string.Empty;
    }

    /// <summary>
    /// Diagnostic: log the active dynamic's final orientation after a rotation —
    /// angle of the free connector's BasisX to the parent's BasisX in the
    /// connector plane, and angle of its BasisY to the upright reference
    /// projection (world up). Makes orientation drift measurable in the log
    /// instead of relying on visual checks.
    /// </summary>
    private void LogFinalRotationAngles()
    {
        try
        {
            if (_activeDynamic is null) return;

            var parent = ActiveUpstreamConnector;
            var axisNorm = SmartCon.Core.Math.VectorUtils.Normalize(parent.BasisZVec3);
            var bx = _activeDynamic.BasisXVec3;
            var by = _activeDynamic.BasisYVec3;

            double angleToParent = SmartCon.Core.Math.VectorUtils.AngleBetweenInPlane(
                bx, parent.BasisXVec3, axisNorm) * 180.0 / System.Math.PI;

            var referenceAxis = ConnectorAligner.SelectUprightReference(axisNorm);
            var projBy = by - axisNorm * SmartCon.Core.Math.VectorUtils.DotProduct(by, axisNorm);
            var projRef = referenceAxis - axisNorm * SmartCon.Core.Math.VectorUtils.DotProduct(referenceAxis, axisNorm);
            double angleToUp = projBy.LengthSquared < 1e-12 || projRef.LengthSquared < 1e-12
                ? 0
                : SmartCon.Core.Math.VectorUtils.AngleBetween(projBy, projRef) * 180.0 / System.Math.PI;

            SmartConLogger.Info($"Rotate final: elem={_activeDynamic.OwnerElementId.GetValue()}, " +
                $"BasisX→parent={angleToParent:F2}°, BasisY→up={angleToUp:F2}°");
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"LogFinalRotationAngles failed (ignored): {ex.Message}");
        }
    }
}
