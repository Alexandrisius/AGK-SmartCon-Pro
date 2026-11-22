using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Sets column headings and widths of a schedule (the formatting part of the
/// ГОСТ-style schedule rebuild). Widths are passed in millimetres and converted
/// to internal units; the whole column format snapshot is recorded for undo.
/// </summary>
public sealed class SetScheduleFieldFormatTool : AgentWriteToolBase
{
    public SetScheduleFieldFormatTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_schedule_field_format",
        Description =
            "Sets the formatting of schedule columns: fields is 1..50 objects " +
            "{field, heading?, widthMm?} where field is the column name as " +
            "get_schedule_definition reports it. heading replaces the column heading " +
            "text; widthMm sets the column width in millimetres (both grid and sheet " +
            "column). Values already equal to the current ones are skipped (no-op); " +
            "fields Revit refuses to format (calculated/computed columns for widths) " +
            "fail per item with field_not_formatable. undo_last restores the previous " +
            "heading/width snapshot. Dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""},""fields"":{""type"":""array"",""minItems"":1,""maxItems"":50,""items"":{""type"":""object"",""properties"":{""field"":{""type"":""string""},""heading"":{""type"":""string""},""widthMm"":{""type"":""number"",""description"":""Column width in millimetres""}},""required"":[""field""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""fields""],""additionalProperties"":false}"
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
            || fieldsElement.GetArrayLength() > AgentScheduleWriteHelper.MaxFormatFieldsPerCall)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass fields (1.." + AgentScheduleWriteHelper.MaxFormatFieldsPerCall +
                              " objects {field, heading?, widthMm?}).";
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
        var oldSnapshot = AgentScheduleWriteHelper.SerializeFieldFormats(definition);
        outcome.Description = "Set field format of schedule " + schedule.Name;

        foreach (var fieldElement in fieldsElement.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();

            var fieldName = fieldElement.ValueKind == JsonValueKind.Object
                ? fieldElement.GetProperty("field").GetString()
                : null;
            var report = new WriteItemReport
            {
                Id = schedule.Id.GetValue(),
                Name = fieldName ?? string.Empty
            };

            if (string.IsNullOrWhiteSpace(fieldName))
            {
                report.ErrorCode = "invalid_arguments";
                report.Reason = "Every field item needs field.";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var hasHeading = fieldElement.TryGetProperty("heading", out var headingElement)
                             && headingElement.ValueKind == JsonValueKind.String;
            double widthMm = 0;
            var hasWidth = fieldElement.TryGetProperty("widthMm", out var widthElement)
                           && widthElement.ValueKind == JsonValueKind.Number
                           && widthElement.TryGetDouble(out widthMm);

            if (!hasHeading && !hasWidth)
            {
                report.ErrorCode = "invalid_arguments";
                report.Reason = "Pass heading and/or widthMm for field '" + fieldName + "'.";
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
                report.Reason = "No field '" + fieldName + "' in the schedule (names are as " +
                                "get_schedule_definition reports them).";
                outcome.Batch.Failed.Add(report);
                continue;
            }

            var changed = false;
            if (hasHeading)
            {
                var heading = headingElement.GetString();
                if (ApplyHeading(field, heading, report))
                {
                    changed = true;
                }
                else if (!string.IsNullOrEmpty(report.ErrorCode))
                {
                    outcome.Batch.Failed.Add(report);
                    continue;
                }
            }

            if (hasWidth)
            {
                if (field.IsCalculatedField)
                {
                    report.ErrorCode = "field_not_formatable";
                    report.Reason = "Column '" + fieldName + "' is a calculated field; Revit " +
                                    "does not set its width through the column formatting API.";
                    outcome.Batch.Failed.Add(report);
                    continue;
                }

                string? oldWidth = null;
                var current = AgentScheduleWriteHelper.GetColumnWidthMm(field);
                if (current.HasValue)
                    oldWidth = current.Value.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture);

                if (current.HasValue && Math.Abs(current.Value - widthMm) < 0.05)
                {
                    report.Reason = "The column is already " + oldWidth + " mm wide (no-op).";
                }
                else if (AgentScheduleWriteHelper.TrySetColumnWidthMm(field, widthMm, out var widthReason))
                {
                    report.Detail = (report.Detail is null ? string.Empty : report.Detail + "; ") +
                                    "width " + oldWidth + " -> " +
                                    widthMm.ToString("0.0##", System.Globalization.CultureInfo.InvariantCulture) + " mm";
                    changed = true;
                }
                else
                {
                    report.ErrorCode = "field_not_formatable";
                    report.Reason = "Width of '" + fieldName + "': " + widthReason;
                    outcome.Batch.Failed.Add(report);
                    continue;
                }
            }

            if (changed)
                outcome.Batch.Changed.Add(report);
            else
                outcome.Batch.Skipped.Add(report);
        }

        if (outcome.Batch.Changed.Count == 0)
        {
            outcome.SuppressChangeSet = true;
            outcome.Data["scheduleId"] = schedule.Id.GetValue();
            outcome.Data["fields"] = ReadBackFormats(definition);
            return;
        }

        outcome.AffectedCount = outcome.Batch.Changed.Count;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = schedule.UniqueId,
            ElementId = schedule.Id.GetValue(),
            ParameterName = "schedule_field_format",
            OldValue = oldSnapshot,
            NewValue = AgentScheduleWriteHelper.SerializeFieldFormats(definition),
            Status = ChangeStatus.Applied
        });
        outcome.Data["scheduleId"] = schedule.Id.GetValue();
        outcome.Data["fields"] = ReadBackFormats(definition);
    }

    private static bool ApplyHeading(
        Autodesk.Revit.DB.ScheduleField field,
        string? heading,
        WriteItemReport report)
    {
        string current;
        try
        {
            current = field.ColumnHeading ?? string.Empty;
        }
        catch (Exception ex)
        {
            report.ErrorCode = "field_not_formatable";
            report.Reason = "Heading of the field is unreadable: " + ex.Message;
            return false;
        }

        if (string.Equals(current, heading ?? string.Empty, StringComparison.Ordinal))
        {
            if (report.Reason is null)
                report.Reason = "The heading already equals '" + heading + "' (no-op).";
            return false;
        }

        try
        {
            field.ColumnHeading = heading ?? string.Empty;
            report.OldValue = current;
            report.NewValue = heading;
            return true;
        }
        catch (Exception ex)
        {
            report.ErrorCode = "field_not_formatable";
            report.Reason = "Heading '" + heading + "': " + ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    internal static List<object> ReadBackFormats(ScheduleDefinition definition)
    {
        var rows = new List<object>();
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = AgentScheduleWriteHelper.SafeGetField(definition, fieldId);
            if (field is null)
                continue;

            string? heading = null;
            try
            {
                heading = field.ColumnHeading;
            }
            catch (Exception)
            {
                // Unreadable heading stays null.
            }

            rows.Add(new
            {
                name = AgentScheduleWriteHelper.SafeFieldName(field),
                heading,
                widthMm = AgentScheduleWriteHelper.GetColumnWidthMm(field)
            });
        }

        return rows;
    }
}
