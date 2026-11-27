using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Agent;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Family-definition job action: adds one or more parameters (normal or shared,
/// with an optional authored formula) to every selected family — the bulk path
/// of <c>add_family_parameter</c>. One family is one frozen unit that owns the
/// whole family-document lifecycle (backup, edit, load-back, close); the preview
/// reports existing names as skips so a migration over N families is one preview
/// + one apply instead of N single-family calls.
/// </summary>
public sealed partial class AddFamilyParametersJobAction : RevitJobActionBase
{
    /// <summary>Per-family add problems: duplicate name, bad spec, refused formula.</summary>
    private const string FamilyAddInvalid = "family_add_invalid";

    private const int MaxParameters = 50;

    private static readonly JobSpecObjectRule[] SpecRules =
    {
        new()
        {
            Path = string.Empty,
            AllowedKeys = new[] { "select", "parameters" }
        },
        new()
        {
            Path = "select",
            AllowedKeys = new[] { "familyIds", "familyUniqueIds", "familyNames", "nameContains", "categoryIds", "categoryNames", "categories", "limit", "placedOnly" }
        },
        new()
        {
            Path = "parameters[]",
            // createDefinition is deliberately absent: a job must not create
            // definitions in the customer's external ФОП file — the single tool
            // does that exactly once (validator note N3).
            AllowedKeys = new[]
            {
                "name", "storageType", "group", "isInstance", "isShared",
                "guid", "initialValue", "unit", "formula"
            }
        }
    };

    public override string Name => "add_family_parameter";

    public override JobActionScope Scope => JobActionScope.FamilyDefinition;

    public override int MaxUnits => 200;

    public override JobExpansion Expand(JobActionContext context, string specJson)
    {
        var keyError = JobSpecKeyValidator.Validate(specJson, SpecRules);
        if (keyError is not null)
        {
            return ExpansionFail(JobErrorCodes.InvalidSpec, keyError);
        }

        var spec = Parse<AddFamilyParametersSpec>(specJson);
        if (spec is null || spec.Select is null
            || spec.Parameters is null || spec.Parameters.Count == 0)
        {
            return ExpansionFail(
                JobErrorCodes.InvalidSpec,
                "add_family_parameter requires {select:{familyIds|familyUniqueIds|familyNames|" +
                "nameContains|categoryIds|categoryNames, limit?}, parameters:[{name, storageType, group?, " +
                "isInstance?, isShared?, createDefinition?, guid?, initialValue?, unit?, formula?}]}");
        }

        var parameters = spec.Parameters!;
        if (parameters.Count > MaxParameters)
        {
            return ExpansionFail(
                JobErrorCodes.InvalidSpec,
                "Too many parameters: " + parameters.Count + " (maximum " + MaxParameters +
                "). [Action: split the parameters into several jobs]");
        }

        var parameterError = ValidateParameters(parameters);
        if (parameterError is not null)
        {
            return ExpansionFail(JobErrorCodes.InvalidSpec, parameterError);
        }

        // Live v18 formula pre-gates at EXPANSION (before any family opens —
        // the weak-model run burned ~11 minutes of preview+apply on 34 units
        // Revit would refuse anyway):
        // 1) material/yesno/image parameters take no formulas;
        // 2) a formula referencing a hyphenated name (Revit parses '-' as
        //    minus) fails instantly against the spec's own names.
        foreach (var request in parameters)
        {
            if (string.IsNullOrWhiteSpace(request.Formula))
                continue;

            if (AgentFamilySpecs.TryResolveSpec(
                    request.StorageType, out var specId, out _, out _)
                && specId is not null
                && AgentFamilySpecs.SpecNameOf(specId) is "material" or "yesno" or "integer")
            {
                return ExpansionFail(
                    JobErrorCodes.InvalidSpec,
                    "parameter '" + request.Name + "': the spec '" +
                    AgentFamilySpecs.SpecNameOf(specId) + "' takes no formulas. " +
                    "[Action: drop the formula and transfer values instead]");
            }
        }

        var specNames = parameters
            .Select(parameter => parameter.Name?.Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name) && name!.IndexOf("-", StringComparison.Ordinal) >= 0)
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var request in parameters)
        {
            if (string.IsNullOrWhiteSpace(request.Formula))
                continue;

            foreach (var hyphenated in specNames)
            {
                if (AgentFormulaRepointer.ReferencesParameter(request.Formula!, hyphenated))
                {
                    return ExpansionFail(
                        JobErrorCodes.InvalidSpec,
                        "parameter '" + request.Name + "': the formula references '" + hyphenated +
                        "', whose name contains a '-' — Revit parses it as minus, so the formula " +
                        "string is invalid. [Action: use mode=values in map_family_parameters or " +
                        "drop the formula]");
                }
            }
        }

        var select = spec.Select!;
        var selection = FamilyJobSelectors.Resolve(
            context.Document,
            select.FamilyIds,
            select.FamilyUniqueIds,
            select.FamilyNames,
            select.NameContains,
            select.CategoryIds,
            FamilyJobSelectors.MergeCategoryNameAliases(
                select.CategoryNames, select.Categories),
            select.Limit,
            select.PlacedOnly);
        if (!selection.Success)
        {
            return ExpansionFail(selection.ErrorCode!, selection.ErrorDetail!);
        }

        return new JobExpansion
        {
            Units = selection.Families.Select(family => new JobUnit
            {
                Seq = 0,
                UnitKey = family.UniqueId,
                Label = family.Name,
                ArgsJson = Json(new AddFamilyUnitArgs
                {
                    FamilyId = family.Id.GetValue(),
                    FamilyUniqueId = family.UniqueId,
                    FamilyName = family.Name,
                    Parameters = parameters
                })
            }).ToList(),
            Warnings = selection.Warnings
        };
    }

    private static string? ValidateParameters(IReadOnlyList<AddFamilyParameterRequest> parameters)
    {
        for (var index = 0; index < parameters.Count; index++)
        {
            var parameter = parameters[index];
            var label = "parameter " + (index + 1) + ": ";
            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                return label + "name is required.";
            }

            if (string.IsNullOrWhiteSpace(parameter.StorageType))
            {
                return label + "storageType is required (length | number | text | ...).";
            }

            if (!AgentFamilySpecs.TryResolveSpec(
                    parameter.StorageType,
                    out _,
                    out _,
                    out var specMessage))
            {
                return label + (specMessage ?? "Unknown storageType.");
            }
        }

        return null;
    }

    private static JobUnitApply ApplyFail(string code, string detail) => new()
    {
        Success = false,
        ErrorCode = code,
        ErrorDetail = detail
    };
}
