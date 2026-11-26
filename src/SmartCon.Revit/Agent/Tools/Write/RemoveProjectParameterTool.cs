using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Project;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Removes a project parameter binding (<c>doc.ParameterBindings.Remove</c>)
/// and, on request, the parameter definition element itself
/// (<c>doc.Delete(ParameterElement.Id)</c>). The dry run and the apply share
/// the same usage audit (categories, elements, non-empty values, schedule
/// fields, view filters): Revit deletes a used definition WITHOUT any warning
/// (Autodesk forum, 2017), so the tool refuses until force=true and even then
/// says what breaks. Values are lost in every case and undo_last cannot
/// restore a removed binding — the report says both.
/// </summary>
public sealed class RemoveProjectParameterTool : AgentWriteToolBase
{
    private const int MaxValueScanElements = 5000;
    private const int MaxUsageNames = 20;

    public RemoveProjectParameterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "remove_project_parameter",
        Description =
            "Removes an EXISTING project parameter binding (doc.ParameterBindings.Remove) by " +
            "name or shared GUID; deleteDefinition:true also deletes the parameter element " +
            "(SharedParameterElement / project ParameterElement) from the document. The dry run " +
            "and the apply report the same audit: bound categories, elements in them, how many " +
            "carry a NON-EMPTY value, and usage in schedules (fields) and view filters. Revit " +
            "drops the parameter values and undo_last CANNOT restore the removed binding; " +
            "deleting the definition also silently degrades family formulas that embed the " +
            "GUID (not auditable from the project) — that is why deleteDefinition requires " +
            "force:true, and any non-empty values or schedule/filter usage require force too. " +
            "The shared parameter FILE entry is not touched. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""name"":{""type"":""string"",""description"":""Parameter name (list_project_parameters shows name + guid)""},""guid"":{""type"":""string"",""description"":""Shared parameter GUID — the safe key when several same-name bindings exist""},""deleteDefinition"":{""type"":""boolean"",""default"":false,""description"":""Also delete the parameter element from the document (shared: SharedParameterElement, project: ParameterElement). Requires force:true""},""force"":{""type"":""boolean"",""default"":false,""description"":""Proceed despite non-empty values, schedule/filter usage or deleteDefinition""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        if (doc.IsFamilyDocument)
        {
            Fail(outcome, "not_a_project_document",
                "Project parameter bindings live in a project document; the active document " +
                "is a family.");
            return;
        }

        var name = AgentJson.GetString(args, "name")?.Trim();
        var guidText = AgentJson.GetString(args, "guid")?.Trim();
        if (string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(guidText))
        {
            Fail(outcome, "invalid_arguments",
                "Pass name or guid of an existing project parameter (list_project_parameters).");
            return;
        }

        Guid? guid = null;
        if (!string.IsNullOrWhiteSpace(guidText))
        {
            if (!Guid.TryParse(guidText!, out var parsedGuid))
            {
                Fail(outcome, "invalid_arguments", "'" + guidText + "' is not a valid GUID.");
                return;
            }

            guid = parsedGuid;
        }

        var deleteDefinition = AgentJson.GetBool(args, "deleteDefinition", false);
        var force = AgentJson.GetBool(args, "force", false);

        var row = FindRow(doc, name, guid);
        if (row is null)
        {
            Fail(
                outcome,
                "parameter_not_found",
                "No project parameter binding matches " +
                (name is null ? "GUID " + guidText : "'" + name + "'") + ".",
                "List the bindings with list_project_parameters and pass its name/guid.");
            return;
        }

        // ---- Usage audit (same numbers in the dry run and in the apply) ----
        var affected = CountElements(doc, row.Categories, ct);
        var nonEmpty = CountNonEmptyValues(doc, row, ct, out var scanned, out var scanTruncated);
        var schedules = FindSchedulesUsing(doc, row);
        var filters = FindFiltersUsing(doc, row);

