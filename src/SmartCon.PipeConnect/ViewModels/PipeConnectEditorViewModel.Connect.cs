using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.PipeConnect.Services;
using SmartCon.Core.Compatibility;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel
{
    private ConnectOperationContext CreateOperationContext() => new()
    {
        Doc = _doc,
        GroupSession = _groupSession!,
        Session = _ctx,
        VirtualCtcStore = _virtualCtcStore
    };

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private void Connect()
    {
        SmartConLogger.Info("[Connect] START");
        IsBusy = true;
        StatusMessage = LocalizationService.GetString("Status_Validating");

        try
        {
            var ctx = CreateOperationContext();

            var validateResult = _connectExecutor.ValidateAndFixBeforeConnect(
                ctx, _activeDynamic, _currentFittingId, _primaryReducerId, _userManuallyChangedSize);
            _activeDynamic = validateResult.ActiveDynamic ?? _activeDynamic;
            _needsPrimaryReducer = validateResult.NeedsPrimaryReducer;

            if (_needsPrimaryReducer && _primaryReducerId is null)
            {
                if (_activeFittingRule is null)
                    _activeFittingRule = _ctx.ProposedFittings
                        .FirstOrDefault(r => r.ReducerFamilies.Count > 0);

                StatusMessage = LocalizationService.GetString("Status_InsertingReducer");

                if (_currentFittingId is not null && _activeFittingConn2 is not null)
                {
                    InsertReducerBetweenFittingAndDynamic();
                }
                else
                {
                    InsertReducerBetweenStaticAndDynamic();
                }
            }

            PromoteGuessedCtcToPendingWrites();

            if (_virtualCtcStore.HasPendingWrites)
            {
                StatusMessage = LocalizationService.GetString("Status_WritingCtc");
                FlushVirtualCtcToFamilies();
            }

            _connectExecutor.ExecuteConnectTo(
                ctx, _activeDynamic, _currentFittingId, _primaryReducerId, _activeFittingRule);

            SmartConLogger.Info("[Connect] All operations done, calling Assimilate");
            _groupSession!.Assimilate();
            _groupSession = null;
            StatusMessage = LocalizationService.GetString("Status_Connected");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[Connect] Failed: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsSessionActive = false;
            RequestClose?.Invoke();
        }
    }

    [RelayCommand]
    public void Cancel()
    {
        if (_isClosing) return;
        _isClosing = true;

        if (!IsSessionActive)
        {
            RequestClose?.Invoke();
            return;
        }

        try
        {
            _groupSession?.RollBack();
        }
        catch (Exception ex) { SmartConLogger.Warn($"[Cancel] RollBack error (ignored): {ex.Message}"); }
        finally
        {
            _groupSession = null;
            IsSessionActive = false;
            IsBusy = false;
            RequestClose?.Invoke();
        }
    }

    public bool IsClosing => _isClosing;

    private bool CanOperate() => IsSessionActive && !IsBusy;
    private bool CanInsertFitting() => IsSessionActive && !IsBusy && SelectedFitting is not null;
    private bool CanInsertReducer() => IsSessionActive && !IsBusy && SelectedReducer is not null && _primaryReducerId is null;
    private bool CanReflectFittingCtc() => IsSessionActive && !IsBusy && _currentFittingId is not null;
    private bool CanReflectReducerCtc() => IsSessionActive && !IsBusy && _primaryReducerId is not null;

    partial void OnIsBusyChanged(bool value)
    {
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        CycleConnectorCommand.NotifyCanExecuteChanged();
        InsertFittingCommand.NotifyCanExecuteChanged();
        InsertReducerCommand.NotifyCanExecuteChanged();
        ReflectFittingCtcCommand.NotifyCanExecuteChanged();
        ReflectReducerCtcCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        IncrementChainDepthCommand.NotifyCanExecuteChanged();
        DecrementChainDepthCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSessionActiveChanged(bool value)
    {
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        CycleConnectorCommand.NotifyCanExecuteChanged();
        InsertFittingCommand.NotifyCanExecuteChanged();
        InsertReducerCommand.NotifyCanExecuteChanged();
        ReflectFittingCtcCommand.NotifyCanExecuteChanged();
        ReflectReducerCtcCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        IncrementChainDepthCommand.NotifyCanExecuteChanged();
        DecrementChainDepthCommand.NotifyCanExecuteChanged();
    }

    private ConnectorProxy? SizeFittingConnectors(Document doc, ElementId fittingId, ConnectorProxy? fitConn2, bool adjustDynamicToFit = true, ConnectorProxy? upstreamTarget = null)
    {
        var ctx = CreateOperationContext();
        var result = _connectExecutor.SizeFittingConnectors(
            ctx, _activeDynamic, fittingId, fitConn2, _activeFittingRule, adjustDynamicToFit, upstreamTarget);
        if (result.ActiveDynamic is not null)
            _activeDynamic = result.ActiveDynamic;
        return result.FitConn2;
    }

    private List<ConnectorProxy> GetFreeConnectorsSnapshot()
    {
        try
        {
            return _connSvc.GetAllFreeConnectors(_doc, _ctx.DynamicConnector.OwnerElementId).ToList();
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"[GetFreeConnectorsSnapshot] Error (ignored): {ex.Message}");
            return [];
        }
    }

    private void LogConnectorState(string label)
    {
        var dyn = _activeDynamic ?? _ctx.DynamicConnector;
        PipeConnectDiagnostics.LogConnectorState(
            _doc, _ctx.StaticConnector, dyn, _currentFittingId, _connSvc, label);
    }

    private void InsertReducerBetweenStaticAndDynamic()
    {
        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_InsertTransition"), doc =>
        {
            var dyn = _activeDynamic ?? _ctx.DynamicConnector;
            var dynR = _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;

            _primaryReducerId = _networkMover.InsertReducer(
                doc, _ctx.StaticConnector, dynR,
                directConnectRules: _mappingRepo.GetMappingRules());

            if (_primaryReducerId is not null)
            {
                var overrides = GuessCtcForReducer(_primaryReducerId);
                SmartConLogger.Info($"[Connect] Reducer inserted: id={_primaryReducerId.GetValue()}");

                var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);
                _fittingInsertSvc.AlignFittingToStatic(
                    doc, _primaryReducerId, _ctx.StaticConnector, _transformSvc, _connSvc,
                    dynamicTypeCode: dynCtc,
                    ctcOverrides: overrides,
                    directConnectRules: _mappingRepo.GetMappingRules());
                doc.Regenerate();
            }
            else
                SmartConLogger.Warn("[Connect] Reducer not found in mapping — connecting directly");
        });

        if (_primaryReducerId is not null)
        {
            StatusMessage = LocalizationService.GetString("Status_SizingReducer");
            SizeFittingConnectors(_doc, _primaryReducerId, null, adjustDynamicToFit: false);
        }
    }

    private void InsertReducerBetweenFittingAndDynamic()
    {
        var fitConn2 = _activeFittingConn2;
        if (fitConn2 is null)
        {
            SmartConLogger.Warn("[Connect] fittingConn2 is null — cannot insert reducer after fitting");
            return;
        }

        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_InsertTransition"), doc =>
        {
            var dyn = _activeDynamic ?? _ctx.DynamicConnector;
            var dynR = _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;
            var fitConn2Fresh = _connSvc.RefreshConnector(doc, fitConn2.OwnerElementId, fitConn2.ConnectorIndex)
                ?? fitConn2;

            _primaryReducerId = _networkMover.InsertReducer(
                doc, fitConn2Fresh, dynR,
                directConnectRules: _mappingRepo.GetMappingRules());

            if (_primaryReducerId is not null)
            {
                var overrides = GuessCtcForReducer(_primaryReducerId);
                SmartConLogger.Info($"[Connect] Reducer (fitting→dynamic) inserted: id={_primaryReducerId.GetValue()}");

                var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);
                _fittingInsertSvc.AlignFittingToStatic(
                    doc, _primaryReducerId, fitConn2Fresh, _transformSvc, _connSvc,
                    dynamicTypeCode: dynCtc,
                    ctcOverrides: overrides,
                    directConnectRules: _mappingRepo.GetMappingRules());

                if (_activeDynamic is not null)
                {
                    var rConns = _connSvc.GetAllFreeConnectors(doc, _primaryReducerId).ToList();
                    var (_, rConn2) = ResolveConnectorSidesForElement(_primaryReducerId, rConns, dynCtc);
                    if (rConn2 is not null)
                    {
                        var activeProxy = _connSvc.RefreshConnector(
                            doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                            ?? _activeDynamic;
                        var offset = rConn2.OriginVec3 - activeProxy.OriginVec3;
                        if (!SmartCon.Core.Math.VectorUtils.IsZero(offset))
                            _transformSvc.MoveElement(doc, _activeDynamic.OwnerElementId, offset);
                    }
                }

                doc.Regenerate();
            }
            else
                SmartConLogger.Warn("[Connect] Reducer not found in mapping — connecting without reducer");
        });

        if (_primaryReducerId is not null)
        {
            StatusMessage = LocalizationService.GetString("Status_SizingReducer");
            SizeFittingConnectors(_doc, _primaryReducerId, null, adjustDynamicToFit: false,
                upstreamTarget: fitConn2);
        }
    }
}
