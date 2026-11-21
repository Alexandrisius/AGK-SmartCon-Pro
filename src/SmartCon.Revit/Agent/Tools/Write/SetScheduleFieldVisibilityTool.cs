using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Hides or unhides schedule columns: a hidden field keeps working in filters,
/// sorting and grouping but is not displayed (the pattern Revit uses for
/// service columns such as level or mark). Undo restores the previous
/// hidden-flags from the schedule_fields snapshot.
/// </summary>
public sealed class SetScheduleFieldVisibilityTool : AgentWriteToolBase
{
    public SetScheduleFieldVisibilityTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_schedule_field_visibility",
        Description =
            "Hides or shows columns of an existing schedule: fields is 1..15 objects " +
            "{name, hidden}. A hidden column keeps working in filters, sorting and " +
            "grouping — the standard way to use a service field (level, mark) without " +
            "showing it. A field not in the schedule fails per item with " +
            "field_not_in_schedule; a value that already matches is skipped (no-op). " +
            "Returns the read-back visibility of all fields; undo_last restores it. " +
            "Dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""fields"":{""type"":""array"",""minItems"":1,""maxItems"":15,""items"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""hidden"":{""type"":""boolean""}},""required"":[""name"",""hidden""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""fields""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        if (!args.TryGetProperty("fields", out var fieldsElement)
            || fieldsElement.ValueKind != JsonValueKind.Array
            || fieldsElement.GetArrayLength() == 0
            || fieldsElement.GetArrayLength() > AgentScheduleWriteHelper.MaxFieldsPerCall)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass fields (1.." + AgentScheduleWriteHelper.MaxFieldsPerCall +
                              " objects {name, hidden}).";
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
        var oldSnapshot = AgentScheduleWriteHelper.SerializeFieldStates(definition);
        outcome.Description = "Set field visibility of schedule " + schedule.Name;

        foreach (var fieldElement in fieldsElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            var fieldName = fieldElement.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString()
                : null;
            var hidden = fieldElement.TryGetProperty("hidden", out var hiddenElement)
                && hiddenElement.ValueKind == JsonValueKind.True;
            var report = new WriteItemReport
            {
                Id = schedule.Id.GetValue(),
                Name = fieldName ?? string.Empty
            };

            if (string.IsNullOrWhiteSpace(fieldName))
            {
                report.ErrorCode = "invalid_arguments";
                report.Reason = "Every field item needs a name.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(definition, fieldName!);
            var field = fieldId is null
                ? null
                : AgentScheduleWriteHelper.SafeGetField(definition, fieldId);
            if (field is null)
            {
                report.ErrorCode = "field_not_in_schedule";
                report.Reason = "No field '" + fieldName + "' in the schedule.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            bool currentHidden;
            try
            {
                currentHidden = field.IsHidden;
            }
            catch (Exception ex)
            {
                report.ErrorCode = "set_failed";
                report.Reason = ex.GetType().Name + ": " + ex.Message;
                outcome.Batch.Failed.Add(report);
                continue;
            }

            if (currentHidden == hidden)
            {
                report.Reason = "The field is already " + (hidden ? "hidden" : "visible") +
                                " (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            try
            {
                field.IsHidden = hidden;
                report.OldValue = currentHidden ? "hidden" : "visible";
                report.NewValue = hidden ? "hidden" : "visible";
                outcome.Batch.Changed.Add(report);
            }
            catch (Exception ex)
            {
                report.ErrorCode = "set_failed";
                report.Reason = ex.GetType().Name + ": " + ex.Message;
                outcome.Batch.Failed.Add(report);
            }
        }

        if (outcome.Batch.Changed.Count == 0)
        {
            outcome.SuppressChangeSet = true;
            outcome.Data["fields"] = AgentScheduleReadHelper.FieldStates(definition);
            return;
        }

        var readBack = AgentScheduleReadHelper.FieldStates(definition);
        outcome.AffectedCount = outcome.Batch.Changed.Count;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = schedule.UniqueId,
            ElementId = schedule.Id.GetValue(),
            ParameterName = "schedule_fields",
            OldValue = oldSnapshot,
            NewValue = AgentScheduleWriteHelper.SerializeFieldStates(definition),
            Status = ChangeStatus.Applied
        });
        outcome.Data["scheduleId"] = schedule.Id.GetValue();
        outcome.Data["fields"] = readBack;
    }
}
