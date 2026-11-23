using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Explication half of build_system_sheets (case 6 v2): per item an optional
/// narrow clone of the common project specification — only the keepFields
/// columns visible, the filter field added hidden when it is not a kept
/// column, an equals filter by the system name — placed to the right of the
/// view inside the same sheet transaction. Compact cloning follows
/// clone_schedule's private mechanics; failures throw so ApplyItem discards
/// the half-built sheet of the item.
/// </summary>
public sealed partial class BuildSystemSheetsTool
{
    /// <summary>
    /// Pre-flight of the optional explication block: the source must be a
    /// clonable schedule, keepFields non-empty, the filter field schedulable
    /// for the category, and the clone name unique (existing schedules and
    /// names planned by earlier items of this batch).
    /// </summary>
    private static PlannedExplication? ValidateExplication(
        Document doc,
        WriteOutcome outcome,
        BuildSheetItem item,
        WriteItemReport report,
        string sheetNumber,
        HashSet<string> usedScheduleNames)
    {
        var explication = item.Explication;
        if (explication is null)
            return null;

        if (item.ScheduleId is > 0)
        {
            Fail(outcome, report, "invalid_arguments",
                "Pass either scheduleId (a ready clone to place) or explication " +
                "(build the clone here) — not both.");
            return null;
        }

        if (explication.SourceScheduleId <= 0)
        {
            Fail(outcome, report, "invalid_arguments",
                "explication.sourceScheduleId is required (the common specification " +
                "to clone, see list_schedules).");
            return null;
        }

        if (doc.GetElement(ElementIdCompat.Create(explication.SourceScheduleId))
            is not ViewSchedule source)
        {
            Fail(outcome, report, "schedule_not_found",
                "explication.sourceScheduleId " + explication.SourceScheduleId +
                " is not a schedule.");
            return null;
        }

        if (source.IsTemplate)
        {
            Fail(outcome, report, "schedule_is_template",
                "Schedule '" + source.Name + "' is a template and cannot be cloned.");
            return null;
        }

        if (IsKeySchedule(source.Definition))
        {
            Fail(outcome, report, "schedule_not_cloneable",
                "Key schedule '" + source.Name + "' cannot be cloned field-by-field; " +
                "rebuild the key schedule with create_schedule instead.");
            return null;
        }

        var keepFields = explication.KeepFields?
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (keepFields is null || keepFields.Count == 0)
        {
            Fail(outcome, report, "invalid_arguments",
                "explication.keepFields must list at least one visible column name " +
                "(e.g. Наименование, Единица измерения, Количество).");
            return null;
        }

        if (keepFields.Count > AgentScheduleWriteHelper.MaxFieldsPerCall)
        {
            Fail(outcome, report, "limit_exceeded",
                "explication.keepFields allows at most " +
                AgentScheduleWriteHelper.MaxFieldsPerCall + " columns.");
            return null;
        }

        var filterField = explication.FilterField?.Trim();
        if (string.IsNullOrWhiteSpace(filterField))
        {
            Fail(outcome, report, "invalid_arguments",
                "explication.filterField is required (e.g. АДСК_ИмяСистемы).");
            return null;
        }

        if (string.IsNullOrWhiteSpace(explication.FilterValue))
        {
            Fail(outcome, report, "invalid_arguments",
                "explication.filterValue is required (the system name of this sheet).");
            return null;
        }

        // A filter field that is not schedulable for the category cannot be
        // added even as a hidden column — fail the item before any change.
        if (!AgentScheduleWriteHelper.TryFindSchedulableField(
                doc, source.Definition, filterField!, out _))
        {
            Fail(outcome, report, "filter_field_not_schedulable",
                "explication.filterField '" + filterField + "' is not schedulable for " +
                "the category of '" + source.Name + "'.");
            return null;
        }

        var name = "Экспликация " + item.SheetName!.Trim();
        if (usedScheduleNames.Contains(name))
            name = name + " (" + sheetNumber + ")";
        if (usedScheduleNames.Contains(name))
        {
            Fail(outcome, report, "schedule_name_taken",
                "Schedule '" + name + "' already exists; rename it first or pass " +
                "another sheetName.");
            return null;
        }

        usedScheduleNames.Add(name);
        return new PlannedExplication
        {
            Source = source,
            KeepFields = keepFields,
            FilterField = filterField!,
            FilterValue = explication.FilterValue!.Trim(),
            Name = name
        };
    }

