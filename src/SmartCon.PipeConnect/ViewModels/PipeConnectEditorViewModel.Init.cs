using System.Collections.ObjectModel;
using Autodesk.Revit.DB;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.Services;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel
{
    private void InitLegacyFlow()
    {
        using var _scope = SmartConLogger.BeginScope("Editor",
            ("Method", "InitLegacyFlow"));
        var defaultFitting = SelectedFitting;
        if (defaultFitting is not null && !defaultFitting.IsDirectConnect)
        {
            StatusMessage = LocalizationService.GetString("Status_InsertingFitting");
            InsertFittingSilent(defaultFitting);
        }
        else if (_ctx.ParamTargetRadius is { } directTargetRadius)
        {
            _activeDynamic = _initHandler.RunDirectConnectSizing(
                _doc, _ctx, _groupSession!, directTargetRadius, AvailableDynamicSizes)
                ?? _ctx.DynamicConnector;
            StatusMessage = LocalizationService.GetString("Status_ReadyToConnect");
        }
        else
        {
            StatusMessage = LocalizationService.GetString("Status_ReadyToConnect");
        }

        if (_primaryReducerId is null && _activeDynamic is not null)
        {
            bool needsReducer;

            if (_currentFittingId is not null && _activeFittingConn2 is not null)
            {
                needsReducer = PipeConnectSizeHandler.DetectReducerNeededAfterFitting(
                    _activeDynamic, _activeFittingConn2);
            }
            else if (_currentFittingId is null)
            {
                const double radiusEps = 1e-5;
                var dynRadius = _activeDynamic.Radius;
                var staticRadius = _ctx.StaticConnector.Radius;
                needsReducer = Math.Abs(dynRadius - staticRadius) > radiusEps;

                if (needsReducer)
                    SmartConLogger.Info($"Radii mismatch: dyn={dynRadius * FeetToMm:F1}mm, " +
                        $"static={staticRadius * FeetToMm:F1}mm → reducer needed");
            }
            else
            {
                needsReducer = false;
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
            }
        }
    }

    private void InitReducerFittingChain()
    {
        using var _scope = SmartConLogger.BeginScope("Editor",
            ("Method", "InitReducerFittingChain"));
        // TODO [ChainV2]: Обобщить для N звеньев. Сейчас работает для 2 звеньев: reducer + fitting.
        var plan = _activeChainPlan!;

        if (plan.Links.Count < 2)
        {
            SmartConLogger.Warn("ReducerFitting plan has < 2 links — falling back to legacy flow " +
                "[Action: если соединение собрано не так, как ожидалось, сообщите разработчикам — план цепочки фитингов некорректен]");
            InitLegacyFlow();
            return;
        }

        var reducerLink = plan.Links[0];
        var fittingLink = plan.Links[1];

        if (reducerLink.Type != FittingChainNodeType.Reducer ||
            fittingLink.Type != FittingChainNodeType.Fitting)
        {
            SmartConLogger.Warn("ReducerFitting plan has unexpected link types — falling back to legacy flow " +
                "[Action: если соединение собрано не так, как ожидалось, сообщите разработчикам — типы звеньев плана некорректны]");
            InitLegacyFlow();
            return;
        }

        _activeFittingRule = fittingLink.Rule;

        // Step 1: Insert REDUCER aligned to static
        ElementId? insertedReducerId = null;
        ConnectorProxy? reducerConn2 = null;

        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_InsertReducer"), doc =>
        {
            insertedReducerId = _fittingInsertSvc.InsertFitting(
                doc, reducerLink.Family.FamilyName, reducerLink.Family.SymbolName,
                _ctx.StaticConnector.Origin);

            if (insertedReducerId is null) return;

            SmartConLogger.Info($"ReducerFitting: inserted reducer id={insertedReducerId.GetValue()}");
            doc.Regenerate();

            var overrides = GuessCtcForReducer(insertedReducerId);

            reducerConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, insertedReducerId, _ctx.StaticConnector, _transformSvc, _connSvc,
                dynamicTypeCode: reducerLink.CtcOut,
                ctcOverrides: overrides,
                directConnectRules: _mappingRepo.GetMappingRules());

            doc.Regenerate();
        });

        if (insertedReducerId is null)
        {
            SmartConLogger.Warn("ReducerFitting: reducer insertion failed — falling back " +
                "[Action: проверьте, что семейство переходника загружено в проект и mapping указывает на существующий тип]");
            InitLegacyFlow();
            return;
        }

        _primaryReducerId = insertedReducerId;
        SizeFittingConnectors(_doc, insertedReducerId, reducerConn2, adjustDynamicToFit: false);

        // Refresh reducer conn2 after sizing
        var allRConns = _connSvc.GetAllFreeConnectors(_doc, insertedReducerId).ToList();
        reducerConn2 = allRConns.Count >= 2 ? allRConns[1] : allRConns.FirstOrDefault();

        // Step 2: Insert FITTING aligned to reducer.conn2
        var fittingFamily = fittingLink.Family;
        ElementId? insertedFittingId = null;
        ConnectorProxy? fitConn2 = null;
        ConnectorProxy? alignTarget = reducerConn2 ?? _ctx.StaticConnector;

        _groupSession!.RunInTransaction(LocalizationService.GetString("Tx_InsertFitting"), doc =>
        {
            insertedFittingId = _fittingInsertSvc.InsertFitting(
                doc, fittingFamily.FamilyName, fittingFamily.SymbolName,
                alignTarget.Origin);

            if (insertedFittingId is null) return;

            SmartConLogger.Info($"ReducerFitting: inserted fitting id={insertedFittingId.GetValue()}");
            doc.Regenerate();

            var ctcOverrides = GuessCtcForFitting(insertedFittingId, fittingLink.Rule);
            var dynCtc = ResolveDynamicTypeFromRule(_activeFittingRule);

            fitConn2 = _fittingInsertSvc.AlignFittingToStatic(
                doc, insertedFittingId, alignTarget, _transformSvc, _connSvc,
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
                    PipeAbsorptionApplier.MoveOrAbsorb(
                        doc, _transformSvc, _activeDynamic.OwnerElementId, activeProxy.OriginVec3, offset);
            }

            doc.Regenerate();
        });

        if (insertedFittingId is not null)
        {
            _currentFittingId = insertedFittingId;
            _activeFittingConn2 = fitConn2;
            StatusMessage = string.Format(LocalizationService.GetString("Status_Inserted"), fittingFamily.FamilyName);

            var newFitConn2 = SizeFittingConnectors(_doc, insertedFittingId, fitConn2);
            if (newFitConn2 is not null)
                _activeFittingConn2 = newFitConn2;
        }

        _needsPrimaryReducer = true;
        IsReducerVisible = true;
        SmartConLogger.Info($"ReducerFitting: DONE reducer={_primaryReducerId?.GetValue()}, fitting={_currentFittingId?.GetValue()}");
    }

    private void EnsureReducersForFittingPair(ConnectorProxy fitConn2, ConnectorProxy dynamicConn)
    {
        using var _scope = SmartConLogger.BeginScope("Editor",
            ("Method", "EnsureReducersForFittingPair"));

        if (AvailableReducers.Count > 0)
        {
            SmartConLogger.Debug($"AvailableReducers already populated (Count={AvailableReducers.Count}) — skip rebuild");
            if (SelectedReducer is null)
                SelectedReducer = AvailableReducers[0];
            return;
        }

        var fitCtc = fitConn2.ConnectionTypeCode;
        var dynCtc = dynamicConn.ConnectionTypeCode;

        if (!fitCtc.IsDefined || !dynCtc.IsDefined)
        {
            SmartConLogger.Warn($"Cannot resolve reducer rule: fit CTC={fitCtc.Value}, dyn CTC={dynCtc.Value} (undefined) — reducer list stays empty " +
                "[Action: задайте тип соединения для dynamic-элемента через мини-селектор типов, затем повторите]");
            return;
        }

        // Тот же источник правил, что и у NetworkMover.InsertReducer (GetMappings):
        // reducer вставляется по этому правилу — и список обязан строиться из него же,
        // иначе ComboBox остаётся пустым при успешно вставленном reducer.
        var rules = _fittingMapper.GetMappings(fitCtc, dynCtc);

        foreach (var rule in rules)
        {
            if (rule.ReducerFamilies.Count == 0) continue;

            SmartConLogger.Info($"Found reducer rule: From={rule.FromType.Value} To={rule.ToType.Value} ({rule.ReducerFamilies.Count} families)");
            foreach (var reducer in rule.ReducerFamilies.OrderBy(f => f.Priority))
                AvailableReducers.Add(new FittingCardItem(rule, reducer, isReducer: true));

            if (SelectedReducer is null && AvailableReducers.Count > 0)
                SelectedReducer = AvailableReducers[0];
            return;
        }

        SmartConLogger.Warn($"No reducer rule found for pair CTC {fitCtc.Value} ↔ {dynCtc.Value} — reducer list stays empty " +
            "[Action: добавьте правило с семейством переходника в mapping (Настройки → Правила)]");
    }
}
