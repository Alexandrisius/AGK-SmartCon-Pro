using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Removes a family or shared parameter from a family. Built-in parameters are
/// refused; formulas that reference the parameter are removed by Revit and are
/// reported in the answer. The family backup keeps the operation undoable.
/// </summary>
public sealed class RemoveFamilyParameterTool : AgentFamilyWriteToolBase
{
    public RemoveFamilyParameterTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "remove_family_parameter",
        Description =
            "Removes a family or shared parameter from a family by name (or GUID). Built-in " +
            "parameters are refused. Formulas that reference the parameter are removed by Revit " +
            "and listed in the answer. Not reversible by undo_last (undoSupported=false): the " +
            "pre-edit backup .rfa is returned in backupPath for a manual restore. Dry run by " +
            "default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = false,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""parameter"":{""type"":""string""},""scope"":{""type"":""string"",""enum"":[""instance"",""type""]},""guid"":{""type"":""string""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""parameter""],""additionalProperties"":false}"
    };

    private protected override void ApplyInFamilyDocument(
        AgentFamilySession session,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<RemoveFamilyParameterRequest>(args) ??
                      new RemoveFamilyParameterRequest();
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

        var name = parameter.Definition.Name;
        var scope = parameter.IsInstance ? "instance" : "type";

        if (AgentFamilyReader.IsBuiltIn(parameter))
        {
            outcome.Batch.Failed.Add(new WriteItemReport
            {
                Name = name,
                Detail = scope + " parameter",
                ErrorCode = "builtin_not_removable",
                Reason = "Built-in Revit parameters cannot be removed; remove only family or " +
                         "shared parameters."
            });
            Fail(
                outcome,
                "builtin_not_removable",
                "The parameter '" + name + "' is a built-in Revit parameter and cannot be removed.",
                "Remove only family or shared parameters.");
            return;
        }

        var dependents = new List<string>();
        foreach (var candidate in manager.GetParameters())
        {
            if (ReferenceEquals(candidate, parameter))
                continue;

            var formula = candidate.Formula;
            if (!string.IsNullOrWhiteSpace(formula)
                && formula!.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                dependents.Add(candidate.Definition.Name);
            }
        }

        var report = new WriteItemReport
        {
            Name = name,
            Detail = scope + " parameter",
            Reason = dependents.Count == 0
                ? null
                : "Formula(s) referencing it will be removed: " + string.Join(", ", dependents)
        };

        if (dryRun)
        {
            // Risk diff (external harness feedback): affected formulas alone are not
            // enough to judge the removal — list the per-type values it would drop.
            var valuesByType = new List<Dictionary<string, object?>>();
            var types = AgentFamilyReader.GetTypes(manager);
            foreach (var type in types)
            {
                if (valuesByType.Count >= 50)
                {
                    outcome.Data["wouldRemoveValuesByTypeTruncated"] = true;
                    break;
                }

                valuesByType.Add(new Dictionary<string, object?>
                {
                    ["type"] = type.Name,
                    ["value"] = AgentFamilyReader.DisplayValue(type, parameter)
                });
            }

            outcome.Data["wouldRemoveValuesByType"] = valuesByType;

            // Prove removability inside the rolled-back transaction: on some family
            // documents a built-in parameter is not flagged by
            // Definition.BuiltInParameter, and Revit itself rejects the removal
            // (external run 6: "Комментарии" was reported removable). The base rolls
            // the family transaction back, so this attempt changes nothing.
            try
            {
                manager.RemoveParameter(parameter);
                outcome.Data["removalVerified"] = true;
            }
            catch (ArgumentException ex)
            {
                report.ErrorCode = "builtin_not_removable";
                report.Reason = "Revit refused to remove '" + name + "': " + ex.Message;
                outcome.Batch.Failed.Add(report);
                Fail(
                    outcome,
                    "builtin_not_removable",
                    report.Reason,
                    "Built-in family parameters cannot be removed; remove only family or " +
                    "shared parameters.");
                return;
            }
            catch (Exception ex)
            {
                report.ErrorCode = "remove_failed";
                report.Reason = "Revit refused to remove '" + name + "': " + ex.Message;
                outcome.Batch.Failed.Add(report);
                Fail(outcome, "remove_failed", report.Reason);
                return;
            }

            report.Reason = "Dry run: the parameter would be removed (removal verified and " +
                            "rolled back)." +
                            (dependents.Count == 0
                                ? string.Empty
                                : " Formulas removed: " + string.Join(", ", dependents) + ".") +
                            " See wouldRemoveValuesByType for the per-type values that would " +
                            "be lost.";
            outcome.Batch.Changed.Add(report);
            outcome.Data["wouldRemove"] = true;
            return;
        }

        try
        {
            manager.RemoveParameter(parameter);
        }
        catch (ArgumentException ex)
        {
            // Defensive: Revit throws ArgumentException for a built-in parameter even
            // when Definition.BuiltInParameter did not flag it.
            report.ErrorCode = "builtin_not_removable";
            report.Reason = "Revit refused to remove '" + name + "': " + ex.Message;
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            outcome.ErrorCode = "builtin_not_removable";
            outcome.Message = report.Reason + " Built-in family parameters cannot be removed.";
            return;
        }
        catch (Exception ex)
        {
            report.ErrorCode = "remove_failed";
            report.Reason = "Revit refused to remove parameter '" + name + "': " + ex.Message;
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            outcome.ErrorCode = "remove_failed";
            outcome.Message = report.Reason;
            return;
        }

        // Not reversible by undo_last: a removed parameter cannot be re-created from a
        // name alone, and a family reload from the backup does not re-add it in every
        // Revit version. Keep the change out of the audit trail and say so explicitly
        // instead of offering an undo that silently does nothing.
        outcome.SuppressChangeSet = true;
        outcome.Data["undoSupported"] = false;
        outcome.Warnings.Add(
            "undo_last does not revert a removed family parameter; use the Revit Undo " +
            "command or the pre-edit backup .rfa from backupPath.");
        if (session.BackupPath is not null)
            outcome.Data["backupPath"] = session.BackupPath;

        report.NewValue = "removed";
        outcome.Batch.Changed.Add(report);
        outcome.AffectedCount = 1;
        outcome.Data["removed"] = true;
        if (dependents.Count > 0)
        {
            outcome.Warnings.Add(
                "Revit removed the formula(s) that referenced '" + name + "': " +
                string.Join(", ", dependents) + ".");
        }
    }

    private sealed class RemoveFamilyParameterRequest
    {
        public string? Parameter { get; set; }

        public string? Scope { get; set; }

        public string? Guid { get; set; }
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