using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Families;
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
            "Changes an EXISTING project parameter binding: instance↔type and/or its UI group " +
            "(doc.ParameterBindings.ReInsert over the same categories; group-only changes are " +
            "the fix when a family parameter lands in the group of a stale project binding " +
            "instead of the requested one). Address it by name or shared GUID " +
            "(list_project_parameters shows both). Pass binding (instance/type), group (PG_* " +
            "or its name) or both; at least one is required. The dry run reports the blast " +
            "radius: current binding, target binding, categories and how many model elements " +
            "carry the parameter. Revit DROPS the parameter values on a rebind (both " +
            "directions, group changes included) — that loss is unavoidable and is stated in " +
            "the dry run and in the apply warnings; re-run the value migration right after. " +
            "A request that changes nothing is a no_changes skip. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""name"":{""type"":""string""},""guid"":{""type"":""string"",""description"":""Shared parameter GUID — the safe key when several same-name bindings exist""},""binding"":{""type"":""string"",""enum"":[""instance"",""type""],""description"":""The TARGET binding type; omit for a group-only change""},""group"":{""type"":""string"",""description"":""The TARGET parameter group (PG_DATA, PG_TEXT, PG_IDENTITY_DATA, a display name or its Russian name); omit to keep the current group""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[],""anyOf"":[{""required"":[""binding""]},{""required"":[""group""]}],""additionalProperties"":false}"
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
        var groupText = AgentJson.GetString(args, "group");
        if (string.IsNullOrWhiteSpace(bindingText) && string.IsNullOrWhiteSpace(groupText))
        {
            Fail(outcome, "invalid_arguments",
                "Pass binding (instance/type), group (PG_* or its name) or both — a rebind " +
                "with neither changes nothing.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(bindingText)
            && !string.Equals(bindingText, "instance", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(bindingText, "type", StringComparison.OrdinalIgnoreCase))
        {
            Fail(outcome, "invalid_arguments", "binding must be instance or type.");
            return;
        }

        ForgeTypeId? targetGroup = null;
        if (!string.IsNullOrWhiteSpace(groupText)
            && !SmartCon.Revit.Agent.Tools.Families.AgentFamilySpecs.TryResolveGroup(
                groupText, out targetGroup, out var groupMessage))
        {
            Fail(outcome, "invalid_arguments", groupMessage ?? "Unknown group '" + groupText + "'.");
            return;
        }

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

        // Group-only rebind: no binding argument keeps the current arity.
        var effectiveBinding = string.IsNullOrWhiteSpace(bindingText)
            ? row.BindingType!
            : bindingText!;
        var wantInstance = string.Equals(
            effectiveBinding, "instance", StringComparison.OrdinalIgnoreCase);

        var targetGroupName = targetGroup is null
            ? null
            : AgentFamilySpecs.GroupNameOf(targetGroup);
        var groupChanges = targetGroup is not null
                           && !string.Equals(row.ParameterGroup, targetGroupName,
                               StringComparison.OrdinalIgnoreCase);
        var bindingChanges = !string.IsNullOrWhiteSpace(bindingText)
                             && !string.Equals(row.BindingType, bindingText,
                                 StringComparison.OrdinalIgnoreCase);
        if (!bindingChanges && !groupChanges)
        {
            outcome.Batch.Skipped.Add(new WriteItemReport
            {
                Name = row.Name,
                Reason = "the parameter is already bound as " + row.BindingType +
                         (targetGroup is null ? string.Empty : " in the requested group")
            });
            outcome.Message = "No changes: '" + row.Name + "' is already a " + row.BindingType +
                              " project parameter" +
                              (targetGroup is null ? string.Empty : " in the requested group") +
                              " (see skipped[]).";
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
        outcome.Data["targetBinding"] = effectiveBinding;
        outcome.Data["targetGroup"] = targetGroupName;
        outcome.Data["groupOnly"] = !bindingChanges;
        outcome.Data["categories"] = row.Categories;
        outcome.Data["elementsInCategories"] = affected;
        outcome.Data["valuesWillBeLost"] = true;

        var dropped = bindingChanges && groupChanges
            ? "both the binding type (instance↔type) and the parameter group"
            : bindingChanges
                ? "the binding type (instance↔type)"
                : "the parameter group";
        outcome.Warnings.Add(
            "A rebind DROPS the parameter values: it changes " + dropped + ", so " +
            affected + " element(s) in " + row.Categories.Count + " categor(ies) lose the " +
            "current value of '" + row.Name + "'. Re-run the value migration right after the " +
            "rebind.");

        if (dryRun)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Name = row.Name,
                OldValue = DescribeTarget(row.BindingType!, row.ParameterGroup),
                NewValue = DescribeTarget(effectiveBinding, targetGroupName ?? row.ParameterGroup),
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
            // The 3-argument ReInsert carries the target group; without one the
            // two-argument overload keeps whatever group the binding had.
            applied = targetGroup is null
                ? map.ReInsert(mapDefinition, binding)
                : map.ReInsert(mapDefinition, binding, targetGroup);
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
        if (after is null
            || (bindingChanges
                && !string.Equals(after.BindingType, effectiveBinding, StringComparison.OrdinalIgnoreCase))
            || (groupChanges
                && !string.Equals(after.ParameterGroup, targetGroupName,
                    StringComparison.OrdinalIgnoreCase)))
        {
            // Report only what this call actually changed: a binding-only
            // rebind used to print "group '(null)' instead of ''" (validator
            // v16, P3).
            var expected = new List<string>();
            var actual = new List<string>();
            if (bindingChanges)
            {
                expected.Add("binding '" + effectiveBinding + "'");
                actual.Add("binding '" + (after?.BindingType ?? "(missing)") + "'");
            }

            if (groupChanges)
            {
                expected.Add("group '" + targetGroupName + "'");
                actual.Add("group '" + (after?.ParameterGroup ?? "(missing)") + "'");
            }

            Fail(outcome, "binding_failed",
                "The rebind was applied but the read-back shows " +
                string.Join(" / ", actual) + " instead of " + string.Join(" / ", expected) + ".",
                "[Action: verify with list_project_parameters]");
            return;
        }

        outcome.AffectedCount = 1;
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Name = row.Name,
            OldValue = DescribeTarget(row.BindingType!, row.ParameterGroup),
            NewValue = DescribeTarget(effectiveBinding, targetGroupName ?? row.ParameterGroup),
            Detail = affected + " element(s) affected; previous values were dropped by the rebind"
        });
        outcome.Data["verifiedBinding"] = after.BindingType;
        outcome.Data["verifiedGroup"] = after.ParameterGroup;
        outcome.Message = "Rebound '" + row.Name + "': " + row.BindingType +
                          (bindingChanges ? " -> " + effectiveBinding : string.Empty) +
                          (groupChanges
                              ? " group " + row.ParameterGroup + " -> " + targetGroupName
                              : string.Empty) +
                          " over " + row.Categories.Count + " categor(ies); " +
                          affected + " element(s) lost the previous value — re-run the value " +
                          "migration.";
    }

    private static string DescribeTarget(string binding, string? group) =>
        binding + " binding" + (string.IsNullOrWhiteSpace(group) ? string.Empty : ", " + group);

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
