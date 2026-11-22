using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Rebuilds a schedule as an exact analogue on other parameters (the customer
/// ФОП replacement): creates a new schedule of the source category, copies the
/// column structure (order, headings, widths, hidden flags), filters, sorting
/// with group decoration, totals and the table style, replacing fields through
/// the fieldMapping (by column name or shared parameter GUID). Columns without
/// an analog are skipped and listed. One schedule per call.
/// </summary>
public sealed partial class CloneScheduleTool : AgentWriteToolBase
{
    public CloneScheduleTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "clone_schedule",
        Description =
            "Creates a new schedule as an exact analogue of the source one on other " +
            "parameters: sourceScheduleId is required (list_schedules), name is the new " +
            "unique name (schedule_name_taken on a duplicate). fieldMapping [{sourceField, " +
            "targetField?, targetGuid?}] maps source columns to target fields — targetGuid " +
            "wins (a shared parameter GUID from the customer ФОП, see get_schedule_definition " +
            "guid), targetField is the schedulable field name in the new schedule; both " +
            "empty = skip the column. Columns absent from the mapping are copied " +
            "identically (same field name — built-in columns like Mark or Type survive " +
            "this way); when no analog exists the column lands in skippedFields[]. " +
            "copyFilters/copySort/copyTotals/copyFormat/copyStyle (default true) select " +
            "the blocks to transfer; itemized inherits from the source when not passed. " +
            "Returns created{scheduleId}, copiedFields[], skippedFields[], copiedFilters, " +
            "copiedSortGroups and warnings[]. undo_last deletes the clone. One schedule " +
            "per call; dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""sourceScheduleId"":{""type"":""integer""},""name"":{""type"":""string""},""fieldMapping"":{""type"":""array"",""maxItems"":40,""items"":{""type"":""object"",""properties"":{""sourceField"":{""type"":""string""},""targetField"":{""type"":[""string"",""null""]},""targetGuid"":{""type"":[""string"",""null""],""description"":""Shared parameter GUID of the target field (customer ФОП)""}},""required"":[""sourceField""],""additionalProperties"":false}},""copyFilters"":{""type"":""boolean"",""default"":true},""copySort"":{""type"":""boolean"",""default"":true},""copyTotals"":{""type"":""boolean"",""default"":true},""copyFormat"":{""type"":""boolean"",""default"":true},""copyStyle"":{""type"":""boolean"",""default"":true},""itemized"":{""type"":""boolean""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""sourceScheduleId"",""name""],""additionalProperties"":false}"
    };

    private sealed class SourceFieldSnapshot
    {
        public string Name = string.Empty;

        public string? Heading;

        public bool Hidden;

        public bool Calculated;

        public double? WidthMm;

        public ScheduleFieldDisplayType DisplayType = ScheduleFieldDisplayType.Standard;
    }

    private sealed class MappingEntry
    {
        public string SourceField = string.Empty;

        public string? TargetField;

        public string? TargetGuid;
    }

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var sourceId = AgentJson.GetLong(args, "sourceScheduleId", 0);
        if (sourceId <= 0)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass sourceScheduleId (the schedule to rebuild, see list_schedules).";
            return;
        }

        var name = AgentJson.GetString(args, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass name (the new unique schedule name).";
            return;
        }

        if (doc.GetElement(ElementIdCompat.Create(sourceId)) is not ViewSchedule source)
        {
            outcome.Success = false;
            outcome.ErrorCode = "schedule_not_found";
            outcome.Message = "Element " + sourceId + " is not a schedule. Use list_schedules " +
                              "to find the source id.";
            return;
        }

        if (source.IsTemplate)
        {
            outcome.Success = false;
            outcome.ErrorCode = "schedule_is_template";
            outcome.Message = "Schedule '" + source.Name + "' is a template and cannot be cloned.";
            return;
        }

        var sourceDefinition = source.Definition;
        if (SafeKeySchedule(sourceDefinition))
        {
            outcome.Success = false;
            outcome.ErrorCode = "schedule_not_cloneable";
            outcome.Message = "Key schedules cannot be cloned field-by-field; rebuild the " +
                              "key schedule with create_schedule instead.";
            return;
        }

        var trimmedName = name!.Trim();
        foreach (var existing in AgentScheduleReader.Collect(doc))
        {
            if (string.Equals(existing.Name, trimmedName, StringComparison.OrdinalIgnoreCase))
            {
                outcome.Success = false;
                outcome.ErrorCode = "schedule_name_taken";
                outcome.Message = "Schedule '" + trimmedName + "' already exists (id " +
                                  existing.Id.GetValue() + "). Rename it away first " +
                                  "(rename_schedule) or choose another name.";
                outcome.Data["owner"] = new
                {
                    scheduleId = existing.Id.GetValue(),
                    name = existing.Name
                };
                return;
            }
        }

        var copyFilters = AgentJson.GetBool(args, "copyFilters", true);
        var copySort = AgentJson.GetBool(args, "copySort", true);
        var copyTotals = AgentJson.GetBool(args, "copyTotals", true);
        var copyFormat = AgentJson.GetBool(args, "copyFormat", true);
        var copyStyle = AgentJson.GetBool(args, "copyStyle", true);
        var hasItemized = args.TryGetProperty("itemized", out var itemizedElement)
                          && itemizedElement.ValueKind is JsonValueKind.True or JsonValueKind.False;

        var warnings = new List<string>();
        var skippedFields = new List<object>();
        var copiedFields = new List<string>();

        // 1. Snapshot the source structure.
        var sourceFields = ReadSourceFields(sourceDefinition, warnings);
        var sourceSort = AgentScheduleWriteHelper.SerializeSort(sourceDefinition);
        var sourceFilters = AgentScheduleWriteHelper.SerializeFilters(
            sourceDefinition, AgentScheduleWriteHelper.FieldNamesByFieldId(sourceDefinition));
        var sourceTotals = AgentScheduleWriteHelper.SerializeTotals(sourceDefinition);
        var sourceStyle = AgentScheduleWriteHelper.SerializeStyle(source);

        var mapping = ReadMapping(args);
        var mappingBySource = new Dictionary<string, MappingEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in mapping)
        {
            if (!mappingBySource.ContainsKey(entry.SourceField))
                mappingBySource.Add(entry.SourceField, entry);
            else
                warnings.Add("Duplicate fieldMapping entry for '" + entry.SourceField +
                             "'; the first one wins.");
        }

        // 2. Create the clone and add the mapped fields in the source order.
        ViewSchedule clone;
        try
        {
            clone = ViewSchedule.CreateSchedule(doc, sourceDefinition.CategoryId);
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_category";
            outcome.Message = "Revit refused a schedule for the source category: " + ex.Message;
            return;
        }

        try
        {
            clone.Name = trimmedName;
        }
        catch (Exception ex)
        {
            doc.Delete(clone.Id);
            outcome.Success = false;
            outcome.ErrorCode = "invalid_name";
            outcome.Message = "Revit refused the name '" + trimmedName + "': " + ex.Message;
            return;
        }

        var cloneDefinition = clone.Definition;
        try
        {
            cloneDefinition.IsItemized = hasItemized
                ? itemizedElement.ValueKind == JsonValueKind.True
                : SafeItemized(sourceDefinition);
        }
        catch (Exception)
        {
            warnings.Add("The clone refused the itemized flag; it stays at the Revit default.");
        }

        outcome.Description = "Clone schedule " + source.Name + " as " + trimmedName;

        // source field name -> cloned field name (for filters/sorting transfer).
        var nameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in sourceFields)
        {
            ct.ThrowIfCancellationRequested();

            if (snapshot.Calculated)
            {
                skippedFields.Add(new
                {
                    sourceField = snapshot.Name,
                    reason = "calculated field — cannot be re-created on the target parameters"
                });
                warnings.Add("Column '" + snapshot.Name + "' is a calculated field and was " +
                             "skipped; re-create the formula in the clone if needed.");
                continue;
            }

            var (target, skipReason) = ResolveTarget(
                doc, cloneDefinition, snapshot.Name, mappingBySource);
            if (target is null)
            {
                skippedFields.Add(new { sourceField = snapshot.Name, reason = skipReason });
                continue;
            }

            try
            {
                var added = cloneDefinition.AddField(target!);
                var cloneField = cloneDefinition.GetField(added.FieldId);
                if (copyFormat)
                    CopyFieldFormat(snapshot, cloneField);
                if (copyTotals)
                    CopyDisplayType(snapshot, cloneField, warnings);

                copiedFields.Add(AgentScheduleWriteHelper.SafeFieldName(cloneField));
                nameMap[snapshot.Name] = AgentScheduleWriteHelper.SafeFieldName(cloneField);
            }
            catch (Exception ex)
            {
                skippedFields.Add(new
                {
                    sourceField = snapshot.Name,
                    reason = "Revit refused the field: " + ex.Message
                });
            }
        }

        // 3. Filters.
        var copiedFilters = 0;
        if (copyFilters)
            copiedFilters = CopyFilters(cloneDefinition, sourceFilters, nameMap, warnings);

        // 4. Sorting with group decoration.
        var copiedSortGroups = 0;
        if (copySort)
            copiedSortGroups = CopySorting(cloneDefinition, sourceSort, nameMap, warnings);

        // 5. Totals and the grand total row.
        if (copyTotals)
            CopyTotals(cloneDefinition, sourceTotals, warnings);

        // 6. Table style.
        if (copyStyle)
            CopyStyle(clone, sourceStyle, warnings);

        var readBackFields = AgentScheduleWriteHelper.CurrentFieldNames(cloneDefinition);
        var cloneId = clone.Id.GetValue();
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = cloneId,
            Name = clone.Name,
            NewValue = clone.Name,
            Detail = copiedFields.Count + " field(s) from '" + source.Name + "', " +
                     copiedFilters + " filter(s), " + copiedSortGroups + " sort group(s)"
        });
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = clone.UniqueId,
            ElementId = cloneId,
            ParameterName = "schedule_created",
            NewValue = clone.Name,
            Status = ChangeStatus.Applied
        });
        outcome.Data["created"] = new { scheduleId = cloneId, name = clone.Name };
        outcome.Data["copiedFields"] = copiedFields;
        outcome.Data["skippedFields"] = skippedFields;
        outcome.Data["copiedFilters"] = copiedFilters;
        outcome.Data["copiedSortGroups"] = copiedSortGroups;
        outcome.Data["warnings"] = warnings;
        outcome.Data["itemized"] = SafeItemized(cloneDefinition);
        outcome.Data["fields"] = readBackFields;
    }

    private static bool SafeKeySchedule(ScheduleDefinition definition)
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

    private static bool SafeItemized(ScheduleDefinition definition)
    {
        try
        {
            return definition.IsItemized;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

