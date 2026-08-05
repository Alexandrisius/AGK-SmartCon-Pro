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
public static class SystemCategoryRegistry
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

    private static Element? PlacePipe(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not PipeType pipeType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place pipe", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .First();
            created = Pipe.Create(d, sysType.Id, pipeType.Id, level.Id, start, end);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>
    /// <see cref="FlexPipe.Create"/> требует <see cref="FlexPipeType"/> и массив
    /// точек сплайна с касательными — обычный <see cref="PlacePipe"/> не подходит.
    /// </summary>
    private static Element? PlaceFlexPipe(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not FlexPipeType flexPipeType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place flex pipe", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .First();
            var points = new List<XYZ> { start, end };
            var tangent = XYZ.BasisX;
            created = FlexPipe.Create(d, sysType.Id, flexPipeType.Id, level.Id, tangent, tangent, points);
        }))
        {
            return null;
        }
        return created;
    }

    private static Element? PlaceDuct(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not DuctType ductType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place duct", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .First();
            created = Duct.Create(d, sysType.Id, ductType.Id, level.Id, start, end);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>Зеркалит <see cref="PlaceFlexPipe"/> для воздуховодов.</summary>
    private static Element? PlaceFlexDuct(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not FlexDuctType flexDuctType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place flex duct", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .First();
            var points = new List<XYZ> { start, end };
            var tangent = XYZ.BasisX;
            created = FlexDuct.Create(d, sysType.Id, flexDuctType.Id, level.Id, tangent, tangent, points);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>
    /// Reflection lookup of the 2022+ <c>Create(Document, IList&lt;CurveLoop&gt;, ElementId, ElementId)</c>
    /// for sketch hosts (Floor/Ceiling) in an R21-compiled binary. Exact
    /// binder match first; on failure — manual scan (name + 4 params) with a
    /// Warn dump of the candidates, so the next staging failure log carries
    /// the ground truth instead of a silent legacy fallback (2026-08-05:
    /// Floor lookup returned null on Revit 2023 while the identical Ceiling
    /// lookup succeeded — cause still unknown, this instrumentation is the
    /// way to see it).
    /// </summary>
    private static MethodInfo? FindModernCreateMethod(Type hostType, string label)
    {
        var exactTypes = new[] { typeof(Document), typeof(IList<CurveLoop>), typeof(ElementId), typeof(ElementId) };
        var exact = hostType.GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.Static,
            null,
            exactTypes,
            null);
        if (exact is not null) return exact;

        var candidates = hostType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == "Create")
            .ToList();
        SmartConLogger.Warn(
            $"{label}.Create exact reflection lookup returned null (Revit runtime {exactTypes[0].Assembly.GetName().Version}). " +
            $"Static 'Create' candidates: [{string.Join("; ", candidates.Select(m => m.ToString()))}]. " +
            "[Action: пришлите этот лог разработчикам — он показывает, что видит reflection в вашем Revit]");
        return candidates.FirstOrDefault(m =>
            m.GetParameters().Length == 4
            && m.GetParameters()[0].ParameterType == typeof(Document)
            && m.GetParameters()[1].ParameterType.IsGenericType
            && m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(IList<>));
    }

