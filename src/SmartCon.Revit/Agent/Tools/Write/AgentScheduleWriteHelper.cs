using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Serialized shape of one schedule filter stored in change items (old/new) and
/// used by undo to restore the previous filter set.
/// </summary>
internal sealed class ScheduleFilterRecord
{
    public string Field { get; set; } = string.Empty;

    public long FieldId { get; set; }

    public string Op { get; set; } = "equals";

    /// <summary>string | number | elementId</summary>
    public string ValueType { get; set; } = "string";

    public string? ValueString { get; set; }

    public double? ValueNumber { get; set; }

    public long? ValueElementId { get; set; }
}

/// <summary>
/// Serialized shape of the schedule sorting state: the sort/group fields plus
/// the itemized flag, restored as one unit by undo.
/// </summary>
internal sealed class ScheduleSortRecord
{
    public List<ScheduleSortFieldRecord> SortGroups { get; set; } = new();

    public bool Itemized { get; set; } = true;
}

internal sealed class ScheduleSortFieldRecord
{
    public string Field { get; set; } = string.Empty;

    public long FieldId { get; set; }

    public string Order { get; set; } = "asc";

    /// <summary>Group decoration flags; null = not recorded (leave as-is on restore).</summary>
    public bool? ShowHeader { get; set; }

    public bool? ShowFooter { get; set; }

    public bool? ShowBlankLine { get; set; }
}

/// <summary>
/// Shared resolution and (de)serialization for schedule write tools: schedule
/// lookup by id/name, schedulable-field matching by display name, and the
/// JSON snapshots of filters/sorting that change items carry for undo.
/// </summary>
internal static partial class AgentScheduleWriteHelper
{
    internal const int MaxFilters = 8;

    internal const int MaxSortFields = 4;

    internal const int MaxFieldsPerCall = 15;

    private static readonly Dictionary<string, ScheduleFilterType> FilterTypesByOp =
        new(StringComparer.Ordinal)
        {
            ["equals"] = ScheduleFilterType.Equal,
            ["not_equals"] = ScheduleFilterType.NotEqual,
            ["contains"] = ScheduleFilterType.Contains,
            ["begins_with"] = ScheduleFilterType.BeginsWith,
            ["ends_with"] = ScheduleFilterType.EndsWith,
            ["greater_than"] = ScheduleFilterType.GreaterThan,
            ["greater_or_equal"] = ScheduleFilterType.GreaterThanOrEqual,
            ["less_than"] = ScheduleFilterType.LessThan,
            ["less_or_equal"] = ScheduleFilterType.LessThanOrEqual
        };

    private static readonly Dictionary<ScheduleFilterType, string> OpsByFilterType =
        FilterTypesByOp.ToDictionary(pair => pair.Value, pair => pair.Key);

    internal static bool TryResolveSchedule(
        Document doc,
        JsonElement args,
        out ViewSchedule? schedule,
        out string? errorCode,
        out string? message)
    {
        schedule = null;
        var scheduleId = AgentJson.GetLong(args, "scheduleId", 0);
        var scheduleName = AgentJson.GetString(args, "scheduleName");

        if (scheduleId <= 0 && string.IsNullOrWhiteSpace(scheduleName))
        {
            errorCode = "invalid_arguments";
            message = "Pass scheduleId or exact scheduleName to identify the schedule.";
            return false;
        }

        if (scheduleId > 0)
        {
            if (doc.GetElement(ElementIdCompat.Create(scheduleId)) is ViewSchedule byId)
            {
                errorCode = null;
                message = null;
                schedule = byId;
                return true;
            }

            errorCode = "schedule_not_found";
            message = "Element " + scheduleId + " is not a schedule. Use list_schedules to find the id.";
            return false;
        }

        var matches = new List<ViewSchedule>();
        foreach (var candidate in AgentScheduleReader.Collect(doc))
        {
            if (string.Equals(candidate.Name, scheduleName!.Trim(), StringComparison.OrdinalIgnoreCase))
                matches.Add(candidate);
        }

        if (matches.Count == 1)
        {
            errorCode = null;
            message = null;
            schedule = matches[0];
            return true;
        }

        if (matches.Count > 1)
        {
            errorCode = "ambiguous_schedule";
            message = matches.Count + " schedules are named '" + scheduleName +
                      "'; pass scheduleIds instead.";
            return false;
        }

        errorCode = "schedule_not_found";
        message = "No schedule named '" + scheduleName + "'. Use list_schedules to see the names.";
        return false;
    }