        outcome.Data["name"] = row.Name;
        outcome.Data["guid"] = row.Guid?.ToString("D");
        outcome.Data["binding"] = row.BindingType;
        outcome.Data["isShared"] = row.IsShared;
        outcome.Data["categories"] = row.Categories;
        outcome.Data["elementsInCategories"] = affected;
        outcome.Data["nonEmptyValues"] = nonEmpty;
        outcome.Data["valuesScanned"] = scanned;
        outcome.Data["valuesScanTruncated"] = scanTruncated ? true : null;
        outcome.Data["schedulesUsing"] = schedules;
        outcome.Data["filtersUsing"] = filters;
        outcome.Data["valuesWillBeLost"] = true;
        outcome.Data["undoSupported"] = false;

        var usageBlocked = schedules.Count > 0 || filters.Count > 0;
        var blocked = usageBlocked || nonEmpty > 0 || deleteDefinition;
        if (blocked)
        {
            outcome.Warnings.Add(
                (nonEmpty > 0
                    ? nonEmpty + " element(s)/type(s) carry a NON-EMPTY value; "
                    : string.Empty) +
                (schedules.Count > 0
                    ? "used by schedule(s): " + string.Join(", ", schedules) + "; "
                    : string.Empty) +
                (filters.Count > 0
                    ? "used by view filter(s): " + string.Join(", ", filters) + "; "
                    : string.Empty) +
                (deleteDefinition
                    ? "deleteDefinition also degrades family formulas embedding the GUID " +
                      "(not auditable from the project); "
                    : string.Empty) +
                "pass force:true to proceed anyway.");
        }

