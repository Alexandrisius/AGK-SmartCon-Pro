using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Reads the definition of one schedule: fields, filters, sorting/grouping and the
/// basic flags. This is what explains why a schedule shows zero rows even though
/// the model has elements (external benchmark, task 4). Read-only.
/// </summary>
public sealed class GetScheduleDefinitionTool : AgentReadToolBase
{
    private const int MaxFields = 300;
    private const int MaxFilters = 50;
    private const int MaxSortFields = 20;

    public GetScheduleDefinitionTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_schedule_definition",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Reads the definition of one schedule by scheduleId or exact scheduleName: " +
            "category, key-schedule/itemized flags, fields (name, heading, hidden, calculated, " +
            "field type), filters (field, operator, value) and sorting/grouping fields. Use it " +
            "to explain why a schedule shows zero rows or fewer rows than the model has, and " +
            "to verify changes made by add_schedule_field / set_schedule_filter / " +
            "set_schedule_sort.",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var scheduleId = AgentJson.GetLong(args, "scheduleId", 0);
        var scheduleName = AgentJson.GetString(args, "scheduleName");
        var schedule = FindSchedule(doc, scheduleId, scheduleName);
        if (schedule is null)
        {
            return ToolResult.Fail(
                "schedule_not_found",
                "Pass scheduleId or exact scheduleName of an existing schedule " +
                "(see list_schedules).");
        }

