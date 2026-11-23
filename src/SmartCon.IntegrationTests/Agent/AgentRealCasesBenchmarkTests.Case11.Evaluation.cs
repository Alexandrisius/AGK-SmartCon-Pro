using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 11 verdict (v2 case 6): (а) 3 new sheets continuing the album (4-6),
/// seeded sheets 1-3 intact; (б) per sheet: the viewport box inside the sheet
/// border (±1 mm) in the TOP-LEFT corner; (в) to the right an explication —
/// a schedule placement whose definition filters «АДСК_ИмяСистемы = система
/// листа» (equals), shows ONLY Наименование/Ед.изм./Количество (the wide
/// columns hidden or removed) and its rows include fittings; (г) the Т1 sheet
/// (long route) has title-block multiplicity &gt; 1 OR the answer justifies the
/// base format; (д) sheet names contain the system name; (е) the batch was
/// assembled with one build_system_sheets call (3 items), the source общая
/// спека stays intact. Red flags: seeded album sheets touched.
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private sealed record Case11FilterFact(string Field, string? Value, string OpName);

    private CaseVerdict EvaluateCase11(
        string? answer,
        List<BenchCall> calls,
        List<int> callsPerRound)
    {
        var redFlags = new List<string>();
        var notes = new List<string>();
        var answerText = answer ?? string.Empty;
        var sequence = calls.Count == 0
            ? "(нет вызовов)"
            : string.Join(" → ", calls.Select(call =>
                call.ToolName + (call.Success ? string.Empty : "(!" + call.ErrorCode + ")")));

        // (а) Новые листы с продолжением нумерации альбома (сид: 1-3 → новые 4-6).
        var newSheets = Case11NewSheets();
        var numbers = newSheets
            .Select(sheet => sheet.SheetNumber ?? string.Empty)
            .OrderBy(number => number, StringComparer.Ordinal)
            .ToList();
        var numbersOk = newSheets.Count == 3 &&
                        numbers.SequenceEqual(new[] { "4", "5", "6" });
        if (newSheets.Count == 0)
            notes.Add("новых листов нет");
        else if (!numbersOk)
            notes.Add("нумерация не продолжает альбом (1-3): получено «" +
                      string.Join(", ", numbers) + "»");

        // Существующие листы альбома не тронуты.
        if (!Case11AlbumIntact(out var damage))
            foreach (var item in damage)
                redFlags.Add("существующий лист альбома тронут: " + item);

        // (б)+(в)+(д) Постатейная проверка каждого нового листа.
        var sheetsFullyOk = 0;
        var perSheetNotes = new List<string>();
        foreach (var sheet in newSheets)
        {
            var system = Case11SystemOfText(sheet.Name);
            var problems = new List<string>();
            if (system is null)
                problems.Add("имя без системы («" + sheet.Name + "»)");
            else if (sheet.Name.Length > 60)
                problems.Add("имя не краткое (" + sheet.Name.Length + " симв.)");

            if (!Case11ViewportInside(sheet, 1.0))
                problems.Add("viewport торчит за границы (или его нет)");
            else if (!Case11ViewportTopLeft(sheet, 150.0))
                problems.Add("viewport не в левом верхнем углу");

            var instance = Case11ScheduleOnSheet(sheet);
            if (instance is null)
            {
                problems.Add("экспликации нет");
            }
            else
            {
                if (!Case11ScheduleRightOfView(sheet, instance))
                    problems.Add("экспликация не справа от вида");
                if (Case11FeetToMm(instance.Point.X) >
                    Case11FeetToMm(sheet.Outline.Max.U) + 1.0)
                    problems.Add("экспликация заведена за правую границу листа");

                if (sheet.Document.GetElement(instance.ScheduleId) is not ViewSchedule schedule)
                {
                    problems.Add("вынос без спецификации");
                }
                else
                {
                    var filter = Case11AdskFilterOf(schedule);
                    if (filter is null)
                    {
                        var filterCount = 0;
                        var filterFieldNames = new List<string>();
                        try
                        {
                            foreach (var f in schedule.Definition.GetFilters())
                            {
                                filterCount++;
                                filterFieldNames.Add(
                                    schedule.Definition.GetField(f.FieldId)?.GetName() ?? "(null)");
                            }
                        }
                        catch (Exception)
                        {
                            // диагностика недоступна
                        }

                        problems.Add("фильтр по «АДСК_ИмяСистемы» не найден (фильтров: " +
                                     filterCount + ", поля: " +
                                     string.Join("|", filterFieldNames) + ")");
                    }
                    else if (!ContainsText(filter.OpName, "equal") ||
                             !string.Equals(filter.Value, system, StringComparison.Ordinal))
                        problems.Add("фильтр «" + filter.Field + "» " + filter.OpName +
                                     " «" + filter.Value + "» ≠ системы «" + system + "»");
                    if (!Case11ExplicationColumnsOk(schedule))
                        problems.Add("колонки не «только Наименование/Ед.изм./Количество»");
                    if (!Case11RowsIncludeFittings(schedule))
                        problems.Add("строки без фитингов (отводов)");
                }
            }

            if (problems.Count == 0)
                sheetsFullyOk++;
            else
                perSheetNotes.Add("лист " + sheet.SheetNumber + ": " +
                                  string.Join("; ", problems));
        }

        notes.AddRange(perSheetNotes);

        // (г) Формат длинной трассы: кратность > 1 у титульника листа «Т1» ИЛИ
        // явное обоснование базового формата в ответе.
        var t1Sheet = newSheets.FirstOrDefault(
            sheet => Case11SystemOfText(sheet.Name) == "Т1");
        var t1Multiplicity = t1Sheet is null ? 0 : Case11MultiplicityOf(t1Sheet);
        var formatJustified = t1Multiplicity > 1 ||
                              ContainsText(answerText, "кратн") ||
                              ContainsText(answerText, "влез") ||
                              ContainsText(answerText, "умеща") ||
                              ContainsText(answerText, "достаточн");
        if (!formatJustified)
            notes.Add("лист «Т1» (длинная трасса): кратность " + t1Multiplicity +
                      " без обоснования формата в ответе");

        // (е) Исходная общая спецификация цела (C6-05: сид создаёт её БЕЗ
        // фильтров — чужой фильтр на источнике, даже при живых колонках,
        // проваливает целостность; 0 видимых строк ловится порогом строк)
        // и сборка пачкой.
        var mainSchedule = Doc.GetElement(ElementIdCompat.Create(_case11MainScheduleId))
            as ViewSchedule;
        var mainIntact = mainSchedule is not null &&
                         AgentScheduleReader.ReadColumns(Doc, mainSchedule!, out _).Count >= 8;
        if (mainIntact)
        {
            var mainFilters = 0;
            var mainRows = 0;
            try
            {
                mainFilters = mainSchedule!.Definition.GetFilters().Count;
                mainRows = AgentScheduleReader.ReadRowCount(mainSchedule, skipHeader: true);
            }
            catch (Exception)
            {
                // Чужая спека без definition — уже не целая.
            }

            if (mainFilters > 0 || mainRows < 10)
            {
                mainIntact = false;
                notes.Add("исходная общая спецификация испорчена: фильтров " + mainFilters +
                          " (сид создаёт без фильтров), видимых строк " + mainRows + " (нужно ≥10)");
            }
        }

        if (!mainIntact)
            notes.Add("исходная общая спецификация урезана или удалена");

        var batchOk = Case11BatchApplied(calls);
        if (newSheets.Count > 0 && !batchOk)
            notes.Add("листы собирались не пачкой (нет одного применённого " +
                      "build_system_sheets с 3 элементами)");

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; новые листы " + newSheets.Count + " (" +
                     string.Join(", ", numbers) + "), полных " + sheetsFullyOk + "/" + newSheets.Count +
                     ", альбом цел " + (damage.Count == 0 ? "да" : "нет") +
                     ", Т1 кратность " + t1Multiplicity +
                     ", формат обоснован " + (formatJustified ? "да" : "нет") +
                     ", общая спека цела " + (mainIntact ? "да" : "нет") +
                     ", пачка " + (batchOk ? "да" : "нет") +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        if (redFlags.Count == 0 && newSheets.Count == 3 && numbersOk &&
            sheetsFullyOk == 3 && formatJustified && mainIntact && batchOk && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        // 0.5 = листы есть, экспликация/формат/нумерация частично (шкала SSOT кейса).
        if (redFlags.Count == 0 && newSheets.Count > 0 && (sheetsFullyOk > 0 || numbersOk))
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    // ── Листы и титульные блоки ─────────────────────────────────────────

    private List<ViewSheet> Case11AllSheets() =>
        new FilteredElementCollector(Doc)
            .OfClass(typeof(ViewSheet))
            .Cast<ViewSheet>()
            .Where(sheet => !sheet.IsPlaceholder)
            .OrderBy(sheet => sheet.SheetNumber ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private List<ViewSheet> Case11NewSheets()
    {
        var albumIds = _case11Album.Select(album => album.Id).ToHashSet();
        return Case11AllSheets()
            .Where(sheet => !albumIds.Contains(sheet.Id.GetValue()))
            .ToList();
    }

    private bool Case11AlbumIntact(out List<string> damage)
    {
        damage = new List<string>();
        foreach (var album in _case11Album)
        {
            if (Doc.GetElement(ElementIdCompat.Create(album.Id)) is not ViewSheet sheet)
            {
                damage.Add("лист " + album.Number + " «" + album.Name + "» удалён");
                continue;
            }

            if (!string.Equals(sheet.SheetNumber, album.Number, StringComparison.Ordinal))
                damage.Add("лист «" + album.Name + "»: номер " + album.Number + " → " + sheet.SheetNumber);
            if (!string.Equals(sheet.Name, album.Name, StringComparison.Ordinal))
                damage.Add("лист " + album.Number + ": имя «" + album.Name + "» → «" + sheet.Name + "»");
        }

        return damage.Count == 0;
    }

    private static FamilyInstance? Case11TitleBlockOf(ViewSheet sheet) =>
        new FilteredElementCollector(sheet.Document)
            .OfCategory(BuiltInCategory.OST_TitleBlocks)
            .WhereElementIsNotElementType()
            .Cast<FamilyInstance>()
            .FirstOrDefault(instance => instance.OwnerViewId == sheet.Id);

    private static int Case11MultiplicityOf(ViewSheet sheet)
    {
        try
        {
            return Case11TitleBlockOf(sheet)
                ?.LookupParameter(Case11TitleblockMultiplicity)
                ?.AsInteger() ?? 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// Система листа по его имени: имя — ТОКЕН на границах (ORCH-5: наивный
    /// substring матчил «Т2» внутри «Система Т21», а фильтр экспликации
    /// стоял «Т1»). «Т1.11» проверяется раньше «Т1» (длинные имена первым —
    /// иначе «Т1» съест префикс «Т1.11»).
    /// </summary>
    internal static string? Case11SystemOfText(string text)
    {
        foreach (var system in Case11Systems.OrderByDescending(candidate => candidate.Length))
        {
            if (Case11ContainsSystemToken(text, system))
                return system;
        }

        return null;
    }

    /// <summary>Имя системы встречается в тексте как целое слово (границы — не буквы/цифры).</summary>
    private static bool Case11ContainsSystemToken(string text, string system)
    {
        var index = text.IndexOf(system, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var boundaryBefore = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var after = index + system.Length;
            var boundaryAfter = after >= text.Length || !char.IsLetterOrDigit(text[after]);
            if (boundaryBefore && boundaryAfter)
                return true;

            index = text.IndexOf(system, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    // ── Геометрия выносов ───────────────────────────────────────────────

    private static double Case11FeetToMm(double internalFeet) =>
        UnitUtils.ConvertFromInternalUnits(internalFeet, UnitTypeId.Millimeters);

    private static Outline? Case11ViewportBox(ViewSheet sheet)
    {
        foreach (var viewportId in sheet.GetAllViewports())
        {
            if (sheet.Document.GetElement(viewportId) is Viewport viewport)
                return viewport.GetBoxOutline();
        }

        return null;
    }

    private static bool Case11ViewportInside(ViewSheet sheet, double toleranceMm)
    {
        var box = Case11ViewportBox(sheet);
        if (box is null)
            return false;

        var outline = sheet.Outline;
        return Case11FeetToMm(box.MinimumPoint.X) >=
               Case11FeetToMm(outline.Min.U) - toleranceMm &&
               Case11FeetToMm(box.MinimumPoint.Y) >=
               Case11FeetToMm(outline.Min.V) - toleranceMm &&
               Case11FeetToMm(box.MaximumPoint.X) <=
               Case11FeetToMm(outline.Max.U) + toleranceMm &&
               Case11FeetToMm(box.MaximumPoint.Y) <=
               Case11FeetToMm(outline.Max.V) + toleranceMm;
    }

    /// <summary>Левый верхний угол: зазоры рамки вида до левого/верхнего края листа малы.</summary>
    private static bool Case11ViewportTopLeft(ViewSheet sheet, double windowMm)
    {
        var box = Case11ViewportBox(sheet);
        if (box is null)
            return false;

        var outline = sheet.Outline;
        var leftGap = Case11FeetToMm(box.MinimumPoint.X) - Case11FeetToMm(outline.Min.U);
        var topGap = Case11FeetToMm(outline.Max.V) - Case11FeetToMm(box.MaximumPoint.Y);
        return leftGap <= windowMm && topGap <= windowMm;
    }

    private static ScheduleSheetInstance? Case11ScheduleOnSheet(ViewSheet sheet) =>
        new FilteredElementCollector(sheet.Document)
            .OfClass(typeof(ScheduleSheetInstance))
            .Cast<ScheduleSheetInstance>()
            .Where(instance => instance.OwnerViewId == sheet.Id)
            // Лист несёт собственный <Revision Schedule> титула — это не экспликация.
            .FirstOrDefault(instance =>
                sheet.Document.GetElement(instance.ScheduleId) is ViewSchedule candidate &&
                !candidate.IsTitleblockRevisionSchedule);

    private static bool Case11ScheduleRightOfView(ViewSheet sheet, ScheduleSheetInstance instance)
    {
        var box = Case11ViewportBox(sheet);
        return box is not null &&
               Case11FeetToMm(instance.Point.X) >
               Case11FeetToMm(box.MaximumPoint.X) - 0.5;
    }

    // ── Экспликация: фильтр, колонки, строки ────────────────────────────

    /// <summary>Фильтр экспликации по «АДСК_ИмяСистемы» (поле + значение + операция).</summary>
    private static Case11FilterFact? Case11AdskFilterOf(ViewSchedule schedule)
    {
        try
        {
            var definition = schedule.Definition;
            foreach (var filter in definition.GetFilters())
            {
                var fieldName = definition.GetField(filter.FieldId)?.GetName();
                if (fieldName is null || !ContainsText(fieldName, "АДСК_ИмяСистемы"))
                    continue;

                string? value = null;
                if (filter.IsStringValue)
                    value = filter.GetStringValue();

                return new Case11FilterFact(fieldName, value, filter.FilterType.ToString());
            }
        }
        catch (Exception)
        {
            // Definition не читается — фильтра не нашли.
        }

        return null;
    }

    /// <summary>Видимые колонки — только Наименование/Ед.изм./Количество, широкие скрыты/убраны.</summary>
    private static bool Case11ExplicationColumnsOk(ViewSchedule schedule)
    {
        var columns = AgentScheduleReader.ReadColumns(schedule.Document, schedule, out _);
        bool Has(params string[] markers) => columns.Any(column =>
            markers.Any(marker => ContainsText(column.Heading, marker) ||
                                  ContainsText(column.ParameterName ?? string.Empty, marker)));

        var hasName = Has("наименован");
        var hasUnit = Has("единиц", "ед.");
        var hasCount = Has("количеств");
        var wideVisible = columns.Any(column => Case11ColumnMatches(
            column, "артикул", "марка", "адск", "семейств", "family", "тип", "type", "коммент"));
        return columns.Count == 3 && hasName && hasUnit && hasCount && !wideVisible;
    }

    private static bool Case11ColumnMatches(
        ScheduleColumn column,
        params string[] markers) =>
        markers.Any(marker => ContainsText(column.Heading, marker) ||
                              ContainsText(column.ParameterName ?? string.Empty, marker));

    /// <summary>Строки экспликации включают фитинги (в сиде фитинги = «Отвод 90° …»).</summary>
    private static bool Case11RowsIncludeFittings(ViewSchedule schedule)
    {
        var columns = AgentScheduleReader.ReadColumns(schedule.Document, schedule, out _);
        var (rows, _, _) = AgentScheduleReader.ReadBodyRows(schedule, columns, 0, 200);
        return rows.Any(row => row.Any(cell => ContainsText(cell, "Отвод")));
    }

    // ── Пакетность сборки ───────────────────────────────────────────────

    private static bool Case11BatchApplied(List<BenchCall> calls) =>
        calls.Any(call => call.ToolName == "build_system_sheets" &&
                          call.Success && !IsDryRunCall(call) &&
                          Case11ItemsCountOf(call.ArgumentsJson) >= 3);

    private static int Case11ItemsCountOf(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return document.RootElement.TryGetProperty("items", out var items) &&
                   items.ValueKind == JsonValueKind.Array
                ? items.GetArrayLength()
                : 0;
        }
        catch (JsonException)
        {
            return 0;
        }
    }
}
