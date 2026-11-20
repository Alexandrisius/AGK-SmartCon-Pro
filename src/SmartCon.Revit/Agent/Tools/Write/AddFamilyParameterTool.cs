using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Adds a family parameter (normal or shared, instance or type) to every type of
/// the family. Shared parameters are created through the shared parameter file;
/// the GUID is preserved and returned.
/// </summary>
public sealed class AddFamilyParameterTool : AgentFamilyWriteToolBase
{
    private const int MaxExistenceChecks = 20;
    private const int MaxNameLength = 255;

    public AddFamilyParameterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "add_family_parameter",
        Description =
            "Adds a parameter to a family: normal or shared, instance or type, with a parameter " +
            "group and storage type. For a shared parameter the definition must already exist " +
            "in the active shared parameter file (list_shared_parameters shows what is there); " +
            "pass createDefinition=true to add a missing definition to that file. Checks " +
            "duplicate names, preserves the GUID and reports the new parameter. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""name"":{""type"":""string""},""storageType"":{""type"":""string"",""description"":""length | ductSize | pipeSize | distance | area | volume | angle | number | integer | text | yesno | material""},""group"":{""type"":""string"",""description"":""Revit group id, e.g. PG_GENERAL, PG_IDENTITY_DATA, PG_TEXT, PG_CONSTRAINTS, PG_DATA; localized Russian names are accepted""},""isInstance"":{""type"":""boolean"",""default"":false},""isShared"":{""type"":""boolean"",""default"":false},""createDefinition"":{""type"":""boolean"",""default"":false,""description"":""Create a missing shared definition in the active shared parameter file; false reports definition_not_found""},""guid"":{""type"":""string""},""initialValue"":{""type"":""string""},""unit"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""name"",""storageType""],""additionalProperties"":false}"
    };

    private protected override void ApplyInFamilyDocument(
        AgentFamilySession session,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<AddFamilyParameterRequest>(args) ??
                      new AddFamilyParameterRequest();
        var manager = session.FamilyDoc.FamilyManager;

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            Fail(outcome, "invalid_value", "name is required.");
            return;
        }

        var name = request.Name!.Trim();
        if (name.Length > MaxNameLength)
        {
            Fail(
                outcome,
                "invalid_value",
                "The parameter name is " + name.Length + " characters long; names are " +
                "limited to " + MaxNameLength + " characters.");
            return;
        }

        if (!AgentFamilySpecs.TryResolveSpec(
                request.StorageType,
                out var spec,
                out var kind,
                out var specMessage))
        {
            Fail(outcome, "invalid_value", specMessage ?? "Unknown storageType.");
            return;
        }

        if (!AgentFamilySpecs.TryResolveGroup(request.Group, out var group, out var groupMessage))
        {
            Fail(outcome, "invalid_value", groupMessage ?? "Unknown parameter group.");
            return;
        }

        foreach (var existing in manager.GetParameters())
        {
            if (string.Equals(existing.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                Fail(
                    outcome,
                    "parameter_exists",
                    "The family already has a parameter named '" + name + "' (" +
                    (existing.IsInstance ? "instance" : "type") + ").",
                    "Use rename_family_parameter to rename it or pick another name.");
                return;
            }
        }

        Guid? guid = null;
        if (!string.IsNullOrWhiteSpace(request.Guid))
        {
            if (!Guid.TryParse(request.Guid, out var parsedGuid))
            {
                Fail(outcome, "invalid_value", "'" + request.Guid + "' is not a valid GUID.");
                return;
            }

            if (!request.IsShared)
            {
                Fail(
                    outcome,
                    "invalid_value",
                    "guid is only valid for a shared parameter; set isShared=true or drop guid.");
                return;
            }

            guid = parsedGuid;
            var byGuid = manager.get_Parameter(parsedGuid);
            if (byGuid is not null)
            {
                Fail(
                    outcome,
                    "parameter_exists",
                    "A shared parameter with GUID " + request.Guid + " already exists in the " +
                    "family as '" + byGuid.Definition.Name + "'.");
                return;
            }
        }

        FamilyValueSnapshot? initialValue = null;
        if (!string.IsNullOrWhiteSpace(request.InitialValue))
        {
            if (kind == AgentFamilyValueKind.ElementId)
            {
                Fail(outcome, "invalid_value",
                    "initialValue is not supported for material parameters.");
                return;
            }

            if (manager.CurrentType is null)
            {
                Fail(
                    outcome,
                    "empty_family",
                    "The family has no types, so an initial value cannot be set. Add the " +
                    "parameter without initialValue, or create a type first.");
                return;
            }

            if (!AgentFamilyUnits.TryConvert(
                    kind,
                    request.InitialValue,
                    request.Unit,
                    out var converted,
                    out var valueErrorCode,
                    out var valueMessage))
            {
                Fail(outcome, valueErrorCode ?? "invalid_value", valueMessage ?? "Invalid value.");
                return;
            }

            initialValue = AgentFamilyValues.FromConverted(converted);
        }

        ExternalDefinition? sharedDefinition = null;
        var wouldCreateDefinition = false;
        if (request.IsShared)
        {
            sharedDefinition = ResolveSharedDefinition(
                session,
                name,
                spec!,
                guid,
                request.CreateDefinition,
                dryRun,
                outcome,
                out wouldCreateDefinition);
            if (sharedDefinition is null && !wouldCreateDefinition)
                return;

            if (wouldCreateDefinition)
            {
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Name = name,
                    NewValue = "shared definition would be created in the shared parameter file",
                    Detail = dryRun ? "dry run: no shared parameter file change" : null
                });
                outcome.AffectedCount = 1;
                outcome.Data["parameter"] = new
                {
                    name,
                    scope = request.IsInstance ? "instance" : "type",
                    isShared = true,
                    guid = guid?.ToString("D"),
                    storageType = kind switch
                    {
                        AgentFamilyValueKind.Length or AgentFamilyValueKind.Angle
                            or AgentFamilyValueKind.Area or AgentFamilyValueKind.Volume
                            or AgentFamilyValueKind.Number => "double",
                        AgentFamilyValueKind.WholeNumber or AgentFamilyValueKind.YesNo => "integer",
                        AgentFamilyValueKind.Text => "string",
                        AgentFamilyValueKind.ElementId => "elementId",
                        _ => "unknown"
                    },
                    spec = AgentFamilySpecs.SpecNameOf(spec!),
                    group = AgentFamilySpecs.GroupNameOf(group!),
                    wouldCreateDefinition = true
                };
                outcome.Warnings.Add(
                    "Dry run: the shared parameter definition does not exist yet; a real " +
                    "apply would create it in the shared parameter file (an external file " +
                    "that cannot be rolled back).");
                return;
            }
        }

        FamilyParameter? created;
        try
        {
            created = request.IsShared
                ? manager.AddParameter(sharedDefinition!, group!, request.IsInstance)
                : manager.AddParameter(name, group!, spec!, request.IsInstance);
        }
        catch (Exception ex)
        {
            Fail(
                outcome,
                "invalid_value",
                "Revit refused to add the parameter: " + ex.Message,
                "Check the parameter name, group and storage type.");
            return;
        }

        if (created is null)
        {
            Fail(outcome, "invalid_value", "Revit did not create the parameter.");
            return;
        }

        if (initialValue.HasValue)
        {
            try
            {
                AgentFamilyValues.Set(manager, created, initialValue.Value);
            }
            catch (Exception ex)
            {
                Fail(
                    outcome,
                    "invalid_value",
                    "The parameter was created, but the initial value could not be set: " +
                    ex.Message);
                return;
            }
        }

        var sharedText = request.IsShared
            ? "shared " + (created.IsShared ? created.GUID.ToString("D") : "?")
            : "family";
        var scopeText = created.IsInstance ? "instance" : "type";
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Name = created.Definition.Name,
            OldValue = null,
            NewValue = scopeText + " " + AgentFamilySpecs.StorageTypeName(created.StorageType) +
                       " (" + sharedText + ")",
            Detail = request.Group is null ? null : "group " + AgentFamilySpecs.GroupName(created.Definition)
        });
        session.AddUndoOperation(new FamilyUndoOperation
        {
            Kind = "add",
            Parameter = created.Definition.Name,
            Scope = scopeText
        });
        outcome.AffectedCount = 1;
        outcome.Data["parameter"] = new
        {
            name = created.Definition.Name,
            scope = scopeText,
            isShared = created.IsShared,
            guid = created.IsShared ? created.GUID.ToString("D") : null,
            storageType = AgentFamilySpecs.StorageTypeName(created.StorageType),
            spec = AgentFamilySpecs.SpecName(created.Definition),
            group = AgentFamilySpecs.GroupName(created.Definition)
        };

        var types = AgentFamilyReader.GetTypes(manager);
        for (var index = 0; index < types.Count && index < MaxExistenceChecks; index++)
        {
            session.AddExistenceExpectation(types[index].Name, created.Definition.Name, !created.IsInstance);
        }

        if (types.Count == 0)
        {
            outcome.Warnings.Add(
                "The family has no types, so the new parameter will only appear after a type " +
                "is created.");
        }
    }

    private static ExternalDefinition? ResolveSharedDefinition(
        AgentFamilySession session,
        string name,
        ForgeTypeId spec,
        Guid? guid,
        bool createDefinition,
        bool dryRun,
        WriteOutcome outcome,
        out bool wouldCreateDefinition)
    {
        wouldCreateDefinition = false;
        DefinitionFile? file;
        try
        {
            file = session.ProjectDoc.Application.OpenSharedParameterFile();
        }
        catch (Exception ex)
        {
            Fail(
                outcome,
                "shared_parameter_file_missing",
                "The shared parameter file could not be opened: " + ex.Message,
                "[Action: configure a shared parameter file in Manage > Shared Parameters " +
                "and retry]");
            return null;
        }

        if (file is null)
        {
            Fail(
                outcome,
                "shared_parameter_file_missing",
                "No shared parameter file is configured, so a shared parameter cannot be " +
                "created.",
                "[Action: configure a shared parameter file in Manage > Shared Parameters " +
                "and retry]");
            return null;
        }

        ExternalDefinition? existing = null;
        try
        {
            foreach (DefinitionGroup candidate in file.Groups)
            {
                foreach (Definition definition in candidate.Definitions)
                {
                    if (definition is not ExternalDefinition external)
                        continue;

                    var sameGuid = guid.HasValue && external.GUID == guid.Value;
                    var sameName = string.Equals(
                        external.Name,
                        name,
                        StringComparison.OrdinalIgnoreCase);
                    if (!sameGuid && !sameName)
                        continue;

                    existing = external;
                    break;
                }

                if (existing is not null)
                    break;
            }
        }
        catch (Exception ex)
        {
            Fail(
                outcome,
                "shared_parameter_file_missing",
                "The shared parameter file could not be read: " + ex.Message,
                "[Action: check the shared parameter file and retry]");
            return null;
        }

        if (existing is not null)
        {
            if (!existing.GetDataType().Equals(spec))
            {
                Fail(
                    outcome,
                    "invalid_value",
                    "The shared parameter definition '" + existing.Name + "' already exists " +
                    "with a different data type.",
                    "Use another name or fix the definition in the shared parameter file.");
                return null;
            }

            if (guid.HasValue && existing.GUID != guid.Value)
            {
                Fail(
                    outcome,
                    "invalid_value",
                    "The shared parameter definition '" + existing.Name + "' already exists " +
                    "with GUID " + existing.GUID.ToString("D") + ", not " +
                    guid.Value.ToString("D") + ".",
                    "Drop guid to reuse the existing definition, or use another name.");
                return null;
            }

            return existing;
        }

        if (!createDefinition)
        {
            // Default: never write into the active shared parameter file. A typo in a
            // customer's ФОП cannot be rolled back (external file), so a missing
            // definition is reported with close matches instead.
            var suggestions = SuggestDefinitions(file, name);
            Fail(
                outcome,
                "definition_not_found",
                "The active shared parameter file has no definition named '" + name + "'" +
                (guid.HasValue ? " with GUID " + guid.Value.ToString("D") : string.Empty) + ".",
                "Existing definitions are listed by list_shared_parameters" +
                (suggestions.Count == 0
                    ? "."
                    : "; closest: " + string.Join(", ", suggestions) + ".") +
                " Pass createDefinition=true to add the definition to " + file.Filename +
                " (this changes an external file and cannot be undone by undo_last).");
            return null;
        }

        if (dryRun)
        {
            wouldCreateDefinition = true;
            return null;
        }

        DefinitionGroup? group;
        try
        {
            group = file.Groups.get_Item("SmartCon") ?? file.Groups.Create("SmartCon");
        }
        catch (Exception ex)
        {
            Fail(
                outcome,
                "shared_parameter_file_missing",
                "The shared parameter group could not be created: " + ex.Message,
                "[Action: check that the shared parameter file is writable]");
            return null;
        }

        if (group is null)
        {
            Fail(outcome, "shared_parameter_file_missing",
                "The shared parameter group 'SmartCon' could not be created.");
            return null;
        }

        try
        {
            var options = new ExternalDefinitionCreationOptions(name, spec);
            if (guid.HasValue)
                options.GUID = guid.Value;

            var created = group.Definitions.Create(options) as ExternalDefinition;
            if (created is null)
            {
                Fail(
                    outcome,
                    "shared_parameter_file_missing",
                    "Revit did not create the shared parameter definition.",
                    "[Action: check the shared parameter file and retry]");
            }
            else
            {
                // The external file is not part of the family transaction: state the
                // side effect up front so a rolled-back family never hides it.
                outcome.Data["sharedDefinitionCreatedInFile"] = true;
                outcome.Warnings.Add(
                    "The shared parameter definition '" + name + "' was written to the " +
                    "shared parameter file (" + System.IO.Path.GetFileName(file.Filename) +
                    ") before the family transaction committed; undo_last cannot revert " +
                    "this external file change.");
            }

            return created;
        }
        catch (Exception ex)
        {
            Fail(
                outcome,
                "shared_parameter_file_missing",
                "The shared parameter definition could not be created: " + ex.Message,
                "[Action: check that the shared parameter file is writable and has no " +
                "duplicate names]");
            return null;
        }
    }

    private static List<string> SuggestDefinitions(DefinitionFile file, string name)
    {
        var candidates = new List<string>();
        try
        {
            foreach (DefinitionGroup group in file.Groups)
            {
                foreach (Definition definition in group.Definitions)
                {
                    if (definition is ExternalDefinition external
                        && !string.IsNullOrWhiteSpace(external.Name))
                    {
                        candidates.Add(external.Name);
                    }
                }
            }
        }
        catch (Exception)
        {
            return new List<string>();
        }

        var normalized = name.Trim().ToLowerInvariant();
        var threshold = System.Math.Max(3, normalized.Length / 2);
        return candidates
            .Select(candidate => (
                Name: candidate,
                Distance: Distance(normalized, candidate.ToLowerInvariant())))
            .Where(pair => pair.Distance <= threshold)
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Name, StringComparer.Ordinal)
            .Take(5)
            .Select(pair => pair.Name)
            .ToList();
    }

    private static int Distance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
            previous[index] = index;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = System.Math.Min(
                    System.Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[right.Length];
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