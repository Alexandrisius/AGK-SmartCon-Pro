using System.Reflection;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
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
/// Все 14 категорий размещают инстансы. Версионная доступность задаётся
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
/// </summary>
internal static class SystemCategoryRegistry
{
    /// <summary>
    /// Размещает один инстанс типа в staged мини-проекте и возвращает его.
    /// <paramref name="start"/>/<paramref name="end"/> — ячейка сетки (2 м шаг,
    /// 1 м глубина) на уровне <paramref name="level"/>; handler'ы с контурной
    /// геометрией строят из неё прямоугольник 1×1 м. Возвращает <c>null</c>,
    /// если размещение невозможно (несовпадение класса типа, версия API).
    /// </summary>
    public delegate Element? PlacementHandler(
        Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end);

    public sealed record Entry(
        BuiltInCategory Category,
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
            new(BuiltInCategory.OST_Railings,        "Ограждения",   PlaceRailing),
            new(BuiltInCategory.OST_PipeInsulations, "Изоляция труб", PlacePipeInsulation),
            new(BuiltInCategory.OST_DuctInsulations, "Изоляция воздуховодов", PlaceDuctInsulation),
        };
    }

    // ── Геометрия-хелперы ───────────────────────────────────────────────

    /// <summary>Прямоугольный замкнутый контур 1×1 м от точки ячейки (в её плоскости).</summary>
    private static CurveLoop BuildRectangularLoop(XYZ origin)
    {
        var sizeFt = RevitUnitsCompat.MetersToInternal(1.0);
        var p1 = origin;
        var p2 = new XYZ(origin.X + sizeFt, origin.Y, origin.Z);
        var p3 = new XYZ(origin.X + sizeFt, origin.Y + sizeFt, origin.Z);
        var p4 = new XYZ(origin.X, origin.Y + sizeFt, origin.Z);
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
        txService.RunInTransaction(doc, "Place pipe", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .First();
            created = Pipe.Create(d, sysType.Id, pipeType.Id, level.Id, start, end);
        });
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
        txService.RunInTransaction(doc, "Place flex pipe", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(PipingSystemType))
                .Cast<PipingSystemType>()
                .First();
            var points = new List<XYZ> { start, end };
            var tangent = XYZ.BasisX;
            created = FlexPipe.Create(d, sysType.Id, flexPipeType.Id, level.Id, tangent, tangent, points);
        });
        return created;
    }

    private static Element? PlaceDuct(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not DuctType ductType) return null;
        Element? created = null;
        txService.RunInTransaction(doc, "Place duct", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .First();
            created = Duct.Create(d, sysType.Id, ductType.Id, level.Id, start, end);
        });
        return created;
    }

    /// <summary>Зеркалит <see cref="PlaceFlexPipe"/> для воздуховодов.</summary>
    private static Element? PlaceFlexDuct(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not FlexDuctType flexDuctType) return null;
        Element? created = null;
        txService.RunInTransaction(doc, "Place flex duct", d =>
        {
            var sysType = new FilteredElementCollector(d)
                .OfClass(typeof(MechanicalSystemType))
                .Cast<MechanicalSystemType>()
                .First();
            var points = new List<XYZ> { start, end };
            var tangent = XYZ.BasisX;
            created = FlexDuct.Create(d, sysType.Id, flexDuctType.Id, level.Id, tangent, tangent, points);
        });
        return created;
    }

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
        txService.RunInTransaction(doc, "Place conduit", d =>
        {
            try
            {
                created = createMethod.Invoke(null, new object[] { d, type.Id, start, end, level.Id }) as Element;
            }
            catch (TargetInvocationException tex)
            {
                SmartConLogger.Warn($"{tex.InnerException?.Message ?? tex.Message}");
            }
        });
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
        txService.RunInTransaction(doc, "Place cable tray", d =>
        {
            try
            {
                created = createMethod.Invoke(null, new object[] { d, type.Id, start, end, level.Id }) as Element;
            }
            catch (TargetInvocationException tex)
            {
                SmartConLogger.Warn($"{tex.InnerException?.Message ?? tex.Message}");
            }
        });
        return created;
    }

    // ── Стены (Phase 1) ─────────────────────────────────────────────────

    private static Element? PlaceWall(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not WallType wallType) return null;
        Element? created = null;
        txService.RunInTransaction(doc, "Place wall", d =>
        {
            var line = Line.CreateBound(start, end);
            var height = RevitUnitsCompat.MetersToInternal(3.0);
            created = Wall.Create(d, line, wallType.Id, level.Id, height, 0.0, false, false);
        });
        return created;
    }

    // ── Слоистые контурные (Phase 2) ────────────────────────────────────

    /// <summary>
    /// Перекрытие 1×1 м на уровне. <c>Floor.Create</c> — Revit 2022+;
    /// для R19–R21 — legacy <c>Creation.Document.NewFloor</c> (в Revit 2024+
    /// уже нерабочий — подтверждено Autodesk forum 12954276, поэтому ветки
    /// строго по <c>REVIT2022_OR_GREATER</c>).
    /// </summary>
    private static Element? PlaceFloor(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not FloorType floorType) return null;
        Element? created = null;
        txService.RunInTransaction(doc, "Place floor", d =>
        {
            var loop = BuildRectangularLoop(start);
#if REVIT2022_OR_GREATER
            created = Floor.Create(d, new List<CurveLoop> { loop }, floorType.Id, level.Id);
#else
            created = d.Create.NewFloor(ToCurveArray(loop), floorType, level, structural: false);
#endif
        });
        return created;
    }

    /// <summary>
    /// Плоская крыша по контуру 1×1 м. <c>Creation.Document.NewFootPrintRoof</c>
    /// доступен на всех поддерживаемых версиях; скат не задаём
    /// (<c>DefinesSlope</c> по умолчанию отключён — эталону достаточно
    /// плоского экземпляра).
    /// </summary>
    private static Element? PlaceRoof(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not RoofType roofType) return null;
        Element? created = null;
        txService.RunInTransaction(doc, "Place roof", d =>
        {
            var footPrint = ToCurveArray(BuildRectangularLoop(start));
            created = d.Create.NewFootPrintRoof(footPrint, level, roofType, out _);
        });
        return created;
    }

    /// <summary>
    /// Потолок 1×1 м. <c>Ceiling.Create</c> существует только с Revit 2022
    /// (раньше API создания потолков не было вообще). На R19–R21 handler
    /// недостижим — версионный гейт
    /// <see cref="SystemCategoryPlacementAvailability"/> отсекает вызов.
    /// </summary>
    private static Element? PlaceCeiling(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
#if REVIT2022_OR_GREATER
        if (type is not CeilingType ceilingType) return null;
        Element? created = null;
        txService.RunInTransaction(doc, "Place ceiling", d =>
        {
            var loop = BuildRectangularLoop(start);
            created = Ceiling.Create(d, new List<CurveLoop> { loop }, ceilingType.Id, level.Id);
        });
        return created;
#else
        return null;
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
            txService.RunInTransaction(doc, "Create top level", d =>
            {
                topLevel = Level.Create(d, level.Elevation + RevitUnitsCompat.MetersToInternal(4.0));
            });
        }

        Element? created = null;
        using (var scope = new StairsEditScope(doc, "SmartCon_StairPlacement"))
        {
            var stairsId = scope.Start(level.Id, topLevel!.Id);

            txService.RunInTransaction(doc, "Place stair run", d =>
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

                foreach (var railingId in stairs.GetAssociatedRailings())
                {
                    d.Delete(railingId);
                }

                created = stairs;
            });

            scope.Commit(new StairsFailuresPreprocessor());
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
    /// Ограждение по прямоугольному пути 1×1 м на уровне.
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
        txService.RunInTransaction(doc, "Place railing", d =>
        {
            var path = BuildRectangularLoop(start);
            if (!Railing.IsValidPathForRailing(path)) return;
            created = Railing.Create(d, path, railingType.Id, level.Id);
        });
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
        txService.RunInTransaction(doc, "Place pipe insulation", d =>
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
        });
        return created;
    }

    /// <summary>Зеркалит <see cref="PlacePipeInsulation"/> для воздуховодов.</summary>
    private static Element? PlaceDuctInsulation(Document doc, ITransactionService txService, Element type, Level level, XYZ start, XYZ end)
    {
        if (type is not DuctInsulationType insulationType) return null;
        Element? created = null;
        txService.RunInTransaction(doc, "Place duct insulation", d =>
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
        });
        return created;
    }
}
