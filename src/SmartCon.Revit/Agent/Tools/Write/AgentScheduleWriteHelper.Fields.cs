using System.Text.Json;
using Autodesk.Revit.DB;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>Serialized shape of one schedule field with its hidden flag, stored
/// in schedule_fields change items by the field visibility tool and restored by
/// undo (the plain string lists of the field add/remove/order tools keep
/// working — hidden stays untouched for them).</summary>
internal sealed class ScheduleFieldStateRecord
{
    public string Name { get; set; } = string.Empty;

    public bool Hidden { get; set; }
}

/// <summary>Serialized totals state of a schedule: per-field display modes plus
/// the grand total row settings, restored as one unit by undo.</summary>
internal sealed class ScheduleTotalsRecord
{
    public List<ScheduleFieldTotalsRecord> Fields { get; set; } = new();

    public bool GrandTotal { get; set; }

    public bool GrandTotalCount { get; set; }

    public bool ShowGrandTotalTitle { get; set; }

    public string? GrandTotalTitle { get; set; }
}

internal sealed class ScheduleFieldTotalsRecord
{
    public string Name { get; set; } = string.Empty;

    public string Mode { get; set; } = "standard";
}

/// <summary>Read-side projections of the schedule field/totals state used by
/// the write tools in their outcome data (read-back blocks).</summary>
internal static class AgentScheduleReadHelper
{
    internal static List<object> FieldStates(ScheduleDefinition definition)
    {
        var states = new List<object>();
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = AgentScheduleWriteHelper.SafeGetField(definition, fieldId);
            if (field is null)
                continue;

            states.Add(new
            {
                name = AgentScheduleWriteHelper.SafeFieldName(field),
                hidden = SafeHidden(field)
            });
        }

        return states;
    }

    internal static object TotalsState(ScheduleDefinition definition)
    {
        var fields = new List<object>();
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = AgentScheduleWriteHelper.SafeGetField(definition, fieldId);
            if (field is null)
                continue;

            string mode;
            try
            {
                mode = AgentScheduleWriteHelper.ModeName(field.DisplayType);
            }
            catch (Exception)
            {
                mode = "standard";
            }

            fields.Add(new
            {
                name = AgentScheduleWriteHelper.SafeFieldName(field),
                mode
            });
        }

        bool grandTotal;
        bool grandTotalCount;
        bool grandTotalTitleShown;
        string? grandTotalTitle;
        try
        {
            grandTotal = definition.ShowGrandTotal;
            grandTotalCount = definition.ShowGrandTotalCount;
            grandTotalTitleShown = definition.ShowGrandTotalTitle;
            grandTotalTitle = definition.GrandTotalTitle;
        }
        catch (Exception)
        {
            grandTotal = false;
            grandTotalCount = false;
            grandTotalTitleShown = false;
            grandTotalTitle = null;
        }

        return new
        {
            fields,
            grandTotal,
            grandTotalCount,
            grandTotalTitleShown,
            grandTotalTitle
        };
    }

    private static bool SafeHidden(ScheduleField field)
    {
        try
        {
            return field.IsHidden;
        }
        catch (Exception)
        {
            return false;
        }
    }
}

internal static partial class AgentScheduleWriteHelper
{
    private static readonly Dictionary<string, ScheduleFieldDisplayType> DisplayTypesByMode =
        new(StringComparer.Ordinal)
        {
            ["standard"] = ScheduleFieldDisplayType.Standard,
            ["totals"] = ScheduleFieldDisplayType.Totals,
            ["minmax"] = ScheduleFieldDisplayType.MinMax,
            ["min"] = ScheduleFieldDisplayType.Min,
            ["max"] = ScheduleFieldDisplayType.Max
        };

    private static readonly Dictionary<ScheduleFieldDisplayType, string> ModesByDisplayType =
        DisplayTypesByMode.ToDictionary(pair => pair.Value, pair => pair.Key);

    internal static bool TryParseDisplayMode(string? mode, out ScheduleFieldDisplayType displayType) =>
        DisplayTypesByMode.TryGetValue(mode ?? string.Empty, out displayType);

    internal static string ModeName(ScheduleFieldDisplayType displayType) =>
        ModesByDisplayType.TryGetValue(displayType, out var mode) ? mode : displayType.ToString();

    /// <summary>All fields with hidden flags as change-item JSON (undo restore).</summary>
    internal static string SerializeFieldStates(ScheduleDefinition definition)
    {
        var records = new List<ScheduleFieldStateRecord>();
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = SafeGetField(definition, fieldId);
            if (field is null)
                continue;

            bool hidden;
            try
            {
                hidden = field.IsHidden;
            }
            catch (Exception)
            {
                hidden = false;
            }

            records.Add(new ScheduleFieldStateRecord
            {
                Name = SafeFieldName(field),
                Hidden = hidden
            });
        }

        return JsonSerializer.Serialize(records);
    }

    internal static List<ScheduleFieldStateRecord>? DeserializeFieldStates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<ScheduleFieldStateRecord>>(json!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string SerializeTotals(ScheduleDefinition definition)
    {
        var record = new ScheduleTotalsRecord();
        try
        {
            record.GrandTotal = definition.ShowGrandTotal;
            record.GrandTotalCount = definition.ShowGrandTotalCount;
            record.ShowGrandTotalTitle = definition.ShowGrandTotalTitle;
            record.GrandTotalTitle = definition.GrandTotalTitle;
            foreach (var fieldId in definition.GetFieldOrder())
            {
                var field = SafeGetField(definition, fieldId);
                if (field is null)
                    continue;

                record.Fields.Add(new ScheduleFieldTotalsRecord
                {
                    Name = SafeFieldName(field),
                    Mode = ModeName(field.DisplayType)
                });
            }
        }
        catch (Exception)
        {
            // Keep what was read.
        }

        return JsonSerializer.Serialize(record);
    }

    internal static ScheduleTotalsRecord? DeserializeTotals(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ScheduleTotalsRecord>(json!);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
