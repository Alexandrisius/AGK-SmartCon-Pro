namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Снимок расширенного поиска (#87): категория-охват (поддерево каталога,
/// null = все категории) + условия по атрибутам, объединённые по «И».
/// Комбинируется с обычным поиском по имени тоже по «И».
/// </summary>
public sealed record AdvancedSearchFilter(
    string? CategoryId,
    IReadOnlyList<AttributeFilterCondition> Conditions)
{
    public static AdvancedSearchFilter Empty { get; } = new(null, []);

    /// <summary>Фильтр ничего не сужает — дерево строится без него.</summary>
    public bool IsEmpty =>
        string.IsNullOrEmpty(CategoryId) && Conditions.Count == 0;
}