#if !REVIT2022_OR_GREATER
    [System.Runtime.CompilerServices.MethodImpl(
        System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Element? PlaceFloorLegacy(Document doc, CurveLoop loop, FloorType floorType, Level level)
        => doc.Create.NewFloor(ToCurveArray(loop), floorType, level, structural: false);
#endif

    private static Element? PlaceConduit(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        var assembly = doc.GetType().Assembly;
        var conduitType = assembly.GetType("Autodesk.Revit.DB.Electrical.ConduitType");
        var conduit = assembly.GetType("Autodesk.Revit.DB.Electrical.Conduit");
        if (conduitType is null || conduit is null) return null;
        if (!conduitType.IsInstanceOfType(type)) return null;

        var createMethod = conduit.GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Document), typeof(ElementId), typeof(XYZ), typeof(XYZ), typeof(ElementId) },
            null);
        if (createMethod is null) return null;

        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place conduit", d =>
        {
            try
            {
                created = createMethod.Invoke(null, new object[] { d, type.Id, start, end, level.Id }) as Element;
            }
            catch (TargetInvocationException tex)
            {
                SmartConLogger.Warn($"{tex.InnerException?.Message ?? tex.Message} [Action: проверьте, что тип короба/лотка валиден для двухточечного размещения]");
            }
        }))
        {
            return null;
        }
        return created;
    }

    private static Element? PlaceCableTray(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        var assembly = doc.GetType().Assembly;
        var cableTrayType = assembly.GetType("Autodesk.Revit.DB.Electrical.CableTrayType");
        var cableTray = assembly.GetType("Autodesk.Revit.DB.Electrical.CableTray");
        if (cableTrayType is null || cableTray is null) return null;
        if (!cableTrayType.IsInstanceOfType(type)) return null;

        var createMethod = cableTray.GetMethod(
            "Create",
            BindingFlags.Public | BindingFlags.Static,
            null,
            new[] { typeof(Document), typeof(ElementId), typeof(XYZ), typeof(XYZ), typeof(ElementId) },
            null);
        if (createMethod is null) return null;

        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place cable tray", d =>
        {
            try
            {
                created = createMethod.Invoke(null, new object[] { d, type.Id, start, end, level.Id }) as Element;
            }
            catch (TargetInvocationException tex)
            {
                SmartConLogger.Warn($"{tex.InnerException?.Message ?? tex.Message} [Action: проверьте, что тип короба/лотка валиден для двухточечного размещения]");
            }
        }))
        {
            return null;
        }
        return created;
    }

    // ── Стены (Phase 1) ─────────────────────────────────────────────────

    private static Element? PlaceWall(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not WallType wallType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place wall", d =>
        {
            var line = Line.CreateBound(start, end);
            var height = RevitUnitsCompat.MetersToInternal(3.0);
            created = Wall.Create(d, line, wallType.Id, level.Id, height, 0.0, false, false);
        }))
        {
            return null;
        }
        return created;
    }

    // ── Слоистые контурные (Phase 2) ────────────────────────────────────

    /// <summary>
    /// Перекрытие по двум углам на уровне (#200: в staging — ячейка сетки,
    /// в pick-активации — реальные углы). <c>Floor.Create</c> — Revit 2022+;
    /// для R19–R21 — legacy <c>Creation.Document.NewFloor</c> (в Revit 2024+
    /// уже нерабочий — подтверждено Autodesk forum 12954276, поэтому ветки
    /// строго по <c>REVIT2022_OR_GREATER</c>).
    /// </summary>
    private static Element? PlaceFloor(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not FloorType floorType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place floor", d =>
        {
            var loop = BuildRectangularLoop(start, end);
#if REVIT2022_OR_GREATER
            created = Floor.Create(d, new List<CurveLoop> { loop }, floorType.Id, level.Id);
#else
            // Stress test 2026-08-05 (2023 runtime): the R21 binary ships to
            // Revit 2022/2023 where NewFloor was REMOVED — a direct call is a
            // MissingMethodException at runtime. Reflection picks the modern
            // API when it exists (2022+), legacy NewFloor only on true 2021.
            var createMethod = FindModernCreateMethod(typeof(Floor), "Floor");
            if (createMethod is not null)
            {
                try
                {
                    created = createMethod.Invoke(
                        null, new object[] { d, new List<CurveLoop> { loop }, floorType.Id, level.Id }) as Element;
                }
                catch (TargetInvocationException tex)
                {
                    SmartConLogger.Warn(
                        $"Floor.Create failed: {tex.InnerException?.Message ?? tex.Message} " +
                        "[Action: проверьте уровень и тип перекрытия; повторите импорт категории]");
                }
            }
            else
            {
                SmartConLogger.Warn(
                    "Floor.Create not found by reflection — falling back to legacy NewFloor (valid only on Revit 2021). " +
                    "[Action: если это Revit 2022+, пришлите лог — в нём дамп кандидатов Create]");
                // NoInlining-вынос ОБЯЗАТЕЛЕН (2026-08-05): прямая ссылка на
                // удалённый NewFloor в теле лямбды убивает JIT ВСЕГО метода
                // на Revit 2022+ (MissingMethodException до выполнения
                // любой строки, включая reflection lookup выше).
                created = PlaceFloorLegacy(d, loop, floorType, level);
            }
#endif
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>
    /// Двускатная крыша 1×1 м вытягиванием открытого профиля.
    /// <c>NewFootPrintRoof</c> непригоден: старый Creation-метод требует
    /// UI-контекст и бросает <c>Autodesk.Revit.Exceptions.ArgumentNullException</c>
    /// в фоновом документе (staged мини-проект никогда не активен — проверено
    /// зондом в реальном Revit). <c>NewExtrusionRoof</c> с ОТКРЫТЫМ профилем
    /// (замкнутый контур отклоняется «Invalid profile») работает в фоновом
    /// документе на всех версиях. ReferencePlane строится на первом ViewPlan
    /// шаблона (<c>doc.ActiveView</c> у фонового документа = null).
    /// </summary>
    private static Element? PlaceRoof(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not RoofType roofType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place roof", d =>
        {
            var viewPlan = new FilteredElementCollector(d)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .First();

            var sizeFt = RevitUnitsCompat.MetersToInternal(1.0);
            var riseFt = RevitUnitsCompat.MetersToInternal(0.3);

            // Вертикальная рабочая плоскость через ячейку (нормаль +X):
            // bubbleEnd/freeEnd задают ось Z плоскости, cutVec — её ось Y.
            var refPlane = d.Create.NewReferencePlane(
                start,
                new XYZ(start.X, start.Y, start.Z + sizeFt),
                XYZ.BasisY,
                viewPlan);

            // Открытый двускатный профиль в плоскости YZ.
            var p1 = start;
            var p2 = new XYZ(start.X, start.Y + sizeFt / 2, start.Z + riseFt);
            var p3 = new XYZ(start.X, start.Y + sizeFt, start.Z);
            var profile = new CurveArray();
            profile.Append(Line.CreateBound(p1, p2));
            profile.Append(Line.CreateBound(p2, p3));

            created = d.Create.NewExtrusionRoof(profile, refPlane, level, roofType, 0, sizeFt);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>
    /// Потолок по двум углам (#200). <c>Ceiling.Create</c> существует только с Revit 2022
    /// (раньше API создания потолков не было вообще). На R19–R21 handler
    /// недостижим — версионный гейт
    /// <see cref="SystemCategoryPlacementAvailability"/> отсекает вызов.
    /// </summary>
    private static Element? PlaceCeiling(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
#if REVIT2022_OR_GREATER
        if (type is not CeilingType ceilingType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place ceiling", d =>
        {
            var loop = BuildRectangularLoop(start, end);
            created = Ceiling.Create(d, new List<CurveLoop> { loop }, ceilingType.Id, level.Id);
        }))
        {
            return null;
        }
        return created;
#else
        // Stress test 2026-08-05 (2023 runtime): the R21 binary has no
        // REVIT2022_OR_GREATER symbol and used to return null SILENTLY —
        // ceilings never landed in the staged mini-project. Reflection
        // finds Ceiling.Create when the runtime Revit has it (2022+); on a
        // true 2021 the version gate already blocks the call, but a Warn
        // replaces the silent failure if we ever get here.
        if (type is not CeilingType ceilingType) return null;
        var createMethod = FindModernCreateMethod(typeof(Ceiling), "Ceiling");
        if (createMethod is null)
        {
            SmartConLogger.Warn(
                "Ceiling.Create is unavailable in this Revit (API added in 2022) — ceiling type NOT placed in the mini-project. " +
                "[Action: используйте Revit 2022+ для импорта потолков в каталог]");
            return null;
        }

        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place ceiling", d =>
        {
            var loop = BuildRectangularLoop(start, end);
            try
            {
                created = createMethod.Invoke(
                    null, new object[] { d, new List<CurveLoop> { loop }, ceilingType.Id, level.Id }) as Element;
            }
            catch (TargetInvocationException tex)
            {
                SmartConLogger.Warn(
                    $"Ceiling.Create failed: {tex.InnerException?.Message ?? tex.Message} " +
                    "[Action: проверьте уровень и тип потолка; повторите импорт категории]");
            }
        }))
        {
            return null;
        }
        return created;
#endif
    }

    // ── Лестницы и ограждения (Phase 2) ─────────────────────────────────

    /// <summary>
    /// Лестница конкретного типа одним прямым маршем между уровнями.
    /// <c>StairsEditScope</c> нельзя стартовать внутри активной транзакции,
    /// поэтому handler управляет scope сам: <c>Start</c> создаёт пустую
    /// лестницу с дефолтным типом, затем <c>ChangeTypeId</c> переключает на
    /// целевой, марш строится <c>StairsRun.CreateStraightRun</c> с длиной,
    /// вычисленной из высоты уровней и <c>MaxRiserHeight</c>/<c>MinTreadDepth</c>
    /// типа — иначе Revit добивает марш лишними ступенями и предупреждает
    /// о переполнении верхнего уровня. Дефолтные ограждения, создаваемые
    /// <c>StairsEditScope.Start</c>, удаляются — эталон содержит только
    /// целевой тип лестницы.
    /// </summary>
    private static Element? PlaceStairs(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not StairsType stairsType) return null;

        var topLevel = FindLevelAbove(doc, level);
        if (topLevel is null)
        {
            if (!txService.RunInTransaction(doc, "Create top level", d =>
            {
                topLevel = Level.Create(d, level.Elevation + RevitUnitsCompat.MetersToInternal(4.0));
            }))
            {
                return null;
            }
        }

        ElementId stairsId;
        using (var scope = new StairsEditScope(doc, "SmartCon_StairPlacement"))
        {
            stairsId = scope.Start(level.Id, topLevel!.Id);

            // Откат «Place stair run» (silent RolledBack, #178): scope НЕ
            // коммитим — Dispose отменит его, пустая лестница не останется.
            if (!txService.RunInTransaction(doc, "Place stair run", d =>
            {
                var stairs = (Stairs)d.GetElement(stairsId);
                stairs.ChangeTypeId(stairsType.Id);

                var heightFt = topLevel.Elevation - level.Elevation;
                var risers = Math.Max(2, (int)Math.Ceiling(heightFt / stairsType.MaxRiserHeight));
                var runLengthFt = (risers - 1) * stairsType.MinTreadDepth;
                var runLine = Line.CreateBound(
                    start,
                    new XYZ(start.X + runLengthFt, start.Y, level.Elevation));
                var run = StairsRun.CreateStraightRun(d, stairsId, runLine, StairsRunJustification.Center);
                run.EndsWithRiser = true;
            }))
            {
                return null;
            }

            scope.Commit(new StairsFailuresPreprocessor());
        }

        // Дефолтные ограждения StairsEditScope материализуются только при
        // scope.Commit — удалять их надо ПОСЛЕ коммита scope (внутри scope
        // GetAssociatedRailings ещё пуст).
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Remove default railings", d =>
        {
            var stairs = (Stairs)d.GetElement(stairsId);
            foreach (var railingId in stairs.GetAssociatedRailings())
            {
                d.Delete(railingId);
            }
            created = stairs;
        }))
        {
            return null;
        }

        return created;
    }

    /// <summary>Ближайший уровень выше базового (для верхней привязки лестницы).</summary>
    private static Level? FindLevelAbove(Document doc, Level baseLevel)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(Level))
            .Cast<Level>()
            .Where(l => l.Elevation > baseLevel.Elevation + 1e-6)
            .OrderBy(l => l.Elevation)
            .FirstOrDefault();
    }

    /// <summary>
    /// Ограждение по ОТКРЫТОМУ линейному пути (start→end) на уровне (#200).
    /// Ограждение — path-based элемент (как труба): типичное использование —
    /// линия балкона/лестницы, а не замкнутая область. Одиночный Line
    /// валиден для <c>Railing.IsValidPathForRailing</c> (revitapidocs:
    /// "continuous, lines or arcs only, max two curves meet in one end point").
    /// <c>Railing.Create(Document, CurveLoop, …)</c> — только Revit 2025+
    /// (host-вариант требует лестницу/пандус и засорял бы эталон чужим
    /// элементом — отклонено). На R19–R24 handler недостижим — версионный
    /// гейт отсекает вызов.
    /// </summary>
    private static Element? PlaceRailing(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
#if REVIT2025_OR_GREATER
        if (type is not RailingType railingType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place railing", d =>
        {
            if (start.DistanceTo(end) < 1e-6) return;
            var path = new CurveLoop();
            path.Append(Line.CreateBound(start, end));
            if (!Railing.IsValidPathForRailing(path)) return;
            created = Railing.Create(d, path, railingType.Id, level.Id);
        }))
        {
            return null;
        }
        return created;
