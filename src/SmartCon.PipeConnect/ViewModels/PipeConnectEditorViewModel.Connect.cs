using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
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
        using var _scope = SmartConLogger.BeginScope("EditorConnect",
            ("Method", "Connect"));

        if (!ConfirmConnectWithUnconnectedChain())
            return;

        // Final validation and ConnectTo always run on the ROOT connection point
        // (static ↔ root dynamic) regardless of where the user stopped in the queue.
        SwitchToPoint(0);

        IsBusy = true;
        StatusMessage = LocalizationService.GetString("Status_Validating");

        try
        {
            var ctx = CreateOperationContext();

            var topology = _activeChainPlan?.Topology ?? ChainTopology.Direct;

            // CTC flush BEFORE validation: writing CTC to the family reloads it and
            // may reset instance DN parameters to the type values — the size fix in
            // ValidateAndFixBeforeConnect must run after that reset (log evidence:
            // DN15 corrected → CTC flush → DN reverted to DN65 → ConnectTo mismatch).
            PromoteGuessedCtcToPendingWrites();

            if (_virtualCtcStore.HasPendingWrites)
            {
                StatusMessage = LocalizationService.GetString("Status_WritingCtc");
                FlushVirtualCtcToFamilies();
            }

            var validateResult = _connectExecutor.ValidateAndFixBeforeConnect(
                ctx, _activeDynamic, _currentFittingId, _primaryReducerId, _userManuallyChangedSize,
                topology, LockNetwork);
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

            _connectExecutor.ExecuteConnectTo(
                ctx, _activeDynamic, _currentFittingId, _primaryReducerId, _activeFittingRule,
                topology);

            // Revit может удалить элемент при ConnectTo (co-направленные коннекторы:
            // «её направление изменено и она не может существовать»). Assimilate такого
            // результата закоммитит потерю элемента под видом успеха — детектируем и
            // откатываем всю группу (rollback восстановит удалённый элемент).
            bool staticAlive = _doc.GetElement(_ctx.StaticConnector.OwnerElementId) is not null;
            var dynId = (_rootDynamicConnector ?? _ctx.DynamicConnector).OwnerElementId;
            bool dynAlive = _doc.GetElement(dynId) is not null;
            if (!staticAlive || !dynAlive)
            {
                SmartConLogger.Error($"Revit removed element(s) during ConnectTo: staticAlive={staticAlive}, dynamicAlive={dynAlive}");
                throw new InvalidOperationException(LocalizationService.GetString("Error_ConnectRemovedElement"));
            }

            SmartConLogger.Info("All operations done, calling Assimilate");
            _groupSession!.Assimilate();
            _groupSession = null;
            StatusMessage = LocalizationService.GetString("Status_Connected");
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"Failed: {ex.Message}\n{ex.StackTrace}");

            // Группа обязана быть закрыта до выхода из ExternalEvent handler: открытая
            // TransactionGroup провоцирует Revit-ошибку «A transaction or sub-transaction
            // was opened but not closed» с откатом ВСЕХ изменений сессии (Tammik: Revit
            // rolls back open groups on leaving the API context). Явный RollBack здесь
            // восстанавливает элементы, удалённые Revit'ом при неудачном ConnectTo.
            try
            {
                _groupSession?.RollBack();
                if (_groupSession is not null)
                    SmartConLogger.Info("Group 'PipeConnect' rolled back after failure — model restored to pre-session state");
            }
            catch (Exception rbEx)
            {
                SmartConLogger.Warn($"RollBack after failure error (ignored): {rbEx.Message} [Action: проверьте Undo-стек Revit вручную — состояние модели может быть частично изменено]");
            }
            _groupSession = null;

            StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message);
        }
        finally
        {
            IsBusy = false;
            IsSessionActive = false;
            RequestClose?.Invoke(null);
        }
    }

    public void ConfirmClose(CloseConfirmationArgs args)
    {
        if (!IsSessionActive) return;
        args.Cancel = true;
        if (!IsBusy && !IsClosing)
            args.DeferredAction = Cancel;
    }

    [RelayCommand]
    public void Cancel()
    {
        if (_isClosing) return;
        _isClosing = true;

        if (!IsSessionActive)
        {
            RequestClose?.Invoke(null);
            return;
        }

        try
        {
            _groupSession?.RollBack();
        }
        catch (Exception ex) { SmartConLogger.Warn($"RollBack error (ignored): {ex.Message} [Action: откат группы транзакций не удался — проверьте Undo-стек Revit вручную]"); }
        finally
        {
            _groupSession = null;
            IsSessionActive = false;
            IsBusy = false;
            RequestClose?.Invoke(null);
        }
    }

    private bool CanOperate() => IsSessionActive && !IsBusy;

    /// <summary>
    /// Editing operations (rotate / resize / insert / reflect / cycle) stay available
    /// even when the chain is sealed (ADR-052): the seal is a transparent traversal
    /// optimization and is torn down automatically (UnsealIfSealed) before any edit.
    /// </summary>
    private bool CanEditOperations() => IsSessionActive && !IsBusy;

    private bool ConfirmConnectWithUnconnectedChain()
    {
        if (!HasUnconnectedChainElements)
            return true;

        var choice = _dialogSvc.ShowUnconnectedChainWarning(
            ConnectedChainElementCount, TotalChainElementCount);

        switch (choice)
        {
            case UnconnectedChainChoice.GoBack:
                SmartConLogger.Info("Connect postponed by user — unconnected chain elements remain");
                StatusMessage = LocalizationService.GetString("Status_ConnectPostponedChain");
                return false;

            case UnconnectedChainChoice.ConnectAsIs:
                SmartConLogger.Warn($"User confirmed connect as-is: {ConnectedChainElementCount}/{TotalChainElementCount} chain elements attached, " +
                    $"the rest stays detached. [Action: убедитесь, что пользователь осознаёт разрыв сети — при жалобах на оторванную сеть проверьте эту запись]");
                return true;

            case UnconnectedChainChoice.ConnectAll:
                SmartConLogger.Info("User chose to attach all chain elements before connect");
                ConnectAllChain();
                if (HasUnconnectedChainElements)
                {
                    SmartConLogger.Warn("ConnectAllChain did not attach every element — Connect aborted, editor stays open. " +
                        "[Action: проверьте статусную строку на ошибку обхода цепи и повторите]");
                    return false;
                }
                return true;

            default:
                SmartConLogger.Warn($"Unexpected dialog choice '{choice}' — Connect aborted. " +
                    $"[Action: сообщите разработчикам — неизвестное значение UnconnectedChainChoice]");
                return false;
        }
    }
    private bool CanInsertFitting() => IsSessionActive && !IsBusy && SelectedFitting is not null;
    private bool CanInsertReducer() => IsSessionActive && !IsBusy && SelectedReducer is not null && _primaryReducerId is null;
    private bool CanReflectFittingCtc() => IsSessionActive && !IsBusy && _currentFittingId is not null;
    private bool CanReflectReducerCtc() => IsSessionActive && !IsBusy && _primaryReducerId is not null;

    partial void OnIsBusyChanged(bool value)
    {
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        ZeroRotationCommand.NotifyCanExecuteChanged();
        CycleConnectorCommand.NotifyCanExecuteChanged();
        ChangeDynamicSizeCommand.NotifyCanExecuteChanged();
        InsertFittingCommand.NotifyCanExecuteChanged();
        InsertReducerCommand.NotifyCanExecuteChanged();
        ReflectFittingCtcCommand.NotifyCanExecuteChanged();
        ReflectReducerCtcCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        ConnectAllChainCommand.NotifyCanExecuteChanged();
        IncrementChainDepthCommand.NotifyCanExecuteChanged();
        DecrementChainDepthCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsSessionActiveChanged(bool value)
    {
        RotateLeftCommand.NotifyCanExecuteChanged();
        RotateRightCommand.NotifyCanExecuteChanged();
        ZeroRotationCommand.NotifyCanExecuteChanged();
        CycleConnectorCommand.NotifyCanExecuteChanged();
        ChangeDynamicSizeCommand.NotifyCanExecuteChanged();
        InsertFittingCommand.NotifyCanExecuteChanged();
        InsertReducerCommand.NotifyCanExecuteChanged();
        ReflectFittingCtcCommand.NotifyCanExecuteChanged();
        ReflectReducerCtcCommand.NotifyCanExecuteChanged();
        ConnectCommand.NotifyCanExecuteChanged();
        ConnectAllChainCommand.NotifyCanExecuteChanged();
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

    private ConnectorProxy? GetReducerConn2()
    {
        if (_primaryReducerId is null) return null;
        try
        {
            var conns = _connSvc.GetAllFreeConnectors(_doc, _primaryReducerId).ToList();
            return conns.Count >= 2 ? conns[1] : conns.FirstOrDefault();
        }
        catch { return null; }
    }

    private List<ConnectorProxy> GetFreeConnectorsSnapshot()
        => GetFreeConnectorsSnapshot(_ctx.DynamicConnector.OwnerElementId);

    private List<ConnectorProxy> GetFreeConnectorsSnapshot(ElementId elementId)
    {
        try
        {
            return _connSvc.GetAllFreeConnectors(_doc, elementId).ToList();
        }
        catch (Exception ex)
        {
            SmartConLogger.Info($"GetFreeConnectors failed (ignored): {ex.Message}");
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
            // RefreshWithCtcOverride (не RefreshConnector): persistent CTC коннектора
            // может отличаться от виртуального (mini-selector при старте сессии) —
            // без override поиск правила идёт по устаревшему CTC и reducer не находится.
            var dynR = RefreshWithCtcOverride(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;

            _primaryReducerId = _networkMover.InsertReducer(
                doc, _ctx.StaticConnector, dynR,
                directConnectRules: _mappingRepo.GetMappingRules());

            if (_primaryReducerId is not null)
            {
                var overrides = GuessCtcForReducer(_primaryReducerId);
                SmartConLogger.Info($"Reducer inserted: id={_primaryReducerId.GetValue()}");

                // Effective CTC dyn (virtual override мини-селектора): повторный align обязан
                // использовать ТОТ ЖЕ dynCtc, что и первый (NetworkMover.InsertReducer) — иначе
                // с dynCtc=0 срабатывает Strategy 1 (прямая) вместо Strategy 0 (cross) и reducer
                // переворачивается обратно: убывающая DN-комбинация ломает семейство (кейс #167).
                var dynCtc = dynR.ConnectionTypeCode.IsDefined
                    ? dynR.ConnectionTypeCode
                    : ResolveDynamicTypeFromRule(_activeFittingRule);
                _fittingInsertSvc.AlignFittingToStatic(
                    doc, _primaryReducerId, _ctx.StaticConnector, _transformSvc, _connSvc,
                    dynamicTypeCode: dynCtc,
                    ctcOverrides: overrides,
                    directConnectRules: _mappingRepo.GetMappingRules());
                doc.Regenerate();
            }
            else
                SmartConLogger.Warn("Reducer not found in mapping — connecting directly " +
                    "[Action: добавьте семейство переходника в mapping (Настройки → Правила), если требуется переход диаметров]");
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
            SmartConLogger.Warn("fittingConn2 is null — cannot insert reducer after fitting " +
                "[Action: переустановите фитинг кнопкой «Примерить», затем повторите вставку переходника]");
            return;
        }

        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_InsertTransition"), doc =>
        {
            var dyn = _activeDynamic ?? _ctx.DynamicConnector;
            // RefreshWithCtcOverride (не RefreshConnector): virtual CTC мини-селектора/
            // Reflect иначе теряется, и правило ищется по CTC=0 — «rule 0↔0 not found»
            // (кейс #167: reducer fitting↔dynamic не вставлялся при Lock).
            var dynR = RefreshWithCtcOverride(doc, dyn.OwnerElementId, dyn.ConnectorIndex) ?? dyn;
            var fitConn2Fresh = RefreshWithCtcOverride(doc, fitConn2.OwnerElementId, fitConn2.ConnectorIndex)
                ?? fitConn2;

            _primaryReducerId = _networkMover.InsertReducer(
                doc, fitConn2Fresh, dynR,
                directConnectRules: _mappingRepo.GetMappingRules());

            if (_primaryReducerId is not null)
            {
                var overrides = GuessCtcForReducer(_primaryReducerId);
                SmartConLogger.Info($"Reducer (fitting→dynamic) inserted: id={_primaryReducerId.GetValue()}");

                // Effective CTC dyn — см. комментарий в InsertReducerBetweenStaticAndDynamic.
                var dynCtc = dynR.ConnectionTypeCode.IsDefined
                    ? dynR.ConnectionTypeCode
                    : ResolveDynamicTypeFromRule(_activeFittingRule);
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
                            PipeAbsorptionApplier.MoveOrAbsorb(
                                doc, _transformSvc, _activeDynamic.OwnerElementId, activeProxy.OriginVec3, offset);
                    }
                }

                doc.Regenerate();
            }
            else
                SmartConLogger.Warn("Reducer not found in mapping — connecting without reducer " +
                    "[Action: добавьте семейство переходника в mapping (Настройки → Правила), если требуется переход диаметров]");
        });

        if (_primaryReducerId is not null)
        {
            StatusMessage = LocalizationService.GetString("Status_SizingReducer");
            SizeFittingConnectors(_doc, _primaryReducerId, null, adjustDynamicToFit: false,
                upstreamTarget: fitConn2);
        }
    }
}

