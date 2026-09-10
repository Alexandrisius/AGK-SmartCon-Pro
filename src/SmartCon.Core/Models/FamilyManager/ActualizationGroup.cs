namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Work unit of the actualization engine (ADR-054): one
/// (catalog_item, version_label) group with ALL its Revit variants.
/// <see cref="Key"/> is the detection key shared with the actualization
/// tasks (<c>catalogItemId|versionLabel</c>).
/// </summary>
public sealed record ActualizationGroup(
    string CatalogItemId,
    string ItemName,
    string VersionLabel,
    bool IsActiveLabel,
    IReadOnlyList<ActualizationVariant> Variants)
{
    public string Key => CatalogItemId + "|" + VersionLabel;
}
