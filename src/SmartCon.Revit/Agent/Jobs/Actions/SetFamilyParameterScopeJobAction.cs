using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Family-definition job action: switches an existing family parameter between
/// type and instance scope (<c>FamilyManager.MakeType/MakeInstance</c>) across
/// many families. The native switch keeps per-type values and formulas (unlike
/// the unbind/rebind path, which loses them); Revit refuses when a type
/// parameter is referenced by a formula of another type parameter — that case
/// is a per-family failure with a replace_family_parameter hint, not a job
/// abort. One family is one frozen unit (backup, edit, load-back, close).
/// </summary>
public sealed partial class SetFamilyParameterScopeJobAction : RevitJobActionBase
{
    private const string FamilyScopeInvalid = "family_scope_invalid";

    private static readonly JobSpecObjectRule[] SpecRules =
    {
        new()
        {
            Path = string.Empty,
            AllowedKeys = new[] { "select", "parameterRef", "isInstance" }
        },
        new()
        {
            Path = "select",
            AllowedKeys = new[] { "familyIds", "familyUniqueIds", "familyNames", "nameContains", "categoryIds", "categoryNames", "categories", "limit", "placedOnly" }
        },
        new()
        {
            Path = "parameterRef",
            AllowedKeys = new[] { "name", "guid" }
        }
    };

    public override string Name => "set_family_parameter_scope";

    public override JobActionScope Scope => JobActionScope.FamilyDefinition;

    public override int MaxUnits => 200;

    public override JobExpansion Expand(JobActionContext context, string specJson)
    {
        var keyError = JobSpecKeyValidator.Validate(specJson, SpecRules);
        if (keyError is not null)
        {
            return ExpansionFail(JobErrorCodes.InvalidSpec, keyError);
        }

        var spec = Parse<SetScopeSpec>(specJson);
        if (spec is null || spec.Select is null || spec.ParameterRef is null
            || !spec.IsInstance.HasValue)
        {
            return ExpansionFail(
                JobErrorCodes.InvalidSpec,
                "set_family_parameter_scope requires {select:{familyIds|familyUniqueIds|" +
                "familyNames|nameContains|categoryIds, limit?}, parameterRef:{name|guid}, " +
                "isInstance:true|false} — the key is parameterRef (an object), because the " +
                "shared spec.parameter key is a string owned by set_parameters");
        }

        var parameter = spec.ParameterRef!;
        if (string.IsNullOrWhiteSpace(parameter.Name)
            && string.IsNullOrWhiteSpace(parameter.SharedParameterGuid))
        {
            return ExpansionFail(
                JobErrorCodes.InvalidSpec,
                "parameter needs a name or a shared-parameter guid. [Action: pass the parameter " +
                "name from get_family_parameters]");
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
                ArgsJson = Json(new SetScopeUnitArgs
                {
                    FamilyId = family.Id.GetValue(),
                    FamilyUniqueId = family.UniqueId,
                    FamilyName = family.Name,
                    ParameterRef = parameter,
                    IsInstance = spec.IsInstance!.Value
                })
            }).ToList(),
            Warnings = selection.Warnings
        };
    }
}
