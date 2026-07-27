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
        using var _scope = SmartConLogger.BeginScope("EditorCycle",
            ("Method", "CycleConnector"),
            ("DynId", _activeDynamic?.OwnerElementId.GetValue() ?? -1));
        if (_cycleService.State.Count <= 1) return;

        var target = _cycleService.State.FindNext();
        if (target is null) return;

        SmartConLogger.Info($"Cycling connector on element {target.OwnerElementId.GetValue()}: " +
            $"{_activeDynamic?.ConnectorIndex} → {target.ConnectorIndex}");

        IsBusy = true;
        StatusMessage = LocalizationService.GetString("Status_SwitchingConnector");

        try
        {
            UnsealIfSealed("смена коннектора");

            var previousActive = _activeDynamic;
            var alignTarget = _activeFittingConn2 ?? ActiveUpstreamConnector;
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
                SmartConLogger.Info($"Connector {target.ConnectorIndex} is chain connector — chain disabled");
            else
                SmartConLogger.Info($"Connector {target.ConnectorIndex} is NOT chain connector — chain preserved");

            // Cycling is root-only (CanCycleConnector requires ChainDepth == 0): the chosen
            // connector becomes the root pair connector — Connect must use it, not the
            // session-default one (issue: tee connected to the branch instead of the run).
            _rootDynamicConnector = _activeDynamic ?? target;

            _activeChainPlan = null;
            UpdateChainUI();

            ReevaluateAfterCycle();
            RefreshCycleSnapshot();
            SaveActivePointState();

            StatusMessage = LocalizationService.GetString("Status_ConnectorChanged");
            CycleConnectorCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Connector cycling is available only for the ROOT connection point
    /// (ChainDepth == 0): the root dynamic is not physically connected yet
    /// (final ConnectTo happens in Connect), so re-alignment is safe.
    /// A chain element at depth N is already connected to its parent — cycling
    /// its connector would require a disconnect→align→reconnect cycle and would
    /// invalidate the immutable graph edge, so it is intentionally not offered.
    /// </summary>
    private bool CanCycleConnector() => IsSessionActive && !IsBusy && ChainDepth == 0 && _cycleService.State.Count > 1;

    private void RollbackChainLevels()
    {
        using var _scope = SmartConLogger.BeginScope("EditorCycle",
            ("Method", "RollbackChainLevels"));
        if (ChainDepth <= 0 || _chainGraph is null || _elementQueue is null) return;

        SmartConLogger.Info($"Rolling back {ChainDepth} chain element(s) before alignment");

        try
        {
            while (ChainDepth > 0)
            {
                var entry = _elementQueue[ChainDepth];
                _chainOpHandler.DetachSingleElement(
                    _doc, _groupSession!, _chainGraph, _snapshotStore, entry);
                _attachedElementIds.Remove(entry.ElementId.GetValue());
                ChainDepth--;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"Error (ignored): {ex.Message} [Action: глубина цепочки сброшена в 0 — проверьте целостность цепочки элементов]");
            ChainDepth = 0;
        }

        UpdateChainUI();
    }

    private void RestoreChainLevels(int targetDepth)
    {
        if (targetDepth <= 0 || _chainGraph is null || _elementQueue is null) return;

        SmartConLogger.Info($"Restoring {targetDepth} chain element(s) after cancel");

        try
        {
            for (int i = 0; i < targetDepth; i++)
            {
                int nextIndex = ChainDepth + 1;
                if (nextIndex >= _elementQueue.Count) break;

                var entry = _elementQueue[nextIndex];
                var result = _chainOpHandler.AttachSingleElement(
                    _doc, _groupSession!, _chainGraph, _snapshotStore, _warmedElementIds, entry,
                    _attachedElementIds, LockNetwork);

                if (result.Edge is null)
                {
                    SmartConLogger.Warn($"RestoreChainLevels: element {entry.ElementId.GetValue()} could not be re-attached — stopping restore. " +
                        $"[Action: проверьте целостность цепочки элементов и подключите остаток вручную кнопкой «+»]");
                    break;
                }

                _attachedElementIds.Add(entry.ElementId.GetValue());
                ChainDepth = nextIndex;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"Error (ignored): {ex.Message} [Action: восстановление элементов цепочки прервано — проверьте целостность цепочки элементов]");
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
        SmartConLogger.Info($"Re-aligning to previous connector {previousConnector.ConnectorIndex}");

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
            _dialogSvc.ShowWarning(LocalizationService.GetString("App_Name"), LocalizationService.GetString("Msg_ConfigureTypes"));
            return false;
        }

        var selected = _dialogSvc.ShowMiniTypeSelector(types);
        if (selected is null)
        {
            SmartConLogger.Info("User cancelled MiniTypeSelector — rolling back");
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
            SmartConLogger.Info($"Virtual CTC for {proxy.OwnerElementId.GetValue()}:{proxy.ConnectorIndex} = {selected.Code}.{selected.Name}");
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

        SmartConLogger.Info($"New dynamic CTC={newCtc.Value}, Static CTC={staticCtc.Value}, " +
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
            SmartConLogger.Info("Deleting old fitting before re-insert");
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
            SmartConLogger.Info("Deleting old reducer before re-insert");
            _groupSession!.RunInTransaction("Tx_CleanupOldReducer", doc =>
            {
                _fittingInsertSvc.DeleteElement(doc, _primaryReducerId);
                _virtualCtcStore.RemoveForElement(_primaryReducerId);
            });
            _primaryReducerId = null;
            _needsPrimaryReducer = false;
            IsReducerVisible = false;
            _lockInsertedReducer = false;
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
                SmartConLogger.Info($"Radii mismatch: dyn={dynRadius * Core.Units.FeetToMm:F1}mm, " +
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
                SmartConLogger.Warn("Reducer needed but no reducer families found " +
                    "[Action: добавьте семейство переходника в mapping (Настройки → Правила)]");
            }
        }
        else
        {
            _needsPrimaryReducer = false;
            IsReducerVisible = false;
        }
    }
}

