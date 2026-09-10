using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Electrical;
using Autodesk.Revit.DB.Mechanical;
using Autodesk.Revit.DB.Plumbing;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Compatibility;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Реестр поддерживаемых системных категорий и их handlers для placement
/// в staged мини-проекте (ADR-027, Phase 1 + Phase 2).
/// Содержит ОДНО место для добавления/удаления категорий.
///
/// Все 16 категорий размещают инстансы. Версионная доступность задаётся
/// <see cref="SystemCategoryPlacementAvailability"/> (Core, единый источник
/// правды): потолки — Revit 2022+ (<c>Ceiling.Create</c>), ограждения —
/// Revit 2025+ (<c>Railing.Create</c> по <c>CurveLoop</c>). На версиях ниже
/// порога <see cref="GetPlacementHandler"/> возвращает <c>null</c>, а импорт
/// категории блокируется гейтом в UI.
///
/// Модель транзакций: handler САМ управляет транзакциями. Это жёсткое
/// требование <c>StairsEditScope</c> — его нельзя стартовать внутри активной
/// транзакции («can only be started when there is no transaction active»,
/// revitapidocs). Простые handler'ы открывают транзакцию через
/// <see cref="ITransactionService"/> (I-03), лестницы — через scope,
/// внутри которого транзакция сервиса легальна.
/// Видимость public — по прецеденту <c>TemplateCollisionResolver</c>:
/// интеграционные тесты (SmartCon.IntegrationTests) вызывают handler'ы напрямую.
/// </summary>
public static partial class SystemCategoryRegistry
{
    /// <summary>
    /// Размещает один инстанс типа в staged мини-проекте и возвращает его.
    /// <paramref name="start"/>/<paramref name="end"/> — ячейка сетки (2 м шаг,
    /// 1 м глубина) на уровне <paramref name="level"/>; handler'ы с контурной
    /// геометрией строят прямоугольник по двум углам (вырождение — до 1 м). Возвращает <c>null</c>,
    /// если размещение невозможно (несовпадение класса типа, версия API,
    /// откат транзакции — в т.ч. silent <c>Commit()==RolledBack</c> из #178,
    /// результаты <see cref="ITransactionService.RunInTransaction"/> проверяются).
    /// Возвращённый элемент создан в уже закоммиченной транзакции — используйте
    /// его ТОЛЬКО для чтения (Id, тип, связи); модификация вне транзакции = краш.
    /// </summary>
    public delegate Element? PlacementHandler(
        Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end);

    public sealed record Entry(
        BuiltInCategory Category,
        // Fallback-only display name (#192): the user-facing category name
        // ALWAYS comes from the document (typeElem.Category.Name — the
        // picker, AnalyzeActiveProject, the snapshot extractor). This value
        // is used only when the document name cannot be resolved, and in
        // log messages. Keep it equal to the official RU Revit category
        // name so the fallback never invents names of its own.
        string DisplayName,
        PlacementHandler? Handler);

    public static readonly IReadOnlyList<Entry> Entries = BuildEntries();

    /// <summary>
    /// Handler размещения категории, или <c>null</c>, если размещение
    /// недоступно на версии <paramref name="revitMajorVersion"/>
    /// (версионный гейт <see cref="SystemCategoryPlacementAvailability"/>).
    /// </summary>
    public static PlacementHandler? GetPlacementHandler(BuiltInCategory category, int revitMajorVersion)
    {
        if (!SystemCategoryPlacementAvailability.IsSupported(category, revitMajorVersion))
            return null;

        foreach (var e in Entries)
        {
            if (e.Category == category) return e.Handler;
        }
        return null;
    }

    /// <summary>
    /// Единый источник правды для набора поддерживаемых системных категорий.
    /// Используется:
    ///   - <see cref="Selection.AnyElementSelectionFilter"/> для фильтрации выбора в Revit UI
    ///   - <c>SystemFamilyRevitOperations.PickSelectedElements</c> для проверки
    ///     соответствия категории типа каноническому списку (defense in depth)
    /// </summary>
    public static readonly HashSet<BuiltInCategory> SupportedCategories =
        new(Entries.Select(e => e.Category));

