using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Controls the totals of an existing schedule: per-column display mode
/// (totals/min/max via ScheduleField.DisplayType) and the grand total row
/// (ScheduleDefinition.ShowGrandTotal*). Undo restores the previous state.
/// </summary>
public sealed class SetScheduleTotalsTool : AgentWriteToolBase
{
    public SetScheduleTotalsTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_schedule_totals",
        Description =
            "Enables or disables totals of an existing schedule. fields is 0..15 objects " +
            "{name, mode} where mode is standard (no totals), totals (sum/count), min, " +
            "max or minmax — a field that cannot total fails per item with " +
            "field_cannot_total. grandTotal (bool) shows the grand total row at the " +
            "bottom; grandTotalCount adds the element count; grandTotalTitle adds a " +
            "title row with the given text. Totals need a non-itemized schedule to " +
            "display per group. Returns the read-back totals state; undo_last restores " +
            "it. Dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""fields"":{""type"":""array"",""maxItems"":15,""items"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""mode"":{""type"":""string"",""enum"":[""standard"",""totals"",""minmax"",""min"",""max""]}},""required"":[""name"",""mode""],""additionalProperties"":false}},""grandTotal"":{""type"":""boolean"",""description"":""Show the grand total row""},""grandTotalCount"":{""type"":""boolean"",""description"":""Show the element count in the grand total row""},""grandTotalTitle"":{""type"":""string"",""description"":""Title text of the grand total row""},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var hasFields = args.TryGetProperty("fields", out var fieldsElement)
            && fieldsElement.ValueKind == JsonValueKind.Array
            && fieldsElement.GetArrayLength() > 0;
        var hasGrandTotal = args.TryGetProperty("grandTotal", out _)
            || args.TryGetProperty("grandTotalCount", out _)
            || args.TryGetProperty("grandTotalTitle", out _);
        if (!hasFields && !hasGrandTotal)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message =
                "Pass fields[{name, mode}] and/or grandTotal/grandTotalCount/grandTotalTitle.";
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
        var oldSnapshot = AgentScheduleWriteHelper.SerializeTotals(definition);
        outcome.Description = "Set totals of schedule " + schedule.Name;
        var changed = 0;

        if (hasFields)
        {
            foreach (var fieldElement in fieldsElement.EnumerateArray())
            {
                ct.ThrowIfCancellationRequested();

                var fieldName = fieldElement.TryGetProperty("name", out var nameElement)
                    ? nameElement.GetString()
                    : null;
                var mode = fieldElement.TryGetProperty("mode", out var modeElement)
                    ? modeElement.GetString()
                    : null;
                var report = new WriteItemReport
                {
                    Id = schedule.Id.GetValue(),
                    Name = fieldName ?? string.Empty
                };

                if (!AgentScheduleWriteHelper.TryParseDisplayMode(mode, out var displayType))
                {
                    report.ErrorCode = "invalid_arguments";
                    report.Reason = "mode must be standard, totals, minmax, min or max.";
                    outcome.Batch.Failed.Add(report);
                    continue;
                }

                var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(definition, fieldName ?? string.Empty);
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

                bool canTotal;
                try
                {
                    canTotal = displayType == ScheduleFieldDisplayType.Standard || field.CanTotal();
                }
                catch (Exception)
                {
                    canTotal = false;
                }

                if (!canTotal)
                {
                    report.ErrorCode = "field_cannot_total";
                    report.Reason = "The field '" + fieldName + "' cannot display totals " +
                                    "(text fields can only be standard/counted).";
                    outcome.Batch.Failed.Add(report);
                    continue;
                }

                try
                {
                    var current = field.DisplayType;
                    if (current == displayType)
                    {
                        report.Reason = "The field already has mode '" + mode + "' (no-op).";
                        outcome.Batch.Skipped.Add(report);
                        continue;
                    }

                    field.DisplayType = displayType;
                    report.OldValue = AgentScheduleWriteHelper.ModeName(current);
                    report.NewValue = mode;
                    outcome.Batch.Changed.Add(report);
                    changed++;
                }
                catch (Exception ex)
                {
                    report.ErrorCode = "set_failed";
                    report.Reason = ex.GetType().Name + ": " + ex.Message;
                    outcome.Batch.Failed.Add(report);
                }
            }
        }

        if (TrySetGrandTotal(args, definition, outcome, ref changed))
            changed++;

        if (changed == 0)
        {
            outcome.SuppressChangeSet = true;
            outcome.Data["totals"] = AgentScheduleReadHelper.TotalsState(definition);
            return;
        }

        outcome.AffectedCount = changed;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = schedule.UniqueId,
            ElementId = schedule.Id.GetValue(),
            ParameterName = "schedule_totals",
            OldValue = oldSnapshot,
            NewValue = AgentScheduleWriteHelper.SerializeTotals(definition),
            Status = ChangeStatus.Applied
        });
        outcome.Data["scheduleId"] = schedule.Id.GetValue();
        outcome.Data["totals"] = AgentScheduleReadHelper.TotalsState(definition);
    }

    private static bool TrySetGrandTotal(
        JsonElement args,
        ScheduleDefinition definition,
        WriteOutcome outcome,
        ref int changed)
    {
        var touched = false;
        try
        {
            var initial = AgentScheduleWriteHelper.SerializeTotals(definition);
            var targetRow = TryGetBool(args, "grandTotal", definition.ShowGrandTotal);
            var targetCount = TryGetBool(args, "grandTotalCount", definition.ShowGrandTotalCount);
            var targetTitleShown = definition.ShowGrandTotalTitle;
            string? targetTitle = definition.GrandTotalTitle;
            if (args.TryGetProperty("grandTotalTitle", out var titleElement))
            {
                targetTitle = titleElement.ValueKind == JsonValueKind.String
                    ? titleElement.GetString()
                    : null;
                targetTitleShown = !string.IsNullOrEmpty(targetTitle);
            }

            // The sub-flags may only be touched while the row is on ("Display of a
            // grand total row is not enabled") — enable the row first, then the
            // sub-flags, then drop the row when the target state has it off.
            if (targetRow || targetCount || targetTitleShown)
                definition.ShowGrandTotal = true;
            definition.ShowGrandTotalCount = targetCount;
            definition.ShowGrandTotalTitle = targetTitleShown;
            if (targetTitleShown)
                definition.GrandTotalTitle = targetTitle;
            definition.ShowGrandTotal = targetRow;

            touched = AgentScheduleWriteHelper.SerializeTotals(definition) != initial;
        }
        catch (Exception ex)
        {
            outcome.Batch.Failed.Add(new WriteItemReport
            {
                ErrorCode = "set_failed",
                Name = "grandTotal",
                Reason = ex.GetType().Name + ": " + ex.Message
            });
            return false;
        }

        if (touched)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Name = "grandTotal",
                NewValue = "grand total row settings"
            });
        }

        return touched;
    }

    private static bool TryGetBool(JsonElement args, string name, bool fallback)
    {
        return args.TryGetProperty(name, out var element)
               && element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : fallback;
    }
}
