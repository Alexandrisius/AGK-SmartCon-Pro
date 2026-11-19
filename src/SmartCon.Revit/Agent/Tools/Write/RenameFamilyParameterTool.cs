using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Renames a family parameter. Shared and built-in parameters cannot be renamed;
/// the conflict is reported instead of silently doing nothing.
/// </summary>
public sealed class RenameFamilyParameterTool : AgentFamilyWriteToolBase
{
    public RenameFamilyParameterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "rename_family_parameter",
        Description =
            "Renames a family parameter in every type of the family. Shared and built-in " +
            "parameters cannot be renamed (reported as an error). Duplicate names are " +
            "rejected. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""parameter"":{""type"":""string""},""scope"":{""type"":""string"",""enum"":[""instance"",""type""]},""guid"":{""type"":""string""},""newName"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""newName""],""additionalProperties"":false}"
    };

    private protected override void ApplyInFamilyDocument(
        AgentFamilySession session,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<RenameFamilyParameterRequest>(args) ??
                      new RenameFamilyParameterRequest();
        var manager = session.FamilyDoc.FamilyManager;

        if (string.IsNullOrWhiteSpace(request.NewName))
        {
            Fail(outcome, "invalid_value", "newName is required.");
            return;
        }

        var newName = request.NewName!.Trim();
        if (newName.Length > 255)
        {
            Fail(
                outcome,
                "invalid_value",
                "The new name is " + newName.Length + " characters long; parameter names " +
                "are limited to 255 characters.");
            return;
        }
        var parameter = AgentFamilyReader.FindParameter(
            manager,
            request.Parameter,
            request.Guid,
            request.Scope,
            out var errorCode,
            out var message);
        if (parameter is null)
        {
            Fail(outcome, errorCode ?? "parameter_not_found", message ?? "Parameter not found.");
            return;
        }

        var oldName = parameter.Definition.Name;
        if (string.Equals(oldName, newName, StringComparison.Ordinal))
        {
            outcome.Batch.Skipped.Add(new WriteItemReport
            {
                Name = oldName,
                Reason = "The parameter already has this name; nothing to rename."
            });
            outcome.Message = "No changes: the parameter is already named '" + newName +
                              "' (see skipped[]).";
            return;
        }

        if (parameter.IsShared)
        {
            Fail(
                outcome,
                "shared_parameter_not_renameable",
                "The parameter '" + oldName + "' is shared. Shared parameters cannot be " +
                "renamed in a family; the name comes from the shared parameter definition.",
                "Edit the shared parameter name in the shared parameter file or add a new " +
                "family parameter instead.");
            return;
        }

        if (parameter.Definition is InternalDefinition internalDefinition
            && internalDefinition.BuiltInParameter != BuiltInParameter.INVALID)
        {
            Fail(
                outcome,
                "builtin_parameter_not_renameable",
                "The parameter '" + oldName + "' is a built-in parameter and cannot be renamed.");
            return;
        }

        foreach (var other in manager.GetParameters())
        {
            if (ReferenceEquals(other, parameter))
                continue;

            if (string.Equals(other.Definition.Name, newName, StringComparison.OrdinalIgnoreCase))
            {
                Fail(
                    outcome,
                    "duplicate_name",
                    "Another parameter is already named '" + newName + "' (" +
                    (other.IsInstance ? "instance" : "type") + ").",
                    "Pick another name or rename the existing parameter first.");
                return;
            }
        }

        try
        {
            manager.RenameParameter(parameter, newName);
        }
        catch (Exception ex)
        {
            var notRenameable = AgentFamilySpecs.IsRevitInvalidOperation(ex)
                                || ex is System.InvalidOperationException;
            Fail(
                outcome,
                notRenameable ? "shared_parameter_not_renameable" : "invalid_value",
                "Revit refused to rename the parameter: " + ex.Message,
                notRenameable
                    ? "Shared and built-in parameters cannot be renamed."
                    : "Check the new name for forbidden characters.");
            return;
        }

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Name = newName,
            OldValue = oldName,
            NewValue = newName,
            Detail = (parameter.IsInstance ? "instance" : "type") + " parameter renamed"
        });
        session.AddUndoOperation(new FamilyUndoOperation
        {
            Kind = "rename",
            Parameter = oldName,
            NewName = newName,
            Scope = parameter.IsInstance ? "instance" : "type"
        });
        outcome.AffectedCount = 1;
        outcome.Data["parameter"] = new
        {
            oldName,
            newName,
            scope = parameter.IsInstance ? "instance" : "type"
        };

        if (!parameter.IsInstance)
        {
            var types = AgentFamilyReader.GetTypes(manager);
            for (var index = 0; index < types.Count && index < 20; index++)
                session.AddExistenceExpectation(types[index].Name, newName, isTypeParameter: true);
        }
    }

    private static void Fail(WriteOutcome outcome, string errorCode, string message, string? hint = null)
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