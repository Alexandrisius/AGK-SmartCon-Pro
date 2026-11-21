using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Deletes schedules (ViewSchedule) by id or name. Titleblock revision and
/// internal keynote schedules are managed by Revit and refused per item. The
/// apply requires confirmNames equal to a fresh recomputation of the target
/// names (S.5). Destructive: not reversible by undo_last.
/// </summary>
public sealed class DeleteSchedulesTool : AgentWriteToolBase
{
    private const int MaxSchedules = 50;

    public DeleteSchedulesTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "delete_schedules",
        Description =
            "Deletes schedules (specifications) by scheduleIds or exact scheduleNames " +
            "(up to 50 in total; several schedules with one name → ambiguous_schedule with " +
            "candidates). Titleblock revision and internal keynote schedules are refused with " +
            "schedule_not_deletable — Revit manages them. The dry run returns names[]; apply " +
            "requires confirmNames equal to a FRESH dry-run names[] list (a model change " +
            "refuses the call). Destructive: deletion cannot be undone by undo_last. " +
            "Dry run by default.",
        Risk = AgentToolRisk.Destructive,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleIds"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""integer""}},""scheduleNames"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""string""}},""confirmNames"":{""type"":""array"",""items"":{""type"":""string""}},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var scheduleIds = AgentJson.GetLongArray(args, "scheduleIds");
        var scheduleNames = AgentJson.GetStringArray(args, "scheduleNames");
        var total = scheduleIds.Count + scheduleNames.Count;
        if (total == 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass scheduleIds and/or scheduleNames (1.." + MaxSchedules +
                              " schedules in total).";
            return;
        }

        if (total > MaxSchedules)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "At most " + MaxSchedules +
                              " schedules can be deleted per call; split the request.";
            return;
        }

        if (!TryResolve(doc, scheduleIds, scheduleNames, outcome, ct,
                out var targets, out var failedCount))
        {
            return;
        }

        var names = targets.Select(target => target.Name).ToArray();
        AgentDeleteHelper.PublishNames(outcome, names);

        var items = new List<object>(targets.Count);
        foreach (var target in targets)
        {
            items.Add(new { scheduleId = target.Id, name = target.Name });
        }

        outcome.Data["items"] = items;

        if (dryRun)
        {
            foreach (var target in targets)
            {
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Id = target.Id,
                    Name = target.Name,
                    Reason = "Dry run: the schedule would be deleted."
                });
            }

            outcome.AffectedCount = targets.Count;
            if (failedCount > 0)
            {
                outcome.Success = false;
                outcome.ErrorCode ??= "partial_failure";
                outcome.Message = targets.Count + " schedule(s) planned, " + failedCount +
                                  " failed. See failed[].";
            }

            return;
        }

        var confirm = AgentDeleteConfirm.Verify(names, AgentDeleteHelper.GetConfirmNames(args));
        if (!confirm.Ok)
        {
            AgentDeleteHelper.RefuseConfirmation(outcome, confirm);
            return;
        }

        var deletedCount = AgentDeleteHelper.DeleteBatch(doc, targets, outcome, "schedules", ct);
        outcome.AffectedCount = deletedCount;

        if (outcome.Batch.Failed.Count > 0)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= "partial_failure";
            outcome.Message = deletedCount + " schedule(s) deleted, " +
                              outcome.Batch.Failed.Count + " failed. See failed[].";
        }

        if (deletedCount > 0)
            AgentDeleteHelper.MarkDestructiveApplied(outcome, "schedules");
        else
            outcome.SuppressChangeSet = true;
    }

    /// <summary>
    /// Resolves ids and names to deletable targets. Per-item failures are
    /// recorded in failed[]; an ambiguous name fails the whole call with
    /// candidates because the model must not delete a random match.
    /// </summary>
    private static bool TryResolve(
        Document doc,
        IReadOnlyList<long> scheduleIds,
        IReadOnlyList<string> scheduleNames,
        WriteOutcome outcome,
        CancellationToken ct,
        out List<AgentDeleteHelper.DeleteTarget> targets,
        out int failedCount)
    {
        targets = new List<AgentDeleteHelper.DeleteTarget>();
        failedCount = 0;
        var seen = new HashSet<long>();

        foreach (var scheduleId in scheduleIds)
        {
            ct.ThrowIfCancellationRequested();

            if (!seen.Add(scheduleId))
                continue;

            var report = new WriteItemReport { Id = scheduleId };
            if (doc.GetElement(ElementIdCompat.Create(scheduleId)) is not ViewSchedule schedule)
            {
                report.ErrorCode = "schedule_not_found";
                report.Reason = "Element " + scheduleId +
                                " is not a schedule. Use list_schedules to find the id.";
                outcome.Batch.Failed.Add(report);
                failedCount++;
                continue;
            }

            if (!TryAccept(schedule, report, outcome, targets, ref failedCount))
                return false;
        }

        foreach (var scheduleName in scheduleNames)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(scheduleName))
                continue;

            var matches = new List<ViewSchedule>();
            foreach (var candidate in AgentScheduleReader.Collect(doc))
            {
                if (string.Equals(candidate.Name, scheduleName, StringComparison.OrdinalIgnoreCase))
                    matches.Add(candidate);
            }

            var report = new WriteItemReport { Name = scheduleName };
            if (matches.Count == 0)
            {
                report.ErrorCode = "schedule_not_found";
                report.Reason = "No schedule named '" + scheduleName +
                                "'. Use list_schedules to see the names.";
                outcome.Batch.Failed.Add(report);
                failedCount++;
                continue;
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

                outcome.Success = false;
                outcome.ErrorCode = "ambiguous_schedule";
                outcome.Message = matches.Count + " schedules are named '" + scheduleName +
                                  "'; pass scheduleIds instead.";
                outcome.Data["candidates"] = candidates;
                return false;
            }

            var found = matches[0];
            report.Id = found.Id.GetValue();
            if (!seen.Add(found.Id.GetValue()))
                continue;

            if (!TryAccept(found, report, outcome, targets, ref failedCount))
                return false;
        }

        return true;
    }

    private static bool TryAccept(
        ViewSchedule schedule,
        WriteItemReport report,
        WriteOutcome outcome,
        List<AgentDeleteHelper.DeleteTarget> targets,
        ref int failedCount)
    {
        report.Name = schedule.Name;
        if (schedule.IsTitleblockRevisionSchedule)
        {
            report.ErrorCode = "schedule_not_deletable";
            report.Reason = "Schedule '" + schedule.Name +
                            "' is a titleblock revision schedule managed by Revit; " +
                            "it cannot be deleted.";
            outcome.Batch.Failed.Add(report);
            failedCount++;
            return true;
        }

        if (schedule.IsInternalKeynoteSchedule)
        {
            report.ErrorCode = "schedule_not_deletable";
            report.Reason = "Schedule '" + schedule.Name +
                            "' is an internal keynote schedule managed by Revit; " +
                            "it cannot be deleted.";
            outcome.Batch.Failed.Add(report);
            failedCount++;
            return true;
        }

        targets.Add(new AgentDeleteHelper.DeleteTarget
        {
            Id = schedule.Id.GetValue(),
            Name = schedule.Name
        });
        return true;
    }
}
