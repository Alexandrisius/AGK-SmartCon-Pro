using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Field/block transfer of clone_schedule: source snapshots, target resolution
/// through the fieldMapping and the filters/sorting/totals/style copies.
/// </summary>
public sealed partial class CloneScheduleTool
{
    private static List<SourceFieldSnapshot> ReadSourceFields(
        ScheduleDefinition definition,
        List<string> warnings)
    {
        var fields = new List<SourceFieldSnapshot>();
        foreach (var fieldId in definition.GetFieldOrder())
        {
            var field = AgentScheduleWriteHelper.SafeGetField(definition, fieldId);
            if (field is null)
                continue;

            var snapshot = new SourceFieldSnapshot
            {
                Name = AgentScheduleWriteHelper.SafeFieldName(field),
                WidthMm = AgentScheduleWriteHelper.GetColumnWidthMm(field)
            };

            try
            {
                snapshot.Heading = field.ColumnHeading;
            }
            catch (Exception)
            {
                // The heading stays null; the clone falls back to the field name.
            }

            try
            {
                snapshot.Hidden = field.IsHidden;
                snapshot.Calculated = field.IsCalculatedField;
                snapshot.DisplayType = field.DisplayType;
            }
            catch (Exception ex)
            {
                warnings.Add("Field '" + snapshot.Name + "' state is partially unreadable: " +
                             ex.Message);
            }

            fields.Add(snapshot);
        }

        return fields;
    }

    private static List<MappingEntry> ReadMapping(JsonElement args)
    {
        var mapping = new List<MappingEntry>();
        if (!args.TryGetProperty("fieldMapping", out var mappingElement)
            || mappingElement.ValueKind != JsonValueKind.Array)
            return mapping;

        foreach (var entry in mappingElement.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
                continue;

            var sourceField = entry.TryGetProperty("sourceField", out var sourceElement)
                && sourceElement.ValueKind == JsonValueKind.String
                    ? sourceElement.GetString()
                    : null;
            if (string.IsNullOrWhiteSpace(sourceField))
                continue;

            string? targetField = entry.TryGetProperty("targetField", out var targetElement)
                && targetElement.ValueKind == JsonValueKind.String
                    ? targetElement.GetString()
                    : null;
            string? targetGuid = entry.TryGetProperty("targetGuid", out var guidElement)
                && guidElement.ValueKind == JsonValueKind.String
                    ? guidElement.GetString()
                    : null;

            mapping.Add(new MappingEntry
            {
                SourceField = sourceField!.Trim(),
                TargetField = string.IsNullOrWhiteSpace(targetField) ? null : targetField!.Trim(),
                TargetGuid = string.IsNullOrWhiteSpace(targetGuid) ? null : targetGuid!.Trim()
            });
        }

        return mapping;
    }

    /// <summary>
    /// The target schedulable field for one source column: an explicit mapping
    /// entry (guid first, then name; both empty = skip) or the identity by name.
    /// </summary>
    private static (SchedulableField? Target, string? SkipReason) ResolveTarget(
        Document doc,
        ScheduleDefinition cloneDefinition,
        string sourceName,
        IReadOnlyDictionary<string, MappingEntry> mapping)
    {
        if (mapping.TryGetValue(sourceName, out var entry))
        {
            if (!string.IsNullOrEmpty(entry.TargetGuid))
            {
                if (!Guid.TryParse(entry.TargetGuid, out var guid))
                    return (null, "targetGuid '" + entry.TargetGuid + "' is not a GUID");

                var shared = SharedParameterElement.Lookup(doc, guid);
                if (shared is null)
                    return (null, "no shared parameter with GUID " + entry.TargetGuid +
                                  " in this project");

                if (AgentScheduleWriteHelper.TryFindSchedulableFieldByParameterId(
                        cloneDefinition, shared.Id, out var byId))
                    return (byId, null);

                return (null, "shared parameter (GUID " + entry.TargetGuid +
                              ") is not schedulable for this category");
            }

            if (!string.IsNullOrEmpty(entry.TargetField))
            {
                return AgentScheduleWriteHelper.TryFindSchedulableField(
                    doc, cloneDefinition, entry.TargetField!, out var byName)
                    ? (byName, null)
                    : (null, "no schedulable field '" + entry.TargetField +
                             "' for this category");
            }

            return (null, "skipped by mapping (no target)");
        }

        return AgentScheduleWriteHelper.TryFindSchedulableField(
            doc, cloneDefinition, sourceName, out var identity)
            ? (identity, null)
            : (null, "no analog field '" + sourceName + "' for this category");
    }

