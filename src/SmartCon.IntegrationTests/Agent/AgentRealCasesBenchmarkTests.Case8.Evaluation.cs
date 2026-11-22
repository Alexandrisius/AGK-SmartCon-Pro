using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Case 8 verdict (v2 case 3): for every seeded sheet schedule — a new one
/// with the SAME name exists, its columns are analogs of the archive's
/// (target GUIDs replace the ADSK ones; the no-analog column is skipped and
/// reported), order/headings/widths (±0.1 mm)/hidden flags/sorting with
/// flags/filters/totals/table style match the archive, it sits on the same
/// sheet at the same point (±2 mm), while the old schedule stays alive,
/// renamed with «Архив» and off the sheet. The draft bait outside sheets must
/// be untouched. 0.5 = ≥2 complete specs. Сверка идёт напрямую по
/// ScheduleDefinition новой спеки против архивной (истина — сам архив).
/// Partial of <see cref="AgentRealCasesBenchmarkTests"/>.
/// </summary>
public sealed partial class AgentRealCasesBenchmarkTests
{
    private CaseVerdict EvaluateCase8(
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

        var completeSpecs = 0;
        var specDetails = new List<string>();
        foreach (var placement in _case8Placements)
        {
            var problems = new List<string>();
            if (Case8SpecComplete(placement, problems))
                completeSpecs++;
            else
                redFlags.Add("«" + placement.Name + "»: " +
                             string.Join("; ", problems.Take(3)) +
                             (problems.Count > 3 ? " …" : string.Empty));
            specDetails.Add("«" + placement.Name + "» — " +
                            (problems.Count == 0 ? "полный аналог на месте" :
                                string.Join("; ", problems.Take(3))));
        }

        // Колонка без аналога пропущена и перечислена в отчёте.
        var skippedReported = ContainsText(answerText, "Примечание") ||
                              ContainsText(answerText, "без аналога");
        if (!skippedReported)
            notes.Add("пропущенная колонка «" + Case8NoteName + "» не перечислена в отчёте");

        // Приманка вне листов не тронута.
        var baitOk = Case8BaitUntouched();
        if (!baitOk)
            redFlags.Add("приманка «" + Case8BaitName + "» тронута (переименована/на листе/" +
                         "потеряла старые поля или получила целевые)");

        var feedbackMarkers = new[] { "удобн", "неудобн", "невозможно", "не хватило" }
            .Count(marker => ContainsText(answerText, marker));
        var feedbackOk = feedbackMarkers >= 2;
        if (!feedbackOk)
            notes.Add("нет блока обратной связи из стартового блока (маркеров " + feedbackMarkers + ")");

        var detail = "tools [" + sequence + "], rounds " + callsPerRound.Count +
                     "; полных спек " + completeSpecs + "/3" +
                     " (" + string.Join("; ", specDetails) + ")" +
                     ", пропуск без аналога " + (skippedReported ? "объявлен" : "НЕ объявлен") +
                     ", приманка " + (baitOk ? "не тронута" : "тронута") +
                     (notes.Count > 0 ? "; " + string.Join("; ", notes) : string.Empty);

        if (redFlags.Count == 0 && completeSpecs == 3 && skippedReported && baitOk && feedbackOk)
            return new CaseVerdict(true, 1.0, detail, redFlags);

        // 0.5 = не меньше двух полных спек (шкала SSOT кейса).
        if (completeSpecs >= 2)
            return new CaseVerdict(false, 0.5, detail, redFlags);

        return new CaseVerdict(false, 0.0, detail, redFlags);
    }

    /// <summary>Полный аналог одной спеки: имя, поля, настройки, вынос, архив.</summary>
    private bool Case8SpecComplete(Case8Placement placement, List<string> problems)
    {
        if (Doc.GetElement(ElementIdCompat.Create(placement.ScheduleId)) is not ViewSchedule archive)
        {
            problems.Add("старая спека УДАЛЕНА (должна жить как архивная)");
            return false;
        }

        if (!ContainsText(archive.Name, "Архив"))
            problems.Add("старая не помечена «Архив» (имя «" + archive.Name + "»)");

        if (Case8PlacementsOf(placement.ScheduleId).Count > 0)
            problems.Add("старая не снята с листа");

        var replacement = Case8FindByNameExcept(placement.Name, placement.ScheduleId);
        if (replacement is null)
        {
            problems.Add("новой спеки с именем «" + placement.Name + "» нет");
            return false;
        }

        try
        {
            Case8CompareColumns(archive, replacement, problems);
            Case8CompareSorts(archive, replacement, problems);
            Case8CompareFilters(archive, replacement, problems);
            Case8CompareTotals(archive, replacement, problems);
        }
        catch (Exception ex)
        {
            problems.Add("definition не читается: " + ex.GetType().Name);
        }

        // Вынос: та же точка на том же листе (±2 мм).
        var placed = Case8PlacementsOf(replacement.Id.GetValue())
            .Any(item => string.Equals(item.SheetNumber, placement.SheetNumber, StringComparison.Ordinal) &&
                         Math.Abs(item.XMm - placement.PointMmX) <= 2.0 &&
                         Math.Abs(item.YMm - placement.PointMmY) <= 2.0);
        if (!placed)
            problems.Add("новая не на месте старой (лист " + placement.SheetNumber + ", точка " +
                         placement.PointMmX + ";" + placement.PointMmY + " мм)");

        return problems.Count == 0;
    }

