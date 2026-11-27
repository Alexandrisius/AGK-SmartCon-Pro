using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Family-definition job action: maps source parameter values (or a formula) onto a
/// target parameter across every family type. One family is one frozen unit, and the
/// unit owns the whole family-document lifecycle (backup, edit, load-back, close),
/// because EditFamily/LoadFamily are forbidden inside a project transaction.
/// </summary>
public sealed partial class MapFamilyParametersJobAction : RevitJobActionBase
{
    /// <summary>Per-family mapping problems: missing parameter/type, scope or spec mismatch.</summary>
    private const string FamilyMappingInvalid = "family_mapping_invalid";

    private const int MaxMappings = 100;

    private static readonly JobSpecObjectRule[] SpecRules =
    {
        new()
        {
            Path = string.Empty,
            AllowedKeys = new[] { "select", "mappings" }
        },
        new()
        {
            Path = "select",
            AllowedKeys = new[] { "familyIds", "familyUniqueIds", "familyNames", "nameContains", "categoryIds", "categoryNames", "categories", "limit", "placedOnly" }
        },
        new()
        {
            Path = "mappings[]",
            AllowedKeys = new[] { "source", "target", "mode", "typeNames", "createTarget", "skipIfSourceMissing", "formulaUnit" }
        },
        new()
        {
            Path = "mappings[].source",
            AllowedKeys = new[] { "name", "scope", "guid" }
        },
        new()
        {
            Path = "mappings[].target",
            AllowedKeys = new[] { "name", "scope", "guid" }
        }
    };

    public override string Name => "map_family_parameters";

    public override JobActionScope Scope => JobActionScope.FamilyDefinition;

    public override int MaxUnits => 200;

