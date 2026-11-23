using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Project;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Binds a shared parameter definition from the active shared parameter file
/// (ФОП) to project categories as a project parameter. The definition is only
/// created in the file with an explicit createDefinition=true; an existing
/// binding is extended with the missing categories (Insert → ReInsert quirk).
/// </summary>
public sealed class AddProjectSharedParameterTool : AgentWriteToolBase
{
    private const int MaxCategories = 20;
    private const string ChangeItemKind = "project_parameter_binding";

    public AddProjectSharedParameterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "add_project_shared_parameter",
        Description =
            "Binds a shared parameter from the active shared parameter file (ФОП) to project " +
            "categories as an instance or type project parameter. name + spec + categories are " +
            "required (categories accept OST_ names, localized names, 1..20). The definition " +
            "must already exist in the file (list_shared_parameters); createDefinition=true " +
            "(explicit, default false) creates it in the file group first — that file change " +
            "cannot be reverted by undo_last. An existing binding of the same definition is " +
            "extended with the missing categories; a binding type mismatch fails with " +
            "binding_type_conflict. Read-back with list_project_parameters confirms the result. " +
            "Not idempotent (already-bound categories are reported as no_changes); dry run " +
            "defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""name"":{""type"":""string"",""description"":""Parameter name, e.g. Этаж_Монтажа""},""spec"":{""type"":""string"",""description"":""Data type: length, ductSize, pipeSize, distance, number, text, yesno, integer, angle, area, volume or material""},""categories"":{""type"":""array"",""minItems"":1,""maxItems"":20,""items"":{""type"":""string""},""description"":""OST_Walls, localized names or category ids from list_categories""},""binding"":{""type"":""string"",""enum"":[""instance"",""type""],""default"":""instance""},""group"":{""type"":""string"",""default"":""SmartCon"",""description"":""Shared parameter file group for createDefinition""},""createDefinition"":{""type"":""boolean"",""default"":false,""description"":""Create the definition in the file group when it is missing (changes the external file)""},""visible"":{""type"":""boolean"",""default"":true},""userModifiable"":{""type"":""boolean"",""default"":true},""description"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""name"",""spec"",""categories""],""additionalProperties"":false}"
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
                "Project parameters are bound in a project document; the active document is " +
                "a family.");
            return;
        }

        var name = AgentJson.GetString(args, "name")?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            Fail(outcome, "invalid_arguments", "Pass name (the parameter name).");
            return;
        }

        // C5-01: a project parameter named like a built-in (e.g. the localized
        // 'Код по классификатору' = UNIFORMAT_CODE/ASSEMBLY_CODE) binds happily,
        // but every write meant for the built-in parameter keeps missing it and
        // the read-back of the new parameter stays empty.
        var collision = AgentParameterResolver.BuiltInNameCollision(name!);
        if (collision is not null)
        {
            Fail(
                outcome,
                "parameter_name_taken",
                "The name '" + name + "' is the display name of the built-in parameter " +
                collision.Value + ": a project parameter with this name shadows nothing, and " +
                "writes meant for the built-in parameter keep going to the built-in one.",
                "Write the built-in parameter instead — set_parameters accepts the name '" +
                name + "' or the enum id '" + collision.Value +
                "'. If a separate parameter is really needed, use a different name.");
            return;
        }

        var specArg = AgentJson.GetString(args, "spec");
        if (!AgentFamilySpecs.TryResolveSpec(specArg, out var specTypeId, out _, out var specMessage))
        {
            Fail(outcome, "invalid_spec",
                specMessage ?? "Unknown spec '" + specArg + "'.",
                "Use list_shared_parameters to see the data types of existing definitions.");
            return;
        }

        var bindingArg = AgentJson.GetString(args, "binding") ?? "instance";
        if (bindingArg is not ("instance" or "type"))
        {
            Fail(outcome, "invalid_arguments",
                "binding must be 'instance' or 'type', not '" + bindingArg + "'.");
            return;
        }

        var categoryNames = AgentJson.GetStringArray(args, "categories");
        if (categoryNames.Count == 0)
        {
            Fail(outcome, "invalid_arguments", "Pass categories (1.." + MaxCategories + ").");
            return;
        }

        if (categoryNames.Count > MaxCategories)
        {
            Fail(outcome, "invalid_arguments",
                "At most " + MaxCategories + " categories can be passed per call.");
            return;
        }

        var group = AgentJson.GetString(args, "group");
        if (string.IsNullOrWhiteSpace(group))
            group = "SmartCon";

        var createDefinition = AgentJson.GetBool(args, "createDefinition", false);
        var visible = AgentJson.GetBool(args, "visible", true);
        var userModifiable = AgentJson.GetBool(args, "userModifiable", true);
        var description = AgentJson.GetString(args, "description");

        outcome.Description = "Bind project parameter " + name;

        var categories = ResolveCategories(doc, categoryNames, outcome);
        if (categories.Count == 0)
            return;

        DefinitionFile? file;
        try
        {
            file = doc.Application.OpenSharedParameterFile();
        }
        catch (Exception ex)
        {
            Fail(outcome, "shared_parameter_file_missing",
                "The active shared parameter file could not be opened: " + ex.Message,
                "[Action: call set_shared_parameter_file(path=<absolute path of the .txt file>, " +
                "dryRun=true) to attach an existing file - createIfMissing=true creates a new " +
                "empty file - then retry; in the Revit UI: Manage > Shared Parameters]");
            return;
        }

        if (file is null)
        {
            Fail(outcome, "shared_parameter_file_missing",
                "No active shared parameter file is configured.",
                "[Action: call set_shared_parameter_file(path=<absolute .txt path>, " +
                "createIfMissing=true, dryRun=true) to create a new shared parameter file or " +
                "attach the existing one, then retry; in the Revit UI: Manage > Shared " +
                "Parameters > Create/Browse]");
            return;
        }

        var definition = FindFileDefinition(file, name!, out var fileGroup);
        var definitionCreated = false;
        if (definition is null && !createDefinition)
        {
            Fail(
                outcome,
                "definition_not_found",
                "The active shared parameter file has no definition named '" + name + "'.",
                "Add createDefinition=true to create it in group '" + group + "' of " +
                file.Filename + " (this changes an external file and cannot be undone by " +
                "undo_last); list_shared_parameters shows the existing definitions.");
            return;
        }

        if (definition is not null && !SameSpec(definition, specTypeId!))
        {
            Fail(outcome, "invalid_value",
                "The definition '" + definition.Name + "' already exists in the shared " +
                "parameter file with a different data type.",
                "Use another name or fix the definition in the shared parameter file.");
            return;
        }

        if (definition is null && dryRun)
        {
            outcome.Data["wouldCreateDefinition"] = true;
            outcome.Data["fileGroup"] = group;
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Name = name,
                NewValue = bindingArg + " binding for " + CategoryNames(categories),
                Detail = "Would create the definition in group '" + group + "' of the shared " +
                         "parameter file and bind it (dry run keeps the file untouched)."
            });
            return;
        }

        if (definition is null)
        {
            definition = CreateFileDefinition(
                file, group!, name!, specTypeId!, visible, userModifiable, description, outcome);
            if (definition is null)
                return;

            definitionCreated = true;
            fileGroup = group;
            outcome.Warnings.Add(
                "The shared parameter definition '" + name + "' was written to the shared " +
                "parameter file (" + System.IO.Path.GetFileName(file.Filename) +
                "); undo_last cannot revert this external file change.");
        }

        var guid = definition.GUID;
        var matches = AgentProjectParameterReader.FindAll(doc, name!);
        var current = matches.FirstOrDefault(row => row.Guid == guid);

        if (current is null && matches.Count > 0)
        {
            var other = matches[0];
            Fail(outcome, "name_conflict",
                "A project parameter named '" + name + "' is already bound to a different " +
                "definition" + (other.Guid.HasValue
                    ? " (GUID " + other.Guid.Value.ToString("D") + ")"
                    : " (a non-shared project parameter)") + ".",
                "Use another name or remove that binding first (list_project_parameters " +
                "shows it).");
            return;
        }

        if (current is not null && !string.Equals(current.BindingType, bindingArg, StringComparison.Ordinal))
        {
            Fail(outcome, "binding_type_conflict",
                "'" + name + "' is already bound as a " + current.BindingType +
                " parameter; the request asks for " + bindingArg + ".",
                "A definition cannot be bound both to instances and types; pass binding='" +
                current.BindingType + "' or remove the binding first.");
            return;
        }

        var oldSnapshot = current is null
            ? null
            : AgentProjectParameterReader.SnapshotOf(current);

        if (current is not null)
        {
            var missing = categories
                .Where(category => !current.CategoryIds.Contains(category.Id.GetValue()))
                .ToList();
            if (missing.Count == 0)
            {
                outcome.Batch.Skipped.Add(new WriteItemReport
                {
                    Name = name,
                    Reason = "Already bound as a " + current.BindingType + " parameter for " +
                             CategoryNames(categories) + "."
                });
                outcome.ErrorCode = "no_changes";
                outcome.Data["definitionGroup"] = fileGroup;
                outcome.Data["guid"] = guid.ToString("D");
                return;
            }
        }

        var app = doc.Application;
        var categorySet = app.Create.NewCategorySet();
        foreach (var category in categories)
            categorySet.Insert(category);

        if (current is not null)
        {
            foreach (var categoryId in current.CategoryIds)
            {
                var existing = Category.GetCategory(doc, ElementIdCompat.Create(categoryId));
                if (existing is not null)
                    categorySet.Insert(existing);
            }
        }

        var binding = bindingArg == "instance"
            ? (Binding)app.Create.NewInstanceBinding(categorySet)
            : app.Create.NewTypeBinding(categorySet);

        bool applied;
        try
        {
            var map = doc.ParameterBindings;
            applied = map.Insert(definition, binding) || map.ReInsert(definition, binding);
        }
        catch (Exception ex)
        {
            Fail(outcome, "binding_failed",
                "Revit refused the binding of '" + name + "': " + ex.GetType().Name + ": " +
                ex.Message,
                "[Action: check that the categories accept project parameters and that no " +
                "name conflict exists]");
            return;
        }

        if (!applied)
        {
            Fail(outcome, "binding_failed",
                "Revit refused the binding of '" + name + "' (Insert and ReInsert returned " +
                "false).",
                "[Action: check list_project_parameters for an existing binding of this name]");
            return;
        }

        var after = AgentProjectParameterReader.FindAll(doc, name!)
            .FirstOrDefault(row => row.Guid == guid);
        if (after is null)
        {
            Fail(outcome, "binding_failed",
                "The binding was applied but the read-back could not find '" + name + "'.",
                "[Action: verify with list_project_parameters]");
            return;
        }

        var newSnapshot = AgentProjectParameterReader.SnapshotOf(after);
        var summary = after.BindingType + " binding for " +
                      string.Join(", ", after.Categories);
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = after.DefinitionId,
            Name = name,
            OldValue = oldSnapshot is null
                ? null
                : oldSnapshot.BindingType + " binding for " + string.Join(", ", oldSnapshot.Categories),
            NewValue = summary
        });
        outcome.AffectedCount++;
        outcome.Items.Add(new ChangeItem
        {
            ElementId = after.DefinitionId,
            ParameterName = ChangeItemKind,
            OldValue = oldSnapshot is null
                ? null
                : AgentProjectParameterReader.SerializeSnapshot(oldSnapshot),
            NewValue = AgentProjectParameterReader.SerializeSnapshot(newSnapshot),
            Status = ChangeStatus.Applied
        });

        outcome.Data["parameter"] = new
        {
            name = after.Name,
            definitionGroup = fileGroup,
            guid = guid.ToString("D"),
            spec = AgentFamilySpecs.SpecName(definition),
            bindingType = after.BindingType,
            categories = after.Categories,
            definitionCreated
        };
        if (definitionCreated)
        {
            outcome.Data["definitionCreated"] = true;
            outcome.Data["note"] = "the definition stays in the shared parameter file";
        }

        outcome.Data["readBack"] = new
        {
            found = true,
            bindingType = after.BindingType,
            categories = after.Categories,
            visible = after.Visible
        };
    }

    private static List<Category> ResolveCategories(
        Document doc,
        IReadOnlyList<string> names,
        WriteOutcome outcome)
    {
        var unknown = new List<string>();
        var ids = AgentCategoryResolver.Resolve(doc, names, unknown);
        foreach (var missed in unknown)
        {
            outcome.Batch.Failed.Add(new WriteItemReport
            {
                Name = missed,
                ErrorCode = "category_not_found",
                Reason = "No category '" + missed + "' in this document" +
                         AgentCategoryResolver.SuggestSuffix(doc, missed)
            });
        }

        var categories = new List<Category>();
        foreach (var id in ids)
        {
            Category? category;
            try
            {
                category = Category.GetCategory(doc, id);
            }
            catch (Exception)
            {
                category = null;
            }

            if (category is null)
                continue;

            if (!category.AllowsBoundParameters)
            {
                outcome.Batch.Failed.Add(new WriteItemReport
                {
                    Name = category.Name,
                    ErrorCode = "category_not_supported",
                    Reason = "The category '" + category.Name +
                             "' does not accept project parameters."
                });
                continue;
            }

            categories.Add(category);
        }

        return categories;
    }

    private static ExternalDefinition? FindFileDefinition(
        DefinitionFile file,
        string name,
        out string? groupName)
    {
        groupName = null;
        foreach (DefinitionGroup group in file.Groups)
        {
            foreach (Definition candidate in group.Definitions)
            {
                if (candidate is ExternalDefinition external
                    && string.Equals(external.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    groupName = group.Name;
                    return external;
                }
            }
        }

        return null;
    }

    private static ExternalDefinition? CreateFileDefinition(
        DefinitionFile file,
        string group,
        string name,
        ForgeTypeId spec,
        bool visible,
        bool userModifiable,
        string? description,
        WriteOutcome outcome)
    {
        try
        {
            var definitionGroup = file.Groups.get_Item(group) ?? file.Groups.Create(group);
            if (definitionGroup is null)
            {
                Fail(outcome, "shared_parameter_file_missing",
                    "The shared parameter group '" + group + "' could not be created.",
                    "[Action: check that the shared parameter file is writable]");
                return null;
            }

            var options = new ExternalDefinitionCreationOptions(name, spec)
            {
                Visible = visible,
                UserModifiable = userModifiable
            };
            if (!string.IsNullOrWhiteSpace(description))
                options.Description = description!;

            return definitionGroup.Definitions.Create(options) as ExternalDefinition;
        }
        catch (Exception ex)
        {
            Fail(outcome, "definition_create_failed",
                "The definition '" + name + "' could not be created in group '" + group +
                "': " + ex.Message,
                "[Action: check that the shared parameter file is writable and has no " +
                "duplicate names]");
            return null;
        }
    }

    private static bool SameSpec(ExternalDefinition definition, ForgeTypeId wanted)
    {
        try
        {
            var dataType = definition.GetDataType();
            if (dataType.Equals(wanted))
                return true;

            var key = AgentFamilySpecs.SpecKey(dataType);
            return key.Length > 0
                   && string.Equals(key, AgentFamilySpecs.SpecKey(wanted), StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string CategoryNames(IEnumerable<Category> categories) =>
        string.Join(", ", categories.Select(category => category.Name));

    private static void Fail(
        WriteOutcome outcome,
        string errorCode,
        string message,
        string? hint = null)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = string.IsNullOrEmpty(hint) ? message : message + " " + hint;
    }
}
