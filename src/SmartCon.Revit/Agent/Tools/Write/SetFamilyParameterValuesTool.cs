using System.Text.Json;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Writes values to family parameters per family type. Numeric values require an
/// explicit unit; formula-driven parameters are refused; the instance/type scope
/// is validated against the parameter definition.
/// </summary>
public sealed class SetFamilyParameterValuesTool : AgentFamilyWriteToolBase
{
    private const int MaxItems = 500;

    public SetFamilyParameterValuesTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "set_family_parameter_values",
        Description =
            "Sets family parameter values per family type: items[{parameter, typeName|typeId, " +
            "value, unit}]. Numeric values need a unit (mm/m/deg/…); formula-driven parameters " +
            "are refused with formula_not_allowed; the instance/type scope is checked. " +
            "Dry run by default and a per-type report.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""items"":{""type"":""array"",""minItems"":1,""maxItems"":500,""items"":{""type"":""object"",""properties"":{""parameter"":{""type"":""string""},""scope"":{""type"":""string"",""enum"":[""instance"",""type""]},""guid"":{""type"":""string""},""typeName"":{""type"":""string""},""typeId"":{""type"":""integer""},""value"":{""type"":""string""},""unit"":{""type"":""string""}},""required"":[""parameter"",""value""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""items""],""additionalProperties"":false}"
    };

    private protected override void ApplyInFamilyDocument(
        AgentFamilySession session,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<SetFamilyParameterValuesRequest>(args) ??
                      new SetFamilyParameterValuesRequest();
        var manager = session.FamilyDoc.FamilyManager;

        if (request.Items is null || request.Items.Count == 0)
        {
            Fail(outcome, "invalid_arguments", "items[] is required and must not be empty.");
            return;
        }

        if (request.Items.Count > MaxItems)
        {
            Fail(
                outcome,
                "invalid_value",
                "Too many items: " + request.Items.Count + " (maximum " + MaxItems + ").",
                "Split the request into several calls.");
            return;
        }

        var originalType = manager.CurrentType;
        var changedCount = 0;
        var failedCount = 0;

        try
        {
            foreach (var item in request.Items)
            {
                ct.ThrowIfCancellationRequested();

                var report = new WriteItemReport
                {
                    Name = item.Parameter,
                    Detail = item.TypeName
                };

                var parameter = AgentFamilyReader.FindParameter(
                    manager,
                    item.Parameter,
                    item.Guid,
                    item.Scope,
                    out var errorCode,
                    out var message);
                if (parameter is null)
                {
                    Failed(outcome, report, errorCode ?? "parameter_not_found",
                        message ?? "Parameter not found.");
                    failedCount++;
                    continue;
                }

                report.Name = parameter.Definition.Name;
                if (parameter.IsDeterminedByFormula)
                {
                    Failed(
                        outcome,
                        report,
                        "formula_not_allowed",
                        "The parameter '" + report.Name + "' is determined by the formula '" +
                        parameter.Formula + "' and its value cannot be set. Change the formula " +
                        "with set_family_formula or clear it first.");
                    failedCount++;
                    continue;
                }

                if (parameter.StorageType == StorageType.None)
                {
                    Failed(
                        outcome,
                        report,
                        "invalid_value",
                        "The parameter '" + report.Name + "' has no storage type and cannot " +
                        "hold a value.");
                    failedCount++;
                    continue;
                }

                var kind = AgentFamilySpecs.KindOf(parameter.Definition, parameter.StorageType);
                if (kind == SmartCon.Core.Services.Agent.AgentFamilyValueKind.Unsupported)
                {
                    Failed(
                        outcome,
                        report,
                        "invalid_value",
                        "The spec of '" + report.Name + "' (" +
                        AgentFamilySpecs.SpecName(parameter.Definition) + ") is not supported " +
                        "for direct value writing; supported specs: length, ductSize, pipeSize, " +
                        "distance, angle, area, volume, number, integer, text, yesno, material.");
                    failedCount++;
                    continue;
                }

                if (!AgentFamilyUnits.TryConvert(
                        kind,
                        item.Value,
                        item.Unit,
                        out var converted,
                        out var valueErrorCode,
                        out var valueMessage))
                {
                    Failed(
                        outcome,
                        report,
                        valueErrorCode ?? "invalid_value",
                        valueMessage ?? "Invalid value.");
                    failedCount++;
                    continue;
                }

                var typeName = item.TypeName;
                if (item.TypeId.HasValue)
                {
                    var symbol = session.ProjectDoc.GetElement(
                        ElementIdCompat.Create(item.TypeId.Value)) as FamilySymbol;
                    if (symbol is null)
                    {
                        Failed(
                            outcome,
                            report,
                            "type_not_found",
                            "Type id " + item.TypeId.Value + " is not a family type in the project.");
                        failedCount++;
                        continue;
                    }

                    if (symbol.Family is null
                        || !string.Equals(
                            symbol.Family.Name,
                            session.FamilyName,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        Failed(
                            outcome,
                            report,
                            "invalid_value",
                            "Type id " + item.TypeId.Value + " belongs to family '" +
                            (symbol.Family?.Name ?? "?") + "', not to '" +
                            session.FamilyName + "'.");
                        failedCount++;
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(typeName)
                        && !string.Equals(typeName, symbol.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        Failed(
                            outcome,
                            report,
                            "invalid_value",
                            "typeId " + item.TypeId.Value + " is type '" + symbol.Name +
                            "', which does not match typeName '" + typeName + "'.");
                        failedCount++;
                        continue;
                    }

                    typeName = symbol.Name;
                }

                var type = AgentFamilyReader.ResolveType(
                    manager,
                    typeName,
                    out var typeErrorCode,
                    out var typeMessage);
                if (type is null)
                {
                    Failed(outcome, report, typeErrorCode ?? "type_not_found",
                        typeMessage ?? "Type not found.");
                    failedCount++;
                    continue;
                }

                report.Detail = type.Name;
                var oldValue = FamilyValueSnapshot.ReadFamilyType(type, parameter);
                var oldDisplay = AgentFamilyReader.DisplayValue(type, parameter);
                var newValue = AgentFamilyValues.FromConverted(converted);

                if (AgentFamilyValues.RawEquals(oldValue, newValue))
                {
                    report.OldValue = oldDisplay;
                    report.NewValue = oldDisplay;
                    report.Reason = "The type already has this value.";
                    outcome.Batch.Skipped.Add(report);
                    continue;
                }

                try
                {
                    manager.CurrentType = type;
                    AgentFamilyValues.Set(manager, parameter, newValue);
                }
                catch (Exception ex)
                {
                    var formulaDriven = AgentFamilySpecs.IsRevitInvalidOperation(ex)
                                        || ex is System.InvalidOperationException;
                    Failed(
                        outcome,
                        report,
                        formulaDriven ? "formula_not_allowed" : "invalid_value",
                        "Revit refused to set the value: " + ex.Message +
                        (formulaDriven
                            ? " (formula-driven or read-only parameter)."
                            : string.Empty));
                    failedCount++;
                    continue;
                }

                var actual = FamilyValueSnapshot.ReadFamilyType(type, parameter);
                report.OldValue = oldDisplay;
                report.NewValue = AgentFamilyReader.DisplayValue(type, parameter) ?? newValue.ToRaw();
                outcome.Batch.Changed.Add(report);
                changedCount++;

                session.AddUndoOperation(new FamilyUndoOperation
                {
                    Kind = "value",
                    Parameter = parameter.Definition.Name,
                    Scope = parameter.IsInstance ? "instance" : "type",
                    TypeName = type.Name,
                    Storage = AgentFamilySpecs.StorageTypeName(oldValue.StorageType),
                    Value = oldValue.ToRaw()
                });

                session.AddExpectations(type.Name, parameter, actual);
            }
        }
        finally
        {
            if (originalType is not null)
            {
                try
                {
                    manager.CurrentType = originalType;
                }
                catch (Exception)
                {
                    outcome.Warnings.Add(
                        "The original current family type could not be restored.");
                }
            }
        }

        outcome.AffectedCount = changedCount;
        if (failedCount > 0)
        {
            outcome.Success = false;
            if (changedCount == 0 && outcome.Batch.Failed.Count > 0)
                outcome.ErrorCode ??= outcome.Batch.Failed[0].ErrorCode ?? "invalid_value";
            else
                outcome.ErrorCode ??= "partial_failure";
            outcome.Message = changedCount + " value(s) set, " + failedCount +
                              " item(s) failed. See failed[] for reasons.";
        }
        else if (changedCount == 0)
        {
            outcome.Message = "No changes: every requested value already matches (see skipped[]).";
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

    private static void Failed(
        WriteOutcome outcome,
        WriteItemReport report,
        string errorCode,
        string reason)
    {
        report.ErrorCode = errorCode;
        report.Reason = reason;
        outcome.Batch.Failed.Add(report);
    }
}