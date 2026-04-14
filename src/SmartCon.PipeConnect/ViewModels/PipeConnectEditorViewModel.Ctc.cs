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
using SmartCon.Core;
using SmartCon.PipeConnect.Services;


using static SmartCon.Core.Units;
namespace SmartCon.PipeConnect.ViewModels;

public sealed partial class PipeConnectEditorViewModel
{
    private readonly FittingCtcManager _ctcManager;

    private ConnectionTypeCode ResolveDynamicTypeFromRule(FittingMappingRule? rule)
        => _ctcManager.ResolveDynamicTypeFromRule(rule, _ctx.StaticConnector.ConnectionTypeCode);

    private ConnectorProxy? RefreshWithCtcOverride(Document doc, ElementId elemId, int connIdx)
        => _ctcManager.RefreshWithCtcOverride(doc, elemId, connIdx);

    private IReadOnlyDictionary<int, ConnectionTypeCode> GuessCtcForFitting(ElementId fittingId, FittingMappingRule? rule)
        => _ctcManager.GuessCtcForFitting(_doc, fittingId, _ctx.StaticConnector, _ctx.DynamicConnector, _activeDynamic);

    private IReadOnlyDictionary<int, ConnectionTypeCode> GuessCtcForReducer(ElementId reducerId)
        => _ctcManager.GuessCtcForReducer(_doc, reducerId, _ctx.StaticConnector, _ctx.DynamicConnector, _activeDynamic);

    private List<FittingCtcSetupItem> BuildCtcItemsFromVirtualStore(
        ElementId elementId, IReadOnlyList<ConnectorTypeDefinition> types)
        => _ctcManager.BuildCtcItemsFromVirtualStore(_doc, elementId, types);

    private void PromoteGuessedCtcToPendingWrites()
        => _ctcManager.PromoteGuessedCtcToPendingWrites(_doc, _currentFittingId, _primaryReducerId);

    private (ConnectorProxy? ToStatic, ConnectorProxy? ToDynamic) ResolveConnectorSidesForElement(
        ElementId elementId,
        IReadOnlyList<ConnectorProxy> conns,
        ConnectionTypeCode dynamicTypeCode)
        => _ctcManager.ResolveConnectorSidesForElement(
            _doc, elementId, conns, dynamicTypeCode, _ctx.StaticConnector);

    private void FlushVirtualCtcToFamilies()
        => _ctcManager.FlushVirtualCtcToFamilies(_doc, _groupSession);
}