        ScheduleDefinition? definition;
        try
        {
            definition = schedule.Definition;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                "schedule_read_failed",
                "Revit could not read the schedule definition: " + ex.Message);
        }

        if (definition is null)
            return ToolResult.Fail("schedule_read_failed", "The schedule has no definition.");

        var fields = new List<Dictionary<string, object?>>();
        var fieldNames = new Dictionary<int, string>();
        try
        {
            foreach (var fieldId in definition.GetFieldOrder())
            {
                ct.ThrowIfCancellationRequested();
                if (fields.Count >= MaxFields)
                    break;

                var field = SafeField(definition, fieldId);
                if (field is null)
                    continue;

                var name = SafeText(() => field.GetName());
                fieldNames[fieldId.IntegerValue] = name ?? fieldId.IntegerValue.ToString();

                long? parameterId = null;
                try
                {
                    var raw = field.ParameterId;
                    if (raw is not null && raw != ElementId.InvalidElementId)
                        parameterId = raw.GetValue();
                }
                catch (Exception)
                {
                    // Calculated/combined fields have no parameter id.
                }

                fields.Add(new Dictionary<string, object?>
                {
                    ["fieldId"] = fieldId.IntegerValue,
                    ["name"] = name,
                    ["heading"] = SafeText(() => field.ColumnHeading),
                    ["hidden"] = field.IsHidden,
                    ["calculated"] = field.IsCalculatedField,
                    ["parameterId"] = parameterId,
                    ["fieldType"] = field.FieldType.ToString()
                });
            }
        }
        catch (Exception)
        {
            // Keep the fields read so far.
        }

        var filters = new List<Dictionary<string, object?>>();
        try
        {
            foreach (var filter in definition.GetFilters())
            {
                ct.ThrowIfCancellationRequested();
                if (filters.Count >= MaxFilters)
                    break;

                filters.Add(new Dictionary<string, object?>
                {
                    ["field"] = FieldName(fieldNames, filter.FieldId),
                    ["fieldId"] = filter.FieldId?.IntegerValue,
                    ["op"] = filter.FilterType.ToString(),
                    ["value"] = ReadFilterValue(filter)
                });
            }
        }
        catch (Exception)
        {
            // Filters are optional detail.
        }

        var sortFields = new List<Dictionary<string, object?>>();
        try
        {
            foreach (var sort in definition.GetSortGroupFields())
            {
                ct.ThrowIfCancellationRequested();
                if (sortFields.Count >= MaxSortFields)
                    break;

                sortFields.Add(new Dictionary<string, object?>
                {
                    ["field"] = FieldName(fieldNames, sort.FieldId),
                    ["fieldId"] = sort.FieldId?.IntegerValue,
                    ["order"] = sort.SortOrder.ToString(),
                    ["showHeader"] = sort.ShowHeader,
                    ["showFooter"] = sort.ShowFooter,
                    ["showBlankLine"] = sort.ShowBlankLine
                });
            }
        }
        catch (Exception)
        {
            // Sorting is optional detail.
        }

        var payload = new Dictionary<string, object?>
        {
            ["scheduleId"] = schedule.Id.GetValue(),
            ["name"] = schedule.Name,
            ["isTemplate"] = schedule.IsTemplate,
            ["categoryId"] = CategoryId(definition),
            ["categoryName"] = CategoryName(doc, definition),
            ["isKeySchedule"] = SafeBool(() => definition.IsKeySchedule),
            ["isItemized"] = SafeBool(() => definition.IsItemized),
            ["isFilteredBySheet"] = SafeBool(() => definition.IsFilteredBySheet),
            ["includeLinkedFiles"] = SafeBool(() => definition.IncludeLinkedFiles),
            ["hasEmbeddedSchedule"] = SafeBool(() => definition.HasEmbeddedSchedule),
            ["showGrandTotal"] = SafeBool(() => definition.ShowGrandTotal),
            ["fieldCount"] = fields.Count,
            ["fields"] = fields,
            ["fieldsTruncated"] = fields.Count >= MaxFields,
            ["filterCount"] = filters.Count,
            ["filters"] = filters,
            ["filtersTruncated"] = filters.Count >= MaxFilters,
            ["sortGroupFieldCount"] = sortFields.Count,
            ["sortGroupFields"] = sortFields,
            ["hint"] = "Zero rows with elements present usually mean a filter, a phase or an " +
                       "empty parameter used by the filter. Fields, filters and sorting can " +
                       "be changed: add_schedule_field, set_schedule_filter, set_schedule_sort."
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Read schedule '" + schedule.Name + "': " + fields.Count + " field(s), " +
            filters.Count + " filter(s), " + sortFields.Count + " sort/group field(s).");
    }

    private static ViewSchedule? FindSchedule(
        Document doc,
        long scheduleId,
        string? scheduleName)
    {
        if (scheduleId > 0
            && doc.GetElement(ElementIdCompat.Create(scheduleId)) is ViewSchedule byId)
        {
            return byId;
        }

        if (string.IsNullOrWhiteSpace(scheduleName))
            return null;

        var trimmed = scheduleName!.Trim();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)))
        {
            if (element is ViewSchedule schedule
                && string.Equals(schedule.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                return schedule;
            }
        }

        return null;
    }

    private static ScheduleField? SafeField(
        ScheduleDefinition definition,
        ScheduleFieldId fieldId)
    {
        try
        {
            return definition.GetField(fieldId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? FieldName(
        IReadOnlyDictionary<int, string> names,
        ScheduleFieldId? fieldId)
    {
        if (fieldId is null)
            return null;

        return names.TryGetValue(fieldId.IntegerValue, out var name)
            ? name
            : fieldId.IntegerValue.ToString();
    }

    private static object? ReadFilterValue(ScheduleFilter filter)
    {
        try
        {
            if (filter.IsStringValue)
                return filter.GetStringValue();
            if (filter.IsDoubleValue)
                return filter.GetDoubleValue();
            if (filter.IsIntegerValue)
                return filter.GetIntegerValue();
            if (filter.IsElementIdValue)
                return filter.GetElementIdValue()?.GetValue();
        }
        catch (Exception)
        {
            // The value kind did not match the filter type.
        }

        return null;
    }

    private static long? CategoryId(ScheduleDefinition definition)
    {
        try
        {
            var categoryId = definition.CategoryId;
            return categoryId is null || categoryId == ElementId.InvalidElementId
                ? null
                : categoryId.GetValue();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? CategoryName(Document doc, ScheduleDefinition definition)
    {
        try
        {
            var categoryId = definition.CategoryId;
            if (categoryId is null || categoryId == ElementId.InvalidElementId)
                return null;

            return Category.GetCategory(doc, categoryId)?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? SafeText(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}