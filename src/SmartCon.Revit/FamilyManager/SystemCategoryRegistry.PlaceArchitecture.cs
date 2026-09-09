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

public static partial class SystemCategoryRegistry
{
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
}
