using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Rebinds a family parameter to another existing shared parameter definition
/// (<c>FamilyManager.ReplaceParameter</c>): per-type values are kept and formulas
/// plus labels are re-pointed to the new parameter by Revit. Built-in parameters
/// are refused. Replaces the add → transfer → remove chain whose last step is not
/// reversible (external harness feedback).
/// </summary>
public sealed class ReplaceFamilyParameterTool : AgentFamilyWriteToolBase
{
    public ReplaceFamilyParameterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "replace_family_parameter",
        Description =
            "Replaces a family or shared parameter of a family with another EXISTING shared " +
            "parameter definition from the shared parameter file (FamilyManager." +
            "ReplaceParameter): per-type values are kept, formulas and labels are re-pointed " +
            "to the new parameter automatically. Built-in parameters are refused. The target " +
            "definition must already exist — list_shared_parameters lists candidates; the tool " +
            "never writes into the shared parameter file. Not reversible by undo_last " +
            "(undoSupported=false): the pre-edit backup .rfa is returned in backupPath. Dry run " +
            "by default and the dry run proves the replacement inside a rolled-back " +
            "transaction.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""parameter"":{""type"":""string""},""scope"":{""type"":""string"",""enum"":[""instance"",""type""]},""guid"":{""type"":""string""},""newSharedParameterName"":{""type"":""string""},""newSharedParameterGuid"":{""type"":""string""},""isInstance"":{""type"":""boolean""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""parameter""],""additionalProperties"":false}"
    };

    private protected override void ApplyInFamilyDocument(
        AgentFamilySession session,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<ReplaceFamilyParameterRequest>(args) ??
                      new ReplaceFamilyParameterRequest();
        var manager = session.FamilyDoc.FamilyManager;

        if (string.IsNullOrWhiteSpace(request.Parameter) && string.IsNullOrWhiteSpace(request.Guid))
        {
            Fail(outcome, "invalid_arguments", "Parameter name or GUID is required.");
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
            Fail(
                outcome,
                errorCode ?? "parameter_not_found",
                message ?? "Parameter '" + request.Parameter + "' was not found.");
            return;
        }

        var oldName = parameter.Definition.Name;
        var oldScope = parameter.IsInstance ? "instance" : "type";

        if (AgentFamilyReader.IsBuiltIn(parameter))
        {
            Fail(
                outcome,
                "builtin_not_replaceable",
                "The parameter '" + oldName + "' is a built-in Revit parameter and cannot be " +
                "replaced.",
                "Replace only family or shared parameters.");
            return;
        }

        var external = ResolveExistingSharedDefinition(
            session,
            request.NewSharedParameterName,
            request.NewSharedParameterGuid,
            outcome);
        if (external is null)
            return;

        var newName = external.Name;
        if (string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase))
        {
            Fail(
                outcome,
                "invalid_value",
                "The target shared parameter '" + newName + "' has the same name as the " +
                "current parameter; a replacement needs a different definition.",
                "Pick another definition from list_shared_parameters.");
            return;
        }

        // Formulas and labels that reference the old name are re-pointed by Revit;
        // list them up front so the dry run shows the blast radius.
        var dependents = new List<string>();
        foreach (var candidate in manager.GetParameters())
        {
            if (ReferenceEquals(candidate, parameter))
                continue;

            var formula = candidate.Formula;
            if (!string.IsNullOrWhiteSpace(formula)
                && formula!.IndexOf(oldName, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                dependents.Add(candidate.Definition.Name);
            }
        }

        var isInstance = request.IsInstance ?? parameter.IsInstance;
        var groupTypeId = parameter.Definition.GetGroupTypeId();
        var oldSpec = parameter.Definition.GetDataType();
        if (!oldSpec.Equals(external.GetDataType()))
        {
            outcome.Warnings.Add(
                "Data types differ (current " + oldSpec.TypeId + ", target " +
                external.GetDataType().TypeId + "): Revit keeps per-type values only for " +
                "compatible types, and re-pointed formulas may fail. Check the result with " +
                "get_family_parameters.");
        }

        var report = new WriteItemReport
        {
            Name = oldName,
            Detail = oldScope + " parameter → shared '" + newName + "'",
            OldValue = oldName,
            NewValue = newName
        };

        if (dryRun)
        {
            // Prove the replacement inside the rolled-back transaction: Revit validates
            // group compatibility and formula errors only on the real call. The base
            // rolls the family transaction back, so this attempt changes nothing.
            try
            {
                manager.ReplaceParameter(parameter, external, groupTypeId, isInstance);
                outcome.Data["replacementVerified"] = true;
            }
            catch (Exception ex)
            {
                report.ErrorCode = "replace_failed";
                report.Reason = "Revit refused to replace '" + oldName + "' with '" + newName +
                                "': " + ex.Message;
                outcome.Batch.Failed.Add(report);
                Fail(
                    outcome,
                    "replace_failed",
                    report.Reason,
                    "Check the group and data type of the target shared parameter.");
                return;
            }

            // The replacement already ran inside the rolled-back transaction: read
            // the formulas Revit re-pointed instead of guessing, so an empty
            // formulasUpdated honestly means "no formula referenced the parameter"
            // (external harness feedback: [] used to read as "not touched").
            var updatedFormulas = new List<string>();
            foreach (var candidate in manager.GetParameters())
            {
                var formula = candidate.Formula;
                if (!string.IsNullOrWhiteSpace(formula)
                    && formula!.IndexOf(newName, StringComparison.OrdinalIgnoreCase) >= 0
                    && dependents.Contains(
                        candidate.Definition.Name,
                        StringComparer.OrdinalIgnoreCase))
                {
                    updatedFormulas.Add(candidate.Definition.Name);
                }
            }

            outcome.Data["wouldReplace"] = true;
            outcome.Data["oldParameter"] = new Dictionary<string, object?>
            {
                ["name"] = oldName,
                ["scope"] = oldScope,
                ["isShared"] = parameter.IsShared
            };
            outcome.Data["newParameter"] = new Dictionary<string, object?>
            {
                ["name"] = newName,
                ["guid"] = external.GUID.ToString("D"),
                ["isInstance"] = isInstance
            };
            outcome.Data["formulasUpdated"] = updatedFormulas;
            report.Reason = "Dry run: '" + oldName + "' would be replaced by shared '" +
                            newName + "' (verified and rolled back)." +
                            (updatedFormulas.Count == 0
                                ? " No formula references it."
                                : " Formulas re-pointed: " +
                                  string.Join(", ", updatedFormulas) + ".");
            outcome.Batch.Changed.Add(report);
            return;
        }

        try
        {
            manager.ReplaceParameter(parameter, external, groupTypeId, isInstance);
        }
        catch (Exception ex)
        {
            report.ErrorCode = "replace_failed";
            report.Reason = "Revit refused to replace parameter '" + oldName + "': " + ex.Message;
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            outcome.ErrorCode = "replace_failed";
            outcome.Message = report.Reason;
            return;
        }

        // Like remove_family_parameter: going back needs another replacement, which
        // undo_last cannot replay — keep the change out of the audit trail and say so.
        outcome.SuppressChangeSet = true;
        outcome.Data["undoSupported"] = false;
        outcome.Warnings.Add(
            "undo_last does not revert a replaced family parameter; use the Revit Undo " +
            "command or the pre-edit backup .rfa from backupPath.");
        if (session.BackupPath is not null)
            outcome.Data["backupPath"] = session.BackupPath;

        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount = 1;
        outcome.Data["replaced"] = true;
        if (dependents.Count > 0)
        {
            outcome.Warnings.Add(
                "Revit re-pointed the formula(s) that referenced '" + oldName + "' to '" +
                newName + "': " + string.Join(", ", dependents) + ".");
        }
    }

    private static ExternalDefinition? ResolveExistingSharedDefinition(
        AgentFamilySession session,
        string? name,
        string? guidText,
        WriteOutcome outcome)
    {
        Guid? guid = null;
        if (!string.IsNullOrWhiteSpace(guidText))
        {
            if (!Guid.TryParse(guidText!.Trim(), out var parsed))
            {
                Fail(
                    outcome,
                    "invalid_arguments",
                    "newSharedParameterGuid is not a valid GUID: '" + guidText + "'.",
                    "Pass the definition GUID from list_shared_parameters.");
                return null;
            }

            guid = parsed;
        }

        if (string.IsNullOrWhiteSpace(name) && guid is null)
        {
            Fail(
                outcome,
                "invalid_arguments",
                "newSharedParameterName or newSharedParameterGuid is required.",
                "list_shared_parameters lists the existing definitions.");
            return null;
        }

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
                "No shared parameter file is configured, so a shared definition cannot be " +
                "resolved.",
                "[Action: configure a shared parameter file in Manage > Shared Parameters " +
                "and retry]");
            return null;
        }

        try
        {
            foreach (DefinitionGroup group in file.Groups)
            {
                foreach (Definition definition in group.Definitions)
                {
                    if (definition is not ExternalDefinition external)
                        continue;

                    var sameGuid = guid.HasValue && external.GUID == guid.Value;
                    var sameName = !string.IsNullOrWhiteSpace(name)
                        && string.Equals(external.Name, name!.Trim(), StringComparison.OrdinalIgnoreCase);
                    if (!sameGuid && !sameName)
                        continue;

                    if (guid.HasValue && external.GUID != guid.Value)
                        continue;

                    return external;
                }
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

        Fail(
            outcome,
            "definition_not_found",
            "The active shared parameter file has no definition named '" + name + "'" +
            (guid.HasValue ? " with GUID " + guid.Value.ToString("D") : string.Empty) + ".",
            "This tool never writes into the shared parameter file: pick an existing " +
            "definition from list_shared_parameters, or create it with " +
            "add_family_parameter createDefinition=true first.");
        return null;
    }

    private sealed class ReplaceFamilyParameterRequest
    {
        public string? Parameter { get; set; }

        public string? Scope { get; set; }

        public string? Guid { get; set; }

        public string? NewSharedParameterName { get; set; }

        public string? NewSharedParameterGuid { get; set; }

        public bool? IsInstance { get; set; }
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
    }
}
