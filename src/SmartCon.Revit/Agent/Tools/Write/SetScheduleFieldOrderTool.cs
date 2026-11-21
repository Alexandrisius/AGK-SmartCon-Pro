using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Reorders the columns of an existing schedule: fields is the complete list of
/// the current display names in the new order. Undo restores the previous order
/// from the schedule_fields snapshot.
/// </summary>
public sealed class SetScheduleFieldOrderTool : AgentWriteToolBase
{
    public SetScheduleFieldOrderTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_schedule_field_order",
        Description =
            "Sets the column order of an existing schedule: fields is the COMPLETE list " +
            "of the schedule's current field display names in the new order (read " +
            "get_schedule_definition first, then pass the same names reordered). The set " +
            "of names must match exactly — a missing or extra name fails with " +
            "order_mismatch. The same order as current is a no-op. Returns the read-back " +
            "order; undo_last restores the previous one. Dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""fields"":{""type"":""array"",""minItems"":1,""maxItems"":64,""items"":{""type"":""string""}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""fields""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var fields = AgentJson.GetStringArray(args, "fields");
        if (fields.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass fields — the complete field name list in the new order.";
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
        var oldFieldNames = AgentScheduleWriteHelper.CurrentFieldNames(definition);
        outcome.Description = "Reorder fields of schedule " + schedule.Name;

        var missing = fields
            .Where(name => !oldFieldNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        var extra = oldFieldNames
            .Where(name => !fields.Contains(name, StringComparer.OrdinalIgnoreCase))
            .ToList();
        if (missing.Count > 0 || extra.Count > 0 || fields.Count != oldFieldNames.Count)
        {
            outcome.Success = false;
            outcome.ErrorCode = "order_mismatch";
            outcome.Message = "fields must list every current field exactly once (" +
                              oldFieldNames.Count + " fields, " + fields.Count +
                              " passed). Missing: " + string.Join(", ", missing) +
                              "; not in schedule: " + string.Join(", ", extra) +
                              ". Read get_schedule_definition and pass the same names reordered.";
            return;
        }

        var sameOrder = oldFieldNames.Zip(fields, (oldName, newName) =>
            string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase)).All(equal => equal);
        if (sameOrder)
        {
            outcome.SuppressChangeSet = true;
            outcome.Data["fields"] = oldFieldNames;
            outcome.Message = "The schedule already has this field order (no-op).";
            return;
        }

        var order = new List<ScheduleFieldId>();
        foreach (var fieldName in fields)
        {
            var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(definition, fieldName);
            if (fieldId is not null)
                order.Add(fieldId);
        }

        try
        {
            definition.SetFieldOrder(order);
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "set_failed";
            outcome.Message = "Revit refused the new field order: " + ex.Message;
            return;
        }

        var readBackFields = AgentScheduleWriteHelper.CurrentFieldNames(definition);
        outcome.AffectedCount = 1;
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = schedule.Id.GetValue(),
            Name = schedule.Name,
            OldValue = string.Join(", ", oldFieldNames),
            NewValue = string.Join(", ", readBackFields)
        });
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = schedule.UniqueId,
            ElementId = schedule.Id.GetValue(),
            ParameterName = "schedule_fields",
            OldValue = JsonSerializer.Serialize(oldFieldNames),
            NewValue = JsonSerializer.Serialize(readBackFields),
            Status = ChangeStatus.Applied
        });
        outcome.Data["scheduleId"] = schedule.Id.GetValue();
        outcome.Data["fields"] = readBackFields;
    }
}