    private static void CopyFieldFormat(SourceFieldSnapshot snapshot, ScheduleField cloneField)
    {
        try
        {
            if (snapshot.Heading is not null)
                cloneField.ColumnHeading = snapshot.Heading;
        }
        catch (Exception)
        {
            // The heading falls back to the field name.
        }

        try
        {
            cloneField.IsHidden = snapshot.Hidden;
        }
        catch (Exception)
        {
            // A field Revit refuses to hide stays visible.
        }

        if (snapshot.WidthMm.HasValue && !snapshot.Calculated)
            AgentScheduleWriteHelper.TrySetColumnWidthMm(cloneField, snapshot.WidthMm.Value, out _);
    }

    private static void CopyDisplayType(
        SourceFieldSnapshot snapshot,
        ScheduleField cloneField,
        List<string> warnings)
    {
        if (snapshot.DisplayType == ScheduleFieldDisplayType.Standard)
            return;

        try
        {
            cloneField.DisplayType = snapshot.DisplayType;
        }
        catch (Exception)
        {
            warnings.Add("Totals of column '" + snapshot.Name + "' were not transferred: " +
                         "the target field cannot display them.");
        }
    }

    private static int CopyFilters(
        ScheduleDefinition cloneDefinition,
        string sourceFiltersJson,
        IReadOnlyDictionary<string, string> nameMap,
        List<string> warnings)
    {
        var records = JsonSerializer.Deserialize<List<ScheduleFilterRecord>>(sourceFiltersJson);
        if (records is null)
            return 0;

        var copied = 0;
        foreach (var record in records)
        {
            if (!nameMap.TryGetValue(record.Field, out var cloneFieldName)
                || cloneFieldName is null
                || AgentScheduleWriteHelper.FindFieldIdByName(cloneDefinition, cloneFieldName) is null)
            {
                warnings.Add("Filter on '" + record.Field + "' was not transferred: the " +
                             "column was skipped or has no analog.");
                continue;
            }

            var cloneFieldId = AgentScheduleWriteHelper.FindFieldIdByName(cloneDefinition, cloneFieldName);
            try
            {
                cloneDefinition.AddFilter(
                    AgentScheduleWriteHelper.BuildFilter(record, cloneFieldId!));
                copied++;
            }
            catch (Exception ex)
            {
                warnings.Add("Filter on '" + record.Field + "' was refused by Revit: " + ex.Message);
            }
        }

        return copied;
    }

    private static int CopySorting(
        ScheduleDefinition cloneDefinition,
        string sourceSortJson,
        IReadOnlyDictionary<string, string> nameMap,
        List<string> warnings)
    {
        var record = JsonSerializer.Deserialize<ScheduleSortRecord>(sourceSortJson);
        if (record is null)
            return 0;

        var copied = 0;
        foreach (var group in record.SortGroups)
        {
            if (!nameMap.TryGetValue(group.Field, out var cloneFieldName)
                || AgentScheduleWriteHelper.FindFieldIdByName(cloneDefinition, cloneFieldName!) is null)
            {
                warnings.Add("Sorting by '" + group.Field + "' was not transferred: the " +
                             "column was skipped or has no analog.");
                continue;
            }

            var cloneFieldId = AgentScheduleWriteHelper.FindFieldIdByName(cloneDefinition, cloneFieldName!);
            var sortGroup = new ScheduleSortGroupField(
                cloneFieldId!,
                string.Equals(group.Order, "desc", StringComparison.OrdinalIgnoreCase)
                    ? ScheduleSortOrder.Descending
                    : ScheduleSortOrder.Ascending);
            if (group.ShowHeader.HasValue)
                sortGroup.ShowHeader = group.ShowHeader.Value;
            if (group.ShowFooter.HasValue)
                sortGroup.ShowFooter = group.ShowFooter.Value;
            if (group.ShowBlankLine.HasValue)
                sortGroup.ShowBlankLine = group.ShowBlankLine.Value;

            try
            {
                cloneDefinition.AddSortGroupField(sortGroup);
                copied++;
            }
            catch (Exception ex)
            {
                warnings.Add("Sorting by '" + group.Field + "' was refused by Revit: " + ex.Message);
            }
        }

        return copied;
    }

