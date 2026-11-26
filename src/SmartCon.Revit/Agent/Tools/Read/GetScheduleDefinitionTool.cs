using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Reads the definition of one schedule: fields, filters, sorting/grouping and the
/// basic flags. This is what explains why a schedule shows zero rows even though
/// the model has elements (external benchmark, task 4). Read-only.
/// </summary>
public sealed class GetScheduleDefinitionTool : AgentReadToolBase
{
    private const int MaxFields = 300;
    private const int MaxFilters = 50;
    private const int MaxSortFields = 20;

    public GetScheduleDefinitionTool(IRevitContext revitContext) : base(revitContext)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "get_schedule_definition",
        Risk = AgentToolRisk.Read,
        SupportsDryRun = false,
        Description =
            "Reads the definition of one schedule by scheduleId or exact scheduleName: " +
            "category, key-schedule/itemized/multicategory flags, fields (name, heading, hidden, " +
            "calculated, field type, scope, shared parameter guid and parameterName, " +
            "columnWidthMm and the totalsDisplay mode), filters (field, operator, value), " +
            "sorting/grouping fields with the group decoration flags, the table style " +
            "(stripedRows, useStripedRowsOnSheets, body/header/title text type names, " +
            "showTitle) and placements[] (sheetId, sheetNumber, xMm, yMm, rotation of every " +
            "ScheduleSheetInstance), plus isMulticategory and placedOnSheet/sheetNumbers. " +
            "fields[].scope is instance or type: the level the field's parameter lives on, " +
            "i.e. the scope= value the write tools expect. Pass that scope, not fieldType: " +
            "for a built-in field fieldType is the real Revit kind (Instance or ElementType) " +
            "and agrees with scope; for a shared parameter it is always Instance whether it " +
            "is instance- or type-bound. scope is null " +
            "when the level does not apply (a calculated, room or material field) or cannot be " +
            "established yet (a family parameter that is not bound in the project and has no " +
            "element to ask, or one bound to types in some families and to instances in " +
            "others). scopeSource says which signal produced it (fieldType, binding or the " +
            "scheduled elements); scopeConflict=true means the binding map disagreed with the " +
            "elements and the element verdict won. This tool does not return element ids: use " +
            "get_schedule_elements for the elements behind the rows. Use it to explain why a " +
            "schedule shows zero rows or fewer rows than the model has, to check whether a " +
            "schedule is issued documentation (placed on a sheet) or a draft, and to verify " +
            "changes made by the schedule write tools (add_schedule_field, set_schedule_filter, " +
            "set_schedule_sort, set_schedule_field_format, set_schedule_style, clone_schedule).",
        InputSchemaJson = @"{""type"":""object"",""properties"":{" + DocumentIdPropertyJson + @"""scheduleId"":{""type"":""integer""},""scheduleName"":{""type"":""string""}},""additionalProperties"":false}"
    };

    protected override ToolResult Execute(Document doc, JsonElement args, CancellationToken ct)
    {
        var scheduleId = AgentJson.GetLong(args, "scheduleId", 0);
        var scheduleName = AgentJson.GetString(args, "scheduleName");
        var schedule = AgentScheduleReader.FindSchedule(doc, scheduleId, scheduleName);
        if (schedule is null)
        {
            return ToolResult.Fail(
                "schedule_not_found",
                "Pass scheduleId or exact scheduleName of an existing schedule " +
                "(see list_schedules).");
        }

        ScheduleDefinition? definition;
        try
        {
            definition = schedule.Definition;
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                "schedule_read_failed",
                "Revit could not read the schedule definition: " + ex.Message);
        }

        if (definition is null)
            return ToolResult.Fail("schedule_read_failed", "The schedule has no definition.");

        var fields = new List<Dictionary<string, object?>>();
        var fieldNames = new Dictionary<int, string>();
        var sourceElements = CollectScopeSourceElements(doc, definition, ct);
        try
        {
            foreach (var fieldId in definition.GetFieldOrder())
            {
                ct.ThrowIfCancellationRequested();
                if (fields.Count >= MaxFields)
                    break;

                var field = SafeField(definition, fieldId);
                if (field is null)
                    continue;

                var name = SafeText(() => field.GetName());
                fieldNames[fieldId.IntegerValue] = name ?? fieldId.IntegerValue.ToString();

                long? parameterId = null;
                try
                {
                    var raw = field.ParameterId;
                    if (raw is not null && raw != ElementId.InvalidElementId)
                        parameterId = raw.GetValue();
                }
                catch (Exception)
                {
                    // Calculated/combined fields have no parameter id.
                }

                var scope = AgentScheduleParameterScope.Describe(
                    doc, definition, field, sourceElements);
                var entry = new Dictionary<string, object?>
                {
                    ["fieldId"] = fieldId.IntegerValue,
                    ["name"] = name,
                    ["heading"] = SafeText(() => field.ColumnHeading),
                    ["hidden"] = field.IsHidden,
                    ["calculated"] = field.IsCalculatedField,
                    ["parameterId"] = parameterId,
                    ["guid"] = AgentScheduleReader.DescribeParameterGuid(doc, field),
                    ["parameterName"] = AgentScheduleReader.DescribeParameter(doc, field),
                    ["fieldType"] = field.FieldType.ToString(),
                    ["scope"] = scope.Scope,
                    ["scopeSource"] = scope.Source,
                    ["columnWidthMm"] = AgentScheduleWriteHelper.GetColumnWidthMm(field),
                    ["totalsDisplay"] = SafeText(() =>
                        AgentScheduleWriteHelper.ModeName(field.DisplayType))
                };
                if (scope.Conflict)
                    entry["scopeConflict"] = true;
                if (scope.Note is not null)
                    entry["scopeNote"] = scope.Note;
                fields.Add(entry);
            }
        }
        catch (Exception)
        {
            // Keep the fields read so far.
        }

        var filters = new List<Dictionary<string, object?>>();
        var phaseFilters = new List<Dictionary<string, object?>>();
        try
        {
            foreach (var filter in definition.GetFilters())
            {
                ct.ThrowIfCancellationRequested();
                if (filters.Count >= MaxFilters)
                    break;

                // An ElementId filter value (a phase, a level, a type) is a bare
                // number without the name; phase filters stayed invisible in
                // diagnostics for exactly that reason (#451).
                string? valueName = null;
                try
                {
                    if (filter.IsElementIdValue)
                    {
                        var valueId = filter.GetElementIdValue();
                        valueName = valueId is null || valueId == ElementId.InvalidElementId
                            ? null
                            : doc.GetElement(valueId)?.Name;
                    }
                }
                catch (Exception)
                {
                    // Value kind did not match; keep the raw value only.
                }

                var entry = new Dictionary<string, object?>
                {
                    ["field"] = FieldName(fieldNames, filter.FieldId),
                    ["fieldId"] = filter.FieldId?.IntegerValue,
                    ["op"] = filter.FilterType.ToString(),
                    ["value"] = ReadFilterValue(filter)
                };
                if (!string.IsNullOrWhiteSpace(valueName))
                    entry["valueName"] = valueName;

                if (IsPhaseField(definition, filter.FieldId))
                {
                    entry["isPhase"] = true;
                    phaseFilters.Add(new Dictionary<string, object?>
                    {
                        ["field"] = entry["field"],
                        ["op"] = entry["op"],
                        ["value"] = entry["value"],
                        ["valueName"] = valueName
                    });
                }

                filters.Add(entry);
            }
        }
        catch (Exception)
        {
            // Filters are optional detail.
        }

        var sortFields = new List<Dictionary<string, object?>>();
        try
        {
            foreach (var sort in definition.GetSortGroupFields())
            {
                ct.ThrowIfCancellationRequested();
                if (sortFields.Count >= MaxSortFields)
                    break;

                sortFields.Add(new Dictionary<string, object?>
                {
                    ["field"] = FieldName(fieldNames, sort.FieldId),
                    ["fieldId"] = sort.FieldId?.IntegerValue,
                    ["order"] = sort.SortOrder.ToString(),
                    ["showHeader"] = sort.ShowHeader,
                    ["showFooter"] = sort.ShowFooter,
                    ["showBlankLine"] = sort.ShowBlankLine
                });
            }
        }
        catch (Exception)
        {
            // Sorting is optional detail.
        }

        var sheetNumbers = AgentScheduleReader.MapSheetNumbers(doc);
        sheetNumbers.TryGetValue(schedule.Id.GetValue(), out var onSheets);
        var placedOnSheet = onSheets is { Count: > 0 };
        var placements = ReadPlacements(doc, schedule.Id, ct);

#if REVIT2021_OR_GREATER
        var stripedRows = SafeBoolOrNull(() => schedule.HasStripedRows);
        var stripedRowsOnSheets = SafeBoolOrNull(() => schedule.UseStripedRowsOnSheets);
#else
        // Striped rows exist only since the Revit 2021 API (R19/R20: null).
        bool? stripedRows = null;
        bool? stripedRowsOnSheets = null;
#endif

        var payload = new Dictionary<string, object?>
        {
            ["scheduleId"] = schedule.Id.GetValue(),
            ["name"] = schedule.Name,
            ["isTemplate"] = schedule.IsTemplate,
            ["placedOnSheet"] = placedOnSheet,
            ["sheetNumbers"] = onSheets ?? Array.Empty<string>(),
            ["placements"] = placements,
            ["categoryId"] = CategoryId(definition),
            ["categoryName"] = CategoryName(doc, definition),
            ["isMulticategory"] = SafeBoolOrNull(() =>
                definition.CategoryId is null
                || definition.CategoryId == ElementId.InvalidElementId),
            ["isKeySchedule"] = SafeBool(() => definition.IsKeySchedule),
            ["isItemized"] = SafeBool(() => definition.IsItemized),
            ["isFilteredBySheet"] = SafeBool(() => definition.IsFilteredBySheet),
            ["includeLinkedFiles"] = SafeBool(() => definition.IncludeLinkedFiles),
            ["hasEmbeddedSchedule"] = SafeBool(() => definition.HasEmbeddedSchedule),
            ["showGrandTotal"] = SafeBool(() => definition.ShowGrandTotal),
            ["showTitle"] = SafeBool(() => definition.ShowTitle),
            ["stripedRows"] = stripedRows,
            ["useStripedRowsOnSheets"] = stripedRowsOnSheets,
            ["bodyTextTypeId"] = SafeLong(() => schedule.BodyTextTypeId.GetValue()),
            ["headerTextTypeId"] = SafeLong(() => schedule.HeaderTextTypeId.GetValue()),
            ["titleTextTypeId"] = SafeLong(() => schedule.TitleTextTypeId.GetValue()),
            ["bodyTextTypeName"] = TextTypeName(doc, schedule.BodyTextTypeId),
            ["headerTextTypeName"] = TextTypeName(doc, schedule.HeaderTextTypeId),
            ["titleTextTypeName"] = TextTypeName(doc, schedule.TitleTextTypeId),
            ["fieldCount"] = fields.Count,
            ["fields"] = fields,
            ["fieldsTruncated"] = fields.Count >= MaxFields,
            ["filterCount"] = filters.Count,
            ["filters"] = filters,
            ["filtersTruncated"] = filters.Count >= MaxFilters,
            ["hasPhaseFilter"] = phaseFilters.Count > 0,
            ["phaseFilters"] = phaseFilters.Count > 0 ? phaseFilters : null,
            ["sortGroupFieldCount"] = sortFields.Count,
            ["sortGroupFields"] = sortFields,
            ["hint"] = "Zero rows with elements present usually mean a filter, a phase or an " +
                       "empty parameter used by the filter; phase filters are listed in " +
                       "phaseFilters with the phase name. Fields, filters and sorting can be " +
                       "changed with the schedule write tools if they are available in this " +
                       "session (add_schedule_field, set_schedule_filter, set_schedule_sort)."
        };

        return ToolResult.Ok(
            AgentJson.Serialize(payload),
            "Read schedule '" + schedule.Name + "': " + fields.Count + " field(s), " +
            filters.Count + " filter(s), " + sortFields.Count + " sort/group field(s)" +
            (placedOnSheet
                ? ", placed on sheet(s) " + string.Join(", ", onSheets!)
                : ", not placed on any sheet") + ".");
    }

    /// <summary>
    /// Elements the schedule rows come from, collected once for all fields: the
    /// scope probe uses them instead of a full collector per field.
    /// </summary>
    private static IReadOnlyList<Element> CollectScopeSourceElements(
        Document doc,
        ScheduleDefinition definition,
        CancellationToken ct)
    {
        var elements = new List<Element>();
        try
        {
            foreach (var element in AgentScheduleReader.CollectSourceElements(doc, definition, 400))
            {
                ct.ThrowIfCancellationRequested();
                elements.Add(element);
                if (elements.Count >= 400)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The element probe is optional; fieldType/binding still answer.
        }

        return elements;
    }

    private static ScheduleField? SafeField(
        ScheduleDefinition definition,
        ScheduleFieldId fieldId)
    {
        try
        {
            return definition.GetField(fieldId);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The filter field is Phase Created / Phase Demolished.</summary>
    private static bool IsPhaseField(ScheduleDefinition definition, ScheduleFieldId? fieldId)
    {
        if (fieldId is null)
            return false;

        try
        {
            var parameterId = definition.GetField(fieldId).ParameterId;
            if (parameterId is null || parameterId == ElementId.InvalidElementId)
                return false;

            var value = parameterId.GetValue();
            return value == (long)(int)BuiltInParameter.PHASE_CREATED
                   || value == (long)(int)BuiltInParameter.PHASE_DEMOLISHED;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string? FieldName(
        IReadOnlyDictionary<int, string> names,
        ScheduleFieldId? fieldId)
    {
        if (fieldId is null)
            return null;

        return names.TryGetValue(fieldId.IntegerValue, out var name)
            ? name
            : fieldId.IntegerValue.ToString();
    }

    private static object? ReadFilterValue(ScheduleFilter filter)
    {
        try
        {
            if (filter.IsStringValue)
                return filter.GetStringValue();
            if (filter.IsDoubleValue)
                return filter.GetDoubleValue();
            if (filter.IsIntegerValue)
                return filter.GetIntegerValue();
            if (filter.IsElementIdValue)
                return filter.GetElementIdValue()?.GetValue();
        }
        catch (Exception)
        {
            // The value kind did not match the filter type.
        }

        return null;
    }

    private static long? CategoryId(ScheduleDefinition definition)
    {
        try
        {
            var categoryId = definition.CategoryId;
            return categoryId is null || categoryId == ElementId.InvalidElementId
                ? null
                : categoryId.GetValue();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? CategoryName(Document doc, ScheduleDefinition definition)
    {
        try
        {
            var categoryId = definition.CategoryId;
            if (categoryId is null || categoryId == ElementId.InvalidElementId)
                return null;

            return Category.GetCategory(doc, categoryId)?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static bool SafeBool(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool? SafeBoolOrNull(Func<bool> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static long? SafeLong(Func<long> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TextTypeName(Document doc, ElementId? textTypeId)
    {
        try
        {
            return textTypeId is null || textTypeId == ElementId.InvalidElementId
                ? null
                : doc.GetElement(textTypeId)?.Name;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// All sheet placements of the schedule: sheet, top-left point in millimetres
    /// and the table rotation (the data remove_schedule_from_sheet undo and the
    /// rebuild flow place at the same point need).
    /// </summary>
    private static List<object> ReadPlacements(Document doc, ElementId scheduleId, CancellationToken ct)
    {
        var placements = new List<object>();
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ScheduleSheetInstance)))
        {
            ct.ThrowIfCancellationRequested();
            if (element is not ScheduleSheetInstance instance)
                continue;
            if (instance.ScheduleId != scheduleId)
                continue;

            double? pointX = null;
            double? pointY = null;
            string? rotation = null;
            try
            {
                var point = instance.Point;
                if (point is not null)
                {
                    pointX = AgentUnitsFormat.ToMillimeters(point.X);
                    pointY = AgentUnitsFormat.ToMillimeters(point.Y);
                }
            }
            catch (Exception)
            {
                // Revision schedules may not expose a point.
            }

            try
            {
                rotation = AgentScheduleWriteHelper.RotationName(instance.Rotation);
            }
            catch (Exception)
            {
                rotation = null;
            }

            var sheet = doc.GetElement(instance.OwnerViewId) as ViewSheet;
            placements.Add(new
            {
                instanceId = instance.Id.GetValue(),
                sheetId = instance.OwnerViewId.GetValue(),
                sheetNumber = sheet?.SheetNumber,
                xMm = pointX,
                yMm = pointY,
                rotation
            });
        }

        return placements;
    }

    private static string? SafeText(Func<string?> read)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            return null;
        }
    }
}