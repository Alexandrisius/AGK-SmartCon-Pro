using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Replaces the sorting/grouping of a schedule (max 4 levels) and optionally
/// the itemized flag. An empty list removes sorting.
/// </summary>
public sealed class SetScheduleSortTool : AgentWriteToolBase
{
    public SetScheduleSortTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_schedule_sort",
        Description =
            "Replaces the sorting/grouping of a schedule by scheduleId or exact scheduleName. " +
            "sortGroups (0..4; [] removes sorting) — each {field, order: asc|desc, " +
            "showHeader?, showFooter?, showBlankLine?}; the group decoration flags add a " +
            "header row before each group, a footer row (with the enabled column totals) " +
            "after it and a blank line between groups. The field must already be a column " +
            "of the schedule (add_schedule_field first) — otherwise field_not_in_schedule. " +
            "itemized (optional bool) sets whether every element gets its own row (true) " +
            "or grouped rows are combined (false). Read the result back with " +
            "get_schedule_definition (sortGroupFields, isItemized). Dry run defaults " +
            "to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""sortGroups"":{""type"":""array"",""maxItems"":4,""items"":{""type"":""object"",""properties"":{""field"":{""type"":""string""},""order"":{""type"":""string"",""enum"":[""asc"",""desc""],""default"":""asc""},""showHeader"":{""type"":""boolean""},""showFooter"":{""type"":""boolean""},""showBlankLine"":{""type"":""boolean""}},""required"":[""field""],""additionalProperties"":false}},""itemized"":{""type"":""boolean""},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("sortGroups", out var groupsElement)
            || groupsElement.ValueKind != JsonValueKind.Array)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass sortGroups (0.." + AgentScheduleWriteHelper.MaxSortFields +
                              " sort objects; [] removes sorting) and optionally itemized.";
            return;
        }

        if (groupsElement.GetArrayLength() > AgentScheduleWriteHelper.MaxSortFields)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Revit allows at most " + AgentScheduleWriteHelper.MaxSortFields +
                              " sorting/grouping levels.";
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
        var hasItemized = args.TryGetProperty("itemized", out var itemizedElement)
                          && itemizedElement.ValueKind is JsonValueKind.True or JsonValueKind.False;
        var requestedItemized = hasItemized && itemizedElement.ValueKind == JsonValueKind.True;
        outcome.Description = "Set sorting on schedule " + schedule.Name;

        var built = new List<(string Field, ScheduleFieldId FieldId, ScheduleSortOrder Order,
            bool? ShowHeader, bool? ShowFooter, bool? ShowBlankLine)>();
        foreach (var groupElement in groupsElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            var field = groupElement.ValueKind == JsonValueKind.Object
                ? groupElement.GetProperty("field").GetString()
                : null;
            var orderText = groupElement.ValueKind == JsonValueKind.Object &&
                            groupElement.TryGetProperty("order", out var orderElement) &&
                            orderElement.ValueKind == JsonValueKind.String
                ? orderElement.GetString()
                : "asc";
            var showHeader = ReadOptionalFlag(groupElement, "showHeader");
            var showFooter = ReadOptionalFlag(groupElement, "showFooter");
            var showBlankLine = ReadOptionalFlag(groupElement, "showBlankLine");

            var report = new WriteItemReport
            {
                Id = schedule.Id.GetValue(),
                Name = field ?? string.Empty
            };

            if (string.IsNullOrWhiteSpace(field))
            {
                report.ErrorCode = "invalid_arguments";
                report.Reason = "Each sort group needs field (and optional order).";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(definition, field!);
            if (fieldId is null)
            {
                report.ErrorCode = "field_not_in_schedule";
                report.Reason = "Field '" + field + "' is not a column of this schedule; " +
                                "add it with add_schedule_field first.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            if (!CanSortByField(definition, fieldId))
            {
                report.ErrorCode = "filter_value_not_supported";
                report.Reason = "The schedule cannot sort by '" + field + "'.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            built.Add((field!,
                fieldId,
                string.Equals(orderText, "desc", StringComparison.OrdinalIgnoreCase)
                    ? ScheduleSortOrder.Descending
                    : ScheduleSortOrder.Ascending,
                showHeader,
                showFooter,
                showBlankLine));
        }

        if (outcome.Batch.Failed.Count > 0)
        {
            outcome.SuppressChangeSet = true;
            outcome.Message = outcome.Batch.Failed.Count + " sort group(s) invalid; nothing " +
                              "changed. See failed[].";
            return;
        }

        var oldJson = AgentScheduleWriteHelper.SerializeSort(definition);
        definition.ClearSortGroupFields();
        foreach (var (_, fieldId, order, showHeader, showFooter, showBlankLine) in built)
        {
            try
            {
                var sortGroupField = new ScheduleSortGroupField(fieldId, order);
                if (showHeader.HasValue)
                    sortGroupField.ShowHeader = showHeader.Value;
                if (showFooter.HasValue)
                    sortGroupField.ShowFooter = showFooter.Value;
                if (showBlankLine.HasValue)
                    sortGroupField.ShowBlankLine = showBlankLine.Value;
                definition.AddSortGroupField(sortGroupField);
            }
            catch (Exception ex)
            {
                definition.ClearSortGroupFields();
                RestoreSort(definition, oldJson);
                outcome.Success = false;
                outcome.ErrorCode = "filter_value_not_supported";
                outcome.Message = "Revit refused a sort group: " + ex.Message + " Nothing changed.";
                return;
            }
        }

        var itemizedChanged = false;
        if (hasItemized)
        {
            try
            {
                var currentItemized = definition.IsItemized;
                definition.IsItemized = requestedItemized;
                itemizedChanged = currentItemized != requestedItemized;
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.ErrorCode = "filter_value_not_supported";
                outcome.Message = "The schedule refused the itemized flag: " + ex.Message;
                return;
            }
        }

        var newJson = AgentScheduleWriteHelper.SerializeSort(definition);
        if (newJson == oldJson)
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
            NewValue = newJson,
            Detail = built.Count + " sort level(s), itemized=" +
                     (hasItemized ? requestedItemized.ToString() : "unchanged")
        });
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = schedule.UniqueId,
            ElementId = schedule.Id.GetValue(),
            ParameterName = "schedule_sort",
            OldValue = oldJson,
            NewValue = newJson,
            Status = ChangeStatus.Applied
        });
        outcome.Data["scheduleId"] = schedule.Id.GetValue();
        outcome.Data["sortGroups"] = JsonSerializer.Deserialize<ScheduleSortRecord>(newJson);
        outcome.Data["itemized"] = SafeItemized(definition);
        if (itemizedChanged)
            outcome.Data["itemizedChanged"] = true;
    }

    private static bool? ReadOptionalFlag(JsonElement groupElement, string name) =>
        groupElement.TryGetProperty(name, out var element)
        && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.ValueKind == JsonValueKind.True
            : null;

    private static bool CanSortByField(ScheduleDefinition definition, ScheduleFieldId fieldId)
    {
        try
        {
            return definition.CanSortByField(fieldId);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool SafeItemized(ScheduleDefinition definition)
    {
        try
        {
            return definition.IsItemized;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal static void RestoreSort(ScheduleDefinition definition, string json)
    {
        var record = JsonSerializer.Deserialize<ScheduleSortRecord>(json);
        if (record is null)
            return;

        definition.ClearSortGroupFields();
        foreach (var group in record.SortGroups)
        {
            var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(definition, group.Field);
            if (fieldId is null)
                continue;

            var sortGroupField = new ScheduleSortGroupField(
                fieldId,
                string.Equals(group.Order, "desc", StringComparison.OrdinalIgnoreCase)
                    ? ScheduleSortOrder.Descending
                    : ScheduleSortOrder.Ascending);
            if (group.ShowHeader.HasValue)
                sortGroupField.ShowHeader = group.ShowHeader.Value;
            if (group.ShowFooter.HasValue)
                sortGroupField.ShowFooter = group.ShowFooter.Value;
            if (group.ShowBlankLine.HasValue)
                sortGroupField.ShowBlankLine = group.ShowBlankLine.Value;
            definition.AddSortGroupField(sortGroupField);
        }

        try
        {
            definition.IsItemized = record.Itemized;
        }
        catch (Exception)
        {
            // Key schedules refuse IsItemized.
        }
    }
}