    /// <summary>
    /// Builds the explication clone (visible keepFields columns, hidden filter
    /// column, equals filter) and places it to the right of the view. Throws
    /// on a refused clone/filter so ApplyItem discards the half-built sheet;
    /// per-column misses are warnings.
    /// </summary>
    private static ExplicationPlacement BuildAndPlaceExplication(
        Document doc,
        ViewSheet sheet,
        PlannedItem entry,
        ViewportPlacement placement,
        List<string> warnings)
    {
        var planned = entry.Explication!;
        var source = planned.Source;

        ViewSchedule clone;
        try
        {
            clone = ViewSchedule.CreateSchedule(doc, source.Definition.CategoryId);
            clone.Name = planned.Name;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Revit refused the explication clone '" + planned.Name + "': " + ex.Message);
        }

        try
        {
            return FillAndPlaceExplication(
                doc, sheet, entry, placement, warnings, clone, source);
        }
        catch (Exception)
        {
            // The clone must not survive its failed item: ApplyItem discards
            // the half-built sheet, the orphan schedule dies here.
            try
            {
                doc.Delete(clone.Id);
            }
            catch (Exception)
            {
                // Best effort — the reason of the original failure wins.
            }

            throw;
        }
    }

    private static ExplicationPlacement FillAndPlaceExplication(
        Document doc,
        ViewSheet sheet,
        PlannedItem entry,
        ViewportPlacement placement,
        List<string> warnings,
        ViewSchedule clone,
        ViewSchedule source)
    {
        var planned = entry.Explication!;
        var definition = clone.Definition;
        try
        {
            // A specification-like explication inherits the source's itemized
            // flag (the same default clone_schedule applies).
            definition.IsItemized = source.Definition.IsItemized;
        }
        catch (Exception)
        {
            // The flag stays at the Revit default; not worth failing the item.
        }

        var visible = new List<string>();
        foreach (var keepName in planned.KeepFields)
        {
            if (!AgentScheduleWriteHelper.TryFindSchedulableField(
                    doc, definition, keepName, out var field))
            {
                warnings.Add(
                    "Sheet " + sheet.SheetNumber + ": explication column '" + keepName +
                    "' has no analog for this category and was skipped. [Action: check " +
                    "the exact display name via get_schedule_definition of schedule " +
                    source.Id.GetValue() + "]");
                continue;
            }

            try
            {
                definition.AddField(field!);
                visible.Add(keepName);
            }
            catch (Exception ex)
            {
                warnings.Add(
                    "Sheet " + sheet.SheetNumber + ": explication column '" + keepName +
                    "' was refused by Revit: " + ex.Message +
                    " [Action: drop the column from keepFields and rebuild the sheet]");
            }
        }

        if (visible.Count == 0)
            throw new InvalidOperationException(
                "None of explication.keepFields could be added for the category of '" +
                source.Name + "'.");

        // The filter needs its field as a column: add it hidden when it is not
        // one of the kept columns.
        var filterFieldId = AgentScheduleWriteHelper.FindFieldIdByName(
            definition, planned.FilterField);
        var filterColumnHidden = false;
        if (filterFieldId is null)
        {
            if (!AgentScheduleWriteHelper.TryFindSchedulableField(
                    doc, definition, planned.FilterField, out var filterField))
                throw new InvalidOperationException(
                    "explication.filterField '" + planned.FilterField +
                    "' is not schedulable for the clone category.");

            try
            {
                var added = definition.AddField(filterField!);
                var addedField = AgentScheduleWriteHelper.SafeGetField(
                    definition, added.FieldId);
                if (addedField is null)
                    throw new InvalidOperationException(
                        "The hidden filter column '" + planned.FilterField +
                        "' could not be read back.");

                addedField.IsHidden = true;
                filterColumnHidden = true;
                filterFieldId = added.FieldId;
            }
            catch (InvalidOperationException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add(
                    "Sheet " + sheet.SheetNumber + ": the filter column '" +
                    planned.FilterField + "' is visible (hiding was refused: " + ex.Message +
                    "). [Action: hide it with set_schedule_field_visibility]");
                filterFieldId = AgentScheduleWriteHelper.FindFieldIdByName(
                    definition, planned.FilterField);
            }
        }

        if (filterFieldId is null)
            throw new InvalidOperationException(
                "explication.filterField '" + planned.FilterField +
                "' could not be added as a column of the clone.");

        if (!AgentScheduleWriteHelper.TryBuildFilter(
                filterFieldId,
                ScheduleFilterType.Equal,
                AgentJson.Serialize(planned.FilterValue),
                out var filter,
                out var filterReason))
            throw new InvalidOperationException(
                "The explication filter on '" + planned.FilterField + "' was refused: " +
                filterReason);

        try
        {
            definition.AddFilter(filter!);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Revit refused the explication filter on '" + planned.FilterField +
                "': " + ex.Message);
        }

