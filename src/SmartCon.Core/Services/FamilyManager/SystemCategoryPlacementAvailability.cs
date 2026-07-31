using Autodesk.Revit.DB;

namespace SmartCon.Core.Services.FamilyManager;

/// <summary>
/// Версионный гейт размещения инстансов системных категорий в staged
/// мини-проекте (ADR-027 Phase 2). Хранит минимальную версию Revit, с которой
/// доступен placement API категории: потолки — Revit 2022 (<c>Ceiling.Create</c>),
/// ограждения — Revit 2025 (<c>Railing.Create</c> по <c>CurveLoop</c>).
/// Категории вне таблицы размещаются на всех поддерживаемых версиях.
/// Единый источник правды для реестра размещения (SmartCon.Revit) и для
/// импортного гейта UI: категория, недоступная на текущей версии Revit,
/// не импортируется в библиотеку.
/// </summary>
public static class SystemCategoryPlacementAvailability
{
    private static readonly IReadOnlyDictionary<BuiltInCategory, int> MinVersions =
        new Dictionary<BuiltInCategory, int>
        {
            { BuiltInCategory.OST_Ceilings, 2022 },
            { BuiltInCategory.OST_Railings, 2025 },
        };

    /// <summary>
    /// Минимальная версия Revit для размещения категории, или <c>null</c>,
    /// если размещение доступно на всех версиях.
    /// </summary>
    public static int? GetMinRevitVersion(BuiltInCategory category)
        => MinVersions.TryGetValue(category, out var min) ? min : null;

    /// <summary>Размещение категории доступно на указанной версии Revit.</summary>
    public static bool IsSupported(BuiltInCategory category, int revitMajorVersion)
        => GetMinRevitVersion(category) is not { } min || revitMajorVersion >= min;
}
