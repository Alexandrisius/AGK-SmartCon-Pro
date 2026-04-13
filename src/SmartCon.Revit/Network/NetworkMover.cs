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
    /// <remarks>
    /// Reducer используется только при IsDirectConnect=true (физически совместимые типы,
    /// например ВР↔НР) когда DN не совпадает. Ищем правило в таблице маппинга по паре
    /// parent→child и берём ReducerFamilies[0]. Никакого сравнения CTC — только запрос к таблице.
    /// </remarks>
    public ElementId? InsertReducer(Document doc,
        ConnectorProxy parentConn, ConnectorProxy childConn,
        IReadOnlyDictionary<int, ConnectionTypeCode>? ctcOverrides = null,
        IReadOnlyList<FittingMappingRule>? directConnectRules = null)
    {
        var parentCtc = parentConn.ConnectionTypeCode;
        var childCtc  = childConn.ConnectionTypeCode;

        // 1. Запрос к таблице маппинга — только ReducerFamilies из подходящего правила
        var rules = _fittingMapper.GetMappings(parentCtc, childCtc);

        FittingMapping? reducerFamily = null;
        foreach (var rule in rules)
        {
            if (rule.ReducerFamilies.Count > 0)
            {
                reducerFamily = rule.ReducerFamilies[0];
                SmartConLogger.Info($"[NetworkMover] InsertReducer: правило {parentCtc.Value}↔{childCtc.Value} → {reducerFamily.FamilyName}/{reducerFamily.SymbolName}");
                break;
            }
        }

        if (reducerFamily is null)
        {
            SmartConLogger.Warn($"[NetworkMover] Reducer не найден в правиле {parentCtc.Value}↔{childCtc.Value}");
            return null;
        }

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
            doc, reducerId, parentConn, _transformSvc, _connSvc,
            dynamicTypeCode: childConn.ConnectionTypeCode,
            ctcOverrides: ctcOverrides,
            directConnectRules: directConnectRules);

        doc.Regenerate();

        SmartConLogger.Info($"[NetworkMover] Reducer вставлен: id={reducerId.Value}");
        return reducerId;
    }
}
