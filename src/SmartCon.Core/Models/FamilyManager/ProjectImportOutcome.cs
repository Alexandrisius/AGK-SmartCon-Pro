namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One catalog item touched by a project import run ("Импорт активного файла"
/// / "Импорт выделенных элементов"). Carries enough data for the post-import
/// stale check (#185) without re-querying the database.
/// </summary>
public sealed record ImportedCatalogItem(
    string CatalogItemId,
    string DisplayName,
    string FamilySource);

/// <summary>
/// Result of <c>ProcessProjectImportAsync</c>. <see cref="ImportStarted"/> is
/// false when the user cancelled the batch dialog before pressing Import —
/// callers must NOT run post-import actions (mini-project close #186, stale
/// check #185) in that case.
/// </summary>
public sealed record ProjectImportOutcome(
    bool ImportStarted,
    int SuccessCount,
    IReadOnlyList<ImportedCatalogItem> ImportedItems)
{
    public static readonly ProjectImportOutcome NotStarted = new(false, 0, []);
}