    private static void CopyTotals(
        ScheduleDefinition cloneDefinition,
        string sourceTotalsJson,
        List<string> warnings)
    {
        var record = AgentScheduleWriteHelper.DeserializeTotals(sourceTotalsJson);
        if (record is null)
            return;

        // C4-14: a source with the grand total row off has nothing to transfer —
        // touching the sub-flag setters anyway makes them throw and produced a
        // false "grand total row was not transferred" warning on every clone.
        if (!record.GrandTotal && !record.GrandTotalCount && !record.ShowGrandTotalTitle)
            return;

        try
        {
            // The sub-flags require the grand total row to be on first.
            cloneDefinition.ShowGrandTotal = true;
            cloneDefinition.ShowGrandTotalCount = record.GrandTotalCount;
            cloneDefinition.ShowGrandTotalTitle = record.ShowGrandTotalTitle;
            if (record.ShowGrandTotalTitle && record.GrandTotalTitle is not null)
                cloneDefinition.GrandTotalTitle = record.GrandTotalTitle;
            cloneDefinition.ShowGrandTotal = record.GrandTotal;
        }
        catch (Exception ex)
        {
            warnings.Add("The grand total row was not transferred: " + ex.Message);
        }

        foreach (var fieldState in record.Fields)
        {
            if (!AgentScheduleWriteHelper.TryParseDisplayMode(fieldState.Mode, out var mode)
                || mode == ScheduleFieldDisplayType.Standard)
                continue;

            var fieldId = AgentScheduleWriteHelper.FindFieldIdByName(cloneDefinition, fieldState.Name);
            if (fieldId is null)
                continue;

            var cloneField = AgentScheduleWriteHelper.SafeGetField(cloneDefinition, fieldId);
            if (cloneField is null)
                continue;

            try
            {
                cloneField.DisplayType = mode;
            }
            catch (Exception)
            {
                warnings.Add("Totals of column '" + fieldState.Name + "' were not transferred.");
            }
        }
    }

    private static void CopyStyle(ViewSchedule clone, string sourceStyleJson, List<string> warnings)
    {
        var record = AgentScheduleWriteHelper.DeserializeStyle(sourceStyleJson);
        if (record is null)
            return;

        try
        {
#if REVIT2021_OR_GREATER
            if (record.StripedRows.HasValue)
                clone.HasStripedRows = record.StripedRows.Value;
            if (record.UseStripedRowsOnSheets.HasValue)
                clone.UseStripedRowsOnSheets = record.UseStripedRowsOnSheets.Value;
#else
            if (record.StripedRows.HasValue || record.UseStripedRowsOnSheets.HasValue)
                warnings.Add("Striped rows need Revit 2021+; the style was not transferred.");
#endif
            if (record.BodyTextTypeId.HasValue)
                clone.BodyTextTypeId = ElementIdCompat.Create(record.BodyTextTypeId.Value);
            if (record.HeaderTextTypeId.HasValue)
                clone.HeaderTextTypeId = ElementIdCompat.Create(record.HeaderTextTypeId.Value);
            if (record.TitleTextTypeId.HasValue)
                clone.TitleTextTypeId = ElementIdCompat.Create(record.TitleTextTypeId.Value);
        }
        catch (Exception ex)
        {
            warnings.Add("The table style was transferred only partially: " + ex.Message);
        }
    }

}
