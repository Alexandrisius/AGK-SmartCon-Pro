using Autodesk.Revit.DB;

namespace SmartCon.Core.Compatibility;

/// <summary>
/// Абстракция над <see cref="Category"/> для совместимости Revit 2019-2021 и 2022+.
/// Revit 2019-2021: свойства <c>Category.BuiltInCategory</c> нет — нужен cast из <c>Id</c>.
/// Revit 2022+:     <c>Category.BuiltInCategory</c> возвращает корректный enum или INVALID
///                  для custom sub-category. Cast из <c>Id</c> опасен коллизиями ID.
/// </summary>
/// <remarks>
/// <para>Cast-based подход (<c>(BuiltInCategory)(int)catId.IntegerValue</c>) фундаментально
/// хрупок: ID custom sub-category может случайно совпасть с реальным enum-значением,
/// и placement пойдёт в чужую категорию. <c>Category.BuiltInCategory</c> — единственный
/// API-поддерживаемый способ получить корректное значение.</para>
///
/// <para>Для R19/R21, где свойства нет, мы используем cast как fallback, но с try/catch —
/// коллизии, дающие невалидный enum-значение, будут пойманы в вызывающем коде проверкой
/// на принадлежность к <see cref="SmartCon.Revit.FamilyManager.SystemCategoryRegistry.SupportedCategories"/>.</para>
/// </remarks>
public static class CategoryCompat
{
#if REVIT2022_OR_GREATER
    /// <summary>
    /// Получить <see cref="BuiltInCategory"/> из <see cref="Category"/>.
    /// Возвращает <see cref="BuiltInCategory.INVALID"/> для custom sub-category
    /// и для <c>null</c>.
    /// </summary>
    public static BuiltInCategory GetBuiltInCategory(Category? category) =>
        category?.BuiltInCategory ?? BuiltInCategory.INVALID;
#else
    /// <summary>
    /// Получить <see cref="BuiltInCategory"/> из <see cref="Category"/> через cast
    /// <c>(BuiltInCategory)(int)catId.IntegerValue</c>. Доступно во всех версиях Revit,
    /// но опасно коллизиями ID для custom sub-category.
    /// </summary>
    public static BuiltInCategory GetBuiltInCategory(Category? category)
    {
        if (category?.Id is not { } catId) return BuiltInCategory.INVALID;
        try
        {
            return (BuiltInCategory)(int)catId.GetValue();
        }
        catch
        {
            return BuiltInCategory.INVALID;
        }
    }
#endif
}
