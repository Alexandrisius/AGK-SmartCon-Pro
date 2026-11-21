using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Renames a family type (FamilySymbol) in the project. Renaming the type
/// does not touch its placed instances: they keep their geometry and only
/// point to the renamed type.
/// </summary>
public sealed class RenameFamilyTypeTool : AgentWriteToolBase
{
    public RenameFamilyTypeTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "rename_family_type",
        Description =
            "Renames one family type (типоразмер) of a loaded family in the project. Pass " +
            "familyTypeId, or familyName + typeName. Guards: type_not_renameable, " +
            "type_name_taken, type_name_invalid (forbidden characters " +
            "{ } [ ] | ; < > ? ~ : \\). Renaming does not modify placed instances; " +
            "undo_last restores the previous name. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyTypeId"":{""type"":""integer""},""familyName"":{""type"":""string""},""typeName"":{""type"":""string"",""description"":""Current type name inside the family""},""newName"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""newName""],""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var newName = AgentJson.GetString(args, "newName")?.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass newName (the new type name).";
            return;
        }

        if (AgentFamilyTypeResolver.IsNameForbidden(newName!, out var forbidden))
        {
            outcome.Success = false;
            outcome.ErrorCode = "type_name_invalid";
            outcome.Message = "The type name '" + newName + "' contains the forbidden character '" +
                              forbidden + "'. Forbidden: { } [ ] | ; < > ? ~ : \\";
            return;
        }

        var type = AgentFamilyTypeResolver.ResolveType(
            doc,
            TryGetLong(args, "familyTypeId"),
            AgentJson.GetString(args, "familyName"),
            AgentJson.GetString(args, "typeName"),
            out var errorCode,
            out var message);
        if (type is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = errorCode;
            outcome.Message = message!;
            return;
        }

        if (!type.CanBeRenamed)
        {
            outcome.Success = false;
            outcome.ErrorCode = "type_not_renameable";
            outcome.Message = "The type '" + type.Name + "' of family '" + type.FamilyName +
                              "' cannot be renamed.";
            return;
        }

        if (string.Equals(type.Name, newName, StringComparison.Ordinal))
        {
            outcome.Batch.Skipped.Add(new WriteItemReport
            {
                Id = type.Id.GetValue(),
                Name = type.FamilyName + " : " + type.Name,
                Reason = "The type already has this name (no-op)."
            });
            return;
        }

        if (AgentFamilyTypeResolver.FindTypeByName(doc, type.Family, newName!) is not null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "type_name_taken";
            outcome.Message = "Family '" + type.FamilyName + "' already has a type named '" +
                              newName + "'. Pass a different newName.";
            return;
        }

        outcome.Description = "Rename family type " + type.Name;
        outcome.Data["familyName"] = type.FamilyName;

        if (dryRun)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Id = type.Id.GetValue(),
                Name = type.FamilyName + " : " + type.Name,
                OldValue = type.Name,
                NewValue = newName
            });
            outcome.AffectedCount = 1;
            return;
        }

        var oldName = type.Name;
        try
        {
            type.Name = newName!;
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "type_name_invalid";
            outcome.Message = "Revit refused the type name '" + newName + "': " + ex.Message;
            return;
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode = "type_not_renameable";
            outcome.Message = "Revit refused to rename the type '" + oldName + "': " +
                              ex.GetType().Name + ": " + ex.Message;
            return;
        }

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = type.Id.GetValue(),
            Name = type.FamilyName + " : " + type.Name,
            OldValue = oldName,
            NewValue = type.Name
        });
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = type.UniqueId,
            ElementId = type.Id.GetValue(),
            ParameterName = "family_type_name",
            OldValue = oldName,
            NewValue = type.Name,
            Status = ChangeStatus.Applied
        });
        outcome.Data["typeId"] = type.Id.GetValue();
        outcome.Data["typeName"] = type.Name;
    }

    private static long? TryGetLong(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result))
        {
            return null;
        }

        return result;
    }
}
