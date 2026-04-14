using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.PipeConnect.Services;

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
        StatusMessage = "Проверка и финальная подгонка…";

        try
        {
            var ctx = CreateOperationContext();

            var validateResult = _connectExecutor.ValidateAndFixBeforeConnect(
                ctx, _activeDynamic, _currentFittingId, _primaryReducerId, _userManuallyChangedSize);
            _activeDynamic = validateResult.ActiveDynamic ?? _activeDynamic;
            _needsPrimaryReducer = validateResult.NeedsPrimaryReducer;

            if (_needsPrimaryReducer && _currentFittingId is null && _primaryReducerId is null)
            {
                if (_activeFittingRule is null)
                    _activeFittingRule = _ctx.ProposedFittings
                        .FirstOrDefault(r => r.ReducerFamilies.Count > 0);

                StatusMessage = "Вставка переходника…";
                _groupSession!.RunInTransaction("PipeConnect — Вставка перехода", doc =>
                {
                    var dyn = _activeDynamic ?? _ctx.DynamicConnector;
                    var dynR = _connSvc.RefreshConnector(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;

                    _primaryReducerId = _networkMover.InsertReducer(
                        doc, _ctx.StaticConnector, dynR,
                        directConnectRules: _mappingRepo.GetMappingRules());

                    if (_primaryReducerId is not null)
                    {
                        var overrides = GuessCtcForReducer(_primaryReducerId);
                        SmartConLogger.Info($"[Connect] Reducer inserted: id={_primaryReducerId.Value}");

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
                    StatusMessage = "Подбор размера переходника…";
                    SizeFittingConnectors(_doc, _primaryReducerId, null, adjustDynamicToFit: false);
                }
            }

            PromoteGuessedCtcToPendingWrites();

            if (_virtualCtcStore.HasPendingWrites)
            {
                StatusMessage = "Запись типов коннекторов…";
                FlushVirtualCtcToFamilies();
            }

            _connectExecutor.ExecuteConnectTo(
                ctx, _activeDynamic, _currentFittingId, _primaryReducerId, _activeFittingRule);

            SmartConLogger.Info("[Connect] All operations done, calling Assimilate");
            _groupSession!.Assimilate();
            _groupSession = null;
            StatusMessage = "Соединение выполнено";
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[Connect] Failed: {ex.Message}\n{ex.StackTrace}");
            StatusMessage = $"Error: {ex.Message}";
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
    private bool CanReassignFittingCtc() => IsSessionActive && !IsBusy && _currentFittingId is not null;
    private bool CanReassignReducerCtc() => IsSessionActive && !IsBusy && _primaryReducerId is not null;

    partial void OnIsBusyChanged(bool value)
    {
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        CycleConnectorCommand.NotifyCanExecuteChanged();
        InsertFittingCommand.NotifyCanExecuteChanged();
        InsertReducerCommand.NotifyCanExecuteChanged();
        ReassignFittingCtcCommand.NotifyCanExecuteChanged();
        ReassignReducerCtcCommand.NotifyCanExecuteChanged();
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
        ReassignFittingCtcCommand.NotifyCanExecuteChanged();
        ReassignReducerCtcCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        IncrementChainDepthCommand.NotifyCanExecuteChanged();
        DecrementChainDepthCommand.NotifyCanExecuteChanged();
    }

    private ConnectorProxy? SizeFittingConnectors(Document doc, ElementId fittingId, ConnectorProxy? fitConn2, bool adjustDynamicToFit = true)
    {
        var ctx = CreateOperationContext();
        var result = _connectExecutor.SizeFittingConnectors(
            ctx, _activeDynamic, fittingId, fitConn2, _activeFittingRule, adjustDynamicToFit);
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
}