    private ViewSchedule? Case8FindByNameExcept(string name, long exceptId)
    {
        foreach (var schedule in AgentScheduleReader.Collect(Doc))
            if (schedule.Id.GetValue() != exceptId &&
                string.Equals(schedule.Name, name, StringComparison.Ordinal))
                return schedule;

        return null;
    }

    private sealed record Case8Column(Guid? Guid, string Name, string Heading, double? WidthMm, bool Hidden);

    private static List<Case8Column> Case8Columns(ViewSchedule schedule)
    {
        var result = new List<Case8Column>();
        try
        {
            var definition = schedule.Definition;
            foreach (var fieldId in definition.GetFieldOrder())
            {
                var field = definition.GetField(fieldId);
                if (field is null)
                    continue;

                var raw = AgentScheduleReader.DescribeParameterGuid(schedule.Document, field);
                string name;
                try
                {
                    name = field.GetName();
                }
                catch (Exception)
                {
                    name = string.Empty;
                }

                result.Add(new Case8Column(
                    raw is not null && Guid.TryParse(raw, out var guid) ? guid : null,
                    name,
                    field.ColumnHeading ?? string.Empty,
                    AgentScheduleWriteHelper.GetColumnWidthMm(field),
                    field.IsHidden));
            }
        }
        catch (Exception)
        {
            // Definition не читается — возвращаем что собрали.
        }

        return result;
    }

    /// <summary>Целевой GUID-аналог старого (null = аналога нет: Примечание).</summary>
    private static Guid? Case8AnalogOf(Guid? oldGuid)
    {
        if (oldGuid is null)
            return null;

        var role = Array.IndexOf(Case7OldGuids[0], oldGuid);
        return role >= 0 ? Case7TargetGuids[role] : null;
    }

