using System.Collections.ObjectModel;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Compatibility;
using SmartCon.PipeConnect.Services;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel
{
    // ── Insert fitting / reducer / reassign CTC ────────────────────────────────

    private bool InsertReducerCore(FittingCardItem reducer, bool moveDynamic)
    {
        SmartConLogger.Info($"[InsertReducer] START reducer={reducer.DisplayName}");
        var primary = reducer.PrimaryFitting;
        if (primary is null) return false;

        _activeFittingRule = reducer.Rule;
        var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);

        ElementId? insertedId = null;
        ConnectorProxy? fitConn2 = null;

        ConnectorProxy? alignTarget = _currentFittingId is not null && _activeFittingConn2 is not null
            ? _activeFittingConn2
            : _ctx.StaticConnector;

        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_InsertReducer"), doc =>
        {
            if (_primaryReducerId is not null)
            {
                SmartConLogger.Info($"[InsertReducer] Deleted old reducer id={_primaryReducerId.GetValue()}");
                _fittingInsertSvc.DeleteElement(doc, _primaryReducerId);
                _virtualCtcStore.RemoveForElement(_primaryReducerId);
                _primaryReducerId = null;
            }

            insertedId = _fittingInsertSvc.InsertFitting(
                doc, primary.FamilyName, primary.SymbolName, alignTarget.Origin);
            if (insertedId is null)
            {
                SmartConLogger.Warn("[InsertReducer] InsertFitting returned null");
                return;
            }

            SmartConLogger.Info($"[InsertReducer] Inserted id={insertedId.GetValue()}");

            doc.Regenerate();

            var overrides = GuessCtcForReducer(insertedId);

            fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, insertedId, alignTarget, _transformSvc, _connSvc,
                dynamicTypeCode: dynCtc,
                ctcOverrides: overrides,
                directConnectRules: _mappingRepo.GetMappingRules());

            if (moveDynamic && fitConn2 is not null && _activeDynamic is not null)
            {
                var activeProxy = _connSvc.RefreshConnector(
                    doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                    ?? _activeDynamic;

                var offset = fitConn2.OriginVec3 - activeProxy.OriginVec3;
                if (!VectorUtils.IsZero(offset))
                    _transformSvc.MoveElement(doc, _activeDynamic.OwnerElementId, offset);
            }

            doc.Regenerate();
        });

        if (insertedId is not null)
        {
            _primaryReducerId = insertedId;
            InsertReducerCommand.NotifyCanExecuteChanged();
            ReflectReducerCtcCommand.NotifyCanExecuteChanged();
            StatusMessage = string.Format(LocalizationService.GetString("Status_ReducerSet"), reducer.DisplayName);
            SizeFittingConnectors(_doc, insertedId, fitConn2, adjustDynamicToFit: false, alignTarget);
            SmartConLogger.Info($"[InsertReducer] DONE reducerId={_primaryReducerId.GetValue()}");
            return true;
        }

        return false;
    }

    private void InsertReducerSilent()
    {
        if (SelectedReducer is null) return;
        InsertReducerCore(SelectedReducer, moveDynamic: true);
    }

    [RelayCommand(CanExecute = nameof(CanInsertReducer))]
    private void InsertReducer()
    {
        if (SelectedReducer is null) return;
        IsBusy = true;
        StatusMessage = LocalizationService.GetString("Status_InsertingReducer");
        try
        {
            var reducer = SelectedReducer;
            var primary = reducer.PrimaryFitting;
            if (primary is null) { StatusMessage = LocalizationService.GetString("Status_NoReducerData"); return; }

            if (!InsertReducerCore(reducer, moveDynamic: false))
            {
                StatusMessage = string.Format(LocalizationService.GetString("Status_FamilyNotFound"), primary.FamilyName);
            }
        }
        catch (Exception ex) { SmartConLogger.Error($"[InsertReducer] Failed: {ex.Message}"); StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message); }
        finally { IsBusy = false; }
    }

    private void ReflectElementCtc(ElementId elemId, bool isReducer)
    {
        if (!_virtualCtcStore.SwapCtcForElement(elemId)) return;

        var overrides = _virtualCtcStore.GetOverridesForElement(elemId);
        var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);

        var upstreamTarget = (isReducer && _currentFittingId is not null && _activeFittingConn2 is not null)
            ? _activeFittingConn2
            : _ctx.StaticConnector;

        ConnectorProxy? reorientedConn2 = null;

        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_ReflectCtc"), doc =>
        {
            var fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, elemId, upstreamTarget, _transformSvc, _connSvc,
                dynamicTypeCode: dynCtc,
                ctcOverrides: overrides,
                directConnectRules: _mappingRepo.GetMappingRules());
            reorientedConn2 = fitConn2;

            if (fitConn2 is not null && _activeDynamic is not null)
            {
                var dynProxy = _connSvc.RefreshConnector(
                    doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                    ?? _activeDynamic;

                var offset = fitConn2.OriginVec3 - dynProxy.OriginVec3;
                if (!VectorUtils.IsZero(offset))
                    _transformSvc.MoveElement(doc, _activeDynamic.OwnerElementId, offset);
            }

            doc.Regenerate();
        });

        if (reorientedConn2 is not null)
        {
            if (isReducer)
            {
                var sizedConn2 = SizeFittingConnectors(_doc, elemId, reorientedConn2, adjustDynamicToFit: false, upstreamTarget);
                if (sizedConn2 is not null && _activeDynamic is not null)
                {
                    _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_PositionAfterReducerReSize"), doc =>
                    {
                        var dynProxy = _connSvc.RefreshConnector(
                            doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                            ?? _activeDynamic;
                        var offset = sizedConn2.OriginVec3 - dynProxy.OriginVec3;
                        if (!VectorUtils.IsZero(offset))
                            _transformSvc.MoveElement(doc, _activeDynamic.OwnerElementId, offset);
                        doc.Regenerate();
                    });
                }
            }
            else
            {
                _activeFittingConn2 = SizeFittingConnectors(_doc, elemId, reorientedConn2);
            }
        }

        StatusMessage = LocalizationService.GetString("Status_CtcReflected");
    }

    [RelayCommand(CanExecute = nameof(CanReflectFittingCtc))]
    private void ReflectFittingCtc()
    {
        if (_currentFittingId is null || _activeFittingRule is null) return;
        IsBusy = true;
        try
        {
            ReflectElementCtc(_currentFittingId, isReducer: false);
        }
        catch (Exception ex) { SmartConLogger.Error($"[ReflectFittingCtc] Failed: {ex.Message}"); StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanReflectReducerCtc))]
    private void ReflectReducerCtc()
    {
        if (_primaryReducerId is null) return;
        IsBusy = true;
        try
        {
            ReflectElementCtc(_primaryReducerId, isReducer: true);
        }
        catch (Exception ex) { SmartConLogger.Error($"[ReflectReducerCtc] Failed: {ex.Message}"); StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanInsertFitting))]
    private void InsertFitting()
    {
        if (SelectedFitting is null) return;

        IsBusy = true;
        StatusMessage = LocalizationService.GetString("Status_InsertingFittingAction");

        try
        {
            InsertFittingSilent(SelectedFitting);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[InsertFitting] Failed: {ex.Message}");
            StatusMessage = string.Format(LocalizationService.GetString("Error_Insert"), ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void InsertFittingSilent(FittingCardItem fitting, bool adjustDynamicToFit = true)
    {
        SmartConLogger.Info($"[InsertFitting] START fitting={fitting.DisplayName}, adjustDynamic={adjustDynamicToFit}");

        if (fitting.IsDirectConnect)
        {
            SmartConLogger.Info("[InsertFitting] Direct connect branch — skip fitting");
            _activeFittingRule = null;
            _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_DirectConnect"), doc =>
            {
                doc.Regenerate();
            });
            StatusMessage = LocalizationService.GetString("Status_DirectConnect");
            return;
        }

        var primary = fitting.PrimaryFitting;
        if (primary is null)
        {
            StatusMessage = LocalizationService.GetString("Status_NoFittingData");
            return;
        }

        _activeFittingRule = fitting.Rule;
        var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);

        ElementId? insertedId = null;
        ConnectorProxy? fitConn2 = null;
        IReadOnlyDictionary<int, ConnectionTypeCode>? ctcOverrides = null;

        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_InsertFitting"), doc =>
        {
            if (_currentFittingId is not null)
            {
                _fittingInsertSvc.DeleteElement(doc, _currentFittingId);
                _virtualCtcStore.RemoveForElement(_currentFittingId);
                _currentFittingId = null;
                _activeFittingConn2 = null;
            }

            if (_primaryReducerId is not null)
            {
                SmartConLogger.Info($"[InsertFitting] Deleting old reducer id={_primaryReducerId.GetValue()} (fitting changed)");
                _fittingInsertSvc.DeleteElement(doc, _primaryReducerId);
                _virtualCtcStore.RemoveForElement(_primaryReducerId);
                _primaryReducerId = null;
                _needsPrimaryReducer = false;
            }

            insertedId = _fittingInsertSvc.InsertFitting(
                doc, primary.FamilyName, primary.SymbolName, _ctx.StaticConnector.Origin);

            if (insertedId is null) return;

            SmartConLogger.Info($"[InsertFitting] Inserted id={insertedId.GetValue()}");
            doc.Regenerate();

            ctcOverrides = GuessCtcForFitting(insertedId, fitting.Rule);

            fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, insertedId, _ctx.StaticConnector, _transformSvc, _connSvc,
                dynamicTypeCode: dynCtc,
                ctcOverrides: ctcOverrides,
                directConnectRules: _mappingRepo.GetMappingRules());

            if (fitConn2 is not null && _activeDynamic is not null)
            {
                var activeProxy = _connSvc.RefreshConnector(
                    doc, _activeDynamic.OwnerElementId, _activeDynamic.ConnectorIndex)
                    ?? _activeDynamic;

                var offset = fitConn2.OriginVec3 - activeProxy.OriginVec3;
                if (!VectorUtils.IsZero(offset))
                    _transformSvc.MoveElement(doc, _activeDynamic.OwnerElementId, offset);
            }

            doc.Regenerate();
        });

        if (insertedId is null)
        {
            SmartConLogger.Warn("[InsertFitting] InsertFitting returned null — family not found");
            StatusMessage = string.Format(LocalizationService.GetString("Status_FamilyNotFoundInProject"), primary.FamilyName);
            return;
        }

        _currentFittingId = insertedId;
        _activeFittingConn2 = fitConn2;
        StatusMessage = string.Format(LocalizationService.GetString("Status_Inserted"), fitting.DisplayName);

        var newFitConn2 = SizeFittingConnectors(_doc, insertedId, fitConn2, adjustDynamicToFit: adjustDynamicToFit);
        if (newFitConn2 is not null)
            _activeFittingConn2 = newFitConn2;

        if (_activeFittingConn2 is not null && _activeDynamic is not null)
        {
            var fitConn2ForCheck = _connSvc.RefreshConnector(
                _doc, _activeFittingConn2.OwnerElementId, _activeFittingConn2.ConnectorIndex)
                ?? _activeFittingConn2;
            bool needsReducer = PipeConnectSizeHandler.DetectReducerNeededAfterFitting(
                _activeDynamic, fitConn2ForCheck);

            if (needsReducer && _primaryReducerId is null)
            {
                EnsureReducersForFittingPair(fitConn2ForCheck, _activeDynamic);

                if (AvailableReducers.Count > 0)
                {
                    _needsPrimaryReducer = true;
                    SelectedReducer = AvailableReducers[0];
                    IsReducerVisible = true;
                    StatusMessage = LocalizationService.GetString("Status_InsertingReducer");
                    InsertReducerSilent();
                }
                else
                {
                    SmartConLogger.Warn("[InsertFitting] Reducer needed but no reducer families found in mapping " +
                        $"for pair fitConn2_CTC={fitConn2ForCheck.ConnectionTypeCode.Value} ↔ dyn_CTC={_activeDynamic.ConnectionTypeCode.Value}");
                    _needsPrimaryReducer = true;
                    IsReducerVisible = true;
                }
            }
        }

        SmartConLogger.Info($"[InsertFitting] DONE fittingId={_currentFittingId?.GetValue()}");
    }
}
