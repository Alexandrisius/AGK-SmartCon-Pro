using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Adds fields (columns) to an existing schedule, matching schedulable fields
/// by display name. A field already present is skipped (no-op); an unknown
/// field fails per item with field_not_available and availableFields[].
/// </summary>
public sealed class AddScheduleFieldTool : AgentWriteToolBase
{
    private const int AvailableFieldsHint = 20;

    public AddScheduleFieldTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "add_schedule_field",
        Description =
            "Adds fields (columns) to an existing schedule by scheduleId or exact " +
            "scheduleName. fields (1..15) are schedulable field display names — the 'name' " +
            "values get_schedule_definition reports, matched case-insensitively. A field " +
            "already in the schedule is skipped (no-op, undo stays simple); an unknown field " +
            "fails per item with field_not_available and availableFields[] (first 20). " +
            "Returns the read-back field list. Dry run defaults to true.",
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
        if (fields.Count == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass fields (1.." + AgentScheduleWriteHelper.MaxFieldsPerCall +
                              " field names).";
            return;
        }

        if (fields.Count > AgentScheduleWriteHelper.MaxFieldsPerCall)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "At most " + AgentScheduleWriteHelper.MaxFieldsPerCall +
                              " fields can be added per call.";
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
        var currentNames = new HashSet<string>(oldFieldNames, StringComparer.OrdinalIgnoreCase);
        outcome.Description = "Add " + fields.Count + " field(s) to schedule " + schedule.Name;

        foreach (var fieldName in fields)
        {
            ct.ThrowIfCancellationRequested();

            var report = new WriteItemReport
            {
                Id = schedule.Id.GetValue(),
                Name = fieldName
            };
            if (currentNames.Contains(fieldName))
            {
                report.Reason = "The field is already in the schedule (no-op).";
                outcome.Batch.Skipped.Add(report);
                continue;
            }

            if (!AgentScheduleWriteHelper.TryFindSchedulableField(
                    doc, definition, fieldName, out var schedulable))
            {
                report.ErrorCode = "field_not_available";
                report.Reason = "No schedulable field '" + fieldName +
                                "' for the schedule category.";
                outcome.Batch.Failed.Add(report);
                if (!outcome.Data.ContainsKey("availableFields"))
                {
                    outcome.Data["availableFields"] = AgentScheduleWriteHelper.AvailableFieldNames(
                        doc, definition, AvailableFieldsHint);
                }

                continue;
            }

            try
            {
                definition.AddField(schedulable!);
                currentNames.Add(fieldName);
                report.NewValue = fieldName;
                outcome.Batch.Changed.Add(report);
            }
            catch (Exception ex)
            {
                report.ErrorCode = "field_not_available";
                report.Reason = "Revit refused field '" + fieldName + "': " + ex.Message;
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