    public override JobExpansion Expand(JobActionContext context, string specJson)
    {
        var keyError = JobSpecKeyValidator.Validate(specJson, SpecRules);
        if (keyError is not null)
        {
            return ExpansionFail(JobErrorCodes.InvalidSpec, keyError);
        }

        var spec = Parse<MapFamilyParametersSpec>(specJson);
        if (spec is null || spec.Select is null || spec.Mappings is null || spec.Mappings.Count == 0)
        {
            return ExpansionFail(
                JobErrorCodes.InvalidSpec,
                "map_family_parameters requires {select:{familyIds|familyUniqueIds|familyNames|nameContains|" +
                "categoryIds|categoryNames, limit?}, mappings:[{source:{name,scope,guid?}, target:{name,scope,guid?}, mode, " +
                "typeNames?, createTarget?, skipIfSourceMissing?}]}");
        }

        var mappings = spec.Mappings!;
        if (mappings.Count > MaxMappings)
        {
            return ExpansionFail(
                JobErrorCodes.InvalidSpec,
                "Too many mappings: " + mappings.Count + " (maximum " + MaxMappings +
                "). [Action: split the mappings into several jobs]");
        }

        var mappingError = ValidateMappings(mappings);
        if (mappingError is not null)
        {
            return ExpansionFail(JobErrorCodes.InvalidSpec, mappingError);
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

        var families = selection.Families.ToList();
        var warnings = selection.Warnings.ToList();

        return new JobExpansion
        {
            Units = families.Select(family => new JobUnit
            {
                Seq = 0,
                UnitKey = family.UniqueId,
                Label = family.Name,
                ArgsJson = Json(new MapFamilyUnitArgs
                {
                    FamilyId = family.Id.GetValue(),
                    FamilyUniqueId = family.UniqueId,
                    FamilyName = family.Name,
                    Mappings = mappings
                })
            }).ToList(),
            Warnings = warnings
        };
    }

    private static string? ValidateMappings(IReadOnlyList<MapFamilyMappingSpec> mappings)
    {
        for (var index = 0; index < mappings.Count; index++)
        {
            var mapping = mappings[index];
            var label = "mapping " + (index + 1) + ": ";
            var sourceRef = mapping.Source;
            var targetRef = mapping.Target;
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
                return label + "target needs a name or a shared-parameter guid. [Action: name the target parameter]";
            }

            if (mapping.CreateTarget && string.IsNullOrWhiteSpace(targetRef.Name))
            {
                return label + "createTarget needs target.name (a guid alone cannot name the parameter to " +
                               "create). [Action: add target.name, or create the parameter with add_family_parameter]";
            }

            var mode = string.IsNullOrWhiteSpace(mapping.Mode)
                ? "values"
                : mapping.Mode!.Trim().ToLowerInvariant();
            if (mode is not ("values" or "formula" or "formula_repointed"))
            {
                return label + "mode must be 'values', 'formula' or 'formula_repointed'. [Action: fix the mode]";
            }

            var typeNames = mapping.TypeNames;
            if (typeNames is null)
            {
                continue;
            }

            foreach (var typeName in typeNames)
            {
                if (string.IsNullOrWhiteSpace(typeName))
                {
                    return label + "typeNames must not contain empty names. [Action: remove the empty entry]";
                }
            }
        }

        return null;
    }

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
        var plan = Parse<FamilyMapUndoPlan>(undoJson);
        return FamilyJobSelectors.TryResolveFamily(
            document,
            unitKey,
            plan?.NewFamilyUniqueId,
            plan?.FamilyName,
            out family);
    }

    private static List<FamilyType>? ResolveTypes(
        RevitFamilyManager manager,
        IReadOnlyList<FamilyType> allTypes,
        IReadOnlyList<string>? requested,
        out string? error)
    {
        error = null;
        if (requested is null || requested.Count == 0)
        {
            return new List<FamilyType>(allTypes);
        }

        var result = new List<FamilyType>();
        foreach (var name in requested)
        {
            var type = AgentFamilyReader.ResolveType(manager, name, out var errorCode, out var message);
            if (type is null)
            {
                error = message ?? ("type '" + name + "' was not found (" + errorCode + ")");
                return null;
            }

            if (!result.Any(existing => string.Equals(existing.Name, type.Name, StringComparison.Ordinal)))
            {
                result.Add(type);
            }
        }

        return result;
    }

    private static string ParameterScope(FamilyParameter parameter) =>
        parameter.IsInstance ? "instance" : "type";

    private static HashSet<string> TypeNameSet(IReadOnlyList<FamilyType> types)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
        {
            names.Add(type.Name);
        }

        return names;
    }

    private static void AddState(
        Dictionary<string, List<FamilyMapValueState>> map,
        string typeName,
        FamilyParameter target,
        string storage,
        string value) =>
        AddState(map, typeName, target.Definition.Name, ParameterScope(target), storage, value);

    private static void AddState(
        Dictionary<string, List<FamilyMapValueState>> map,
        string typeName,
        string name,
        string scope,
        string storage,
        string value)
    {
        if (!map.TryGetValue(typeName, out var list))
        {
            list = new List<FamilyMapValueState>();
            map.Add(typeName, list);
        }

        list.Add(new FamilyMapValueState
        {
            Name = name,
            Scope = scope,
            Storage = storage,
            Value = value
        });
    }

    private static FamilyMapState BuildState(Dictionary<string, List<FamilyMapValueState>> source)
    {
        var state = new FamilyMapState();
        var typeNames = new List<string>(source.Keys);
        typeNames.Sort(StringComparer.Ordinal);

        foreach (var typeName in typeNames)
        {
            var targets = source[typeName];
            targets.Sort((left, right) =>
            {
                var byName = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
                return byName != 0
                    ? byName
                    : string.Compare(left.Scope, right.Scope, StringComparison.Ordinal);
            });

            state.Types.Add(new FamilyMapTypeState
            {
                TypeName = typeName,
                Targets = targets
            });
        }

        return state;
    }

    private static JobUnitApply ApplyFail(string code, string detail) => new()
    {
        ErrorCode = code,
        ErrorDetail = detail
    };

    private static string MissingTargetError(string targetName) =>
        "The target parameter '" + targetName + "' disappeared while applying. [Action: run the preview again]";

    private static string SpecMismatchError(string label, FamilyParameter source, FamilyParameter target) =>
        label + "source '" + source.Definition.Name + "' has spec '" +
        AgentFamilySpecs.SpecName(source.Definition) + "', target '" + target.Definition.Name + "' has spec '" +
        AgentFamilySpecs.SpecName(target.Definition) +
        "'. [Action: map parameters of the same spec instead]";

    private static FamilyParameter? FindParameter(
        RevitFamilyManager manager,
        MapFamilyParameterRef reference,
        out string? message,
        out string? errorCode) =>
        AgentFamilyReader.FindParameter(
            manager,
            reference.Name,
            reference.SharedParameterGuid,
            reference.Scope,
            out errorCode,
            out message);

    private static FamilyParameter? FindParameter(RevitFamilyManager manager, MapFamilyParameterRef reference) =>
        FindParameter(manager, reference, out _, out _);

    private static FamilyParameter? FindParameter(
        RevitFamilyManager manager,
        MapFamilyParameterRef reference,
        out string? message) =>
        FindParameter(manager, reference, out message, out _);
}
