using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.PipeConnect.ViewModels;

using static SmartCon.Core.Units;

namespace SmartCon.PipeConnect.Services;

/// <summary>
/// Facade for CTC (ConnectionTypeCode) management in PipeConnect.
/// Delegates to <see cref="CtcResolutionService"/>, <see cref="CtcGuessService"/>,
/// and <see cref="CtcFamilyWriter"/>. Provides a single entry point for the ViewModel.
/// </summary>
public sealed class FittingCtcManager(
    CtcResolutionService resolutionSvc,
    CtcGuessService guessSvc,
    CtcFamilyWriter familyWriter,
    IFittingMappingRepository mappingRepo,
    IDialogService dialogSvc)
{
    /// <summary>Resolve the dynamic-side CTC from the fitting mapping rule.</summary>
    public ConnectionTypeCode ResolveDynamicTypeFromRule(
        FittingMappingRule? rule, ConnectionTypeCode staticCtc)
        => resolutionSvc.ResolveDynamicTypeFromRule(rule, staticCtc);

    /// <summary>Refresh connector proxy with virtual CTC override applied.</summary>
    public ConnectorProxy? RefreshWithCtcOverride(
        Document doc, ElementId elemId, int connIdx)
        => resolutionSvc.RefreshWithCtcOverride(doc, elemId, connIdx);

    /// <summary>Get the effective CTC for a connector (virtual override or actual).</summary>
    public ConnectionTypeCode GetEffectiveConnectorCtc(ElementId elementId, ConnectorProxy conn)
        => resolutionSvc.GetEffectiveConnectorCtc(elementId, conn);

    /// <summary>
    /// Resolve which connector faces static and which faces dynamic,
    /// using CTC matching rules and spatial proximity as fallback.
    /// </summary>
    public (ConnectorProxy? ToStatic, ConnectorProxy? ToDynamic) ResolveConnectorSidesForElement(
        Document doc,
        ElementId elementId,
        IReadOnlyList<ConnectorProxy> conns,
        ConnectionTypeCode dynamicTypeCode,
        ConnectorProxy staticConnector)
        => resolutionSvc.ResolveConnectorSidesForElement(doc, elementId, conns, dynamicTypeCode, staticConnector);

    /// <summary>Auto-guess CTC assignments for a fitting element.</summary>
    public IReadOnlyDictionary<int, ConnectionTypeCode> GuessCtcForFitting(
        Document doc, ElementId fittingId,
        ConnectorProxy staticConnector, ConnectorProxy dynamicFallback, ConnectorProxy? activeDynamic)
        => guessSvc.GuessCtcForFitting(doc, fittingId, staticConnector, dynamicFallback, activeDynamic);

    /// <summary>Auto-guess CTC assignments for a reducer element.</summary>
    public IReadOnlyDictionary<int, ConnectionTypeCode> GuessCtcForReducer(
        Document doc, ElementId reducerId,
        ConnectorProxy staticConnector, ConnectorProxy dynamicFallback, ConnectorProxy? activeDynamic)
        => guessSvc.GuessCtcForReducer(doc, reducerId, staticConnector, dynamicFallback, activeDynamic);

    /// <summary>Build CTC setup items from the virtual store for the setup dialog.</summary>
    public List<FittingCtcSetupItem> BuildCtcItemsFromVirtualStore(
        Document doc, ElementId elementId, IReadOnlyList<ConnectorTypeDefinition> types)
        => guessSvc.BuildCtcItemsFromVirtualStore(doc, elementId, types);

    /// <summary>Find a ConnectorTypeDefinition by its code.</summary>
    public ConnectorTypeDefinition? FindTypeDef(ConnectionTypeCode ctc)
        => guessSvc.FindTypeDef(ctc);

    /// <summary>Promote guessed CTC values to pending writes for fitting and reducer.</summary>
    public void PromoteGuessedCtcToPendingWrites(
        Document doc, ElementId? currentFittingId, ElementId? primaryReducerId)
        => guessSvc.PromoteGuessedCtcToPendingWrites(doc, currentFittingId, primaryReducerId);

    /// <summary>Promote virtual CTC overrides to pending writes for a single element.</summary>
    public void PromoteElementCtcToPendingWrites(Document doc, ElementId? elementId)
        => guessSvc.PromoteElementCtcToPendingWrites(doc, elementId);

    /// <summary>Build connector setup items for the CTC assignment dialog from a FamilySymbol.</summary>
    public List<FittingCtcSetupItem> BuildConnectorItems(
        Document doc, FamilySymbol symbol, IReadOnlyList<ConnectorTypeDefinition> types,
        FittingMappingRule rule, ConnectionTypeCode staticCtc, bool crossConnect = false)
        => guessSvc.BuildConnectorItems(doc, symbol, types, rule, staticCtc, crossConnect);

    /// <summary>
    /// Ensure a fitting has CTC assigned before insertion. Opens the setup dialog if needed.
    /// Returns false if the user cancels.
    /// </summary>
    public bool EnsureFittingCtcForInsert(
        Document doc, FittingCardItem fitting, ConnectorProxy staticConnector)
    {
        if (doc.IsModifiable) return true;

        var primary = fitting.PrimaryFitting;
        if (primary is null) return true;

        var symbol = CtcFamilyWriter.FindFamilySymbol(doc, primary.FamilyName, primary.SymbolName);
        if (symbol is null) return true;

        if (familyWriter.IsFittingCtcDefined(doc, symbol)) return true;

        var types = mappingRepo.GetConnectorTypes();
        if (types.Count == 0) return true;

        var items = BuildConnectorItems(doc, symbol, types, fitting.Rule, staticConnector.ConnectionTypeCode);

        SmartConLogger.Info($"[CTC] Fitting '{symbol.Family.Name}' ({symbol.Name}): CTC not set → dialog");

        if (!dialogSvc.ShowFittingCtcSetup(
                symbol.Family.Name, symbol.Name, items, types))
        {
            dialogSvc.ShowWarning(LocalizationService.GetString("App_Name"),
                LocalizationService.GetString("Msg_CtcNotAssigned"));
            return false;
        }

        familyWriter.ApplyFittingCtcToFamily(doc, symbol, items);
        return true;
    }

    /// <summary>
    /// Ensure a reducer has CTC assigned before insertion. Opens the setup dialog if needed.
    /// Returns false if the user cancels.
    /// </summary>
    public bool EnsureReducerCtcForInsert(
        Document doc, IReadOnlyList<FittingMappingRule> proposedFittings, ConnectorProxy staticConnector)
    {
        if (doc.IsModifiable) return true;

        var reducerRule = proposedFittings
            .FirstOrDefault(r => r.ReducerFamilies.Count > 0);
        if (reducerRule is null) return true;

        var reducerFam = reducerRule.ReducerFamilies[0];
        var symbol = CtcFamilyWriter.FindFamilySymbol(doc, reducerFam.FamilyName, reducerFam.SymbolName);
        if (symbol is null) return true;

        if (familyWriter.IsFittingCtcDefined(doc, symbol)) return true;

        var types = mappingRepo.GetConnectorTypes();
        if (types.Count == 0) return true;

        bool crossConnect = reducerRule.FromType.Value != reducerRule.ToType.Value;
        var items = BuildConnectorItems(doc, symbol, types, reducerRule, staticConnector.ConnectionTypeCode, crossConnect);

        SmartConLogger.Info($"[CTC] Reducer '{symbol.Family.Name}' ({symbol.Name}): CTC not set → dialog");

        if (!dialogSvc.ShowFittingCtcSetup(
                symbol.Family.Name, symbol.Name, items, types))
        {
            dialogSvc.ShowWarning(LocalizationService.GetString("App_Name"),
                LocalizationService.GetString("Msg_ReducerCtcNotAssigned"));
            return false;
        }

        familyWriter.ApplyFittingCtcToFamily(doc, symbol, items);
        return true;
    }

    /// <summary>Check if all connectors of a FamilySymbol have CTC defined.</summary>
    public bool IsFittingCtcDefined(Document doc, FamilySymbol symbol)
        => familyWriter.IsFittingCtcDefined(doc, symbol);

    /// <summary>Write CTC values to a family document and reload it.</summary>
    public void ApplyFittingCtcToFamily(
        Document doc, FamilySymbol symbol, List<FittingCtcSetupItem> items,
        ElementId? projectElementId = null)
        => familyWriter.ApplyFittingCtcToFamily(doc, symbol, items, projectElementId);

    /// <summary>Flush all pending virtual CTC writes to family documents.</summary>
    public void FlushVirtualCtcToFamilies(
        Document doc, ITransactionGroupSession? groupSession)
        => familyWriter.FlushVirtualCtcToFamilies(doc, groupSession);

    /// <summary>Find a FamilySymbol by family and symbol name (case-insensitive).</summary>
    public static FamilySymbol? FindFamilySymbol(Document doc, string familyName, string symbolName)
        => CtcFamilyWriter.FindFamilySymbol(doc, familyName, symbolName);
}
