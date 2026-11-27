using System.Globalization;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent.Tools;
using SmartCon.Revit.Agent.Tools.Read;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// Tool execution, soft verdicts per task and the state read-back dump for
/// the G.2–G.4 ops dogfood (partial of <see cref="AgentOpsDogfoodTests"/>).
/// </summary>
public sealed partial class AgentOpsDogfoodTests
{
    private string ExecuteTool(
        DogfoodToolCall call,
        IReadOnlyDictionary<string, IAgentTool> tools)
    {
        if (!tools.TryGetValue(call.Name, out var tool))
        {
            return "{\"success\":false,\"errorCode\":\"unknown_tool\",\"message\":\"Unknown tool " +
                   call.Name + "\"}";
        }

        JsonElement args;
        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            args = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            return "{\"success\":false,\"errorCode\":\"invalid_arguments\",\"message\":\"" +
                   ex.Message.Replace("\"", "'") + "\"}";
        }

        var result = tool switch
        {
            AgentWriteToolBase write => write.ExecuteForTests(args, approved: true, CancellationToken.None),
            AgentReadToolBase read => read.ExecuteForTests(Doc, args, CancellationToken.None),
            _ => null
        };

        return result is null
            ? "{\"success\":false,\"errorCode\":\"unknown_tool\",\"message\":\"Tool cannot be executed\"}"
            : ToolResultBudget.Serialize(result, 8000);
    }

    private TaskVerdict EvaluateTask(
        int taskIndex,
        IReadOnlyList<DogfoodCall> calls,
        IReadOnlyList<int> callsPerRound)
    {
        var stats = TaskStats.From(calls, callsPerRound);
        var detail = stats.Describe();

        switch (taskIndex)
        {
            case 0:
            {
                var schedule = FindSchedule("Ведомость стен");
                if (schedule is null)
                    return new TaskVerdict(false, detail + "; спецификация «Ведомость стен» не создана");

                var definition = schedule.Definition;
                var fieldNames = ScheduleFieldNames(schedule);
                var missing = new List<string>();
                foreach (var expected in new[] { _typeField, _markField, _commentsField })
                {
                    if (!fieldNames.Any(name => string.Equals(name, expected,
                            StringComparison.OrdinalIgnoreCase)))
                        missing.Add(expected);
                }

                // Collect EVERY unmet criterion: the first-failure verdict hid the
                // sort/placement gaps behind "still itemized" (dogfood triage v16).
                var problems = new List<string>();
                if (missing.Count > 0)
                    problems.Add("нет полей: " + string.Join(", ", missing));
                if (definition.IsItemized)
                    problems.Add("осталась itemized (нужно itemized:false)");

                var sortField = definition.GetSortGroupFields().FirstOrDefault();
                var sortName = sortField is null
                    ? null
                    : definition.GetField(sortField.FieldId).GetName();
                if (sortField is null || !string.Equals(sortName, _typeField,
                        StringComparison.OrdinalIgnoreCase))
                    problems.Add("сортировка не по полю «" + _typeField + "» (сейчас " +
                                 (sortName ?? "нет") + ")");
                else if (sortField.SortOrder != ScheduleSortOrder.Ascending)
                    problems.Add("сортировка не по возрастанию");

                var placement = FindScheduleOnSheet(schedule.Id);
                if (placement is null)
                {
                    problems.Add("спецификация не размещена на " + SheetNumber);
                }
                else
                {
                    var (xMm, yMm) = placement.Value;
                    if (Math.Abs(xMm - 50) > 15 || Math.Abs(yMm - (_sheetHeightMm - 50)) > 20)
                        problems.Add("точка размещения (" +
                                     Mm(xMm) + "; " + Mm(yMm) + ") мм ≠ 50 мм от левого " +
                                     "верхнего угла (лист " + Mm(_sheetWidthMm) + "×" +
                                     Mm(_sheetHeightMm) + ", ожидается (50; " +
                                     Mm(_sheetHeightMm - 50) + "))");
                }

                if (problems.Count > 0)
                    return new TaskVerdict(false, detail + "; " + string.Join("; ", problems));

                var (placedX, placedY) = placement!.Value;
                return new TaskVerdict(true, detail + "; «Ведомость стен» (" + fieldNames.Count +
                                             " полей, не itemized, сортировка по «" + sortName +
                                             "» asc) на " + SheetNumber + " @ (" + Mm(placedX) + "; " +
                                             Mm(placedY) + ") мм");
            }
            case 1:
            {
                var row = FindProjectParameter(ParameterName);
                if (row is null)
                    return new TaskVerdict(false, detail + "; параметр «" + ParameterName +
                                             "» не найден в проектных");

                var name = row.Value.GetProperty("name").GetString();
                var binding = row.Value.GetProperty("bindingType").GetString();
                var categories = row.Value.GetProperty("categories").EnumerateArray()
                    .Select(category => category.GetString())
                    .ToList();
                var wallName = Category.GetCategory(Doc, BuiltInCategory.OST_Walls)!.Name;
                var ductName = Category.GetCategory(Doc, BuiltInCategory.OST_DuctCurves)!.Name;

                var problems = new List<string>();
                if (!string.Equals(name, ParameterName, StringComparison.Ordinal))
                    problems.Add("имя " + name);
                if (!string.Equals(binding, "instance", StringComparison.Ordinal))
                    problems.Add("binding " + binding);
                if (!categories.Contains(wallName, StringComparer.OrdinalIgnoreCase))
                    problems.Add("нет категории " + wallName);
                if (!categories.Contains(ductName, StringComparer.OrdinalIgnoreCase))
                    problems.Add("нет категории " + ductName);

                return problems.Count == 0
                    ? new TaskVerdict(true, detail + "; «" + ParameterName + "» instance для " +
                                       string.Join("+", categories))
                    : new TaskVerdict(false, detail + "; " + string.Join("; ", problems));
            }
            case 2:
            {
                var seedTypeId = ElementIdCompat.Create(_seedWallTypeId);
                var onSeedType = 0;
                foreach (var wallId in _seedWallIds)
                {
                    if (Doc.GetElement(ElementIdCompat.Create(wallId)) is Wall wall &&
                        wall.GetTypeId() == seedTypeId)
                        onSeedType++;
                }

                WallType? copyType = null;
                foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(WallType)))
                {
                    if (element is WallType type &&
                        type.Name.Contains("_ОВ", StringComparison.Ordinal))
                        copyType = type;
                }

                if (copyType is null)
                    return new TaskVerdict(false, detail + "; копия типа стен *_ОВ не создана");
                if (onSeedType != 0)
                    return new TaskVerdict(false, detail + "; на исходном типе осталось стен: " +
                                             onSeedType);

                var onNewType = 0;
                foreach (var wallId in _seedWallIds)
                {
                    if (Doc.GetElement(ElementIdCompat.Create(wallId)) is Wall wall &&
                        wall.GetTypeId() == copyType.Id)
                        onNewType++;
                }

                if (onNewType != _seedWallIds.Count)
                    return new TaskVerdict(false, detail + "; на типе «" + copyType.Name + "» стен " +
                                             onNewType + " из " + _seedWallIds.Count);

                return new TaskVerdict(true, detail + "; тип «" + copyType.Name + "» создан, все " +
                                             _seedWallIds.Count + " стен(ы) переведены одной операцией");
            }
            default:
                return new TaskVerdict(false, "неизвестная задача " + taskIndex);
        }
    }

    private ViewSchedule? FindSchedule(string name)
    {
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(ViewSchedule)))
        {
            if (element is ViewSchedule schedule && !schedule.IsTemplate &&
                string.Equals(schedule.Name, name, StringComparison.OrdinalIgnoreCase))
                return schedule;
        }

        return null;
    }

    private static List<string> ScheduleFieldNames(ViewSchedule schedule)
    {
        var names = new List<string>();
        foreach (var fieldId in schedule.Definition.GetFieldOrder())
        {
            try
            {
                names.Add(schedule.Definition.GetField(fieldId).GetName());
            }
            catch (Exception)
            {
                // Skip unreadable fields.
            }
        }

        return names;
    }

    private (double XMm, double YMm)? FindScheduleOnSheet(ElementId scheduleId)
    {
        foreach (var element in new FilteredElementCollector(Doc)
                     .OfClass(typeof(ScheduleSheetInstance)))
        {
            if (element is not ScheduleSheetInstance instance)
                continue;
            if (instance.OwnerViewId != ElementIdCompat.Create(_sheetId) ||
                instance.ScheduleId != scheduleId)
                continue;

            var point = instance.Point;
            return (UnitUtils.ConvertFromInternalUnits(point.X, UnitTypeId.Millimeters),
                UnitUtils.ConvertFromInternalUnits(point.Y, UnitTypeId.Millimeters));
        }

        return null;
    }

    private JsonElement? FindProjectParameter(string nameContains)
    {
        var result = new ListProjectParametersTool(new StubRevitContext(Doc))
            .ExecuteForTests(
                Doc,
                Parse("{\"parameterNameContains\":\"" + nameContains + "\"}"),
                CancellationToken.None);
        if (!result.Success || result.Data is null)
            return null;

        foreach (var row in result.Data.Value.GetProperty("parameters").EnumerateArray())
        {
            if (string.Equals(row.GetProperty("name").GetString(), ParameterName,
                    StringComparison.Ordinal))
                return row;
        }

        return null;
    }

    private string DumpState()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Schedules:");
        var anySchedule = false;
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(ViewSchedule)))
        {
            if (element is not ViewSchedule schedule || schedule.IsTemplate)
                continue;
            anySchedule = true;
            var definition = schedule.Definition;
            var sort = definition.GetSortGroupFields()
                .Select(field => definition.GetField(field.FieldId).GetName() +
                                 (field.SortOrder == ScheduleSortOrder.Ascending
                                     ? " asc"
                                     : " desc"))
                .ToList();
            builder.AppendLine(
                "- «" + schedule.Name + "» (id " + schedule.Id.GetValue() + "), itemized=" +
                definition.IsItemized + ", поля [" +
                string.Join(", ", ScheduleFieldNames(schedule)) + "], сортировка [" +
                string.Join(", ", sort) + "]");
        }

        if (!anySchedule)
            builder.AppendLine("(нет)");

        builder.AppendLine("Sheet " + SheetNumber + ":");
        var placementIds = new List<ElementId>();
        foreach (var element in new FilteredElementCollector(Doc)
                     .OfClass(typeof(ScheduleSheetInstance)))
        {
            if (element is ScheduleSheetInstance instance &&
                instance.OwnerViewId == ElementIdCompat.Create(_sheetId))
                placementIds.Add(instance.ScheduleId);
        }

        if (placementIds.Count == 0)
            builder.AppendLine("(спецификаций на листе нет)");
        foreach (var scheduleId in placementIds)
        {
            var point = FindScheduleOnSheet(scheduleId);
            var scheduleName = (Doc.GetElement(scheduleId) as ViewSchedule)?.Name ?? "?";
            builder.AppendLine(
                "- «" + scheduleName + "» @ (" +
                (point.HasValue ? Mm(point.Value.XMm) + "; " + Mm(point.Value.YMm) : "?") +
                ") мм от левого нижнего угла");
        }

        builder.AppendLine("Project parameters «ОВ_»:");
        var row = FindProjectParameter("ОВ_");
        builder.AppendLine(row is null
            ? "(нет)"
            : "- " + row.Value.GetProperty("name").GetString() + " " +
              row.Value.GetProperty("bindingType").GetString() + " [" +
              string.Join(", ", row.Value.GetProperty("categories").EnumerateArray()
                  .Select(category => category.GetString())) + "]");

        builder.AppendLine("Wall types (сид-стен на типе):");
        foreach (var element in new FilteredElementCollector(Doc).OfClass(typeof(WallType)))
        {
            if (element is not WallType type)
                continue;
            var count = 0;
            foreach (var wallId in _seedWallIds)
            {
                if (Doc.GetElement(ElementIdCompat.Create(wallId)) is Wall wall &&
                    wall.GetTypeId() == type.Id)
                    count++;
            }

            builder.AppendLine("- «" + type.Name + "» (id " + type.Id.GetValue() + "): " + count);
        }

        return builder.ToString();
    }

    private static string Mm(double value) =>
        value.ToString("F0", CultureInfo.InvariantCulture);

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed record TaskVerdict(bool Passed, string Detail);
}
