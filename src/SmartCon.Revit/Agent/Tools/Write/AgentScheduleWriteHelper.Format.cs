using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>Serialized column format of one schedule field (heading + width),
/// stored in schedule_field_format change items and restored by undo.</summary>
internal sealed class ScheduleFieldFormatRecord
{
    public string Name { get; set; } = string.Empty;

    public string? Heading { get; set; }

    /// <summary>Column width in millimetres; null when Revit refuses to read it.</summary>
    public double? WidthMm { get; set; }
}

/// <summary>Serialized schedule style (striped rows + default text type ids),
/// stored in schedule_style change items and restored by undo.</summary>
internal sealed class ScheduleStyleRecord
{
    public bool? StripedRows { get; set; }

    public bool? UseStripedRowsOnSheets { get; set; }

    public long? BodyTextTypeId { get; set; }

    public long? HeaderTextTypeId { get; set; }

    public long? TitleTextTypeId { get; set; }
}

/// <summary>
/// Column formatting, table style and placement rotation helpers shared by the
/// schedule rebuild tools (rename/format/style/remove/clone). Widths cross the
/// tool boundary in millimetres (I-02); the Revit API wants internal feet.
/// ScheduleField.ColumnWidth was split into GridColumnWidth/SheetColumnWidth in
/// Revit 2022 (both stay aligned); striped rows exist only since Revit 2021.
/// </summary>
internal static partial class AgentScheduleWriteHelper
{
    internal const int MaxFormatFieldsPerCall = 50;

    internal const int MaxCloneFields = 40;

    // ── Column width (mm ↔ internal feet, version-guarded) ─────────────

