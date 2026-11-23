using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Family-definition job action: rebinds a family/shared parameter of many
/// families to another EXISTING shared parameter definition
/// (<c>FamilyManager.ReplaceParameter</c>). One family is one frozen unit that
/// owns the whole family-document lifecycle (backup, edit, load-back, close);
/// per-type values, formulas and labels survive the rebind (Revit re-points
/// them), so a bulk ADSK → customer migration is one preview + one apply.
/// </summary>
public sealed partial class ReplaceFamilyParameterJobAction : RevitJobActionBase
{
    /// <summary>Per-family replacement problems: missing parameter, builtin, spec mismatch, missing definition.</summary>
    private const string FamilyReplaceInvalid = "family_replace_invalid";

    private const int MaxReplacements = 50;

    private static readonly JobSpecObjectRule[] SpecRules =
    {
        new()
        {
            Path = string.Empty,
            AllowedKeys = new[] { "select", "replacements" }
        },
        new()
        {
            Path = "select",
            AllowedKeys = new[] { "familyIds", "familyUniqueIds", "familyNames", "nameContains", "categoryIds", "limit" }
        },
        new()
        {
            Path = "replacements[]",
            AllowedKeys = new[] { "source", "target" }
        },
        new()
        {
            Path = "replacements[].source",
            AllowedKeys = new[] { "name", "guid" }
        },
        new()
        {
            Path = "replacements[].target",
            AllowedKeys = new[] { "name", "guid" }
        }
    };

    public override string Name => "replace_family_parameter";

    public override JobActionScope Scope => JobActionScope.FamilyDefinition;

    public override int MaxUnits => 200;

    public override JobExpansion Expand(JobActionContext context, string specJson)
    {
        var keyError = JobSpecKeyValidator.Validate(specJson, SpecRules);
        if (keyError is not null)
        {
            return ExpansionFail(JobErrorCodes.InvalidSpec, keyError);
        }

        var spec = Parse<ReplaceFamilyParametersSpec>(specJson);
        if (spec is null || spec.Select is null || spec.Replacements is null || spec.Replacements.Count == 0)
        {
            return ExpansionFail(
                JobErrorCodes.InvalidSpec,
                "replace_family_parameter requires {select:{familyIds|familyUniqueIds|familyNames|" +
                "nameContains|categoryIds, limit?}, replacements:[{source:{name|guid}, target:{name|guid}}]}");
        }

        var replacements = spec.Replacements!;
        if (replacements.Count > MaxReplacements)
        {
            return ExpansionFail(
                JobErrorCodes.InvalidSpec,
                "Too many replacements: " + replacements.Count + " (maximum " + MaxReplacements +
                "). [Action: split the replacements into several jobs]");
        }

        var replacementError = ValidateReplacements(replacements);
        if (replacementError is not null)
        {
            return ExpansionFail(JobErrorCodes.InvalidSpec, replacementError);
        }

        var select = spec.Select!;
        var selection = FamilyJobSelectors.Resolve(
            context.Document,
            select.FamilyIds,
            select.FamilyUniqueIds,
            select.FamilyNames,
            select.NameContains,
            select.CategoryIds,
            select.Limit);
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
                ArgsJson = Json(new ReplaceFamilyUnitArgs
                {
                    FamilyId = family.Id.GetValue(),
                    FamilyUniqueId = family.UniqueId,
                    FamilyName = family.Name,
                    Replacements = replacements
                })
            }).ToList(),
            Warnings = selection.Warnings
        };
    }

    private static string? ValidateReplacements(IReadOnlyList<ReplaceFamilyReplacementSpec> replacements)
    {
        for (var index = 0; index < replacements.Count; index++)
        {
            var replacement = replacements[index];
            var label = "replacement " + (index + 1) + ": ";
            var sourceRef = replacement.Source;
            var targetRef = replacement.Target;
            if (sourceRef is null || targetRef is null)
            {
                return label + "source and target are both required. [Action: add the missing side]";
            }

            if (string.IsNullOrWhiteSpace(sourceRef.Name) && string.IsNullOrWhiteSpace(sourceRef.SharedParameterGuid))
            {
                return label + "source needs a name or a shared-parameter guid. [Action: name the source parameter]";
            }

            if (string.IsNullOrWhiteSpace(targetRef.Name) && string.IsNullOrWhiteSpace(targetRef.SharedParameterGuid))
            {
                return label + "target needs a name or a shared-parameter guid. [Action: pass the definition " +
                               "name/guid from list_shared_parameters]";
            }

            if (!IsValidGuid(sourceRef.SharedParameterGuid) || !IsValidGuid(targetRef.SharedParameterGuid))
            {
                return label + "guid must be a valid GUID. [Action: copy it from get_family_parameters or " +
                               "list_shared_parameters]";
            }
        }

        return null;
    }

    private static bool IsValidGuid(string? text) =>
        string.IsNullOrWhiteSpace(text) || Guid.TryParse(text!.Trim(), out _);

    /// <summary>
    /// LoadFamily recreates the family element, so the family is resolved through
    /// the frozen unit key, the post-load-back unique id and the family name
    /// (see <see cref="FamilyJobSelectors.TryResolveFamily"/>).
    /// </summary>
    private static bool TryResolveFamily(
        Document document,
        string unitKey,
        string? undoJson,
        out RevitFamily? family)
    {
        var plan = Parse<FamilyReplaceUndoPlan>(undoJson);
        return FamilyJobSelectors.TryResolveFamily(
            document,
            unitKey,
            plan?.NewFamilyUniqueId,
            plan?.FamilyName,
            out family);
    }

    private static string ParameterScope(FamilyParameter parameter) =>
        parameter.IsInstance ? "instance" : "type";

    /// <summary>The shared GUID of a bound shared parameter, or null for family parameters.</summary>
    private static Guid? SharedGuidOf(FamilyParameter parameter)
    {
        if (!parameter.IsShared)
        {
            return null;
        }

        try
        {
            return parameter.GUID;
        }
        catch (Exception)
        {
            // Some non-shared states throw on GUID access; a null means "not shared".
            return null;
        }
    }

    private static JobUnitApply ApplyFail(string code, string detail) => new()
    {
        ErrorCode = code,
        ErrorDetail = detail
    };
}
