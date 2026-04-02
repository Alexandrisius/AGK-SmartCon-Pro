using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Logging;
using SmartCon.PipeConnect.Events;
using SmartCon.PipeConnect.ViewModels;
using SmartCon.PipeConnect.Views;

namespace SmartCon.PipeConnect.Commands;

/// <summary>
/// Точка входа PipeConnect с Ribbon.
/// Phase 5+8: workflow S1 → S1.1 → S2 → S2.1 → S3(plan) → S4(plan) → S5(FittingMapper)
///            → S6: открыть PipeConnectEditorView (модальное окно).
/// Все транзакции (Align, SetParam, InsertFitting, ConnectTo) выполняются
/// через ExternalEvent внутри PipeConnectEditorViewModel.
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class PipeConnectCommand : IExternalCommand
{
    /// <summary>
    /// Результат анализа S4 (до TransactionGroup). Иммутабельный.
    /// </summary>
    private sealed record ParameterResolutionPlan(
        bool   Skip,               // радиусы совпадают → S4 пропускается
        double TargetRadius,       // целевой радиус (internal units)
        bool   ExpectNeedsAdapter, // нашли только ближайший или провал S4
        string? WarningMessage     // null = нет предупреждения
    );

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            SmartConLogger.LogSessionStart("PipeConnectCommand");

            var contextWriter    = ServiceHost.GetService<IRevitContextWriter>();
            contextWriter.SetContext(commandData.Application);

            var revitContext     = ServiceHost.GetService<IRevitContext>();
            var selectionSvc     = ServiceHost.GetService<IElementSelectionService>();
            var connectorSvc     = ServiceHost.GetService<IConnectorService>();
            var transformSvc     = ServiceHost.GetService<ITransformService>();
            var txService        = ServiceHost.GetService<ITransactionService>();
            var mappingRepo      = ServiceHost.GetService<IFittingMappingRepository>();
            var familyConnSvc    = ServiceHost.GetService<IFamilyConnectorService>();
            var dialogSvc        = ServiceHost.GetService<IDialogService>();
            var paramResolver    = ServiceHost.GetService<IParameterResolver>();
            var lookupSvc        = ServiceHost.GetService<ILookupTableService>();
            var fittingMapper    = ServiceHost.GetService<IFittingMapper>();
            var fittingInsertSvc = ServiceHost.GetService<IFittingInsertService>();
            var eventHandler     = ServiceHost.GetService<PipeConnectExternalEvent>();

            var doc = revitContext.GetDocument();

            // ── S1: выбор Dynamic-элемента (движется, присоединяется ко второму) ──
            var dynamicPick = selectionSvc.PickElementWithFreeConnector(
                "PipeConnect: выберите ПЕРВЫЙ элемент (будет присоединён)");
            if (dynamicPick is null) return Result.Cancelled;

            var dynamicProxy = connectorSvc.GetNearestFreeConnector(
                doc, dynamicPick.Value.ElementId, dynamicPick.Value.ClickPoint);
            if (dynamicProxy is null)
            {
                dialogSvc.ShowWarning("SmartCon", "Нет свободных коннекторов у первого элемента.");
                return Result.Cancelled;
            }

            // ── S1.1: тип коннектора Dynamic ───────────────────────────────────────
            if (!IsKnownTypeCode(dynamicProxy.ConnectionTypeCode, mappingRepo))
            {
                var result = EnsureTypeCode(doc, dynamicProxy, mappingRepo, familyConnSvc, txService, dialogSvc);
                if (result is null) return Result.Cancelled;
                dynamicProxy = connectorSvc.GetNearestFreeConnector(
                    doc, dynamicProxy.OwnerElementId, dynamicProxy.Origin) ?? dynamicProxy;
            }

            // ── S2: выбор Static-элемента (неподвижный ориентир) ──────────────────
            var staticPick = selectionSvc.PickElementWithFreeConnector(
                "PipeConnect: выберите ВТОРОЙ элемент (неподвижный ориентир)",
                excludeElementId: dynamicPick.Value.ElementId);
            if (staticPick is null) return Result.Cancelled;

            var staticProxy = connectorSvc.GetNearestFreeConnector(
                doc, staticPick.Value.ElementId, staticPick.Value.ClickPoint);
            if (staticProxy is null)
            {
                dialogSvc.ShowWarning("SmartCon", "Нет свободных коннекторов у второго элемента.");
                return Result.Cancelled;
            }

            // ── S2.1: тип коннектора Static ────────────────────────────────────────
            if (!IsKnownTypeCode(staticProxy.ConnectionTypeCode, mappingRepo))
            {
                var result = EnsureTypeCode(doc, staticProxy, mappingRepo, familyConnSvc, txService, dialogSvc);
                if (result is null) return Result.Cancelled;
                staticProxy = connectorSvc.GetNearestFreeConnector(
                    doc, staticProxy.OwnerElementId, staticProxy.Origin) ?? staticProxy;
            }

            // ── S3: вычисление выравнивания (чистая математика Core, без Revit API) ─
            var alignResult = ConnectorAligner.ComputeAlignment(
                staticProxy.OriginVec3,  staticProxy.BasisZVec3,  staticProxy.BasisXVec3,
                dynamicProxy.OriginVec3, dynamicProxy.BasisZVec3, dynamicProxy.BasisXVec3);

            // ── S4: анализ параметров (ВНЕ TransactionGroup — EditFamily запрещён внутри) ──
            var plan = BuildResolutionPlan(doc, dynamicProxy, staticProxy.Radius, paramResolver, lookupSvc);

            // ── S5: подбор фитингов из маппинга ────────────────────────────────────
            var proposed = fittingMapper.GetMappings(
                staticProxy.ConnectionTypeCode, dynamicProxy.ConnectionTypeCode);

            if (proposed.Count == 0 &&
                staticProxy.ConnectionTypeCode.IsDefined &&
                dynamicProxy.ConnectionTypeCode.IsDefined)
            {
                proposed = fittingMapper.FindShortestFittingPath(
                    staticProxy.ConnectionTypeCode, dynamicProxy.ConnectionTypeCode);
            }

            // ── S3+S4 выполняем ЗДЕСЬ на Revit-потоке, ДО открытия окна ─────────
            // S4: EditFamily запрещён внутри TransactionGroup → нужно сделать заранее
            if (!plan.Skip)
            {
                txService.RunInTransaction("PipeConnect — Подгонка размера", txDoc =>
                {
                    paramResolver.TrySetConnectorRadius(
                        txDoc,
                        dynamicProxy.OwnerElementId,
                        dynamicProxy.ConnectorIndex,
                        plan.TargetRadius);
                    txDoc.Regenerate();
                });
                // Перечитать коннектор после изменения размера (по индексу — позиция могла сместиться)
                dynamicProxy = connectorSvc.RefreshConnector(
                    doc, dynamicProxy.OwnerElementId, dynamicProxy.ConnectorIndex) ?? dynamicProxy;
            }

            // S3: Align — перемещение + поворот dynamic к static
            txService.RunInTransaction("PipeConnect — Выравнивание", txDoc =>
            {
                var dynId = dynamicProxy.OwnerElementId;

                if (!VectorUtils.IsZero(alignResult.InitialOffset))
                    transformSvc.MoveElement(txDoc, dynId, alignResult.InitialOffset);

                if (alignResult.BasisZRotation is { } bzRot)
                    transformSvc.RotateElement(txDoc, dynId,
                        alignResult.RotationCenter, bzRot.Axis, bzRot.AngleRadians);

                if (alignResult.BasisXSnap is { } bxSnap)
                    transformSvc.RotateElement(txDoc, dynId,
                        alignResult.RotationCenter, bxSnap.Axis, bxSnap.AngleRadians);

                txDoc.Regenerate();
                var refreshed = connectorSvc.RefreshConnector(txDoc, dynId, dynamicProxy.ConnectorIndex);
                if (refreshed is not null)
                {
                    var corr = staticProxy.OriginVec3 - refreshed.OriginVec3;
                    if (!VectorUtils.IsZero(corr))
                        transformSvc.MoveElement(txDoc, dynId, corr);
                }
                txDoc.Regenerate();
            });

            // Перечитать актуальный dynamic после выравнивания (по индексу — после алайнмента Origin сместился к static)
            dynamicProxy = connectorSvc.RefreshConnector(
                doc, dynamicProxy.OwnerElementId, dynamicProxy.ConnectorIndex) ?? dynamicProxy;

            // Предупреждение S4
            if (plan.WarningMessage is not null)
                dialogSvc.ShowWarning("SmartCon", plan.WarningMessage);

            // ── S6: открыть PipeConnectEditor ──────────────────────────────────────
            var sessionCtx = new PipeConnectSessionContext
            {
                StaticConnector         = staticProxy,
                DynamicConnector        = dynamicProxy,
                AlignResult             = alignResult,
                ParamTargetRadius       = null,   // уже применено выше
                ParamExpectNeedsAdapter = plan.ExpectNeedsAdapter,
                ProposedFittings        = proposed.ToList(),
            };

            var vm = new PipeConnectEditorViewModel(
                sessionCtx, txService, connectorSvc, transformSvc,
                fittingInsertSvc, paramResolver, eventHandler);

            var view = new PipeConnectEditorView(vm);
            view.WindowStartupLocation = System.Windows.WindowStartupLocation.Manual;
            var workArea = System.Windows.SystemParameters.WorkArea;
            view.Left = System.Math.Max(workArea.Left + 20, workArea.Right - 540);
            view.Top  = workArea.Top + 40;
            view.Show();

            return Result.Succeeded;
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return Result.Cancelled;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return Result.Failed;
        }
    }

    /// <summary>
    /// Возвращает true, если код определён (не 0) И существует в таблице маппинга.
    /// False — нужно показать MiniTypeSelector (пустой или "чужой" код).
    /// </summary>
    private static bool IsKnownTypeCode(ConnectionTypeCode code, IFittingMappingRepository mappingRepo)
    {
        if (!code.IsDefined) return false;
        var types = mappingRepo.GetConnectorTypes();
        return types.Any(t => t.Code == code.Value);
    }

    /// <summary>
    /// Шаг S1.1/S2.1: если коннектор не имеет типа — показать MiniTypeSelector,
    /// записать выбранный тип в Description семейства. Возвращает null → отмена.
    /// </summary>
    private static ConnectorTypeDefinition? EnsureTypeCode(
        Document doc,
        Core.Models.ConnectorProxy proxy,
        IFittingMappingRepository mappingRepo,
        IFamilyConnectorService familyConnSvc,
        ITransactionService txService,
        IDialogService dialogSvc)
    {
        var types = mappingRepo.GetConnectorTypes();
        if (types.Count == 0)
        {
            dialogSvc.ShowWarning("SmartCon", "Сначала настройте типы коннекторов в Настройках.");
            return null;
        }

        var selected = dialogSvc.ShowMiniTypeSelector(types);
        if (selected is null) return null;

        var element = doc.GetElement(proxy.OwnerElementId);

        if (element is MEPCurve or FlexPipe)
        {
            // Трубы: пишем в параметр типоразмера — нужна транзакция проекта.
            txService.RunInTransaction("SetConnectorType", txDoc =>
            {
                familyConnSvc.SetConnectorTypeCode(
                    txDoc, proxy.OwnerElementId, proxy.ConnectorIndex, selected);
            });
        }
        else
        {
            // FamilyInstance: EditFamily запрещён внутри транзакции.
            // Сервис сам открывает транзакции через ITransactionService.
            familyConnSvc.SetConnectorTypeCode(
                doc, proxy.OwnerElementId, proxy.ConnectorIndex, selected);
        }

        return selected;
    }

    /// <summary>
    /// S4 — анализ параметров ДО TransactionGroup.
    /// Определяет стратегию подбора размера динамического элемента под статический.
    /// Вызов EditFamily и LookupTable — разрешены здесь (нет активной транзакции).
    /// </summary>
    private static ParameterResolutionPlan BuildResolutionPlan(
        Document doc,
        Core.Models.ConnectorProxy dynamicProxy,
        double staticRadius,
        IParameterResolver paramResolver,
        ILookupTableService lookupSvc)
    {
        const double eps = 1e-6; // ~0.3 мкм

        SmartConLogger.LookupSection("BuildResolutionPlan (S4)");
        SmartConLogger.Lookup($"  dynamic: elementId={dynamicProxy.OwnerElementId.Value}, connIdx={dynamicProxy.ConnectorIndex}, radius={dynamicProxy.Radius:F6} ft ({dynamicProxy.Radius * 304.8:F2} mm)");
        SmartConLogger.Lookup($"  staticRadius={staticRadius:F6} ft ({staticRadius * 304.8:F2} mm)");
        SmartConLogger.Lookup($"  delta={System.Math.Abs(staticRadius - dynamicProxy.Radius):F6} ft");

        // 1. Радиусы совпадают → пропустить S4
        if (System.Math.Abs(staticRadius - dynamicProxy.Radius) < eps)
        {
            SmartConLogger.Lookup("  → Радиусы совпадают (< eps) → Plan(Skip=true)");
            SmartConLogger.Info($"[S4] Радиусы совпадают, S4 пропущен");
            return new ParameterResolutionPlan(Skip: true, TargetRadius: staticRadius,
                ExpectNeedsAdapter: false, WarningMessage: null);
        }

        var dynId    = dynamicProxy.OwnerElementId;
        var connIdx  = dynamicProxy.ConnectorIndex;
        double staticDn = System.Math.Round(staticRadius * 2.0 * 304.8);
        double dynDn    = System.Math.Round(dynamicProxy.Radius * 2.0 * 304.8);
        SmartConLogger.Lookup($"  static=DN{staticDn}, dynamic=DN{dynDn} — нужно подбрать");

        // 2. MEP Curve (Pipe) → прямая запись, без EditFamily
        var element = doc.GetElement(dynId);
        SmartConLogger.Lookup($"  element='{element?.Name}' ({element?.GetType().Name})");

        if (element is MEPCurve or Autodesk.Revit.DB.Plumbing.FlexPipe)
        {
            SmartConLogger.Lookup("  → MEPCurve/FlexPipe: прямая запись RBS_PIPE_DIAMETER_PARAM → Plan(Skip=false, target=staticRadius)");
            SmartConLogger.Info($"[S4] MEPCurve DN{dynDn} → DN{staticDn}: прямая запись");
            return new ParameterResolutionPlan(Skip: false, TargetRadius: staticRadius,
                ExpectNeedsAdapter: false, WarningMessage: null);
        }

        // 3. Анализ семейства: GetConnectorRadiusDependencies (EditFamily здесь)
        SmartConLogger.Lookup("  → GetConnectorRadiusDependencies...");
        var deps = paramResolver.GetConnectorRadiusDependencies(doc, dynId, connIdx);
        var dep  = deps.Count > 0 ? deps[0] : null;
        SmartConLogger.Lookup($"  deps.Count={deps.Count}, dep={( dep is null ? "null" : $"IsInstance={dep.IsInstance}, Formula='{dep.Formula}', DirectParamName='{dep.DirectParamName}', IsDiameter={dep.IsDiameter}")}");

        // 4. LookupTable: есть ли точный размер?
        SmartConLogger.Lookup("  → HasLookupTable...");
        bool hasTable  = lookupSvc.HasLookupTable(doc, dynId, connIdx);
        SmartConLogger.Lookup($"  hasTable={hasTable}");

        if (hasTable)
        {
            SmartConLogger.Lookup("  → ConnectorRadiusExistsInTable...");
            bool exactMatch = lookupSvc.ConnectorRadiusExistsInTable(doc, dynId, connIdx, staticRadius);
            SmartConLogger.Lookup($"  exactMatch={exactMatch}");

            if (exactMatch)
            {
                SmartConLogger.Lookup("  → Точное совпадение в таблице → Plan(Skip=false, target=staticRadius)");
                SmartConLogger.Info($"[S4] LookupTable: DN{staticDn} найден точно");
                return new ParameterResolutionPlan(Skip: false, TargetRadius: staticRadius,
                    ExpectNeedsAdapter: false, WarningMessage: null);
            }

            // Нет точного → берём ближайший
            SmartConLogger.Lookup("  → GetNearestAvailableRadius...");
            double nearest   = lookupSvc.GetNearestAvailableRadius(doc, dynId, connIdx, staticRadius);
            double nearestDn = System.Math.Round(nearest * 2.0 * 304.8);
            SmartConLogger.Lookup($"  nearest={nearest:F6} ft = DN{nearestDn}");
            SmartConLogger.Warn($"[S4] LookupTable: DN{staticDn} не найден, ближайший=DN{nearestDn} (NeedsAdapter)");
            return new ParameterResolutionPlan(
                Skip: false, TargetRadius: nearest,
                ExpectNeedsAdapter: true,
                WarningMessage: $"Размер DN{staticDn} отсутствует в таблице. Будет выбран DN{nearestDn}, нужен переходник.");
        }

        // 5. Нет таблицы и нет dep → полный провал
        if (dep is null)
        {
            SmartConLogger.Lookup("  → Нет таблицы И нет dep → Plan(NeedsAdapter=true, warning)");
            SmartConLogger.Warn($"[S4] Нет таблицы, dep=null — неудача S4 (NeedsAdapter)");
            return new ParameterResolutionPlan(
                Skip: false, TargetRadius: staticRadius,
                ExpectNeedsAdapter: true,
                WarningMessage: "Не удалось определить параметр размера. Будет вставлен переходник если настроен в маппинге.");
        }

        // 6. Dep найден → TrySetConnectorRadius разберётся с формулой и ChangeTypeId внутри транзакции.
        // Нелинейные формулы (SolveFor=null) или несовместимые типы → NeedsAdapter будет выставлен там.
        bool expectAdapter = !dep.IsInstance && dep.Formula is null;
        SmartConLogger.Lookup($"  → Dep найден: IsInstance={dep.IsInstance}, Formula='{dep.Formula}' → Plan(target=staticRadius, ExpectNeedsAdapter={expectAdapter})");
        SmartConLogger.Info($"[S4] dep найден: IsInstance={dep.IsInstance}, Formula='{dep.Formula}', DirectParamName='{dep.DirectParamName}', IsDiameter={dep.IsDiameter}");
        return new ParameterResolutionPlan(Skip: false, TargetRadius: staticRadius,
            ExpectNeedsAdapter: expectAdapter,
            WarningMessage: null);
    }

}