#else
        return null;
#endif
    }

    // ── Изоляция (Phase 2) ──────────────────────────────────────────────

    /// <summary>
    /// Изоляция трубы. <c>PipeInsulation.Create</c> требует host
    /// (труба/фитинг/аксессуар) — в мини-проекте изоляции хоста нет,
    /// поэтому в той же транзакции размещается метровая труба первого
    /// шаблонного типа (базовые типы системных семейств присутствуют в любом
    /// проекте и неудаляемы — инвариант продукта). Хост безвреден для
    /// extraction/хэша: они фильтруют по категории изоляций.
    /// </summary>
    private static Element? PlacePipeInsulation(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not PipeInsulationType insulationType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place pipe insulation", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .First();
            var pipeType = new FilteredElementCollector(d)
                .OfClass(typeof(PipeType))
                .Cast<PipeType>()
                .First();
            var pipe = Pipe.Create(d, sysType.Id, pipeType.Id, level.Id, start, end);
            var thicknessFt = RevitUnitsCompat.MetersToInternal(0.025);
            created = PipeInsulation.Create(d, pipe.Id, insulationType.Id, thicknessFt);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>Зеркалит <see cref="PlacePipeInsulation"/> для воздуховодов.</summary>
    private static Element? PlaceDuctInsulation(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not DuctInsulationType insulationType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place duct insulation", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .First();
            var ductType = new FilteredElementCollector(d)
                .OfClass(typeof(DuctType))
                .Cast<DuctType>()
                .First();
            var duct = Duct.Create(d, sysType.Id, ductType.Id, level.Id, start, end);
            var thicknessFt = RevitUnitsCompat.MetersToInternal(0.025);
            created = DuctInsulation.Create(d, duct.Id, insulationType.Id, thicknessFt);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>
    /// Футеровка воздуховода (#197). <c>DuctLining.Create</c> требует host
    /// (воздуховод/фитинг/аксессуар) — как и у наружной изоляции, в той же
    /// транзакции размещается метровый воздуховод первого шаблонного типа.
    /// </summary>
    private static Element? PlaceDuctLining(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not DuctLiningType liningType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place duct lining", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .First();
            var ductType = new FilteredElementCollector(d)
                .OfClass(typeof(DuctType))
                .Cast<DuctType>()
                .First();
            var duct = Duct.Create(d, sysType.Id, ductType.Id, level.Id, start, end);
            var thicknessFt = RevitUnitsCompat.MetersToInternal(0.025);
            created = DuctLining.Create(d, duct.Id, liningType.Id, thicknessFt);
        }))
        {
            return null;
        }
        return created;
    }

    /// <summary>
    /// Провод (#199). <c>Wire.Create</c> (Since 2015) требует id вида —
    /// только план этажа или RCP. В мини-проекте плана может не быть →
    /// переиспользуем существующий план уровня или создаём
    /// (<c>ViewPlan.Create</c> по ViewFamilyType с ViewFamily.FloorPlan).
    /// <c>WiringType.Chamfer</c> — полилиния через все вершины; коннекторы
    /// null (свободный провод).
    /// </summary>
    private static Element? PlaceWire(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not WireType wireType) return null;
        Element? created = null;
        if (!txService.RunInTransaction(doc, "Place wire", d =>
        {
            var view = FindOrCreateFloorPlanView(d, level);
            if (view is null) return;
            created = Wire.Create(d, wireType.Id, view.Id, WiringType.Chamfer,
                new List<XYZ> { start, end }, null, null);
        }))
        {
            return null;
        }
        return created;
    }

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
