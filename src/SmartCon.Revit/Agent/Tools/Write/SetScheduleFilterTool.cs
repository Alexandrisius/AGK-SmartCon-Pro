using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Replaces the filter set of a schedule (AND only, max 8). An empty list
/// clears the filters. Fields must already be columns of the schedule.
/// </summary>
public sealed class SetScheduleFilterTool : AgentWriteToolBase
{
    private static readonly string[] SubstringOps = { "contains", "begins_with", "ends_with" };

    public SetScheduleFilterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_schedule_filter",
        Description =
            "Replaces the whole filter set of a schedule by scheduleId or exact scheduleName. " +
            "filters (0..8; [] clears all filters) — each {field, op, value}: op is one of " +
            "equals|not_equals|contains|begins_with|ends_with|greater_than|less_than|" +
            "greater_or_equal|less_or_equal; value is a string or a number. The field must " +
            "already be a column of the schedule (add_schedule_field first) — otherwise " +
            "field_not_in_schedule. Filters combine only by AND and Revit allows at most 8 " +
            "(filter_limit_exceeded). Read the result back with get_schedule_definition. " +
            "Dry run defaults to true and reports the preview effect: estimatedRowsAfter " +
            "(elements passing the new filters) with estimatedSampleNames (up to 5).",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""filters"":{""type"":""array"",""maxItems"":8,""items"":{""type"":""object"",""properties"":{""field"":{""type"":""string""},""op"":{""type"":""string"",""enum"":[""equals"",""not_equals"",""contains"",""begins_with"",""ends_with"",""greater_than"",""less_than"",""greater_or_equal"",""less_or_equal""]},""value"":{""type"":[""string"",""number""]}},""required"":[""field"",""op"",""value""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""filters""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("filters", out var filtersElement)
            || filtersElement.ValueKind != JsonValueKind.Array)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass filters (0.." + AgentScheduleWriteHelper.MaxFilters +
                              " filter objects; [] clears the filters).";
            return;
        }

        if (filtersElement.GetArrayLength() > AgentScheduleWriteHelper.MaxFilters)
        {
            outcome.Success = false;
            outcome.ErrorCode = "filter_limit_exceeded";
            outcome.Message = "Revit allows at most " + AgentScheduleWriteHelper.MaxFilters +
                              " filters per schedule (AND only). Combine conditions " +
                              "differently or filter before scheduling.";
            return;
        }

        if (!AgentScheduleWriteHelper.TryResolveSchedule(
                doc, args, out var schedule, out var errorCode, out var message))
        {
            outcome.Success = false;
            outcome.ErrorCode = errorCode;
            outcome.Message = message;
            return;
        }

        var definition = schedule!.Definition;
        outcome.Description = "Set " + filtersElement.GetArrayLength() + " filter(s) on schedule " +
                              schedule.Name;

        var built = new List<(string Field, ScheduleFieldId FieldId, ScheduleFilter Filter)>();
        foreach (var filterElement in filtersElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            var field = filterElement.ValueKind == JsonValueKind.Object
                ? filterElement.GetProperty("field").GetString()
                : null;
            var op = filterElement.ValueKind == JsonValueKind.Object
                ? filterElement.GetProperty("op").GetString()
                : null;

            var report = new WriteItemReport
            {
                Id = schedule.Id.GetValue(),
                Name = field ?? string.Empty
            };

            if (string.IsNullOrWhiteSpace(field))
            {
                report.ErrorCode = "invalid_arguments";
                report.Reason = "Each filter needs field, op and value.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(definition, field!);
            if (fieldId is null)
            {
                report.ErrorCode = "field_not_in_schedule";
                report.Reason = "Field '" + field + "' is not a column of this schedule; " +
                                "add it with add_schedule_field first (a filter cannot use a " +
                                "field the schedule does not show).";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            if (!AgentScheduleWriteHelper.TryParseFilterType(op, out var filterType))
            {
                report.ErrorCode = "invalid_arguments";
                report.Reason = "Unknown op '" + op + "'.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var isSubstring = op is not null && Array.IndexOf(SubstringOps, op) >= 0;
            if (isSubstring && !CanFilterBySubstring(definition, fieldId))
            {
                report.ErrorCode = "filter_value_not_supported";
                report.Reason = "op '" + op + "' works only on text fields; '" + field +
                                "' is not one.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            if (!filterElement.TryGetProperty("value", out var valueElement))
            {
                report.ErrorCode = "invalid_arguments";
                report.Reason = "Each filter needs field, op and value.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            if (!AgentScheduleWriteHelper.TryBuildFilter(
                    fieldId, filterType, valueElement, out var filter, out var reason))
            {
                report.ErrorCode = "filter_value_not_supported";
                report.Reason = "Filter on '" + field + "': " + reason;
                outcome.Batch.Failed.Add(report);
                continue;
            }

            built.Add((field!, fieldId, filter!));
        }

        if (outcome.Batch.Failed.Count > 0)
        {
            outcome.SuppressChangeSet = true;
            outcome.Message = outcome.Batch.Failed.Count + " filter(s) invalid; nothing changed. " +
                              "See failed[].";
            return;
        }

        var namesByFieldId = AgentScheduleWriteHelper.FieldNamesByFieldId(definition);
        var oldJson = AgentScheduleWriteHelper.SerializeFilters(definition, namesByFieldId);

        if (!definition.CanFilter() && built.Count > 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "filter_not_supported";
            outcome.Message = "This schedule type does not support filters.";
            return;
        }

        definition.ClearFilters();
        foreach (var (_, _, filter) in built)
        {
            try
            {
                definition.AddFilter(filter);
            }
            catch (Exception ex)
            {
                // AddFilter is the final authority on value/field compatibility;
                // restore the previous set and report the refused filter.
                definition.ClearFilters();
                RestoreFilters(definition, oldJson);
                outcome.Success = false;
                outcome.ErrorCode = "filter_value_not_supported";
                outcome.Message = "Revit refused a filter: " + ex.Message +
                                  " Nothing changed.";
                return;
            }
        }

        if (filtersElement.GetArrayLength() == 0)
        {
            if (oldJson == "[]")
            {
                outcome.Data["noChanges"] = true;
                outcome.SuppressChangeSet = true;
                return;
            }

            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Id = schedule.Id.GetValue(),
                Name = schedule.Name,
                OldValue = oldJson,
                NewValue = "[]",
                Reason = "Cleared all filters."
            });
        }
        else
        {
            foreach (var (fieldName, _, _) in built)
            {
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = schedule.Id.GetValue(),
                    Name = fieldName
                });
            }
        }

        var newJson = AgentScheduleWriteHelper.SerializeFilters(
            definition, AgentScheduleWriteHelper.FieldNamesByFieldId(definition));
        outcome.AffectedCount = outcome.Batch.Changed.Count;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = schedule.UniqueId,
            ElementId = schedule.Id.GetValue(),
            ParameterName = "schedule_filters",
            OldValue = oldJson,
            NewValue = newJson,
            Status = ChangeStatus.Applied
        });
        outcome.Data["scheduleId"] = schedule.Id.GetValue();
        outcome.Data["filters"] = JsonSerializer.Deserialize<JsonElement>(newJson);

        if (dryRun)
        {
            AppendDryRunEstimate(doc, schedule, outcome, ct);
        }
    }

    /// <summary>
    /// Preview of the effect (#452): the new filters are already applied to the
    /// definition inside the dry-run transaction, so the estimator evaluates
    /// them against the elements of the schedule category. Null means the
    /// definition cannot be evaluated this way (key schedule, calculated field)
    /// — the dry-run report then simply carries no estimate.
    /// </summary>
    private static void AppendDryRunEstimate(
        Document doc,
        ViewSchedule schedule,
        WriteOutcome outcome,
        CancellationToken ct)
    {
        var estimate = AgentScheduleFilterEstimator.Estimate(doc, schedule, ct);
        if (estimate is null)
            return;

        var (count, sample) = estimate.Value;
        outcome.Data["estimatedRowsAfter"] = count;
        outcome.Data["estimatedSampleNames"] = sample;
        outcome.Data["estimateNote"] =
            "Element count under the new filters (itemized schedules: rows == elements; " +
            "grouped schedules may show fewer rows).";
    }

    private static bool CanFilterBySubstring(ScheduleDefinition definition, ScheduleFieldId fieldId)
    {
        try
        {
            return definition.CanFilterBySubstring(fieldId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void RestoreFilters(ScheduleDefinition definition, string json)
    {
        var records = JsonSerializer.Deserialize<List<ScheduleFilterRecord>>(json);
        if (records is null)
            return;

        foreach (var record in records)
        {
            var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(definition, record.Field);
            if (fieldId is null)
                continue;

            definition.AddFilter(AgentScheduleWriteHelper.BuildFilter(record, fieldId));
        }
    }
}
