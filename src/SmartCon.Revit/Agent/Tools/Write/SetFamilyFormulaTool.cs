using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Assigns or clears a family parameter formula. Only parameters where Revit
/// allows a formula are modified; lookup tables referenced by size_lookup are
/// checked before the formula is applied. Dry-run shows the value change per type.
/// </summary>
public sealed class SetFamilyFormulaTool : AgentFamilyWriteToolBase
{
    public SetFamilyFormulaTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_family_formula",
        Description =
            "Sets or clears the formula of a family parameter. Only parameters that accept a " +
            "formula are modified (formula_not_allowed otherwise); size_lookup tables must " +
            "exist (lookup_table_missing). Dry run reports «было → станет» for every family " +
            "type.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""},""familyName"":{""type"":""string""},""parameter"":{""type"":""string""},""scope"":{""type"":""string"",""enum"":[""instance"",""type""]},""guid"":{""type"":""string""},""formula"":{""type"":[""string"",""null""]},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""parameter""],""additionalProperties"":false}"
    };

    private protected override void ApplyInFamilyDocument(
        AgentFamilySession session,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<SetFamilyFormulaRequest>(args) ??
                      new SetFamilyFormulaRequest();
        var manager = session.FamilyDoc.FamilyManager;

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

        var types = AgentFamilyReader.GetTypes(manager);
        if (types.Count == 0)
        {
            Fail(
                outcome,
                "empty_family",
                "The family has no types; a formula cannot be evaluated or stored. Create a " +
                "type in the Family Editor first.");
            return;
        }

        var clearing = string.IsNullOrWhiteSpace(request.Formula);
        var newFormula = clearing ? null : request.Formula!.Trim();
        var oldFormula = parameter.Formula;

        if (clearing && string.IsNullOrEmpty(oldFormula))
        {
            outcome.Batch.Skipped.Add(new WriteItemReport
            {
                Name = parameter.Definition.Name,
                Reason = "The parameter has no formula; nothing to clear."
            });
            outcome.Message = "No changes: the parameter '" + parameter.Definition.Name +
                              "' has no formula (see skipped[]).";
            return;
        }

        if (!clearing && !parameter.CanAssignFormula)
        {
            Fail(
                outcome,
                "formula_not_allowed",
                "The parameter '" + parameter.Definition.Name +
                "' cannot be determined by a formula (reporting or read-only parameter).",
                "Use set_family_parameter_values for value changes.");
            return;
        }

        if (!clearing
            && string.Equals(oldFormula, newFormula, StringComparison.Ordinal))
        {
            outcome.Batch.Skipped.Add(new WriteItemReport
            {
                Name = parameter.Definition.Name,
                Reason = "The parameter already has this formula."
            });
            outcome.Message = "No changes: the formula is already '" + newFormula + "'.";
            return;
        }

        if (!clearing
            && !AgentFamilyReader.TryFindMissingLookupTables(
                session.FamilyDoc,
                newFormula,
                out var missingTables))
        {
            Fail(
                outcome,
                "lookup_table_missing",
                "The formula references lookup table(s) that are not present in the family: " +
                string.Join(", ", missingTables) + ".",
                "Import the size table (CSV) into the family or fix the table name.");
            return;
        }

        var before = new List<string?>(types.Count);
        foreach (var type in types)
        {
            ct.ThrowIfCancellationRequested();
            before.Add(AgentFamilyReader.DisplayValue(type, parameter));
        }

        try
        {
            manager.SetFormula(parameter, newFormula);
        }
        catch (Exception ex)
        {
            var invalidSyntax = AgentFamilySpecs.IsRevitArgumentException(ex)
                                || ex is System.ArgumentException;
            Fail(
                outcome,
                invalidSyntax ? "invalid_formula" : "formula_not_allowed",
                "Revit rejected the formula '" + (newFormula ?? "(clear)") + "': " + ex.Message,
                invalidSyntax
                    ? "Check the formula syntax, parameter names and instance/type rules."
                    : "Check that the parameter can be formula-driven and that the formula " +
                      "does not create a circular chain.");
            return;
        }

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Name = parameter.Definition.Name,
            OldValue = oldFormula ?? "(none)",
            NewValue = newFormula ?? "(cleared)",
            Detail = "formula"
        });
        session.AddUndoOperation(new FamilyUndoOperation
        {
            Kind = "formula",
            Parameter = parameter.Definition.Name,
            Scope = parameter.IsInstance ? "instance" : "type",
            Formula = oldFormula
        });

        var valueChanges = 0;
        for (var index = 0; index < types.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var type = types[index];
            var after = AgentFamilyReader.DisplayValue(type, parameter);
            if (!string.Equals(before[index], after, StringComparison.Ordinal))
            {
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Name = parameter.Definition.Name,
                    Detail = "type " + type.Name,
                    OldValue = before[index],
                    NewValue = after
                });
                valueChanges++;
            }

            session.AddExpectations(
                type.Name,
                parameter,
                FamilyValueSnapshot.ReadFamilyType(type, parameter));
        }

        outcome.AffectedCount = 1 + valueChanges;
        outcome.Data["parameter"] = new
        {
            name = parameter.Definition.Name,
            scope = parameter.IsInstance ? "instance" : "type",
            oldFormula,
            newFormula,
            changedTypes = valueChanges,
            types = types.Count
        };

        if (valueChanges == 0)
        {
            outcome.Warnings.Add(
                "The formula was changed, but the computed value is identical in all " +
                types.Count + " type(s).");
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