    /// <summary>Current column width in millimetres; null when unreadable (calculated fields).</summary>
    internal static double? GetColumnWidthMm(ScheduleField field)
    {
        try
        {
#if REVIT2022_OR_GREATER
            return AgentUnitsFormat.ToMillimeters(field.GridColumnWidth);
#else
            return AgentUnitsFormat.ToMillimeters(field.ColumnWidth);
#endif
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Sets the column width in millimetres on both the grid and the sheet column.</summary>
    internal static bool TrySetColumnWidthMm(
        ScheduleField field,
        double widthMm,
        out string? reason)
    {
        reason = null;
        if (widthMm <= 0 || widthMm > 2000)
        {
            reason = "widthMm must be between 0 and 2000 mm.";
            return false;
        }

        try
        {
            var internalWidth = AgentSheetWriteHelper.MillimetersToInternal(widthMm);
#if REVIT2022_OR_GREATER
            field.GridColumnWidth = internalWidth;
            field.SheetColumnWidth = internalWidth;
#else
            field.ColumnWidth = internalWidth;
#endif
            return true;
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
            return false;
        }
    }

    // ── Field format snapshots (heading + width) ───────────────────────

    internal static string SerializeFieldFormats(ScheduleDefinition definition)
    {
        var records = new List<ScheduleFieldFormatRecord>();
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = SafeGetField(definition, fieldId);
            if (field is null)
                continue;

            string? heading = null;
            try
            {
                heading = field.ColumnHeading;
            }
            catch (Exception)
            {
                // Calculated fields keep a heading too; unreadable stays null.
            }

            records.Add(new ScheduleFieldFormatRecord
            {
                Name = SafeFieldName(field),
                Heading = heading,
                WidthMm = GetColumnWidthMm(field)
            });
        }

        return JsonSerializer.Serialize(records);
    }

    internal static List<ScheduleFieldFormatRecord>? DeserializeFieldFormats(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<List<ScheduleFieldFormatRecord>>(json!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ── Table style snapshots ──────────────────────────────────────────

    internal static string SerializeStyle(ViewSchedule schedule)
    {
        var record = new ScheduleStyleRecord();
        try
        {
#if REVIT2021_OR_GREATER
            record.StripedRows = schedule.HasStripedRows;
            record.UseStripedRowsOnSheets = schedule.UseStripedRowsOnSheets;
#endif
            record.BodyTextTypeId = TextTypeId(schedule.BodyTextTypeId);
            record.HeaderTextTypeId = TextTypeId(schedule.HeaderTextTypeId);
            record.TitleTextTypeId = TextTypeId(schedule.TitleTextTypeId);
        }
        catch (Exception)
        {
            // Keep what was read.
        }

        return JsonSerializer.Serialize(record);
    }

    internal static ScheduleStyleRecord? DeserializeStyle(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            return JsonSerializer.Deserialize<ScheduleStyleRecord>(json!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static long? TextTypeId(ElementId id) =>
        id is null || id == ElementId.InvalidElementId ? null : id.GetValue();

    /// <summary>True when this Revit version supports the striped-row style properties.</summary>
    internal static bool SupportsStripedRows =>
#if REVIT2021_OR_GREATER
        true;
#else
        false;
#endif

    // ── Table style inheritance (create_schedule, S.42) ────────────────

    /// <summary>Table text types adopted by a created schedule from an existing one.</summary>
    internal sealed class InheritedTableStyle
    {
        public long? SourceScheduleId { get; set; }

        public string? SourceScheduleName { get; set; }

        public long? BodyTextTypeId { get; set; }

        public long? HeaderTextTypeId { get; set; }

        public long? TitleTextTypeId { get; set; }

        public string? BodyTextTypeName { get; set; }

        public string? HeaderTextTypeName { get; set; }

        public string? TitleTextTypeName { get; set; }
    }

    /// <summary>
    /// Copies the body/header/title text types of an existing schedule onto a
    /// freshly created one (same category first, else any non-template schedule)
    /// so a new specification does not come out with Revit's default «тело» style
    /// on every row (S.42). Returns null when the document has no schedule to
    /// inherit from.
    /// </summary>
    internal static InheritedTableStyle? InheritTableTextTypes(Document doc, ViewSchedule schedule)
    {
        try
        {
            ViewSchedule? source = null;
            ViewSchedule? fallback = null;
            foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)))
            {
                if (element is not ViewSchedule candidate
                    || candidate.Id == schedule.Id
                    || candidate.IsTemplate)
                {
                    continue;
                }

                fallback ??= candidate;

                try
                {
                    if (source is null
                        && candidate.Definition.CategoryId == schedule.Definition.CategoryId)
                    {
                        source = candidate;
                    }
                }
                catch (Exception)
                {
                    // A key schedule without a readable category: skip for the match.
                }
            }

            source ??= fallback;
            if (source is null)
                return null;

            var inherited = new InheritedTableStyle
            {
                SourceScheduleId = source.Id.GetValue(),
                SourceScheduleName = source.Name,
                BodyTextTypeId = SafeTextTypeId(source.BodyTextTypeId),
                HeaderTextTypeId = SafeTextTypeId(source.HeaderTextTypeId),
                TitleTextTypeId = SafeTextTypeId(source.TitleTextTypeId)
            };

            if (inherited.BodyTextTypeId.HasValue)
                schedule.BodyTextTypeId = ElementIdCompat.Create(inherited.BodyTextTypeId.Value);
            if (inherited.HeaderTextTypeId.HasValue)
                schedule.HeaderTextTypeId = ElementIdCompat.Create(inherited.HeaderTextTypeId.Value);
            if (inherited.TitleTextTypeId.HasValue)
                schedule.TitleTextTypeId = ElementIdCompat.Create(inherited.TitleTextTypeId.Value);

            inherited.BodyTextTypeName = TextTypeName(doc, inherited.BodyTextTypeId);
            inherited.HeaderTextTypeName = TextTypeName(doc, inherited.HeaderTextTypeId);
            inherited.TitleTextTypeName = TextTypeName(doc, inherited.TitleTextTypeId);
            return inherited;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Text note types available for set_schedule_style (id + name).</summary>
    internal static List<object> AvailableTextTypes(Document doc, int limit)
    {
        var result = new List<object>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(TextNoteType)))
        {
            if (result.Count >= limit)
                break;

            result.Add(new { id = element.Id.GetValue(), name = element.Name });
        }

        return result;
    }

    private static long? SafeTextTypeId(ElementId id) =>
        id is null || id == ElementId.InvalidElementId ? null : id.GetValue();

    private static string? TextTypeName(Document doc, long? id) =>
        id.HasValue ? doc.GetElement(ElementIdCompat.Create(id.Value))?.Name : null;

    // ── Placement rotation ─────────────────────────────────────────────

    /// <summary>
    /// Canonical rotation names. The API enum ViewportRotation has exactly three
    /// members — None, Clockwise (90° CW) and Counterclockwise (90° CCW); 180° and
    /// arbitrary angles are not available for schedule placements.
    /// </summary>
    internal static string RotationName(ViewportRotation rotation) => rotation switch
    {
        ViewportRotation.Clockwise => "cw90",
        ViewportRotation.Counterclockwise => "ccw90",
        _ => "none"
    };

    internal static bool TryParseRotation(
        string? text,
        out ViewportRotation rotation,
        out string? reason)
    {
        rotation = ViewportRotation.None;
        reason = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            reason = "rotation is empty.";
            return false;
        }

        switch (text!.Trim().ToLowerInvariant())
        {
            case "none":
            case "0":
                rotation = ViewportRotation.None;
                return true;
            case "cw90":
            case "90cw":
            case "cw":
            case "90":
            case "clockwise":
                rotation = ViewportRotation.Clockwise;
                return true;
            case "ccw90":
            case "90ccw":
            case "ccw":
            case "270":
            case "counterclockwise":
                rotation = ViewportRotation.Counterclockwise;
                return true;
            case "180":
                reason = "Revit supports only none, cw90 (90° clockwise) and ccw90 " +
                         "(90° counterclockwise) for schedule placements; 180° is not " +
                         "available.";
                return false;
            default:
                reason = "Unknown rotation '" + text + "'; use none, cw90 or ccw90.";
                return false;
        }
    }

    /// <summary>
    /// Serialized placement of a removed schedule placement
    /// ("scheduleId;sheetId;x;y;rotation" with the top-left corner in millimetres)
    /// — enough for undo_last to re-create the ScheduleSheetInstance.
    /// </summary>
    internal static string SchedulePlacementText(
        long scheduleId,
        long sheetId,
        double mmX,
        double mmY,
        string rotation) =>
        scheduleId.ToString(CultureInfo.InvariantCulture) + ";" +
        sheetId.ToString(CultureInfo.InvariantCulture) + ";" +
        AgentSheetWriteHelper.CenterText(mmX, mmY) + ";" + rotation;

    internal static bool TryParseSchedulePlacement(
        string? text,
        out long scheduleId,
        out long sheetId,
        out double mmX,
        out double mmY,
        out ViewportRotation rotation)
    {
        scheduleId = 0;
        sheetId = 0;
        mmX = 0;
        mmY = 0;
        rotation = ViewportRotation.None;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var parts = text!.Split(';');
        if (parts.Length != 5)
            return false;

        return long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out scheduleId)
               && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out sheetId)
               && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out mmX)
               && double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out mmY)
               && TryParseRotation(parts[4], out rotation, out _);
    }

    /// <summary>
    /// A schedulable field of the definition whose parameter id equals the given
    /// shared parameter element id — the guid-based resolution path of
    /// clone_schedule fieldMapping.
    /// </summary>
    internal static bool TryFindSchedulableFieldByParameterId(
        ScheduleDefinition definition,
        ElementId parameterId,
        out SchedulableField? field)
    {
        field = null;
        foreach (var candidate in definition.GetSchedulableFields())
        {
            try
            {
                if (candidate.ParameterId == parameterId)
                {
                    field = candidate;
                    return true;
                }
            }
            catch (Exception)
            {
                // Calculated candidates carry no parameter id.
            }
        }

        return false;
    }
}
