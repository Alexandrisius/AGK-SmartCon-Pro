using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Project;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Rebinds an existing project parameter between instance and type binding
/// (<c>doc.ParameterBindings.ReInsert</c>). Revit drops the parameter values on
/// a rebind — the tool says so in the dry run and in the apply warnings; use it
/// for the ФОП migration when a customer parameter was bound with the wrong
/// binding type and the values are being rewritten anyway.
/// </summary>
public sealed class RebindProjectParameterTool : AgentWriteToolBase
{
    private const string ChangeItemKind = "project_parameter_rebind";

    public RebindProjectParameterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "rebind_project_parameter",
        Description =
            "Changes the binding of an EXISTING project parameter between instance and type " +
            "(doc.ParameterBindings.ReInsert over the same categories). Address it by name or " +
            "shared GUID (list_project_parameters shows both). The dry run reports the blast " +
            "radius: current binding, target binding, categories and how many model elements " +
            "carry the parameter. Revit DROPS the parameter values on a rebind (both " +
            "directions) — that loss is unavoidable and is stated in the dry run and in the " +
            "apply warnings; re-run the value migration right after. A same-binding request is " +
            "a no_changes skip. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""name"":{""type"":""string""},""guid"":{""type"":""string"",""description"":""Shared parameter GUID — the safe key when several same-name bindings exist""},""binding"":{""type"":""string"",""enum"":[""instance"",""type""],""description"":""The TARGET binding type""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""binding""],""additionalProperties"":false}"
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

        var bindingText = AgentJson.GetString(args, "binding");
        if (!string.Equals(bindingText, "instance", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bindingText, "type", StringComparison.OrdinalIgnoreCase))
        {
            Fail(outcome, "invalid_arguments", "binding must be instance or type.");
            return;
        }

        var wantInstance = string.Equals(bindingText, "instance", StringComparison.OrdinalIgnoreCase);

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

        if (string.Equals(row.BindingType, bindingText, StringComparison.OrdinalIgnoreCase))
        {
            outcome.Batch.Skipped.Add(new WriteItemReport
            {
                Name = row.Name,
                Reason = "the parameter is already bound as " + row.BindingType
            });
            outcome.Message = "No changes: '" + row.Name + "' is already a " + row.BindingType +
                              " project parameter (see skipped[]).";
            outcome.Data["binding"] = row.BindingType;
            return;
        }

        // BindingMap is keyed by Definition: resolve the live definition and its
        // binding (the row's DefinitionId points at the SharedParameterElement,
        // which is not a Definition itself). The categories come straight from
        // the binding — no name round-trip.
        Definition? mapDefinition = null;
        ElementBinding? existingBinding = null;
        var iterator = doc.ParameterBindings.ForwardIterator();
        while (iterator.MoveNext())
        {
            if (iterator.Current is not ElementBinding candidateBinding
                || iterator.Key is not InternalDefinition candidate)
            {
                continue;
            }

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
            existingBinding = candidateBinding;
            break;
        }

        if (mapDefinition is null || existingBinding is null)
        {
            Fail(outcome, "parameter_not_found",
                "The binding was found but its definition could not be resolved from the " +
                "document binding map.",
                "Non-shared project bindings cannot be rebound — remove and re-add the parameter.");
            return;
        }

        var affected = CountElements(doc, row.Categories, ct);
        outcome.Data["name"] = row.Name;
        outcome.Data["guid"] = row.Guid?.ToString("D");
        outcome.Data["currentBinding"] = row.BindingType;
        outcome.Data["targetBinding"] = bindingText;
        outcome.Data["categories"] = row.Categories;
        outcome.Data["elementsInCategories"] = affected;
        outcome.Data["valuesWillBeLost"] = true;

        outcome.Warnings.Add(
            "A rebind DROPS the parameter values in BOTH directions (instance↔type): " +
            affected + " element(s) in " + row.Categories.Count + " categor(ies) lose the " +
            "current value of '" + row.Name + "'. Re-run the value migration right after the " +
            "rebind.");

        if (dryRun)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Name = row.Name,
                OldValue = row.BindingType + " binding",
                NewValue = bindingText + " binding",
                Detail = "dry run: " + affected + " element(s) affected, values will be lost"
            });
            return;
        }

        var app = doc.Application;
        var categorySet = app.Create.NewCategorySet();
        foreach (Category category in existingBinding.Categories)
            categorySet.Insert(category);

        if (categorySet.IsEmpty)
        {
            Fail(outcome, "binding_failed",
                "None of the bound categories could be resolved to create the new binding.",
                "[Action: check list_project_parameters categories and retry]");
            return;
        }

        var binding = wantInstance
            ? (Binding)app.Create.NewInstanceBinding(categorySet)
            : app.Create.NewTypeBinding(categorySet);

        bool applied;
        try
        {
            var map = doc.ParameterBindings;
            applied = map.ReInsert(mapDefinition, binding);
        }
        catch (Exception ex)
        {
            Fail(outcome, "binding_failed",
                "Revit refused the rebind of '" + row.Name + "': " + ex.GetType().Name + ": " +
                ex.Message,
                "[Action: check that the document is not read-only and no dialog blocks Revit]");
            return;
        }

        if (!applied)
        {
            Fail(outcome, "binding_failed",
                "Revit refused the rebind of '" + row.Name + "' (ReInsert returned false).",
                "[Action: verify the binding with list_project_parameters]");
            return;
        }

        var after = AgentProjectParameterReader.FindAll(doc, row.Name)
            .FirstOrDefault(candidate => row.Guid is null || candidate.Guid == row.Guid);
        if (after is null || !string.Equals(after.BindingType, bindingText, StringComparison.OrdinalIgnoreCase))
        {
            Fail(outcome, "binding_failed",
                "The rebind was applied but the read-back shows '" +
                (after?.BindingType ?? "(missing)") + "' instead of '" + bindingText + "'.",
                "[Action: verify with list_project_parameters]");
            return;
        }

        outcome.AffectedCount = 1;
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Name = row.Name,
            OldValue = row.BindingType + " binding",
            NewValue = bindingText + " binding",
            Detail = affected + " element(s) affected; previous values were dropped by the rebind"
        });
        outcome.Data["verifiedBinding"] = after.BindingType;
        outcome.Message = "Rebound '" + row.Name + "' from " + row.BindingType + " to " +
                          bindingText + " over " + row.Categories.Count + " categor(ies); " +
                          affected + " element(s) lost the previous value — re-run the value " +
                          "migration.";
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

    private static Category? CategoryOfName(Document doc, string categoryName)
    {
        var resolved = AgentCategoryResolver.Resolve(doc, new[] { categoryName }, new List<string>());
        return resolved.Count == 0 ? null : Category.GetCategory(doc, resolved[0]);
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
