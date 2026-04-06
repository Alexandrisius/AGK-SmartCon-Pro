using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Network;

/// <summary>
/// Вставка reducer-а между двумя коннекторами с разным DN.
/// Использует существующие IFittingInsertService, IFittingMapper, IParameterResolver.
/// </summary>
public sealed class NetworkMover : INetworkMover
{
    private readonly IFittingInsertService _fittingInsertSvc;
    private readonly IFittingMapper        _fittingMapper;
    private readonly IConnectorService     _connSvc;
    private readonly ITransformService     _transformSvc;
    private readonly IParameterResolver    _paramResolver;

    public NetworkMover(
        IFittingInsertService fittingInsertSvc,
        IFittingMapper        fittingMapper,
        IConnectorService     connSvc,
        ITransformService     transformSvc,
        IParameterResolver    paramResolver)
    {
        _fittingInsertSvc = fittingInsertSvc;
        _fittingMapper    = fittingMapper;
        _connSvc          = connSvc;
        _transformSvc     = transformSvc;
        _paramResolver    = paramResolver;
    }

    /// <inheritdoc />
    public ElementId? InsertReducer(Document doc,
        ConnectorProxy parentConn, ConnectorProxy childConn)
    {
        // 1. Найти reducer в маппинге (reducer соединяет ОДИН тип коннектора)
        var mappings = _fittingMapper.GetMappings(
            parentConn.ConnectionTypeCode, parentConn.ConnectionTypeCode);

        FittingMapping? reducerFamily = null;
        foreach (var rule in mappings)
        {
            if (rule.ReducerFamilies.Count > 0)
            {
                reducerFamily = rule.ReducerFamilies[0];
                break;
            }
        }

        if (reducerFamily is null)
        {
            SmartConLogger.Warn($"[NetworkMover] Reducer не найден для CTC={parentConn.ConnectionTypeCode.Value}");
            return null;
        }

        SmartConLogger.Info($"[NetworkMover] InsertReducer: {reducerFamily.FamilyName}/{reducerFamily.SymbolName}");

        // 2. Вставить reducer
        var reducerId = _fittingInsertSvc.InsertFitting(
            doc, reducerFamily.FamilyName, reducerFamily.SymbolName, parentConn.Origin);

        if (reducerId is null)
        {
            SmartConLogger.Warn($"[NetworkMover] InsertFitting вернул null для '{reducerFamily.FamilyName}'");
            return null;
        }

        // 3. Выровнять к parentConn
        _fittingInsertSvc.AlignFittingToStatic(
            doc, reducerId, parentConn, _transformSvc, _connSvc);

        // 4. Подогнать размер (TrySetFittingTypeForPair)
        var allConns = _connSvc.GetAllFreeConnectors(doc, reducerId);
        if (allConns.Count >= 2)
        {
            var conn1 = allConns
                .OrderBy(c => VectorUtils.DistanceTo(c.OriginVec3, parentConn.OriginVec3))
                .First();
            var conn2 = allConns.First(c => c.ConnectorIndex != conn1.ConnectorIndex);

            _paramResolver.TrySetFittingTypeForPair(doc, reducerId,
                conn1.ConnectorIndex, parentConn.Radius,
                conn2.ConnectorIndex, childConn.Radius);

            doc.Regenerate();
        }

        // 5. Повторно выровнять (после смены размера геометрия могла измениться)
        _fittingInsertSvc.AlignFittingToStatic(
            doc, reducerId, parentConn, _transformSvc, _connSvc);

        doc.Regenerate();

        SmartConLogger.Info($"[NetworkMover] Reducer вставлен: id={reducerId.Value}");
        return reducerId;
    }
}