        // The clone was built field by field: refresh its table so the row
        // count and the placed size reflect the filter, not the full source.
        try
        {
            doc.Regenerate();
            clone.RefreshData();
        }
        catch (Exception)
        {
            // Revit could not refresh the clone table yet: the row count is
            // then whatever the table currently reports.
        }

        // An empty explication looks like success otherwise: a filter value
        // that matches nothing must be visible in the result.
        var explicationRows = AgentScheduleReader.ReadRowCount(clone, skipHeader: true);
        if (explicationRows == 0)
        {
            var field = AgentScheduleWriteHelper.SafeGetField(definition, filterFieldId);
            var parameterId = field?.ParameterId;
            var hint = parameterId is null || parameterId == ElementId.InvalidElementId
                ? string.Empty
                : AgentScheduleFilterEstimator.DescribeClosestValues(
                    doc, definition, parameterId.GetValue(), planned.FilterValue);

            warnings.Add(
                "Sheet " + sheet.SheetNumber + ": the explication filter '" +
                planned.FilterField + "' = '" + planned.FilterValue +
                "' matched 0 rows of the source '" + source.Name + "'." + hint +
                " [Action: pass an existing value of the field as " +
                "explication.filterValue (or pick another filterField) and rebuild the " +
                "sheet — the sheet now carries an empty table]");
        }

        var schedulePlacement = PlaceScheduleRightOfView(
            doc, sheet, clone, entry.MarginMm, placement);
        schedulePlacement.Warnings.ForEach(warnings.Add);

        return new ExplicationPlacement
        {
            Schedule = clone,
            Instance = schedulePlacement.Instance,
            Rows = explicationRows,
            Info = new
            {
                scheduleId = clone.Id.GetValue(),
                name = clone.Name,
                placed = schedulePlacement.Instance is not null,
                visibleColumns = visible,
                filterColumnHidden,
                explicationRows,
                filter = new
                {
                    field = planned.FilterField,
                    op = "equals",
                    value = planned.FilterValue
                },
                placement = schedulePlacement.Info
            }
        };
    }

    private static bool IsKeySchedule(ScheduleDefinition definition)
    {
        try
        {
            return definition.IsKeySchedule;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The explication block of one item, deserialized from args.</summary>
    private sealed class ExplicationRequest
    {
        public long SourceScheduleId { get; set; }

        public List<string>? KeepFields { get; set; }

        public string? FilterField { get; set; }

        public string? FilterValue { get; set; }
    }

    /// <summary>The validated explication plan of one item.</summary>
    private sealed class PlannedExplication
    {
        public required ViewSchedule Source { get; init; }

        public required IReadOnlyList<string> KeepFields { get; init; }

        public required string FilterField { get; init; }

        public required string FilterValue { get; init; }

        public required string Name { get; init; }
    }

    /// <summary>The built explication clone with its placement on the sheet.</summary>
    private sealed class ExplicationPlacement
    {
        public ViewSchedule? Schedule { get; init; }

        public ScheduleSheetInstance? Instance { get; init; }

        /// <summary>Data rows of the built clone (0 = the filter matched nothing).</summary>
        public int Rows { get; init; }

        public object? Info { get; init; }
    }
}