    /// <summary>
    /// The category for create_schedule: a BuiltInCategory enum name (OST_Pipes),
    /// the localized display name (Трубы) or the numeric category id from
    /// list_categories.
    /// </summary>
    internal static bool TryResolveCategory(
        Document doc,
        JsonElement args,
        out ElementId categoryId,
        out string? errorCode,
        out string? message)
    {
        categoryId = ElementId.InvalidElementId;
        var requested = AgentJson.GetString(args, "category");
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var unknown = new List<string>();
            var resolved = AgentCategoryResolver.Resolve(doc, new[] { requested! }, unknown);
            if (resolved.Count > 0)
            {
                categoryId = resolved[0];
                errorCode = null;
                message = null;
                return true;
            }

            errorCode = "unknown_category";
            message = "Category '" + requested + "' was not found" +
                      AgentCategoryResolver.SuggestSuffix(doc, requested!) + ".";
            return false;
        }

        var categoryIdNumber = AgentJson.GetLong(args, "categoryId", 0);
        if (categoryIdNumber > 0)
        {
            foreach (Category category in doc.Settings.Categories)
            {
                if (category.Id.GetValue() == categoryIdNumber)
                {
                    categoryId = category.Id;
                    errorCode = null;
                    message = null;
                    return true;
                }
            }

            errorCode = "unknown_category";
            message = "No category with id " + categoryIdNumber + " (see list_categories).";
            return false;
        }