    private static IReadOnlyList<Entry> BuildEntries()
    {
        // Unsupported categories — hidden ENTIRELY (never in this registry,
        // never in the picker, never in the batch dialog). Product decision
        // 2026-08-03 (ADR-027 §"Not supported"): no type-only fallbacks, no
        // per-category exceptions — a category is added only when it can be
        // supported end-to-end. Waiting for API:
        //   - OST_Ramps — no public Ramp.Create (verified revitapidocs
        //     2021-2026). Issue #198.
        //   - OST_CurtainWallPanels — panels are non-editable families
        //     (Family.IsEditable == false); excluded from the loadable
        //     scanner (#196). A system path needs a curtain-wall host.
        return new List<Entry>
        {
            new(BuiltInCategory.OST_PipeCurves,      "Трубы",        PlacePipe),
            new(BuiltInCategory.OST_FlexPipeCurves,  "Гибкие трубы", PlaceFlexPipe),
            new(BuiltInCategory.OST_DuctCurves,      "Воздуховоды",  PlaceDuct),
            new(BuiltInCategory.OST_FlexDuctCurves,  "Гибкие воздуховоды", PlaceFlexDuct),
            new(BuiltInCategory.OST_Conduit,         "Короба",       PlaceConduit),
            new(BuiltInCategory.OST_CableTray,       "Лотки",        PlaceCableTray),
            new(BuiltInCategory.OST_Walls,           "Стены",        PlaceWall),
            new(BuiltInCategory.OST_Floors,          "Перекрытия",   PlaceFloor),
            new(BuiltInCategory.OST_Roofs,           "Крыши",        PlaceRoof),
            new(BuiltInCategory.OST_Ceilings,        "Потолки",      PlaceCeiling),
            new(BuiltInCategory.OST_Stairs,          "Лестницы",     PlaceStairs),
            // #182: Railing instances AND RailingType live in OST_StairsRailing,
            // NOT OST_Railings (Tammik tbc/a/0619_retrieve_railings). Using
            // OST_Railings here made railings unpickable and invisible to
            // AnalyzeActiveProject.
            new(BuiltInCategory.OST_StairsRailing, "Ограждения",   PlaceRailing),
            new(BuiltInCategory.OST_PipeInsulations, "Материалы изоляции трубопроводов", PlacePipeInsulation),
            new(BuiltInCategory.OST_DuctInsulations, "Материалы изоляции воздуховодов", PlaceDuctInsulation),
            // #197: lining (внутренняя изоляция) — host-required как и
            // наружная изоляция; DuctLining : InsulationLiningBase, поэтому
            // InsulationHostFilter покрывает lining-хосты автоматически.
            new(BuiltInCategory.OST_DuctLinings, "Материалы футеровки воздуховодов", PlaceDuctLining),
            // #199: Wire.Create (Since 2015) требует viewId плана этажа/RCP —
            // handler переиспользует существующий план уровня или создаёт
            // его (ViewPlan.Create).
            new(BuiltInCategory.OST_Wire,          "Провода",      PlaceWire),
        };
    }

    // ── Геометрия-хелперы ───────────────────────────────────────────────

    /// <summary>
    /// Прямоугольный замкнутый контур между двумя углами (плоскость Z первой
    /// точки). #200: (start, end) — противоположные углы, так что pick-активация
    /// «по области» передаёт реальные размеры; вырожденные/совпадающие точки
    /// расширяются до 1 м (staging-сетка и защита от нулевой площади).
    /// </summary>
    private static CurveLoop BuildRectangularLoop(XYZ corner1, XYZ corner2)
    {
        var sizeFt = RevitUnitsCompat.MetersToInternal(1.0);
        var minX = Math.Min(corner1.X, corner2.X);
        var minY = Math.Min(corner1.Y, corner2.Y);
        var maxX = Math.Max(corner1.X, corner2.X);
        var maxY = Math.Max(corner1.Y, corner2.Y);
        if (maxX - minX < 1e-6) maxX = minX + sizeFt;
        if (maxY - minY < 1e-6) maxY = minY + sizeFt;
        var z = corner1.Z;
        var p1 = new XYZ(minX, minY, z);
        var p2 = new XYZ(maxX, minY, z);
        var p3 = new XYZ(maxX, maxY, z);
        var p4 = new XYZ(minX, maxY, z);
        var loop = new CurveLoop();
        loop.Append(Line.CreateBound(p1, p2));
        loop.Append(Line.CreateBound(p2, p3));
        loop.Append(Line.CreateBound(p3, p4));
        loop.Append(Line.CreateBound(p4, p1));
        return loop;
    }

    /// <summary>Конвертация CurveLoop в legacy CurveArray для Creation.Document API (R19–R21).</summary>
    private static CurveArray ToCurveArray(CurveLoop loop)
    {
        var array = new CurveArray();
        foreach (var curve in loop)
        {
            array.Append(curve);
        }
        return array;
    }

    // ── MEP линейные (Phase 1) ──────────────────────────────────────────

    /// <summary>Существующий план этажа уровня, иначе новый (null — нет FloorPlan ViewFamilyType).</summary>
    private static ViewPlan? FindOrCreateFloorPlanView(Document doc, Level level)
    {
        var existing = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewPlan))
            .Cast<ViewPlan>()
            .FirstOrDefault(v => !v.IsTemplate
                && v.ViewType == ViewType.FloorPlan
                && v.GenLevel is not null
                && v.GenLevel.Id == level.Id);
        if (existing is not null) return existing;

        var viewFamilyType = new FilteredElementCollector(doc)
            .OfClass(typeof(ViewFamilyType))
            .Cast<ViewFamilyType>()
            .FirstOrDefault(v => v.ViewFamily == ViewFamily.FloorPlan);
        return viewFamilyType is null ? null : ViewPlan.Create(doc, viewFamilyType.Id, level.Id);
    }
}
