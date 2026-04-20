using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Auto-guessing of CTC assignments for fittings and reducers.
/// Uses <see cref="CtcGuesser"/> algorithms and virtual CTC store.
/// </summary>
public sealed class CtcGuessService(
    IConnectorService connSvc,
    IFittingMappingRepository mappingRepo,
    VirtualCtcStore virtualCtcStore)
{
    /// <summary>Guess CTC for a fitting element (adapter/nipple/coupling).</summary>
    public IReadOnlyDictionary<int, ConnectionTypeCode> GuessCtcForFitting(
        Document doc, ElementId fittingId,
        ConnectorProxy staticConnector, ConnectorProxy dynamicFallback, ConnectorProxy? activeDynamic)
        => GuessCtcForElement(doc, fittingId, isReducer: false, staticConnector, dynamicFallback, activeDynamic);

    /// <summary>Guess CTC for a reducer element.</summary>
    public IReadOnlyDictionary<int, ConnectionTypeCode> GuessCtcForReducer(
        Document doc, ElementId reducerId,
        ConnectorProxy staticConnector, ConnectorProxy dynamicFallback, ConnectorProxy? activeDynamic)
        => GuessCtcForElement(doc, reducerId, isReducer: true, staticConnector, dynamicFallback, activeDynamic);

    /// <summary>
    /// Core guessing logic for any element (fitting or reducer).
    /// If all connectors already have defined CTCs, uses those; otherwise applies
    /// <see cref="CtcGuesser"/> algorithms based on static/dynamic context.
    /// </summary>
    public IReadOnlyDictionary<int, ConnectionTypeCode> GuessCtcForElement(
        Document doc, ElementId elementId, bool isReducer,
        ConnectorProxy staticConnector, ConnectorProxy dynamicFallback, ConnectorProxy? activeDynamic)
    {
        var label = isReducer ? "Reducer" : "Fitting";
        var conns = connSvc.GetAllConnectors(doc, elementId);

        bool allDefined = conns.Count >= 2 && conns.All(c => c.ConnectionTypeCode.IsDefined);
        if (allDefined)
        {
            foreach (var c in conns)
                virtualCtcStore.Set(elementId, c.ConnectorIndex, c.ConnectionTypeCode);
            SmartConLogger.Info($"[VirtualCTC] {label} {elementId.GetValue()}: CTC already defined → " +
                string.Join(", ", conns.Select(c => $"conn[{c.ConnectorIndex}]={c.ConnectionTypeCode.Value}")));
            return virtualCtcStore.GetOverridesForElement(elementId);
        }

        var staticCTC = staticConnector.ConnectionTypeCode;
        var dynamicCTC = (activeDynamic ?? dynamicFallback).ConnectionTypeCode;
        var mappingRules = mappingRepo.GetMappingRules();

        var (ctcForStaticSide, ctcForDynamicSide) = isReducer
            ? CtcGuesser.GuessReducerCtc(staticCTC, dynamicCTC, mappingRules)
            : CtcGuesser.GuessAdapterCtc(staticCTC, dynamicCTC, mappingRules);

        if (conns.Count >= 2)
        {
            var staticOrigin = staticConnector.Origin;
            var connForStatic = conns
                .OrderBy(c => c.Origin.DistanceTo(staticOrigin))
                .ThenBy(c => c.ConnectorIndex)
                .First();
            var connForDynamic = conns.First(c => c.ConnectorIndex != connForStatic.ConnectorIndex);

            virtualCtcStore.Set(elementId, connForStatic.ConnectorIndex, ctcForStaticSide);
            virtualCtcStore.Set(elementId, connForDynamic.ConnectorIndex, ctcForDynamicSide);
            SmartConLogger.Info($"[VirtualCTC] {label} {elementId.GetValue()} (guessed): " +
                $"conn[{connForStatic.ConnectorIndex}]={ctcForStaticSide.Value}→static(R={connForStatic.Radius * FeetToMm:F1}mm), " +
                $"conn[{connForDynamic.ConnectorIndex}]={ctcForDynamicSide.Value}→dynamic(R={connForDynamic.Radius * FeetToMm:F1}mm)");
        }

        return virtualCtcStore.GetOverridesForElement(elementId);
    }

    /// <summary>Build CTC setup items from the virtual store for the setup dialog.</summary>
    public List<FittingCtcSetupItem> BuildCtcItemsFromVirtualStore(
        Document doc, ElementId elementId, IReadOnlyList<ConnectorTypeDefinition> types)
    {
        var conns = connSvc.GetAllConnectors(doc, elementId);
        var items = new List<FittingCtcSetupItem>();

        foreach (var c in conns)
        {
            var vCtc = virtualCtcStore.Get(elementId, c.ConnectorIndex);
            var selectedType = vCtc.HasValue
                ? types.FirstOrDefault(t => t.Code == vCtc.Value.Value)
                : (c.ConnectionTypeCode.IsDefined
                    ? types.FirstOrDefault(t => t.Code == c.ConnectionTypeCode.Value)
                    : null);

            double diamMm = c.Radius * 2.0 * FeetToMm;
            items.Add(new FittingCtcSetupItem
            {
                ConnectorIndex = c.ConnectorIndex,
                ParameterName = string.Empty,
                DiameterMm = diamMm,
                SelectedType = selectedType,
            });
        }

        return items;
    }

    /// <summary>Find a ConnectorTypeDefinition by CTC code.</summary>
    public ConnectorTypeDefinition? FindTypeDef(ConnectionTypeCode ctc)
    {
        if (!ctc.IsDefined) return null;
        return mappingRepo.GetConnectorTypes().FirstOrDefault(t => t.Code == ctc.Value);
    }

    /// <summary>Promote guessed CTC values to pending writes for fitting and reducer.</summary>
    public void PromoteGuessedCtcToPendingWrites(
        Document doc, ElementId? currentFittingId, ElementId? primaryReducerId)
    {
        PromoteElementCtcToPendingWrites(doc, currentFittingId);
        PromoteElementCtcToPendingWrites(doc, primaryReducerId);
    }

    /// <summary>Promote virtual CTC overrides to pending writes for a single element.</summary>
    public void PromoteElementCtcToPendingWrites(Document doc, ElementId? elementId)
    {
        if (elementId is null) return;

        var overrides = virtualCtcStore.GetOverridesForElement(elementId);
        if (overrides.Count == 0) return;

        var conns = connSvc.GetAllConnectors(doc, elementId);
        bool allDefined = conns.Count >= 2 && conns.All(c => c.ConnectionTypeCode.IsDefined);
        if (allDefined)
        {
            bool allMatch = true;
            foreach (var c in conns)
            {
                var vCtc = virtualCtcStore.Get(elementId, c.ConnectorIndex);
                if (vCtc.HasValue && vCtc.Value.Value != c.ConnectionTypeCode.Value)
                {
                    allMatch = false;
                    break;
                }
            }
            if (allMatch) return;

            SmartConLogger.Info($"[CTC] Virtual CTC differs from family CTC for {elementId.GetValue()} — overwrite");
        }

        foreach (var kvp in overrides)
        {
            var typeDef = FindTypeDef(kvp.Value);
            if (typeDef is not null)
            {
                virtualCtcStore.Set(elementId, kvp.Key, kvp.Value, typeDef);
                SmartConLogger.Info($"[CTC] Promoted guessed CTC {kvp.Value.Value} → pending write for {elementId.GetValue()}:{kvp.Key}");
            }
        }
    }

    /// <summary>
    /// Build CTC setup items by reading connector elements from the family document.
    /// Pre-selects types based on the mapping rule and static CTC.
    /// </summary>
    public List<FittingCtcSetupItem> BuildConnectorItems(
        Document doc, FamilySymbol symbol, IReadOnlyList<ConnectorTypeDefinition> types,
        FittingMappingRule rule, ConnectionTypeCode staticCtc, bool crossConnect = false)
    {
        Document? familyDoc = null;
        try
        {
            familyDoc = doc.EditFamily(symbol.Family);

            var connElems = new FilteredElementCollector(familyDoc)
                .OfCategory(BuiltInCategory.OST_ConnectorElem)
                .WhereElementIsNotElementType()
                .Cast<ConnectorElement>()
                .ToList();

            var items = new List<FittingCtcSetupItem>();

            ConnectionTypeCode? preSelectForConnToStatic = null;
            ConnectionTypeCode? preSelectForConnToDynamic = null;
            if (rule.FromType.Value == staticCtc.Value)
            {
                preSelectForConnToStatic = crossConnect ? rule.ToType : rule.FromType;
                preSelectForConnToDynamic = crossConnect ? rule.FromType : rule.ToType;
            }
            else if (rule.ToType.Value == staticCtc.Value)
            {
                preSelectForConnToStatic = crossConnect ? rule.FromType : rule.ToType;
                preSelectForConnToDynamic = crossConnect ? rule.ToType : rule.FromType;
            }

            for (int i = 0; i < connElems.Count; i++)
            {
                var ce = connElems[i];
                var desc = ce.get_Parameter(BuiltInParameter.RBS_CONNECTOR_DESCRIPTION)?.AsString();
                var currentCtc = ConnectionTypeCode.Parse(desc);

                string paramName = CtcFamilyWriter.GetConnectorParamName(ce, familyDoc);
                double diamMm = ce.Radius * 2.0 * FeetToMm;

                ConnectorTypeDefinition? preSelect = null;
                if (preSelectForConnToStatic.HasValue && preSelectForConnToDynamic.HasValue)
                {
                    var code = i == 0 ? preSelectForConnToStatic.Value : preSelectForConnToDynamic.Value;
                    preSelect = types.FirstOrDefault(t => t.Code == code.Value);
                }

                items.Add(new FittingCtcSetupItem
                {
                    ConnectorIndex = i,
                    ParameterName = paramName,
                    DiameterMm = diamMm,
                    SelectedType = currentCtc.IsDefined
                        ? types.FirstOrDefault(t => t.Code == currentCtc.Value)
                        : null,
                    PreSelectedType = preSelect
                });
            }

            return items;
        }
        finally
        {
            familyDoc?.Close(false);
        }
    }
}
