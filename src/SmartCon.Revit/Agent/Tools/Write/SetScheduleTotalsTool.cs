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
            "bottom; false turns it off and clears the dependent count/title flags with it " +
            "(turning the row off never fails), and a count or a title alone switches the row " +
            "on. grandTotalCount adds the element count; grandTotalTitle adds a " +
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

            var rowSpecified = args.TryGetProperty("grandTotal", out var rowElement)
                && rowElement.ValueKind is JsonValueKind.True or JsonValueKind.False;
            var countSpecified = args.TryGetProperty("grandTotalCount", out var countElement)
                && countElement.ValueKind is JsonValueKind.True or JsonValueKind.False;
            var countValue = countSpecified && countElement.GetBoolean();
            var titleSpecified = args.TryGetProperty("grandTotalTitle", out var titleElement);
            var titleText = titleSpecified && titleElement.ValueKind == JsonValueKind.String
                ? titleElement.GetString()
                : null;
            var titleOn = titleSpecified
                ? !string.IsNullOrEmpty(titleText)
                : definition.ShowGrandTotalTitle;

            // An explicit grandTotal is the answer. Without it a count or a title
            // switches the row on (asking for the count of a row that is off is
            // still a request for the row), and grandTotal:false alone switches
            // it off - the call that used to fail with "grand total row is not
            // enabled" after the column totals had already been applied.
            var rowOn = rowSpecified
                ? rowElement.GetBoolean()
                : definition.ShowGrandTotal
                  || (countSpecified && countValue)
                  || (titleSpecified && titleOn);

            if (rowOn)
            {
                if (!definition.ShowGrandTotal)
                    definition.ShowGrandTotal = true;

                var targetCount = countSpecified ? countValue : definition.ShowGrandTotalCount;
                if (definition.ShowGrandTotalCount != targetCount)
                    definition.ShowGrandTotalCount = targetCount;

                if (titleSpecified)
                {
                    // The flag goes first: Revit resets the title text while the
                    // title row is off, so a title written first reads back as
                    // the built-in default.
                    if (definition.ShowGrandTotalTitle != titleOn)
                        definition.ShowGrandTotalTitle = titleOn;
                    if (!string.Equals(definition.GrandTotalTitle, titleText, StringComparison.Ordinal))
                        definition.GrandTotalTitle = titleText;
                }
            }
            else
            {
                // The dependent flags cannot outlive the row: Revit throws while
                // they are set and the row is off, so clear them first and drop
                // the row last.
                if (definition.ShowGrandTotalCount)
                    definition.ShowGrandTotalCount = false;
                if (definition.ShowGrandTotalTitle)
                    definition.ShowGrandTotalTitle = false;
                if (definition.ShowGrandTotal)
                    definition.ShowGrandTotal = false;
            }

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
}
