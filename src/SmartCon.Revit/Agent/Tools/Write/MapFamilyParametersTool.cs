using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Maps values (or a formula) from a source family parameter to a target
/// parameter across all family types. Values are applied type-major (one
/// CurrentType switch per type, not per value) so a 5000-value map stays inside
/// the tool timeout. Detects instance/type and spec conflicts, clears a target
/// formula before writing values and reports per parameter/type.
/// </summary>
public sealed partial class MapFamilyParametersTool : AgentFamilyWriteToolBase
{
    private const int MaxMappings = 100;

    public MapFamilyParametersTool(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    public override AgentToolDefinition Definition { get; } = new()
    {
        Name = "map_family_parameters",
        Description =
            "Maps a source family parameter to a target parameter: mode='values' copies the " +
            "value of every family type (clearing a target formula first), mode='formula' " +
            "copies the source formula. Detects instance/type scope conflicts and spec " +
            "mismatches; reports per parameter and per type. Dry run by default.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        SupportsBulk = true,
        InputSchemaJson = @"{""type"":""object"",""properties"":{""familyId"":{""type"":""integer""},""familyUniqueId"":{""type"":""string""},""familyName"":{""type"":""string""},""mappings"":{""type"":""array"",""minItems"":1,""maxItems"":100,""items"":{""type"":""object"",""properties"":{""source"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""scope"":{""type"":""string"",""enum"":[""instance"",""type""]},""guid"":{""type"":""string""}},""required"":[""name""],""additionalProperties"":false},""target"":{""type"":""object"",""properties"":{""name"":{""type"":""string""},""scope"":{""type"":""string"",""enum"":[""instance"",""type""]},""guid"":{""type"":""string""}},""required"":[""name""],""additionalProperties"":false},""mode"":{""type"":""string"",""enum"":[""values"",""formula""],""default"":""values""},""typeNames"":{""type"":""array"",""items"":{""type"":""string""}}},""required"":[""source"",""target""],""additionalProperties"":false}},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""mappings""],""additionalProperties"":false}"
    };

    private protected override void ApplyInFamilyDocument(
        AgentFamilySession session,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct)
    {
        var request = AgentJson.Deserialize<MapFamilyParametersRequest>(args) ??
                      new MapFamilyParametersRequest();
        var manager = session.FamilyDoc.FamilyManager;

        if (request.Mappings is null || request.Mappings.Count == 0)
        {
            Fail(outcome, "invalid_arguments", "mappings[] is required and must not be empty.");
            return;
        }

        if (request.Mappings.Count > MaxMappings)
        {
            Fail(
                outcome,
                "invalid_value",
                "Too many mappings: " + request.Mappings.Count + " (maximum " + MaxMappings + ").",
                "Split the request into several calls.");
            return;
        }

        var allTypes = AgentFamilyReader.GetTypes(manager);
        if (allTypes.Count == 0)
        {
            Fail(
                outcome,
                "empty_family",
                "The family has no types; there are no per-type values to map.");
            return;
        }

        var originalType = manager.CurrentType;
        var valuePlans = new List<ValueMapPlan>();
        var changedCount = 0;
        var failedCount = 0;

        try
        {
            for (var index = 0; index < request.Mappings.Count; index++)
            {
                ct.ThrowIfCancellationRequested();
                var mapping = request.Mappings[index];
                var label = "mapping " + (index + 1);

                if (mapping.Source is null || mapping.Target is null)
                {
                    Failed(outcome, label, "invalid_arguments",
                        "Each mapping needs source and target.");
                    failedCount++;
                    continue;
                }

                var source = AgentFamilyReader.FindParameter(
                    manager,
                    mapping.Source.Name,
                    mapping.Source.Guid,
                    mapping.Source.Scope,
                    out var sourceErrorCode,
                    out var sourceMessage);
                if (source is null)
                {
                    Failed(outcome, label, sourceErrorCode ?? "parameter_not_found",
                        "Source: " + (sourceMessage ?? "parameter not found."));
                    failedCount++;
                    continue;
                }

                var target = AgentFamilyReader.FindParameter(
                    manager,
                    mapping.Target.Name,
                    mapping.Target.Guid,
                    mapping.Target.Scope,
                    out var targetErrorCode,
                    out var targetMessage);
                if (target is null)
                {
                    Failed(outcome, label, targetErrorCode ?? "parameter_not_found",
                        "Target: " + (targetMessage ?? "parameter not found. Use " +
                        "add_family_parameter to create it first."));
                    failedCount++;
                    continue;
                }

                if (source.IsInstance != target.IsInstance)
                {
                    Failed(
                        outcome,
                        label,
                        "scope_mismatch",
                        "Source '" + source.Definition.Name + "' is " +
                        (source.IsInstance ? "instance" : "type") + ", target '" +
                        target.Definition.Name + "' is " +
                        (target.IsInstance ? "instance" : "type") + ".");
                    failedCount++;
                    continue;
                }

                var mode = string.IsNullOrWhiteSpace(mapping.Mode)
                    ? "values"
                    : mapping.Mode!.Trim().ToLowerInvariant();
                if (mode != "values" && mode != "formula")
                {
                    Failed(outcome, label, "invalid_value",
                        "mode must be 'values' or 'formula'.");
                    failedCount++;
                    continue;
                }

                var types = ResolveTypes(manager, allTypes, mapping.TypeNames, out var typeError);
                if (types is null)
                {
                    Failed(outcome, label, "type_not_found", typeError!);
                    failedCount++;
                    continue;
                }

                if (mode == "values")
                {
                    var plan = PrepareValuePlan(
                        session,
                        manager,
                        outcome,
                        source,
                        target,
                        types,
                        label,
                        ref changedCount);
                    if (plan is null)
                    {
                        failedCount++;
                        continue;
                    }

                    valuePlans.Add(plan);
                    continue;
                }

                if (!TryMapFormula(
                        session,
                        manager,
                        outcome,
                        source,
                        target,
                        types,
                        label,
                        out var formulaChanges))
                {
                    failedCount++;
                    continue;
                }

                changedCount += formulaChanges;
            }

            ApplyValuePlans(session, manager, outcome, valuePlans, allTypes, ref changedCount, ref failedCount, ct);
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
                    outcome.Warnings.Add("The original current family type could not be restored.");
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
            outcome.Message = changedCount + " value(s) mapped, " + failedCount +
                              " mapping(s) failed. See failed[] for reasons.";
        }
        else if (changedCount == 0)
        {
            outcome.Message = "No changes: the target parameters already match the sources " +
                              "(see skipped[]).";
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
        string name,
        string errorCode,
        string reason)
    {
        outcome.Batch.Failed.Add(new WriteItemReport
        {
            Name = name,
            ErrorCode = errorCode,
            Reason = reason
        });
    }
}
