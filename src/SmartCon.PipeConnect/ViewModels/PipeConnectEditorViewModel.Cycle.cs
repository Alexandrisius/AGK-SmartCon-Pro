using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel
{
    [RelayCommand(CanExecute = nameof(CanCycleConnector))]
    private void CycleConnector()
    {
        if (_cycleService.State.Count <= 1) return;

        var target = _cycleService.State.FindNext();
        if (target is null) return;

        IsBusy = true;
        StatusMessage = LocalizationService.GetString("Status_SwitchingConnector");

        try
        {
            var previousActive = _activeDynamic;
            var alignTarget = _activeFittingConn2 ?? _ctx.StaticConnector;
            var savedChainDepth = ChainDepth;

            RollbackChainLevels();

            _activeDynamic = _cycleService.CycleAndAlign(
                _doc, _groupSession!, target, alignTarget, _activeDynamic);

            if (!EnsureCycleConnectorCtc(_activeDynamic ?? target))
            {
                RollbackCycleAlignment(previousActive!, alignTarget);
                RestoreChainLevels(savedChainDepth);
                _cycleService.State.UnmarkVisited(target.ConnectorIndex);
                _activeDynamic = previousActive;
                return;
            }

            _chainDisabledByCycle = IsChainConnector(target.ConnectorIndex);
            if (_chainDisabledByCycle)
                SmartConLogger.Info($"[CycleConnector] Connector {target.ConnectorIndex} is chain connector — chain disabled");
            else
                SmartConLogger.Info($"[CycleConnector] Connector {target.ConnectorIndex} is NOT chain connector — chain preserved");

            _activeChainPlan = null;
            UpdateChainUI();

            ReevaluateAfterCycle();
            RefreshCycleSnapshot();

            StatusMessage = LocalizationService.GetString("Status_ConnectorChanged");
            CycleConnectorCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[CycleConnector] Failed: {ex.Message}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCycleConnector() => IsSessionActive && !IsBusy && _cycleService.State.Count > 1;

    private void RollbackChainLevels()
    {
        if (ChainDepth <= 0 || _chainGraph is null) return;

        SmartConLogger.Info($"[RollbackChainLevels] Rolling back {ChainDepth} chain levels before alignment");

        try
        {
            while (ChainDepth > 0)
            {
                _chainOpHandler.DecrementLevel(
                    _doc, _groupSession!, _chainGraph, _snapshotStore, ChainDepth);
                ChainDepth--;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"[RollbackChainLevels] Error (ignored): {ex.Message}");
            ChainDepth = 0;
        }

        UpdateChainUI();
    }

    private void RestoreChainLevels(int targetDepth)
    {
        if (targetDepth <= 0 || _chainGraph is null) return;

        SmartConLogger.Info($"[RestoreChainLevels] Restoring {targetDepth} chain levels after cancel");

        try
        {
            for (int i = 0; i < targetDepth; i++)
            {
                int nextLevel = ChainDepth + 1;
                if (nextLevel >= _chainGraph.Levels.Count) break;

                _chainOpHandler.IncrementLevel(
                    _doc, _groupSession!, _chainGraph, _snapshotStore, _warmedElementIds, nextLevel);
                ChainDepth = nextLevel;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"[RestoreChainLevels] Error (ignored): {ex.Message}");
        }

        _chainDisabledByCycle = false;
        UpdateChainUI();
    }

    private void RefreshCycleSnapshot()
    {
        var conns = GetFreeConnectorsSnapshot();
        if (conns.Count > 0 && _activeDynamic is not null)
            _cycleService.State.Initialize(conns, _activeDynamic);
        CycleConnectorCommand.NotifyCanExecuteChanged();
    }

    private void RollbackCycleAlignment(ConnectorProxy previousConnector, ConnectorProxy alignTarget)
    {
        SmartConLogger.Info($"[RollbackCycleAlignment] Re-aligning to previous connector {previousConnector.ConnectorIndex}");

        var dynId = previousConnector.OwnerElementId;

        _groupSession!.RunInTransaction("Tx_RollbackConnector", d =>
        {
            var fresh = _connSvc.RefreshConnector(d, dynId, previousConnector.ConnectorIndex)
                ?? previousConnector;

            var reAlign = ConnectorAligner.ComputeAlignment(
                alignTarget.OriginVec3, alignTarget.BasisZVec3, alignTarget.BasisXVec3,
                fresh.OriginVec3, fresh.BasisZVec3, fresh.BasisXVec3);

            if (!VectorUtils.IsZero(reAlign.InitialOffset))
                _transformSvc.MoveElement(d, dynId, reAlign.InitialOffset);
            if (reAlign.BasisZRotation is { } bz)
                _transformSvc.RotateElement(d, dynId, reAlign.RotationCenter, bz.Axis, bz.AngleRadians);
            if (reAlign.BasisXSnap is { } bx)
                _transformSvc.RotateElement(d, dynId, reAlign.RotationCenter, bx.Axis, bx.AngleRadians);

            d.Regenerate();
            var r = _connSvc.RefreshConnector(d, dynId, previousConnector.ConnectorIndex);
            if (r is not null)
            {
                var corr = alignTarget.OriginVec3 - r.OriginVec3;
                if (!VectorUtils.IsZero(corr))
                    _transformSvc.MoveElement(d, dynId, corr);
            }
            d.Regenerate();
        });
    }

    private bool EnsureCycleConnectorCtc(ConnectorProxy proxy)
    {
        var effectiveCtc = GetEffectiveCtcForConnector(proxy);
        if (IsKnownTypeCode(effectiveCtc))
            return true;

        var types = _mappingRepo.GetConnectorTypes();
        if (types.Count == 0)
        {
            _dialogSvc.ShowWarning("SmartCon", LocalizationService.GetString("Msg_ConfigureTypes"));
            return false;
        }

        var selected = _dialogSvc.ShowMiniTypeSelector(types);
        if (selected is null)
        {
            SmartConLogger.Info("[CycleConnector] User cancelled MiniTypeSelector — rolling back");
            return false;
        }

        var element = _doc.GetElement(proxy.OwnerElementId);

        if (element is MEPCurve or FlexPipe)
        {
            _txService.RunInTransaction("SetConnectorType", txDoc =>
            {
                _familyConnSvc.SetConnectorTypeCode(
                    txDoc, proxy.OwnerElementId, proxy.ConnectorIndex, selected);
            });
        }
        else
        {
            var ctc = new ConnectionTypeCode(selected.Code);
            _virtualCtcStore.Set(proxy.OwnerElementId, proxy.ConnectorIndex, ctc, selected);
            SmartConLogger.Info($"[CycleConnector] Virtual CTC for {proxy.OwnerElementId.GetValue()}:{proxy.ConnectorIndex} = {selected.Code}.{selected.Name}");
        }

        return true;
    }

    private ConnectionTypeCode GetEffectiveCtcForConnector(ConnectorProxy proxy)
    {
        var virtualCtc = _virtualCtcStore.Get(proxy.OwnerElementId, proxy.ConnectorIndex);
        if (virtualCtc.HasValue)
            return virtualCtc.Value;

        return proxy.ConnectionTypeCode;
    }

    private bool IsKnownTypeCode(ConnectionTypeCode code)
    {
        if (!code.IsDefined) return false;
        var types = _mappingRepo.GetConnectorTypes();
        return types.Any(t => t.Code == code.Value);
    }

    private bool IsChainConnector(int connectorIndex)
    {
        if (_chainGraph is null) return false;

        var rootId = _chainGraph.RootId;

        foreach (var edge in _chainGraph.Edges)
        {
            if (ElementIdEqualityComparer.Instance.Equals(edge.FromElementId, rootId)
                && edge.FromConnectorIndex == connectorIndex)
                return true;

            if (ElementIdEqualityComparer.Instance.Equals(edge.ToElementId, rootId)
                && edge.ToConnectorIndex == connectorIndex)
                return true;
        }

        return false;
    }

    private void ReevaluateAfterCycle()
    {
        if (_activeDynamic is null) return;

        _userManuallyChangedSize = false;

        var newCtc = GetEffectiveCtcForConnector(_activeDynamic);
        var staticCtc = _ctx.StaticConnector.ConnectionTypeCode;

        SmartConLogger.Info($"[ReevaluateAfterCycle] New dynamic CTC={newCtc.Value}, Static CTC={staticCtc.Value}, " +
            $"Radius={_activeDynamic.Radius * Core.Units.FeetToMm:F1}mm");

        var proposed = _fittingMapper.GetMappings(staticCtc, newCtc);

        if (proposed.Count == 0 && staticCtc.IsDefined && newCtc.IsDefined)
            proposed = _fittingMapper.FindShortestFittingPath(staticCtc, newCtc);

        AvailableFittings.Clear();
        AvailableReducers.Clear();

        var (newFittings, newReducers) = FittingCardBuilder.Build(
            proposed, staticCtc, newCtc);

        foreach (var f in newFittings) AvailableFittings.Add(f);
        foreach (var r in newReducers) AvailableReducers.Add(r);

        SelectedFitting = AvailableFittings.Count > 0 ? AvailableFittings[0] : null;

        if (_currentFittingId is not null)
        {
            SmartConLogger.Info("[ReevaluateAfterCycle] Deleting old fitting before re-insert");
            _groupSession!.RunInTransaction("Tx_CleanupOldFitting", doc =>
            {
                _fittingInsertSvc.DeleteElement(doc, _currentFittingId);
                _virtualCtcStore.RemoveForElement(_currentFittingId);
            });
            _currentFittingId = null;
            _activeFittingConn2 = null;
        }

        if (_primaryReducerId is not null)
        {
            SmartConLogger.Info("[ReevaluateAfterCycle] Deleting old reducer before re-insert");
            _groupSession!.RunInTransaction("Tx_CleanupOldReducer", doc =>
            {
                _fittingInsertSvc.DeleteElement(doc, _primaryReducerId);
                _virtualCtcStore.RemoveForElement(_primaryReducerId);
            });
            _primaryReducerId = null;
            _needsPrimaryReducer = false;
            IsReducerVisible = false;
        }

        var defaultFitting = SelectedFitting;
        if (defaultFitting is not null && !defaultFitting.IsDirectConnect)
        {
            StatusMessage = LocalizationService.GetString("Status_UpdatingFitting");
            InsertFittingSilent(defaultFitting);
        }
        else if (defaultFitting is not null && defaultFitting.IsDirectConnect)
        {
            _activeFittingRule = null;
        }

        ReloadDynamicSizesForActiveConnector();

        if (_primaryReducerId is null)
            CheckReducerNeededAfterCycle();
    }

    private void ReloadDynamicSizesForActiveConnector()
    {
        if (_activeDynamic is null) return;

        var refreshed = _connSvc.RefreshConnector(
            _doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex);
        var proxy = refreshed ?? _activeDynamic;

        var result = _sizeLoader.LoadInitialSizes(_doc, proxy);
        AvailableDynamicSizes.Clear();
        foreach (var s in result.Sizes) AvailableDynamicSizes.Add(s);
        SelectedDynamicSize = result.DefaultSelection;
        HasSizeOptions = result.HasSizeOptions;
    }

    private void CheckReducerNeededAfterCycle()
    {
        if (_activeDynamic is null) return;

        const double radiusEps = 1e-5;

        bool needsReducer;

        if (_currentFittingId is not null && _activeFittingConn2 is not null)
        {
            needsReducer = PipeConnectSizeHandler.DetectReducerNeededAfterFitting(
                _activeDynamic, _activeFittingConn2);
        }
        else
        {
            var dynRadius = _activeDynamic.Radius;
            var staticRadius = _ctx.StaticConnector.Radius;
            needsReducer = Math.Abs(dynRadius - staticRadius) > radiusEps;

            if (needsReducer)
                SmartConLogger.Info($"[CheckReducerAfterCycle] Radii mismatch: dyn={dynRadius * Core.Units.FeetToMm:F1}mm, " +
                    $"static={staticRadius * Core.Units.FeetToMm:F1}mm → reducer needed");
        }

        if (needsReducer)
        {
            _needsPrimaryReducer = true;

            if (AvailableReducers.Count > 0)
            {
                SelectedReducer = AvailableReducers[0];
                IsReducerVisible = true;
                StatusMessage = LocalizationService.GetString("Status_InsertingReducer");
                InsertReducerSilent();
            }
            else
            {
                IsReducerVisible = true;
                SmartConLogger.Warn("[CheckReducerAfterCycle] Reducer needed but no reducer families found");
            }
        }
        else
        {
            _needsPrimaryReducer = false;
            IsReducerVisible = false;
        }
    }
}
