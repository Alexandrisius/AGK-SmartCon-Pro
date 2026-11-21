using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Removes fields (columns) from an existing schedule by display name. The
/// recorded schedule_fields snapshot lets undo_last re-add the removed fields.
/// </summary>
public sealed class RemoveScheduleFieldTool : AgentWriteToolBase
{
    public RemoveScheduleFieldTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "remove_schedule_field",
        Description =
            "Removes fields (columns) from an existing schedule by scheduleId or exact " +
            "scheduleName. fields (1..15) are the display names get_schedule_definition " +
            "reports, matched case-insensitively. A field not in the schedule is skipped " +
            "(no-op); a field used by the schedule structure that Revit refuses to remove " +
            "fails per item with field_in_use. Returns the read-back field list; " +
            "undo_last re-adds the removed fields. Dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""fields"":{""type"":""array"",""minItems"":1,""maxItems"":15,""items"":{""type"":""string""}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""fields""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var fields = AgentJson.GetStringArray(args, "fields");
        if (fields.Count == 0 || fields.Count > AgentScheduleWriteHelper.MaxFieldsPerCall)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass fields (1.." + AgentScheduleWriteHelper.MaxFieldsPerCall +
                              " field names).";
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
        outcome.Description = "Remove " + fields.Count + " field(s) from schedule " + schedule.Name;

        foreach (var fieldName in fields)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport
            {
                Id = schedule.Id.GetValue(),
                Name = fieldName
            };

            var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(definition, fieldName);
            if (fieldId is null)
            {
                report.Reason = "The field is not in the schedule (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            try
            {
                definition.RemoveField(fieldId);
                report.OldValue = fieldName;
                outcome.Batch.Changed.Add(report);
            }
            catch (Exception ex)
            {
                report.ErrorCode = "field_in_use";
                report.Reason = "Revit refused to remove field '" + fieldName +
                                "' (it may be used by sorting or filters): " + ex.Message;
                outcome.Batch.Failed.Add(report);
            }
        }

        if (outcome.Batch.Changed.Count == 0)
        {
            outcome.SuppressChangeSet = true;
            outcome.Data["fields"] = oldFieldNames;
            return;
        }

        var readBackFields = AgentScheduleWriteHelper.CurrentFieldNames(definition);
        outcome.AffectedCount = outcome.Batch.Changed.Count;
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
