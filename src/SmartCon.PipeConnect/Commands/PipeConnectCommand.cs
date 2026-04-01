using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Plumbing;
using Autodesk.Revit.UI;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.PipeConnect.Commands;

/// <summary>
/// Точка входа PipeConnect с Ribbon.
/// Phase 4: workflow S1 → S1.1 → S2 → S2.1 → S3(Align) → S4(ResolvingParams) → S5(Connect).
/// Два клика — выравнивание + подбор параметра + ConnectTo в одном TransactionGroup (одна запись Undo).
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
            var contextWriter = ServiceHost.GetService<IRevitContextWriter>();
            contextWriter.SetContext(commandData.Application);

            var revitContext  = ServiceHost.GetService<IRevitContext>();
            var selectionSvc  = ServiceHost.GetService<IElementSelectionService>();
            var connectorSvc  = ServiceHost.GetService<IConnectorService>();
            var transformSvc  = ServiceHost.GetService<ITransformService>();
            var txService     = ServiceHost.GetService<ITransactionService>();
            var mappingRepo   = ServiceHost.GetService<IFittingMappingRepository>();
            var familyConnSvc = ServiceHost.GetService<IFamilyConnectorService>();
            var dialogSvc     = ServiceHost.GetService<IDialogService>();
            var paramResolver = ServiceHost.GetService<IParameterResolver>();
            var lookupSvc     = ServiceHost.GetService<ILookupTableService>();

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
            var plan = BuildResolutionPlan(
                doc, dynamicProxy, staticProxy.Radius,
                paramResolver, lookupSvc);

            bool connectSucceeded = false;

            // ── TransactionGroup: одна запись Undo ─────────────────────────────────
            txService.RunInTransactionGroup("PipeConnect — Соединить", groupDoc =>
            {
                // Transaction 1: выравнивание (Align)
                txService.RunInTransaction("Align", alignDoc =>
                {
                    ApplyAlignment(alignDoc, dynamicProxy, alignResult,
                        connectorSvc, transformSvc, staticProxy.OriginVec3);
                    // Регенерация внутри транзакции: обновляем позиции коннекторов
                    alignDoc.Regenerate();
                });

                // Transaction 2: подбор параметра (SetParam) — Phase 4
                if (!plan.Skip)
                {
                    txService.RunInTransaction("SetParam", paramDoc =>
                    {
                        bool success = paramResolver.TrySetConnectorRadius(
                            paramDoc,
                            dynamicProxy.OwnerElementId,
                            dynamicProxy.ConnectorIndex,
                            plan.TargetRadius);

                        if (!success || plan.ExpectNeedsAdapter)
                        {
                            // Флаг для Phase 5 (FittingMapper): понадобится переходник
                            // В Phase 4 просто продолжаем, не отменяем (D4)
                        }
                        // Регенерация внутри транзакции: обновляем геометрию коннекторов
                        paramDoc.Regenerate();
                    });
                }

                // Transaction 3: соединение (Connect)
                txService.RunInTransaction("Connect", connectDoc =>
                {
                    connectSucceeded = connectorSvc.ConnectTo(
                        connectDoc,
                        staticProxy.OwnerElementId,  staticProxy.ConnectorIndex,
                        dynamicProxy.OwnerElementId, dynamicProxy.ConnectorIndex);
                });
            });

            // Показать предупреждение S4 после завершения TransactionGroup
            if (plan.WarningMessage is not null)
                dialogSvc.ShowWarning("SmartCon", plan.WarningMessage);

            if (!connectSucceeded)
            {
                TaskDialog.Show("SmartCon",
                    "Выравнивание выполнено, но ConnectTo не удался.\n" +
                    "Возможно, элементы принадлежат разным системам.");
                return Result.Succeeded;
            }

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

        // 1. Радиусы совпадают → пропустить S4
        if (System.Math.Abs(staticRadius - dynamicProxy.Radius) < eps)
            return new ParameterResolutionPlan(Skip: true, TargetRadius: staticRadius,
                ExpectNeedsAdapter: false, WarningMessage: null);

        var dynId    = dynamicProxy.OwnerElementId;
        var connIdx  = dynamicProxy.ConnectorIndex;
        double dynDn = System.Math.Round(staticRadius * 2.0 * 304.8); // мм для сообщений

        // 2. MEP Curve (Pipe) → прямая запись, без EditFamily
        var element = doc.GetElement(dynId);
        if (element is MEPCurve or Autodesk.Revit.DB.Plumbing.FlexPipe)
            return new ParameterResolutionPlan(Skip: false, TargetRadius: staticRadius,
                ExpectNeedsAdapter: false, WarningMessage: null);

        // 3. Анализ семейства: GetConnectorRadiusDependencies (EditFamily здесь)
        var deps = paramResolver.GetConnectorRadiusDependencies(doc, dynId, connIdx);
        var dep  = deps.Count > 0 ? deps[0] : null;

        // 4. LookupTable: есть ли точный размер?
        bool hasTable  = lookupSvc.HasLookupTable(doc, dynId, connIdx);

        if (hasTable)
        {
            bool exactMatch = lookupSvc.ConnectorRadiusExistsInTable(doc, dynId, connIdx, staticRadius);
            if (exactMatch)
                return new ParameterResolutionPlan(Skip: false, TargetRadius: staticRadius,
                    ExpectNeedsAdapter: false, WarningMessage: null);

            // Нет точного → берём ближайший
            double nearest   = lookupSvc.GetNearestAvailableRadius(doc, dynId, connIdx, staticRadius);
            double nearestDn = System.Math.Round(nearest * 2.0 * 304.8);
            return new ParameterResolutionPlan(
                Skip: false, TargetRadius: nearest,
                ExpectNeedsAdapter: true,
                WarningMessage: $"Размер DN{dynDn} отсутствует в таблице. Будет выбран DN{nearestDn}, нужен переходник.");
        }

        // 5. Нет таблицы и нет dep → полный провал
        if (dep is null)
            return new ParameterResolutionPlan(
                Skip: false, TargetRadius: staticRadius,
                ExpectNeedsAdapter: true,
                WarningMessage: "Не удалось определить параметр размера. Будет вставлен переходник если настроен в маппинге.");

        // 6. Dep найден → TrySetConnectorRadius разберётся с формулой и ChangeTypeId внутри транзакции.
        // Нелинейные формулы (SolveFor=null) или несовместимые типы → NeedsAdapter будет выставлен там.
        return new ParameterResolutionPlan(Skip: false, TargetRadius: staticRadius,
            ExpectNeedsAdapter: !dep.IsInstance && dep.Formula is null,
            WarningMessage: null);
    }

    /// <summary>
    /// Применить AlignmentResult: шаги 1–4 алгоритма выравнивания.
    /// </summary>
    private static void ApplyAlignment(
        Document doc,
        Core.Models.ConnectorProxy dynamicProxy,
        AlignmentResult result,
        IConnectorService connectorSvc,
        ITransformService transformSvc,
        Vec3 staticOrigin)
    {
        var dynId = dynamicProxy.OwnerElementId;

        // Шаг 1: перемещение (совмещение Origins)
        if (!VectorUtils.IsZero(result.InitialOffset))
            transformSvc.MoveElement(doc, dynId, result.InitialOffset);

        // Шаг 2: поворот BasisZ (антипараллельность)
        if (result.BasisZRotation is { } bzRot)
            transformSvc.RotateElement(doc, dynId,
                result.RotationCenter, bzRot.Axis, bzRot.AngleRadians);

        // Шаг 3: снэп BasisX ("красивый" угол, кратный 15°)
        if (result.BasisXSnap is { } bxSnap)
            transformSvc.RotateElement(doc, dynId,
                result.RotationCenter, bxSnap.Axis, bxSnap.AngleRadians);

        // Шаг 4: коррекция позиции (после поворота Origin мог сместиться)
        var refreshed = connectorSvc.RefreshConnector(doc, dynId, dynamicProxy.ConnectorIndex);
        if (refreshed is not null)
        {
            var correction = staticOrigin - refreshed.OriginVec3;
            if (!VectorUtils.IsZero(correction))
                transformSvc.MoveElement(doc, dynId, correction);
        }
    }
}
