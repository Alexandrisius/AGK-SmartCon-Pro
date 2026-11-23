using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Read;

public sealed class GetScheduleDataTool : AgentReadToolBase
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 500;

    public GetScheduleDataTool(IRevitContext revitContext)
        : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_schedule_data",
        Description =
            "Reads the rows of one schedule without exporting it. Identify it by scheduleId or " +
            "exact scheduleName. Returns column headings with parameter names and page of rows as " +
            "string arrays (values as displayed in the schedule, including units). Paged: limit " +
            "(default 50, max 500), offset, returned, nextOffset, truncated, totalRows. A heading " +
            "row inside the body is skipped and reported as headerRowSkipped. includeHidden=true " +
            "adds hiddenColumns[] (name, heading, guid, parameterName and up to 5 sampleValues " +
            "read from the model — the table body has no cells for hidden columns; the single " +
            "hiddenColumnsNote explains the samples once). 0 rows under " +
            "a filter that names a value is reported as a warning with the closest existing " +
            "values of the field; 0 rows under an equals-\"\" control filter is reported as passed.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""limit"":{""type"":""integer"",""default"":50,""maximum"":500},""offset"":{""type"":""integer"",""default"":0},""includeHidden"":{""type"":""boolean"",""default"":false,""description"":""Add hiddenColumns[]: hidden fields of the schedule with up to 5 sample values read from the model elements (the table body carries no cells for hidden columns)""}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(
        Document doc,
        JsonElement args,
        CancellationToken ct)
    {
        var scheduleId = AgentJson.GetLong(args, "scheduleId", 0);
        var scheduleName = AgentJson.GetString(args, "scheduleName");
        var offset = AgentPaging.NormalizeOffset(AgentJson.GetInt(args, "offset", 0));
        var limit = Math.Max(1, Math.Min(AgentJson.GetInt(args, "limit", DefaultLimit), MaxLimit));

        if (scheduleId <= 0 && string.IsNullOrWhiteSpace(scheduleName))
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "Pass scheduleId or scheduleName to identify the schedule.");
        }

        ViewSchedule? schedule = null;
        if (scheduleId > 0)
        {
            schedule = doc.GetElement(ElementIdCompat.Create(scheduleId)) as ViewSchedule;
            if (schedule is null)
            {
                return ToolResult.Fail(
                    "schedule_not_found",
                    "Element " + scheduleId + " is not a schedule. Use list_schedules to find the id.");
            }
        }
        else
        {
            var matches = new List<ViewSchedule>();
            foreach (var candidate in AgentScheduleReader.Collect(doc))
            {
                if (candidate.IsTitleblockRevisionSchedule || candidate.IsInternalKeynoteSchedule)
                    continue;
                if (string.Equals(candidate.Name, scheduleName, StringComparison.OrdinalIgnoreCase))
                    matches.Add(candidate);
            }

            if (matches.Count == 0)
            {
                return ToolResult.Fail(
                    "schedule_not_found",
                    "No schedule named '" + scheduleName + "'. Use list_schedules to see the names.");
            }

            if (matches.Count > 1)
            {
                var candidates = new List<object>(matches.Count);
                foreach (var match in matches)
                {
                    candidates.Add(new
                    {
                        scheduleId = match.Id.GetValue(),
                        name = match.Name,
                        isTemplate = match.IsTemplate
                    });
                }

                return ToolResult.Fail(
                    "ambiguous_schedule",
                    matches.Count + " schedules have this name; pass scheduleId instead.",
                    data: AgentJson.Serialize(new { candidates }));
            }

            schedule = matches[0];
        }

        if (schedule!.IsTemplate)
        {
            return ToolResult.Fail(
                "schedule_is_template",
                "Schedule '" + schedule.Name + "' is a template and has no rows.");
        }

        var columns = AgentScheduleReader.ReadColumns(doc, schedule, out var hiddenFieldCount);
        var (rows, headerSkipped, total) =
            AgentScheduleReader.ReadBodyRows(schedule, columns, offset, limit);
        var hasMore = offset + rows.Count < total;

        var columnPayload = new List<object>(columns.Count);
        foreach (var column in columns)
        {
            columnPayload.Add(new
            {
                index = column.Index,
                heading = column.Heading,
                parameterName = column.ParameterName,
                fieldType = column.FieldType
            });
        }

        var hiddenColumns = AgentJson.GetBool(args, "includeHidden", false)
            ? ReadHiddenColumnSamples(doc, schedule, ct)
            : null;
        var payload = new
        {
            scheduleId = schedule.Id.GetValue(),
            name = schedule.Name,
            rowCount = total,
            hiddenFieldCount,
            hiddenColumns,
            hiddenColumnsNote = hiddenColumns is { Count: > 0 } ? HiddenColumnsNote : null,
            columnCount = columns.Count,
            columns = columnPayload,
            totalRows = total,
            offset,
            returned = rows.Count,
            nextOffset = hasMore ? offset + rows.Count : (int?)null,
            truncated = hasMore,
            headerRowSkipped = headerSkipped,
            rows
        };

        var hasFilters = false;
        var hasValuelessFilterOnly = true;
        ScheduleFilter? equalValueFilter = null;
        try
        {
            foreach (var filter in schedule.Definition.GetFilters())
            {
                hasFilters = true;

                // The equals-"" filter of a control schedule looks for empty
                // cells on purpose: its empty result is the clean state.
                var isEmptinessCheck = filter.FilterType == ScheduleFilterType.Equal
                                       && filter.IsStringValue
                                       && string.IsNullOrEmpty(filter.GetStringValue());
                if (isEmptinessCheck)
                    continue;

                hasValuelessFilterOnly = false;
                if (equalValueFilter is null && filter.IsStringValue)
                    equalValueFilter = filter;
            }
        }
        catch (Exception)
        {
            // Best-effort probe: unreadable filters must not fail the read.
        }

        var zeroRowsNote = string.Empty;
        if (total == 0 && hasFilters && columns.Count > 0)
        {
            if (!hasValuelessFilterOnly)
            {
                zeroRowsNote = equalValueFilter is null
                    ? ZeroRowsUnderValueFilter
                    : ZeroRowsUnderValueFilter +
                      ClosestValuesHint(doc, schedule.Definition, equalValueFilter);
            }
            else
            {
                zeroRowsNote =
                    " 0 rows means the equals-\"\" filter found NOTHING empty — the control " +
                    "check PASSED and the schedule is complete as is. KEEP the filter: it is " +
                    "the point of a control/remainder view; removing it to 'fix' the empty " +
                    "table defeats the check. Report \"0 rows = всё заполнено\".";
            }
        }

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Schedule '" + schedule.Name + "': " + total + " row(s) in " + columns.Count +
            " column(s); returned " + rows.Count + " from offset " + offset + "." +
            (hasMore ? " The result is truncated — pass nextOffset as offset." : string.Empty) +
            zeroRowsNote);
    }

    private const string ZeroRowsUnderValueFilter =
        " 0 rows under the current filters — CHECK THE FILTER VALUES: no row of this schedule " +
        "matches them; a mistyped or missing value looks exactly like this." +
        " [Action: compare the filter values with the existing values of the field " +
        "(set_schedule_filter dry run lists estimatedSampleNames), fix them with " +
        "set_schedule_filter and read again]";

    /// <summary>
    /// One note per hiddenColumns block (S1-R6): repeating it on every hidden
    /// column cost ~200 characters each for the same fact.
    /// </summary>
    private const string HiddenColumnsNote =
        "Hidden columns have no cells in the table body; sampleValues are distinct values " +
        "read from the model elements. Unhide a field " +
        "(set_schedule_field_visibility) to see per-row values.";

    /// <summary>
    /// The closest existing values of the field behind a string filter: what
    /// the caller may have meant by a value that matched nothing. Empty when
    /// they cannot be read.
    /// </summary>
    private static string ClosestValuesHint(
        Document doc,
        ScheduleDefinition definition,
        ScheduleFilter filter)
    {
        try
        {
            var parameterId = filter.FieldId is null
                ? null
                : definition.GetField(filter.FieldId)?.ParameterId;
            if (parameterId is null || parameterId == ElementId.InvalidElementId)
                return string.Empty;

            return AgentScheduleFilterEstimator.DescribeClosestValues(
                doc, definition, parameterId.GetValue(), filter.GetStringValue() ?? string.Empty);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Hidden fields with sample values: the table body has no cells for hidden
    /// columns, so the samples are read from the elements the schedule draws
    /// its rows from (a hidden filter column is exactly what the caller usually
    /// needs the values of).
    /// </summary>
    private static List<object> ReadHiddenColumnSamples(
        Document doc,
        ViewSchedule schedule,
        CancellationToken ct)
    {
        var result = new List<object>();
        try
        {
            var definition = schedule.Definition;
            foreach (var fieldId in definition.GetFieldOrder())
            {
                ct.ThrowIfCancellationRequested();
                ScheduleField? field;
                try
                {
                    field = definition.GetField(fieldId);
                }
                catch (Exception)
                {
                    continue;
                }

                if (field is null || !field.IsHidden)
                    continue;

                var parameterId = field.ParameterId;
                var samples = parameterId is null || parameterId == ElementId.InvalidElementId
                    ? new List<string>()
                    : AgentScheduleFilterEstimator.DistinctValues(
                        doc, definition, parameterId.GetValue(), 5, ct);

                result.Add(new
                {
                    name = SafeFieldName(field),
                    heading = SafeText(field.ColumnHeading),
                    parameterName = AgentScheduleReader.DescribeParameter(doc, field),
                    guid = AgentScheduleReader.DescribeParameterGuid(doc, field),
                    sampleValues = samples.Count > 0 ? samples : null
                });
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Hidden columns are optional detail.
        }

        return result;
    }

    private static string SafeFieldName(ScheduleField field)
    {
        try
        {
            return field.GetName();
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string SafeText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : value!;
}
