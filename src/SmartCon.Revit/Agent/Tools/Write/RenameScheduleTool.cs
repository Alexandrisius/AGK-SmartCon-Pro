using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Renames a schedule (the archive step of the on-place replacement flow: the
/// old schedule keeps its setup under a name with an archive marker, freeing
/// the original name for the rebuilt schedule). Undo restores the old name.
/// </summary>
public sealed class RenameScheduleTool : AgentWriteToolBase
{
    public RenameScheduleTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "rename_schedule",
        Description =
            "Renames a schedule by scheduleId or exact scheduleName. The new name must be " +
            "free (a duplicate fails with schedule_name_taken); renaming to the current " +
            "name is a no-op. Typical use: mark the old schedule as archived ('Спецификация " +
            "труб — исходная') so its name and place become free for the rebuilt schedule " +
            "(clone_schedule). undo_last restores the previous name. Dry run defaults " +
            "to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string"",""description"":""Current exact name of the schedule""},""newName"":{""type"":""string"",""description"":""New unique name, e.g. Спецификация труб — исходная""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""newName""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        if (!AgentScheduleWriteHelper.TryResolveSchedule(
                doc, args, out var schedule, out var errorCode, out var message))
        {
            outcome.Success = false;
            outcome.ErrorCode = errorCode;
            outcome.Message = message;
            return;
        }

        var newName = AgentJson.GetString(args, "newName");
        if (string.IsNullOrWhiteSpace(newName))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass newName (the new unique schedule name).";
            return;
        }

        var trimmed = newName!.Trim();
        var oldName = schedule!.Name;
        outcome.Description = "Rename schedule " + oldName + " to " + trimmed;

        if (string.Equals(oldName, trimmed, StringComparison.OrdinalIgnoreCase))
        {
            outcome.SuppressChangeSet = true;
            outcome.Data["scheduleId"] = schedule.Id.GetValue();
            outcome.Data["oldName"] = oldName;
            outcome.Data["newName"] = trimmed;
            outcome.Message = "The schedule is already named '" + trimmed + "' (no-op).";
            return;
        }

        foreach (var existing in AgentScheduleReader.Collect(doc))
        {
            if (existing.Id == schedule.Id)
                continue;

            if (string.Equals(existing.Name, trimmed, StringComparison.OrdinalIgnoreCase))
            {
                outcome.Success = false;
                outcome.ErrorCode = "schedule_name_taken";
                outcome.Message = "Schedule '" + trimmed + "' already exists (id " +
                                  existing.Id.GetValue() + "). Choose another name.";
                outcome.Data["owner"] = new
                {
                    scheduleId = existing.Id.GetValue(),
                    name = existing.Name
                };
                return;
            }
        }

        var report = new WriteItemReport
        {
            Id = schedule.Id.GetValue(),
            Name = oldName,
            OldValue = oldName,
            NewValue = trimmed
        };

        try
        {
            schedule.Name = trimmed;
        }
        catch (Exception ex)
        {
            report.ErrorCode = "set_failed";
            report.Reason = "Revit refused the name '" + trimmed + "': " +
                            ex.GetType().Name + ": " + ex.Message;
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            outcome.Message = report.Reason;
            return;
        }

        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = schedule.UniqueId,
            ElementId = schedule.Id.GetValue(),
            ParameterName = "schedule_renamed",
            OldValue = oldName,
            NewValue = schedule.Name,
            Status = ChangeStatus.Applied
        });
        outcome.Data["scheduleId"] = schedule.Id.GetValue();
        outcome.Data["oldName"] = oldName;
        outcome.Data["newName"] = schedule.Name;
    }
}
