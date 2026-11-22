using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;

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
            "row inside the body is skipped and reported as headerRowSkipped.",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""limit"":{""type"":""integer"",""default"":50,""maximum"":500},""offset"":{""type"":""integer"",""default"":0}},""additionalProperties"":false}"
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

        var payload = new
        {
            scheduleId = schedule.Id.GetValue(),
            name = schedule.Name,
            rowCount = total,
            hiddenFieldCount,
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
        try
        {
            hasFilters = schedule.Definition.GetFilters().Count > 0;
        }
        catch (Exception)
        {
            // Best-effort probe: unreadable filters must not fail the read.
        }

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Schedule '" + schedule.Name + "': " + total + " row(s) in " + columns.Count +
            " column(s); returned " + rows.Count + " from offset " + offset + "." +
            (hasMore ? " The result is truncated — pass nextOffset as offset." : string.Empty) +
            (total == 0 && hasFilters && columns.Count > 0
                ? " 0 rows under the current filters is often the EXPECTED clean state of a " +
                  "control/remainder schedule (nothing left unfilled) — remove a filter only " +
                  "when the task asks to list everything."
                : string.Empty));
    }
}