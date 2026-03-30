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
/// Phase 3: workflow S1 → S1.1(TypeCode) → S2 → S2.1(TypeCode) → S3 → Committed.
/// Два клика — выравнивание + ConnectTo в одном TransactionGroup (одна запись Undo).
/// </summary>
[Transaction(TransactionMode.Manual)]
public sealed class PipeConnectCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        try
        {
            var contextWriter = ServiceHost.GetService<IRevitContextWriter>();
            contextWriter.SetContext(commandData.Application);

            var revitContext    = ServiceHost.GetService<IRevitContext>();
            var selectionSvc    = ServiceHost.GetService<IElementSelectionService>();
            var connectorSvc    = ServiceHost.GetService<IConnectorService>();
            var transformSvc    = ServiceHost.GetService<ITransformService>();
            var txService       = ServiceHost.GetService<ITransactionService>();
            var mappingRepo     = ServiceHost.GetService<IFittingMappingRepository>();
            var familyConnSvc   = ServiceHost.GetService<IFamilyConnectorService>();
            var dialogSvc       = ServiceHost.GetService<IDialogService>();

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
            if (!dynamicProxy.ConnectionTypeCode.IsDefined)
            {
                var result = EnsureTypeCode(doc, dynamicProxy, mappingRepo, familyConnSvc, txService, dialogSvc);
                if (result is null) return Result.Cancelled;
                dynamicProxy = connectorSvc.GetNearestFreeConnector(
                    doc, dynamicProxy.OwnerElementId, dynamicProxy.Origin) ?? dynamicProxy;
            }

            // ── S2: выбор Static-элемента (неподвижный ориентир) ──────────────────
            var staticPick = selectionSvc.PickElementWithFreeConnector(
                "PipeConnect: выберите ВТОРОЙ элемент (неподвижный ориентир)");
            if (staticPick is null) return Result.Cancelled;

            var staticProxy = connectorSvc.GetNearestFreeConnector(
                doc, staticPick.Value.ElementId, staticPick.Value.ClickPoint);
            if (staticProxy is null)
            {
                dialogSvc.ShowWarning("SmartCon", "Нет свободных коннекторов у второго элемента.");
                return Result.Cancelled;
            }

            // ── S2.1: тип коннектора Static ────────────────────────────────────────
            if (!staticProxy.ConnectionTypeCode.IsDefined)
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

            bool connectSucceeded = false;

            // ── TransactionGroup: одна запись Undo ─────────────────────────────────
            txService.RunInTransactionGroup("PipeConnect — Соединить", groupDoc =>
            {
                // Transaction 1: выравнивание (Align)
                txService.RunInTransaction("Align", alignDoc =>
                {
                    ApplyAlignment(alignDoc, dynamicProxy, alignResult, connectorSvc, transformSvc, staticProxy.OriginVec3);
                });

                // Transaction 2: ConnectTo (Connect)
                txService.RunInTransaction("Connect", connectDoc =>
                {
                    connectSucceeded = connectorSvc.ConnectTo(
                        connectDoc,
                        staticProxy.OwnerElementId,  staticProxy.ConnectorIndex,
                        dynamicProxy.OwnerElementId, dynamicProxy.ConnectorIndex);
                });
            });

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