        if (dryRun)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Name = row.Name,
                OldValue = row.BindingType + " binding" + (deleteDefinition ? " + definition" : string.Empty),
                NewValue = "(removed)",
                Detail = "dry run: " + affected + " element(s) in " + row.Categories.Count +
                         " categor(ies), " + nonEmpty + " non-empty, " +
                         schedules.Count + " schedule(s), " + filters.Count + " filter(s)"
            });
            outcome.Message = "Dry run: would remove '" + row.Name + "' (" + row.BindingType +
                              (deleteDefinition ? ", definition too" : string.Empty) + ").";
            return;
        }

        if (!force)
        {
            if (usageBlocked)
            {
                Fail(outcome, "binding_in_use",
                    "'" + row.Name + "' is used by " + schedules.Count + " schedule field(s) and " +
                    filters.Count + " view filter(s); removing it would break them.",
                    "[Action: clear the usage or pass force:true]");
                return;
            }

            if (nonEmpty > 0)
            {
                Fail(outcome, "values_not_empty",
                    "'" + row.Name + "' has " + nonEmpty + " non-empty value(s) on " +
                    row.BindingType + " owners; removal drops them.",
                    "[Action: migrate the values first (remap_parameter_values) or pass force:true]");
                return;
            }

            if (deleteDefinition)
            {
                Fail(outcome, "definition_delete_requires_force",
                    "deleteDefinition silently degrades family formulas that embed the GUID — " +
                    "it always requires force:true.",
                    "[Action: re-run with force:true once the owner approved the definition delete]");
                return;
            }
        }

        // BindingMap is keyed by Definition: resolve the live definition (the
        // row's DefinitionId points at the ParameterElement, not a Definition).
        Definition? mapDefinition = null;
        var iterator = doc.ParameterBindings.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Key is not InternalDefinition candidate)
                continue;

            var byGuid = false;
            if (row.Guid is not null
                && doc.GetElement(candidate.Id) is SharedParameterElement shared)
            {
                byGuid = shared.GuidValue == row.Guid.Value;
            }

            var byName = row.Guid is null
                         && string.Equals(candidate.Name, row.Name, StringComparison.OrdinalIgnoreCase);
            if (!byGuid && !byName)
                continue;

            mapDefinition = candidate;
            break;
        }

        if (mapDefinition is null)
        {
            Fail(outcome, "parameter_not_found",
                "The binding was found but its definition could not be resolved from the " +
                "document binding map.");
            return;
        }

        bool removed;
        try
        {
            removed = doc.ParameterBindings.Remove(mapDefinition);
        }
        catch (Exception ex)
        {
            Fail(outcome, "binding_failed",
                "Revit refused to remove the binding of '" + row.Name + "': " +
                ex.GetType().Name + ": " + ex.Message,
                "[Action: check that the document is writable and no dialog blocks Revit]");
            return;
        }

        if (!removed)
        {
            Fail(outcome, "binding_failed",
                "Revit refused to remove the binding of '" + row.Name + "' (Remove returned false).",
                "[Action: verify with list_project_parameters]");
            return;
        }

        if (deleteDefinition)
        {
            var element = doc.GetElement(ElementIdCompat.Create(row.DefinitionId));
            if (element is ParameterElement parameterElement)
            {
                try
                {
                    doc.Delete(parameterElement.Id);
                }
                catch (Exception ex)
                {
                    // The binding is already gone; the leftover definition is
                    // reported, not fatal.
                    outcome.Warnings.Add(
                        "The binding was removed but the definition element could not be " +
                        "deleted: " + ex.Message + " [Action: check remaining usage with " +
                        "list_project_parameters]");
                }
            }
            else
            {
                outcome.Warnings.Add(
                    "The binding was removed but the definition element " + row.DefinitionId +
                    " no longer exists (already deleted with the binding).");
            }
        }

        // Read-back: the row must be gone.
        var after = FindRow(doc, row.Guid is null ? row.Name : null, row.Guid);
        outcome.Data["verifiedRemoved"] = after is null;
        if (after is not null)
        {
            Fail(outcome, "binding_failed",
                "The binding was removed but the read-back still finds '" + row.Name + "'.",
                "[Action: verify with list_project_parameters]");
            return;
        }

        outcome.AffectedCount = 1;
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Name = row.Name,
            OldValue = row.BindingType + " binding" + (deleteDefinition ? " + definition" : string.Empty),
            NewValue = "(removed)",
            Action = "remove_project_parameter",
            Detail = affected + " element(s) in " + row.Categories.Count + " categor(ies); " +
                     nonEmpty + " value(s) dropped; NOT restorable by undo_last"
        });
        outcome.Message = "Removed the " + row.BindingType + " project parameter '" + row.Name +
                          "'" + (deleteDefinition ? " and its definition" : string.Empty) +
                          "; " + affected + " element(s) in " + row.Categories.Count +
                          " categor(ies) lose the value. undo_last cannot restore it.";
    }

    private static AgentProjectParameterReader.ProjectParameterRow? FindRow(
        Document doc,
        string? name,
        Guid? guid)
    {
        foreach (var row in AgentProjectParameterReader.Collect(doc))
        {
            if (guid is not null)
            {
                if (row.Guid == guid)
                    return row;
                continue;
            }

            if (string.Equals(row.Name, name, StringComparison.OrdinalIgnoreCase))
                return row;
        }

        return null;
    }

    private static int CountElements(
        Document doc,
        IReadOnlyList<string> categories,
        CancellationToken ct)
    {
        var total = 0;
        foreach (var categoryName in categories)
        {
            ct.ThrowIfCancellationRequested();
            var category = CategoryOfName(doc, categoryName);
            if (category is null)
                continue;

            total += new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .OfCategoryId(category.Id)
                .GetElementCount();
        }

        return total;
    }

    /// <summary>
    /// Non-empty values on the binding's own owners: type binding → element
    /// types, instance binding → instances. Capped at MaxValueScanElements;
    /// truncation is reported so a big model does not read as "all empty".
    /// </summary>
    private static int CountNonEmptyValues(
        Document doc,
        AgentProjectParameterReader.ProjectParameterRow row,
        CancellationToken ct,
        out int scanned,
        out bool truncated)
    {
        scanned = 0;
        truncated = false;
        var nonEmpty = 0;
        var typeBinding = string.Equals(row.BindingType, "type", StringComparison.OrdinalIgnoreCase);

        foreach (var categoryName in row.Categories)
        {
            ct.ThrowIfCancellationRequested();
            var category = CategoryOfName(doc, categoryName);
            if (category is null)
                continue;

            var collector = new FilteredElementCollector(doc)
                .OfCategoryId(category.Id);
            collector = typeBinding
                ? collector.WhereElementIsElementType()
                : collector.WhereElementIsNotElementType();

            foreach (var element in collector)
            {
                if (scanned >= MaxValueScanElements)
                {
                    truncated = true;
                    return nonEmpty;
                }

                scanned++;
                if (HasNonEmptyValue(element, row))
                    nonEmpty++;
            }
        }

        return nonEmpty;
    }

    private static bool HasNonEmptyValue(
        Element element,
        AgentProjectParameterReader.ProjectParameterRow row)
    {
        try
        {
            foreach (Parameter parameter in element.Parameters)
            {
                var parameterName = parameter.Definition?.Name;
                if (parameterName is null
                    || !string.Equals(parameterName, row.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (row.Guid is not null && parameter.IsShared && parameter.GUID != row.Guid.Value)
                    continue;

                if (!parameter.HasValue)
                    continue;

                var text = parameter.AsValueString() ?? parameter.AsString();
                return !string.IsNullOrWhiteSpace(text);
            }
        }
        catch (Exception)
        {
            // An unreadable element must not fail the audit.
        }

        return false;
    }

    private static List<string> FindSchedulesUsing(
        Document doc,
        AgentProjectParameterReader.ProjectParameterRow row)
    {
        var names = new List<string>();
        try
        {
            foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(ViewSchedule)))
            {
                if (element is not ViewSchedule schedule || schedule.IsTemplate)
                    continue;

                var definition = schedule.Definition;
                for (var index = 0; index < definition.GetFieldCount(); index++)
                {
                    ScheduleField? field;
                    try
                    {
                        field = definition.GetField(index);
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (field is null)
                        continue;

                    var parameterId = field.ParameterId;
                    var byId = parameterId is not null
                               && parameterId != ElementId.InvalidElementId
                               && parameterId.GetValue() == row.DefinitionId;
                    var byName = !byId && string.Equals(
                        field.ColumnHeading, row.Name, StringComparison.OrdinalIgnoreCase);
                    if (!byId && !byName)
                        continue;

                    AddUsageName(names, schedule.Name);
                    break;
                }
            }
        }
        catch (Exception)
        {
            // A schedule pass failure must not block the removal; the audit
            // simply reports what it could read.
        }

        return names;
    }

    private static List<string> FindFiltersUsing(
        Document doc,
        AgentProjectParameterReader.ProjectParameterRow row)
    {
        var names = new List<string>();
        try
        {
            foreach (var element in new FilteredElementCollector(doc)
                         .OfClass(typeof(ParameterFilterElement)))
            {
                if (element is not ParameterFilterElement filter)
                    continue;

                if (!FilterUsesParameter(filter.GetElementFilter(), doc, row))
                    continue;

                AddUsageName(names, filter.Name);
            }
        }
        catch (Exception)
        {
            // Same as schedules: report what was readable.
        }

        return names;
    }

    private static void AddUsageName(List<string> names, string name)
    {
        if (names.Count < MaxUsageNames)
        {
            names.Add(name);
            return;
        }

        // One truncation marker, not one per hidden name (validator note).
        const string more = "(+more)";
        if (!names.Contains(more))
            names.Add(more);
    }

    private static bool FilterUsesParameter(
        ElementFilter? filter,
        Document doc,
        AgentProjectParameterReader.ProjectParameterRow row)
    {
        if (filter is null)
            return false;

        if (filter is ElementLogicalFilter logical)
        {
            foreach (var child in logical.GetFilters())
            {
                if (FilterUsesParameter(child, doc, row))
                    return true;
            }

            return false;
        }

        if (filter is not ElementParameterFilter parameterFilter)
            return false;

        foreach (var rule in parameterFilter.GetRules())
        {
            try
            {
                var parameterId = rule.GetRuleParameter();
                if (parameterId is null || parameterId == ElementId.InvalidElementId)
                    continue;

                if (parameterId.GetValue() == row.DefinitionId)
                    return true;

                if (doc.GetElement(parameterId) is ParameterElement ruleParameter
                    && string.Equals(
                        ruleParameter.GetDefinition().Name, row.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // Skip unreadable rules.
            }
        }

        return false;
    }

    private static Category? CategoryOfName(Document doc, string categoryName)
    {
        var resolved = AgentCategoryResolver.Resolve(doc, new[] { categoryName }, new List<string>());
        return resolved.Count == 0 ? null : Category.GetCategory(doc, resolved[0]);
    }

    private static void Fail(
        WriteOutcome outcome,
        string errorCode,
        string message,
        string? hint = null)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = string.IsNullOrEmpty(hint) ? message : message + " " + hint;
        outcome.Batch.Failed.Add(new WriteItemReport
        {
            ErrorCode = errorCode,
            Reason = message
        });
    }
}
