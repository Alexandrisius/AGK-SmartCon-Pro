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
            AllowedKeys = new[] { "familyIds", "familyUniqueIds", "familyNames", "nameContains", "categoryIds", "limit" }
        },
        new()
        {
            Path = "mappings[]",
            AllowedKeys = new[] { "source", "target", "mode", "typeNames", "createTarget" }
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
                "categoryIds, limit?}, mappings:[{source:{name,scope,guid?}, target:{name,scope,guid?}, mode, " +
                "typeNames?, createTarget?}]}");
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
        if (select.Limit.HasValue && select.Limit.Value <= 0)
        {
            return ExpansionFail(
                JobErrorCodes.InvalidSpec,
                "select.limit must be greater than zero. [Action: pass a positive limit or omit it]");
        }

        var hasSelector = select.FamilyIds is { Count: > 0 }
                          || select.FamilyUniqueIds is { Count: > 0 }
                          || select.FamilyNames is { Count: > 0 }
                          || !string.IsNullOrWhiteSpace(select.NameContains)
                          || select.CategoryIds is { Count: > 0 };
        if (!hasSelector)
        {
            return ExpansionFail(
                JobErrorCodes.SelectorEmpty,
                "The select block names no family: pass familyIds, familyUniqueIds, familyNames, " +
                "nameContains or categoryIds. [Action: list the loaded families and narrow the selector]");
        }

        // #433: familyNames resolve to whatever loaded family bears the name; names
        // that match nothing stay here and surface as warnings (or, when nothing at
        // all was selected, in the selector-empty error).
        var pendingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (select.FamilyNames is { Count: > 0 })
        {
            foreach (var name in select.FamilyNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    pendingNames.Add(name.Trim());
                }
            }
        }

        var families = new List<RevitFamily>();
        var skipped = 0;
        foreach (var element in new FilteredElementCollector(context.Document).OfClass(typeof(RevitFamily)))
        {
            if (element is not RevitFamily family)
            {
                continue;
            }

            pendingNames.Remove(family.Name);
            if (!MatchesSelect(family, select))
            {
                continue;
            }

            if (family.IsInPlace || !family.IsEditable)
            {
                skipped++;
                continue;
            }

            families.Add(family);
        }

        families.Sort((left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        if (families.Count == 0)
        {
            if (pendingNames.Count > 0)
            {
                return ExpansionFail(
                    JobErrorCodes.SelectorEmpty,
                    "None of the select.familyNames matched a loaded family: " +
                    JoinNames(pendingNames) + ". [Action: list the loaded families and fix the names]");
            }

            return ExpansionFail(
                JobErrorCodes.SelectorEmpty,
                skipped > 0
                    ? "All " + skipped + " matched families are in-place or not editable, so none can be " +
                      "mapped. [Action: pick a loadable, editable family]"
                    : "The select block matched no family. [Action: list the loaded families and widen the " +
                      "selector]");
        }

        if (select.Limit is { } limit && families.Count > limit)
        {
            families.RemoveRange(limit, families.Count - limit);
        }

        var warnings = new List<string>();
        foreach (var unmatched in pendingNames.OrderBy(name => name, StringComparer.Ordinal))
        {
            warnings.Add(
                "No loaded family named '" + unmatched + "'. [Action: list_families to see the loaded families]");
        }

        if (skipped > 0)
        {
            warnings.Add(
                skipped + " matched family/families are in-place or not editable; they were skipped because " +
                "a family document cannot be opened for them.");
        }

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
            if (mode != "values" && mode != "formula")
            {
                return label + "mode must be 'values' or 'formula'. [Action: fix the mode]";
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

    private static bool MatchesSelect(RevitFamily family, MapFamilySelectSpec select)
    {
        if (select.FamilyIds is { Count: > 0 } && !select.FamilyIds.Contains(family.Id.GetValue()))
        {
            return false;
        }

        if (select.FamilyNames is { Count: > 0 } && !MatchesAnyName(select.FamilyNames, family.Name))
        {
            return false;
        }

        if (select.FamilyUniqueIds is { Count: > 0 })
        {
            var matched = false;
            foreach (var uniqueId in select.FamilyUniqueIds)
            {
                if (string.Equals(uniqueId, family.UniqueId, StringComparison.Ordinal))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                return false;
            }
        }

        if (!string.IsNullOrWhiteSpace(select.NameContains)
            && family.Name.IndexOf(select.NameContains!, StringComparison.OrdinalIgnoreCase) < 0)
        {
            return false;
        }

        if (select.CategoryIds is { Count: > 0 })
        {
            var categoryId = family.FamilyCategory?.Id.GetValue();
            if (!categoryId.HasValue || !select.CategoryIds.Contains(categoryId.Value))
            {
                return false;
            }
        }

        return true;
    }

    private static bool MatchesAnyName(IReadOnlyList<string> names, string familyName)
    {
        foreach (var name in names)
        {
            if (string.Equals(name?.Trim(), familyName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string JoinNames(IEnumerable<string> names) =>
        string.Join(", ", names.OrderBy(name => name, StringComparer.Ordinal));

    /// <summary>
    /// LoadFamily recreates the family element, so the unit key (and the family id)
    /// captured at expansion stops resolving after apply. Resolution order: the
    /// frozen unit key, then the post-load-back unique id stored in the undo payload,
    /// then the family name (family names are unique in a project).
    /// </summary>
    private static bool TryResolveFamily(
        Document document,
        string unitKey,
        string? undoJson,
        out RevitFamily? family)
    {
        family = null;
        if (!string.IsNullOrWhiteSpace(unitKey))
        {
            family = FindFamilyByUniqueId(document, unitKey);
            if (family is not null)
            {
                return true;
            }
        }

        var plan = Parse<FamilyMapUndoPlan>(undoJson);
        if (!string.IsNullOrWhiteSpace(plan?.NewFamilyUniqueId))
        {
            family = FindFamilyByUniqueId(document, plan!.NewFamilyUniqueId!);
            if (family is not null)
            {
                return true;
            }
        }

        if (string.IsNullOrWhiteSpace(plan?.FamilyName))
        {
            return false;
        }

        foreach (var element in new FilteredElementCollector(document).OfClass(typeof(RevitFamily)))
        {
            if (element is RevitFamily candidate
                && string.Equals(candidate.Name, plan!.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                family = candidate;
                return true;
            }
        }

        return false;
    }

    private static RevitFamily? FindFamilyByUniqueId(Document document, string uniqueId)
    {
        try
        {
            return document.GetElement(uniqueId) as RevitFamily;
        }
        catch (Exception)
        {
            return null;
        }
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

    private static string DescribeOutcome(WriteOutcome outcome) =>
        (outcome.ErrorCode ?? "family_edit_failed") + ": " +
        (outcome.Message ?? "The family document could not be prepared.") +
        " [Action: close the Family Editor for that family and retry]";

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
        out string? message) =>
        AgentFamilyReader.FindParameter(
            manager,
            reference.Name,
            reference.SharedParameterGuid,
            reference.Scope,
            out _,
            out message);

    private static FamilyParameter? FindParameter(RevitFamilyManager manager, MapFamilyParameterRef reference) =>
        FindParameter(manager, reference, out _);
}
