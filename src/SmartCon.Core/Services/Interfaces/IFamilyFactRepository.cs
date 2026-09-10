using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Read access to the family-facts subsystem (ADR-055): the item's Revit
/// category ordinal plus the facts extracted per <see cref="FamilyFactRuleSet"/>.
/// Backed by <c>catalog_items.revit_category_id</c> and the
/// <c>family_facts</c> table (schema V22).
/// </summary>
public interface IFamilyFactRepository
{
    /// <summary>
    /// Loads the category ordinal and all facts of one catalog item for the
    /// properties window. Missing column rows (pre-V22 item that has not
    /// been actualized yet) surface as a <c>null</c> category id and an
    /// empty fact list — the UI hides the fact rows in that case.
    /// </summary>
    Task<FamilyFactsData> GetForItemAsync(string catalogItemId, CancellationToken ct = default);
}
