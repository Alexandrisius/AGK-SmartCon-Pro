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
                doc, primary.FamilyName, primary.SymbolName, _ctx.StaticConnector.Origin);
            if (insertedId is null)
            {
                SmartConLogger.Warn("[InsertReducer] InsertFitting returned null");
                return;
            }

            SmartConLogger.Info($"[InsertReducer] Inserted id={insertedId.GetValue()}");

            doc.Regenerate();

            var overrides = GuessCtcForReducer(insertedId);

            fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, insertedId, _ctx.StaticConnector, _transformSvc, _connSvc,
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
            ReassignReducerCtcCommand.NotifyCanExecuteChanged();
            StatusMessage = string.Format(LocalizationService.GetString("Status_ReducerSet"), reducer.DisplayName);
            SizeFittingConnectors(_doc, insertedId, fitConn2, adjustDynamicToFit: false);
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

    private void ReassignElementCtc(ElementId elemId, bool isReducer)
    {
        var types = _mappingRepo.GetConnectorTypes();
        if (types.Count == 0) return;

        var elem = _doc.GetElement(elemId) as FamilyInstance;
        if (elem is null) return;

        var items = BuildCtcItemsFromVirtualStore(elemId, types);

        if (!_dialogSvc.ShowFittingCtcSetup(elem.Symbol.Family.Name, elem.Symbol.Name, items, types))
            return;

        foreach (var item in items)
        {
            if (item.SelectedType is not null)
            {
                var ctc = new ConnectionTypeCode(item.SelectedType.Code);
                _virtualCtcStore.Set(elemId, item.ConnectorIndex, ctc, item.SelectedType);
            }
        }

        var overrides = _virtualCtcStore.GetOverridesForElement(elemId);
        var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);

        ConnectorProxy? reorientedConn2 = null;

        var txName = isReducer
            ? LocalizationService.GetString("Tx_ReorientReducer")
            : LocalizationService.GetString("Tx_ReorientFitting");

        _groupSession!.RunInTransaction(txName, doc =>
        {
            var fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, elemId, _ctx.StaticConnector, _transformSvc, _connSvc,
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
                var sizedConn2 = SizeFittingConnectors(_doc, elemId, reorientedConn2, adjustDynamicToFit: false);
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

        StatusMessage = isReducer
            ? LocalizationService.GetString("Status_CtcReducerUpdated")
            : LocalizationService.GetString("Status_CtcFittingUpdated");
    }

    [RelayCommand(CanExecute = nameof(CanReassignFittingCtc))]
    private void ReassignFittingCtc()
    {
        if (_currentFittingId is null || _activeFittingRule is null) return;
        IsBusy = true;
        try
        {
            ReassignElementCtc(_currentFittingId, isReducer: false);
        }
        catch (Exception ex) { SmartConLogger.Error($"[ReassignFittingCtc] Failed: {ex.Message}"); StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message); }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanReassignReducerCtc))]
    private void ReassignReducerCtc()
    {
        if (_primaryReducerId is null) return;
        IsBusy = true;
        try
        {
            ReassignElementCtc(_primaryReducerId, isReducer: true);
        }
        catch (Exception ex) { SmartConLogger.Error($"[ReassignReducerCtc] Failed: {ex.Message}"); StatusMessage = string.Format(LocalizationService.GetString("Error_General"), ex.Message); }
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

        SmartConLogger.Info($"[InsertFitting] DONE fittingId={_currentFittingId?.GetValue()}");
    }
}
