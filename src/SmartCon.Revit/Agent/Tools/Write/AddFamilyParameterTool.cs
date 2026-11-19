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
            "group and storage type. Checks duplicate names, creates the shared definition when " +
            "needed (GUID preserved) and reports the new parameter. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""},""familyName"":{""type"":""string""},""name"":{""type"":""string""},""storageType"":{""type"":""string""},""group"":{""type"":""string""},""isInstance"":{""type"":""boolean"",""default"":false},""isShared"":{""type"":""boolean"",""default"":false},""guid"":{""type"":""string""},""initialValue"":{""type"":""string""},""unit"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""name"",""storageType""],""additionalProperties"":false}"
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
        if (request.IsShared)
        {
            sharedDefinition = ResolveSharedDefinition(
                session,
                name,
                spec!,
                guid,
                outcome);
            if (sharedDefinition is null)
                return;
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
        WriteOutcome outcome)
    {
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

        foreach (Definition definition in group.Definitions)
        {
            if (definition is not ExternalDefinition external)
                continue;

            var sameGuid = guid.HasValue && external.GUID == guid.Value;
            var sameName = string.Equals(external.Name, name, StringComparison.OrdinalIgnoreCase);
            if (!sameGuid && !sameName)
                continue;

            if (!external.GetDataType().Equals(spec))
            {
                Fail(
                    outcome,
                    "invalid_value",
                    "The shared parameter definition '" + external.Name + "' already exists " +
                    "with a different data type.",
                    "Use another name or fix the definition in the shared parameter file.");
                return null;
            }

            return external;
        }

        try
        {
            var options = new ExternalDefinitionCreationOptions(name, spec);
            if (guid.HasValue)
                options.GUID = guid.Value;

            return group.Definitions.Create(options) as ExternalDefinition;
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