        errorCode = "invalid_arguments";
        message = "Pass category (name or OST_ built-in name) or categoryId.";
        return false;
    }

    internal static string FieldDisplayName(Document doc, SchedulableField field)
    {
        try
        {
            return field.GetName(doc) ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    internal static bool TryFindSchedulableField(
        Document doc,
        ScheduleDefinition definition,
        string fieldName,
        out SchedulableField? field)
    {
        field = null;
        foreach (var candidate in definition.GetSchedulableFields())
        {
            if (string.Equals(
                    FieldDisplayName(doc, candidate),
                    fieldName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
            {
                field = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Reason of a field that is not schedulable for the category. A family
    /// parameter that is not shared never becomes a schedule field — this is a
    /// Revit rule, not a naming problem — so the reason says which parameters can.
    /// </summary>
    internal static string FieldNotAvailableReason(string fieldName) =>
        "No schedulable field '" + fieldName + "' for the schedule category. A family parameter " +
        "that is not shared is never a schedule field: Revit schedules only built-in " +
        "parameters and shared parameters bound to the category. To schedule the value, bind a " +
        "project (shared) parameter to the category (add_project_shared_parameter) or map the " +
        "value into a built-in type parameter (map_family_parameters), then add the field by " +
        "the display name get_schedule_definition reports.";

    internal static IReadOnlyList<string> AvailableFieldNames(
        Document doc,
        ScheduleDefinition definition,
        int max)
    {
        var names = new List<string>();
        foreach (var candidate in definition.GetSchedulableFields())
        {
            var name = FieldDisplayName(doc, candidate);
            if (name.Length == 0)
                continue;

            names.Add(name);
            if (names.Count >= max)
                break;
        }

        return names;
    }

    /// <summary>Names of the fields currently in the schedule, in column order.</summary>
    internal static List<string> CurrentFieldNames(ScheduleDefinition definition)
    {
        var names = new List<string>();
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = SafeGetField(definition, fieldId);
            if (field is null)
                continue;

            var name = SafeFieldName(field);
            if (name.Length > 0)
                names.Add(name);
        }

        return names;
    }

    internal static ScheduleField? SafeGetField(ScheduleDefinition definition, ScheduleFieldId fieldId)
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

    internal static string SafeFieldName(ScheduleField field)
    {
        try
        {
            return field.GetName() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>Field id by display name among the fields already in the schedule.</summary>
    internal static ScheduleFieldId? FindFieldIdByName(
        ScheduleDefinition definition,
        string fieldName)
    {
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = SafeGetField(definition, fieldId);
            if (field is null)
                continue;

            if (string.Equals(SafeFieldName(field), fieldName, StringComparison.OrdinalIgnoreCase))
                return fieldId;
        }

        return null;
    }

    internal static bool TryParseFilterType(string? op, out ScheduleFilterType filterType)
    {
        filterType = ScheduleFilterType.Equal;
        return op is not null && FilterTypesByOp.TryGetValue(op, out filterType);
    }

    internal static string OpName(ScheduleFilterType filterType) =>
        OpsByFilterType.TryGetValue(filterType, out var op) ? op : filterType.ToString();

    /// <summary>Builds the Revit filter object; the value kind must match the field.</summary>
    internal static bool TryBuildFilter(
        ScheduleFieldId fieldId,
        ScheduleFilterType filterType,
        JsonElement value,
        out ScheduleFilter? filter,
        out string? reason)
    {
        filter = null;
        try
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                filter = new ScheduleFilter(fieldId, filterType, value.GetString() ?? string.Empty);
                reason = null;
                return true;
            }

            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            {
                filter = new ScheduleFilter(fieldId, filterType, number);
                reason = null;
                return true;
            }

            reason = "value must be a string or a number.";
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            reason = "the operator is not valid for this field.";
            return false;
        }
        catch (ArgumentException)
        {
            reason = "Revit refused the value type for this field (string vs number).";
            return false;
        }
    }

    internal static ScheduleFilter BuildFilter(ScheduleFilterRecord record, ScheduleFieldId fieldId)
    {
        var filterType = FilterTypesByOp.TryGetValue(record.Op, out var parsed)
            ? parsed
            : ScheduleFilterType.Equal;
        if (record.ValueType == "number" && record.ValueNumber.HasValue)
            return new ScheduleFilter(fieldId, filterType, record.ValueNumber.Value);
        if (record.ValueType == "elementId" && record.ValueElementId.HasValue)
            return new ScheduleFilter(
                fieldId, filterType, ElementIdCompat.Create(record.ValueElementId.Value));

        return new ScheduleFilter(fieldId, filterType, record.ValueString ?? string.Empty);
    }

    /// <summary>The current filter set as change-item JSON (old/new snapshot).</summary>
    internal static string SerializeFilters(
        ScheduleDefinition definition,
        IReadOnlyDictionary<int, string> namesByFieldId)
    {
        var records = new List<ScheduleFilterRecord>();
        try
        {
            foreach (var filter in definition.GetFilters())
            {
                var fieldId = filter.FieldId?.IntegerValue ?? 0;
                namesByFieldId.TryGetValue(fieldId, out var fieldName);
                var record = new ScheduleFilterRecord
                {
                    Field = fieldName ?? fieldId.ToString(CultureInfo.InvariantCulture),
                    FieldId = fieldId,
                    Op = OpName(filter.FilterType)
                };

                if (ReadFilterValue(filter, out var valueString, out var valueNumber, out var valueElementId))
                {
                    if (valueString is not null)
                    {
                        record.ValueType = "string";
                        record.ValueString = valueString;
                    }
                    else if (valueNumber.HasValue)
                    {
                        record.ValueType = "number";
                        record.ValueNumber = valueNumber;
                    }
                    else if (valueElementId.HasValue)
                    {
                        record.ValueType = "elementId";
                        record.ValueElementId = valueElementId;
                    }
                }

                records.Add(record);
            }
        }
        catch (Exception)
        {
            // Keep the filters read so far.
        }

        return JsonSerializer.Serialize(records);
    }

    internal static bool ReadFilterValue(
        ScheduleFilter filter,
        out string? valueString,
        out double? valueNumber,
        out long? valueElementId)
    {
        valueString = null;
        valueNumber = null;
        valueElementId = null;
        try
        {
            if (filter.IsStringValue)
                valueString = filter.GetStringValue();
            else if (filter.IsDoubleValue)
                valueNumber = filter.GetDoubleValue();
            else if (filter.IsIntegerValue)
                valueNumber = filter.GetIntegerValue();
            else if (filter.IsElementIdValue)
                valueElementId = filter.GetElementIdValue()?.GetValue();

            return valueString is not null || valueNumber.HasValue || valueElementId.HasValue;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Field names of the current schedule fields, keyed by field id.</summary>
    internal static Dictionary<int, string> FieldNamesByFieldId(ScheduleDefinition definition)
    {
        var map = new Dictionary<int, string>();
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = SafeGetField(definition, fieldId);
            if (field is null)
                continue;

            map[fieldId.IntegerValue] = SafeFieldName(field);
        }

        return map;
    }

    /// <summary>The current sorting state as change-item JSON (old/new snapshot).</summary>
    internal static string SerializeSort(ScheduleDefinition definition)
    {
        var record = new ScheduleSortRecord();
        try
        {
            record.Itemized = definition.IsItemized;
            var names = FieldNamesByFieldId(definition);
            foreach (var sort in definition.GetSortGroupFields())
            {
                var fieldId = sort.FieldId?.IntegerValue ?? 0;
                names.TryGetValue(fieldId, out var fieldName);
                record.SortGroups.Add(new ScheduleSortFieldRecord
                {
                    Field = fieldName ?? fieldId.ToString(CultureInfo.InvariantCulture),
                    FieldId = fieldId,
                    Order = sort.SortOrder == ScheduleSortOrder.Descending ? "desc" : "asc",
                    ShowHeader = SafeSortFlag(() => sort.ShowHeader),
                    ShowFooter = SafeSortFlag(() => sort.ShowFooter),
                    ShowBlankLine = SafeSortFlag(() => sort.ShowBlankLine)
                });
            }
        }
        catch (Exception)
        {
            // Keep what was read.
        }

        return JsonSerializer.Serialize(record);
    }

    internal static string SerializeFieldNames(ScheduleDefinition definition) =>
        JsonSerializer.Serialize(CurrentFieldNames(definition));

    private static bool? SafeSortFlag(Func<bool> read)
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
