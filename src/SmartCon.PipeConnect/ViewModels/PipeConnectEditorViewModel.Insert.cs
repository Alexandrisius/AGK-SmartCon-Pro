using System.Collections.ObjectModel;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel
{
    // ── Insert fitting / reducer / reassign CTC ────────────────────────────────

    private bool InsertReducerCore(FittingCardItem reducer, bool moveDynamic)
    {
        var primary = reducer.PrimaryFitting;
        if (primary is null) return false;

        _activeFittingRule = reducer.Rule;
        var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);

        ElementId? insertedId = null;
        ConnectorProxy? fitConn2 = null;

        _groupSession!.RunInTransaction("PipeConnect — Вставка reducer", doc =>
        {
            if (_primaryReducerId is not null)
            {
                _fittingInsertSvc.DeleteElement(doc, _primaryReducerId);
                _virtualCtcStore.RemoveForElement(_primaryReducerId);
                _primaryReducerId = null;
            }

            insertedId = _fittingInsertSvc.InsertFitting(
                doc, primary.FamilyName, primary.SymbolName, _ctx.StaticConnector.Origin);
            if (insertedId is null) return;

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
            StatusMessage = $"Переходник: {reducer.DisplayName}";
            SizeFittingConnectors(_doc, insertedId, fitConn2, adjustDynamicToFit: false);
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
        StatusMessage = "Вставка переходника…";
        try
        {
            var reducer = SelectedReducer;
            var primary = reducer.PrimaryFitting;
            if (primary is null) { StatusMessage = "Нет данных о семействе переходника"; return; }

            if (!InsertReducerCore(reducer, moveDynamic: false))
            {
                StatusMessage = $"Семейство '{primary.FamilyName}' не найдено";
            }
        }
        catch (Exception ex) { StatusMessage = $"Ошибка: {ex.Message}"; }
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
            ? "PipeConnect — Переориентация reducer"
            : "PipeConnect — Переориентация фитинга";

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
                    _groupSession!.RunInTransaction("PipeConnect — Позиция dynamic после reducer re-size", doc =>
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
            ? "CTC переходника обновлён — переориентирован"
            : "CTC фитинга обновлён — переориентирован";
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
        catch (Exception ex) { StatusMessage = $"Ошибка: {ex.Message}"; }
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
        catch (Exception ex) { StatusMessage = $"Ошибка: {ex.Message}"; }
        finally { IsBusy = false; }
    }

    [RelayCommand(CanExecute = nameof(CanInsertFitting))]
    private void InsertFitting()
    {
        if (SelectedFitting is null) return;

        IsBusy = true;
        StatusMessage = "Вставка фитинга…";

        try
        {
            InsertFittingSilent(SelectedFitting);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Ошибка вставки: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void InsertFittingSilent(FittingCardItem fitting, bool adjustDynamicToFit = true)
    {
        if (fitting.IsDirectConnect)
        {
            _activeFittingRule = null;
            _groupSession!.RunInTransaction("PipeConnect — Прямое соединение", doc =>
            {
                doc.Regenerate();
            });
            StatusMessage = "Прямое соединение";
            return;
        }

        var primary = fitting.PrimaryFitting;
        if (primary is null)
        {
            StatusMessage = "Нет данных о семействе фитинга";
            return;
        }

        _activeFittingRule = fitting.Rule;
        var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);

        ElementId? insertedId = null;
        ConnectorProxy? fitConn2 = null;
        IReadOnlyDictionary<int, ConnectionTypeCode>? ctcOverrides = null;

        _groupSession!.RunInTransaction("PipeConnect — Вставка фитинга", doc =>
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
            StatusMessage = $"Семейство '{primary.FamilyName}' не найдено в проекте";
            return;
        }

        _currentFittingId = insertedId;
        _activeFittingConn2 = fitConn2;
        StatusMessage = $"Вставлен: {fitting.DisplayName}";

        var newFitConn2 = SizeFittingConnectors(_doc, insertedId, fitConn2, adjustDynamicToFit: adjustDynamicToFit);
        if (newFitConn2 is not null)
            _activeFittingConn2 = newFitConn2;
    }
}
