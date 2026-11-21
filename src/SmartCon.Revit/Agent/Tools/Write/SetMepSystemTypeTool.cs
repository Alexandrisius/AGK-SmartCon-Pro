using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Changes the properties of an MEP system type: the abbreviation and the name.
/// These live on MEPSystemType as properties, not parameters, so
/// set_parameters cannot write them. Undo restores the previous values.
/// </summary>
public sealed class SetMepSystemTypeTool : AgentWriteToolBase
{
    public SetMepSystemTypeTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_mep_system_type",
        Description =
            "Changes the abbreviation and/or the name of an MEP system type. Identify the " +
            "type by typeId or by systemId (an MEP system from list_mep_systems — its " +
            "type is used). At least one of abbreviation/name is required; values equal " +
            "the current ones are skipped (no-op). A name already used by another system " +
            "type fails with name_taken; a value Revit refuses is reported per item. " +
            "Instances of the type reflect the change immediately (the abbreviation is a " +
            "property of the type, not of the system). Returns the read-back typeId, " +
            "name and abbreviation; undo_last restores both. Dry run defaults to true.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""typeId"":{""type"":""integer""},""systemId"":{""type"":""integer""},""abbreviation"":{""type"":""string""},""name"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true}},""additionalProperties"":false}"
    };

    protected override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var abbreviation = AgentJson.GetString(args, "abbreviation")?.Trim();
        var name = AgentJson.GetString(args, "name")?.Trim();
        if (string.IsNullOrEmpty(abbreviation) && string.IsNullOrEmpty(name))
        {
            outcome.Success = false;
            outcome.ErrorCode = "invalid_arguments";
            outcome.Message = "Pass abbreviation and/or name (at least one non-empty value).";
            return;
        }

        var type = ResolveType(doc, args, out var resolveError, out var resolveMessage);
        if (type is null)
        {
            outcome.Success = false;
            outcome.ErrorCode = resolveError;
            outcome.Message = resolveMessage;
            return;
        }

        var oldAbbreviation = type.Abbreviation ?? string.Empty;
        var oldName = type.Name ?? string.Empty;
        outcome.Description = "Set MEP system type '" + oldName + "'";

        if (!string.IsNullOrEmpty(name)
            && !string.Equals(name, oldName, StringComparison.Ordinal)
            && IsNameTaken(doc, type, name!))
        {
            outcome.Success = false;
            outcome.ErrorCode = "name_taken";
            outcome.Message = "Another MEP system type is already named '" + name + "'.";
            return;
        }

        var changed = false;
        if (!string.IsNullOrEmpty(abbreviation))
        {
            var report = new WriteItemReport
            {
                Id = type.Id.GetValue(),
                Name = "abbreviation"
            };
            if (string.Equals(abbreviation, oldAbbreviation, StringComparison.Ordinal))
            {
                report.Reason = "The abbreviation is already '" + abbreviation + "' (no-op).";
                outcome.Batch.Skipped.Add(report);
            }
            else
            {
                try
                {
                    type.Abbreviation = abbreviation;
                    report.OldValue = oldAbbreviation;
                    report.NewValue = abbreviation;
                    outcome.Batch.Changed.Add(report);
                    changed = true;
                }
                catch (Exception ex)
                {
                    report.ErrorCode = "set_failed";
                    report.Reason = ex.GetType().Name + ": " + ex.Message;
                    outcome.Batch.Failed.Add(report);
                }
            }
        }

        if (!string.IsNullOrEmpty(name))
        {
            var report = new WriteItemReport
            {
                Id = type.Id.GetValue(),
                Name = "name"
            };
            if (string.Equals(name, oldName, StringComparison.Ordinal))
            {
                report.Reason = "The type is already named '" + name + "' (no-op).";
                outcome.Batch.Skipped.Add(report);
            }
            else
            {
                try
                {
                    type.Name = name;
                    report.OldValue = oldName;
                    report.NewValue = name;
                    outcome.Batch.Changed.Add(report);
                    changed = true;
                }
                catch (Exception ex)
                {
                    report.ErrorCode = "set_failed";
                    report.Reason = ex.GetType().Name + ": " + ex.Message;
                    outcome.Batch.Failed.Add(report);
                }
            }
        }

        if (!changed)
        {
            outcome.SuppressChangeSet = true;
            outcome.Data["typeId"] = type.Id.GetValue();
            outcome.Data["name"] = type.Name;
            outcome.Data["abbreviation"] = type.Abbreviation;
            return;
        }

        var newAbbreviation = type.Abbreviation ?? string.Empty;
        var newName = type.Name ?? string.Empty;
        outcome.AffectedCount = outcome.Batch.Changed.Count;
        outcome.Items.Add(new ChangeItem
        {
            UniqueId = type.UniqueId,
            ElementId = type.Id.GetValue(),
            ParameterName = "mep_system_type",
            OldValue = JsonSerializer.Serialize(new MepSystemTypeRecord
            {
                Name = oldName,
                Abbreviation = oldAbbreviation
            }),
            NewValue = JsonSerializer.Serialize(new MepSystemTypeRecord
            {
                Name = newName,
                Abbreviation = newAbbreviation
            }),
            Status = ChangeStatus.Applied
        });
        outcome.Data["typeId"] = type.Id.GetValue();
        outcome.Data["name"] = newName;
        outcome.Data["abbreviation"] = newAbbreviation;
    }

    internal sealed class MepSystemTypeRecord
    {
        public string Name { get; set; } = string.Empty;

        public string Abbreviation { get; set; } = string.Empty;
    }

    private static MEPSystemType? ResolveType(
        Document doc,
        JsonElement args,
        out string? errorCode,
        out string? message)
    {
        errorCode = null;
        message = null;

        var typeId = AgentJson.GetLong(args, "typeId", 0);
        if (typeId > 0)
        {
            if (doc.GetElement(ElementIdCompat.Create(typeId)) is MEPSystemType byId)
                return byId;

            errorCode = "type_not_found";
            message = "Element " + typeId +
                      " is not an MEP system type. Use list_mep_systems (typeId) or " +
                      "list_system_types to find it.";
            return null;
        }

        var systemId = AgentJson.GetLong(args, "systemId", 0);
        if (systemId > 0)
        {
            if (doc.GetElement(ElementIdCompat.Create(systemId)) is not MEPSystem system)
            {
                errorCode = "system_not_found";
                message = "Element " + systemId + " is not an MEP system (list_mep_systems).";
                return null;
            }

            if (doc.GetElement(system.GetTypeId()) is MEPSystemType systemType)
                return systemType;

            errorCode = "type_not_found";
            message = "The system type of system " + systemId + " was not found.";
            return null;
        }

        errorCode = "invalid_arguments";
        message = "Pass typeId or systemId to identify the system type.";
        return null;
    }

    private static bool IsNameTaken(Document doc, MEPSystemType type, string name)
    {
        foreach (var element in new FilteredElementCollector(doc).OfClass(typeof(MEPSystemType)))
        {
            if (element is MEPSystemType candidate
                && !candidate.Id.Equals(type.Id)
                && string.Equals(candidate.Name, name, StringComparison.Ordinal))
                return true;
        }

        return false;
    }
}