    private static void Case8CompareColumns(
        ViewSchedule archive, ViewSchedule replacement, List<string> problems)
    {
        var archiveColumns = Case8Columns(archive);
        var newColumns = Case8Columns(replacement);
        var expected = new List<Case8Column>();
        foreach (var column in archiveColumns)
        {
            var analog = Case8AnalogOf(column.Guid);
            if (column.Guid is not null && analog is null)
                continue;

            expected.Add(analog is null
                ? column
                : new Case8Column(analog, column.Name, column.Heading, column.WidthMm, column.Hidden));
        }

        if (newColumns.Count != expected.Count)
        {
            problems.Add("колонок " + newColumns.Count + " вместо " + expected.Count +
                         " (ожидалось " + archiveColumns.Count + " минус лишние)");
            return;
        }

        for (var index = 0; index < expected.Count; index++)
        {
            var want = expected[index];
            var got = newColumns[index];
            var roleLabel = want.Guid is not null &&
                            Array.IndexOf(Case7TargetGuids, want.Guid.Value) is var r && r >= 0
                ? Case7TargetNames[r]
                : want.Name;
            if (want.Guid is not null)
            {
                if (got.Guid != want.Guid)
                    problems.Add("колонка " + (index + 1) + " («" + roleLabel + "»): guid " +
                                 (got.Guid?.ToString("D") ?? "—") + " вместо целевого");
            }
            else if (!string.Equals(got.Name, want.Name, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("колонка " + (index + 1) + ": «" + got.Name + "» вместо «" +
                             want.Name + "»");
            }

            if (!string.Equals(got.Heading, want.Heading, StringComparison.Ordinal))
                problems.Add("колонка «" + roleLabel + "»: заголовок «" + got.Heading +
                             "» вместо «" + want.Heading + "»");
            if (got.Hidden != want.Hidden)
                problems.Add("колонка «" + roleLabel + "»: скрытость " + got.Hidden +
                             " вместо " + want.Hidden);
            if (want.WidthMm.HasValue && got.WidthMm.HasValue &&
                Math.Abs(got.WidthMm.Value - want.WidthMm.Value) > 0.1)
                problems.Add("колонка «" + roleLabel + "»: ширина " +
                             got.WidthMm.Value.ToString("0.0",
                                 System.Globalization.CultureInfo.InvariantCulture) +
                             " вместо " + want.WidthMm.Value.ToString("0.0",
                                 System.Globalization.CultureInfo.InvariantCulture) + " мм");
        }
    }

    private static void Case8CompareSorts(
        ViewSchedule archive, ViewSchedule replacement, List<string> problems)
    {
        var archiveSorts = archive.Definition.GetSortGroupFields();
        var newSorts = replacement.Definition.GetSortGroupFields();
        if (archiveSorts.Count != newSorts.Count)
        {
            problems.Add("сортировок " + newSorts.Count + " вместо " + archiveSorts.Count);
            return;
        }

        for (var index = 0; index < archiveSorts.Count; index++)
        {
            var want = archiveSorts[index];
            var got = newSorts[index];
            if (want.SortOrder != got.SortOrder)
                problems.Add("сортировка " + (index + 1) + ": порядок " + got.SortOrder +
                             " вместо " + want.SortOrder);
            if (want.ShowFooter != got.ShowFooter || want.ShowBlankLine != got.ShowBlankLine)
                problems.Add("сортировка " + (index + 1) + ": флаги итогов/пустой строки не те");
            if (!Case8FieldsAreAnalog(archive, want.FieldId, replacement, got.FieldId))
                problems.Add("сортировка " + (index + 1) + ": поле не аналог исходного");
        }
    }

    private static void Case8CompareFilters(
        ViewSchedule archive, ViewSchedule replacement, List<string> problems)
    {
        var archiveFilters = archive.Definition.GetFilters();
        var newFilters = replacement.Definition.GetFilters();
        if (archiveFilters.Count != newFilters.Count)
        {
            problems.Add("фильтров " + newFilters.Count + " вместо " + archiveFilters.Count);
            return;
        }

        for (var index = 0; index < archiveFilters.Count; index++)
        {
            var want = archiveFilters[index];
            var got = newFilters[index];
            if (want.FilterType != got.FilterType)
                problems.Add("фильтр " + (index + 1) + ": тип " + got.FilterType +
                             " вместо " + want.FilterType);
            if (want.IsStringValue && (!got.IsStringValue ||
                                        want.GetStringValue() != got.GetStringValue()))
                problems.Add("фильтр " + (index + 1) + ": значение не то");
            if (!Case8FieldsAreAnalog(archive, want.FieldId, replacement, got.FieldId))
                problems.Add("фильтр " + (index + 1) + ": поле не аналог исходного");
        }
    }

    private static void Case8CompareTotals(
        ViewSchedule archive, ViewSchedule replacement, List<string> problems)
    {
        var archiveDefinition = archive.Definition;
        var newDefinition = replacement.Definition;
        if (archiveDefinition.IsItemized != newDefinition.IsItemized)
            problems.Add("признак детализации на строку (itemized) не совпал");
        if (archiveDefinition.ShowGrandTotal != newDefinition.ShowGrandTotal ||
            archiveDefinition.ShowGrandTotalCount != newDefinition.ShowGrandTotalCount)
            problems.Add("общие итоги не совпали");
        if (archive.HasStripedRows != replacement.HasStripedRows ||
            archive.UseStripedRowsOnSheets != replacement.UseStripedRowsOnSheets)
            problems.Add("стиль таблиц (полосатые строки) не совпал");
    }

    /// <summary>Поле новой спеки — аналог поля архивной (цель по GUID, builtin по имени).</summary>
    private static bool Case8FieldsAreAnalog(
        ViewSchedule archive,
        ScheduleFieldId archiveFieldId,
        ViewSchedule replacement,
        ScheduleFieldId newFieldId)
    {
        var archiveField = archive.Definition.GetField(archiveFieldId);
        var newField = replacement.Definition.GetField(newFieldId);
        if (archiveField is null || newField is null)
            return false;

        var archiveRaw = AgentScheduleReader.DescribeParameterGuid(archive.Document, archiveField);
        var newRaw = AgentScheduleReader.DescribeParameterGuid(replacement.Document, newField);
        if (archiveRaw is not null && Guid.TryParse(archiveRaw, out var archiveGuid))
        {
            if (newRaw is null || !Guid.TryParse(newRaw, out var newGuid))
                return false;

            var analog = Case8AnalogOf(archiveGuid);
            return analog is not null && newGuid == analog.Value;
        }

        if (newRaw is not null)
            return false;

        string archiveName, newName;
        try
        {
            archiveName = archiveField.GetName();
            newName = newField.GetName();
        }
        catch (Exception)
        {
            return false;
        }

        return string.Equals(archiveName, newName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Приманка жива под своим именем, вне листов, со старым полем и без целевых.</summary>
    private bool Case8BaitUntouched()
    {
        if (Doc.GetElement(ElementIdCompat.Create(_case8BaitId)) is not ViewSchedule bait)
            return false;

        if (!string.Equals(bait.Name, Case8BaitName, StringComparison.Ordinal))
            return false;

        if (AgentScheduleReader.MapSheetNumbers(Doc)
            .TryGetValue(_case8BaitId, out var numbers) && numbers!.Count > 0)
            return false;

        var guids = Case7ColumnGuids(bait);
        return guids.Any(guid => guid == Case7OldGuids[0][1]) &&
               !guids.Any(guid => guid is not null && Case7TargetGuids.Contains(guid.Value));
    }
}
