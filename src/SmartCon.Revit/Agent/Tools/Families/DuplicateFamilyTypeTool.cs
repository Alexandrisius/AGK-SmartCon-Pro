using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Duplicates a family type (FamilySymbol) inside its family in the project
/// document. The copy is independent: later per-type parameter writes on the
/// copy do not touch the source type.
/// </summary>
public sealed class DuplicateFamilyTypeTool : AgentWriteToolBase
{
    public DuplicateFamilyTypeTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "duplicate_family_type",
        Description =
            "Duplicates one family type (типоразмер) in the project: the copy is an independent " +
            "type of the same family and starts with the parameter values of the source. Pass " +
            "familyTypeId, or familyName + typeName. Guards: type_not_copyable, type_name_taken " +
            "(the name already exists in the family), type_name_invalid (forbidden characters " +
            "{ } [ ] | ; < > ? ~ : \\). undo_last deletes the created type. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyTypeId"":{""type"":""integer"",""description"":""Source type id (list_family_types)""},""familyName"":{""type"":""string""},""typeName"":{""type"":""string"",""description"":""Source type name inside the family""},""newName"":{""type"":""string"",""description"":""Name of the created type""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""newName""],""additionalProperties"":false}"
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
            outcome.Message = "Pass newName (the name of the new type).";
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

        var source = AgentFamilyTypeResolver.ResolveType(
            doc,
            TryGetLong(args, "familyTypeId"),
            AgentJson.GetString(args, "familyName"),
            AgentJson.GetString(args, "typeName"),
            out var errorCode,
            out var message);
        if (source is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = errorCode;
            outcome.Message = message!;
            return;
        }

        if (!source.CanBeCopied)
        {
            outcome.Success = false;
            outcome.ErrorCode = "type_not_copyable";
            outcome.Message = "The type '" + source.Name + "' of family '" +
                              source.FamilyName + "' cannot be duplicated.";
            return;
        }

        if (AgentFamilyTypeResolver.FindTypeByName(doc, source.Family, newName!) is not null)
        {
            outcome.Success = false;
            outcome.ErrorCode = "type_name_taken";
            outcome.Message = "Family '" + source.FamilyName + "' already has a type named '" +
                              newName + "'. Pass a different newName.";
            return;
        }

        outcome.Description = "Duplicate family type " + source.Name;
        outcome.Data["familyName"] = source.FamilyName;
        outcome.Data["sourceTypeId"] = source.Id.GetValue();

        if (dryRun)
        {
            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Id = source.Id.GetValue(),
                Name = source.FamilyName + " : " + source.Name,
                NewValue = newName,
                Reason = "Dry run: the type would be duplicated as '" + newName + "'."
            });
            outcome.AffectedCount = 1;
            return;
        }

        ElementType? created;
        try
        {
            created = source.Duplicate(newName!);
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
            outcome.ErrorCode = "type_not_copyable";
            outcome.Message = "Revit refused to duplicate the type '" + source.Name + "': " +
                              ex.GetType().Name + ": " + ex.Message;
            return;
        }

        var readBack = doc.GetElement(created.Id) as ElementType;
        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Id = created.Id.GetValue(),
            Name = source.FamilyName + " : " + readBack?.Name,
            OldValue = source.Name,
            NewValue = readBack?.Name
        });
        outcome.AffectedCount = 1;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = readBack?.UniqueId,
            ElementId = created.Id.GetValue(),
            ParameterName = "family_type_created",
            OldValue = source.FamilyName + " : " + source.Name,
            NewValue = readBack?.Name,
            Status = ChangeStatus.Applied
        });
        outcome.Data["typeId"] = created.Id.GetValue();
        outcome.Data["typeName"] = readBack?.Name;
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
