using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

public sealed partial class MapFamilyParametersJobAction
{
    private static FamilyMapPlan? BuildPlan(
        Document familyDoc,
        RevitFamilyManager manager,
        IReadOnlyList<MapFamilyMappingSpec>? mappings,
        CancellationToken ct,
        out string? error)
    {
        error = null;
        if (mappings is null || mappings.Count == 0)
        {
            error = "mappings[] is required and must not be empty. [Action: add at least one mapping]";
            return null;
        }

        if (mappings.Count > MaxMappings)
        {
            error = "Too many mappings: " + mappings.Count + " (maximum " + MaxMappings +
                    "). [Action: split the mappings into several jobs]";
            return null;
        }

        var allTypes = AgentFamilyReader.GetTypes(manager);
        if (allTypes.Count == 0)
        {
            error = "The family has no types, so there is no per-type value to map. [Action: create a type in " +
                    "the Family Editor first]";
            return null;
        }

        var plan = new FamilyMapPlan();
        var expectedByType = new Dictionary<string, List<FamilyMapValueState>>(StringComparer.Ordinal);
        var actualByType = new Dictionary<string, List<FamilyMapValueState>>(StringComparer.Ordinal);

        for (var index = 0; index < mappings.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            var mapping = mappings[index];
            var label = "mapping " + (index + 1) + ": ";

            var sourceRef = mapping.Source;
            var targetRef = mapping.Target;
            if (sourceRef is null || targetRef is null)
            {
                error = label + "source and target are both required. [Action: add the missing side]";
                return null;
            }

            var source = FindParameter(manager, sourceRef, out var sourceMessage);
            if (source is null)
            {
                error = label + "source: " + (sourceMessage ?? "parameter not found") +
                        " [Action: check the parameter name/scope with get_family_parameters]";
                return null;
            }

            var target = FindParameter(manager, targetRef, out var targetMessage);
            if (target is null)
            {
                error = label + "target: " + (targetMessage ?? "parameter not found") +
                        " [Action: use add_family_parameter to create the target, or fix its name]";
                return null;
            }

            if (source.IsInstance != target.IsInstance)
            {
                error = label + "source '" + source.Definition.Name + "' is " + ParameterScope(source) +
                        ", target '" + target.Definition.Name + "' is " + ParameterScope(target) +
                        ". [Action: map to a target parameter of the same scope]";
                return null;
            }

            var mode = string.IsNullOrWhiteSpace(mapping.Mode)
                ? "values"
                : mapping.Mode!.Trim().ToLowerInvariant();
            if (mode != "values" && mode != "formula")
            {
                error = label + "mode must be 'values' or 'formula'. [Action: fix the mode]";
                return null;
            }

            var types = ResolveTypes(manager, allTypes, mapping.TypeNames, out var typeError);
            if (types is null)
            {
                error = label + typeError + " [Action: list the family types and fix typeNames]";
                return null;
            }

            if (mode == "values")
            {
                if (!AgentFamilySpecs.SameSpec(source.Definition, target.Definition))
                {
                    error = SpecMismatchError(label, source, target);
                    return null;
                }

                if (target.IsReadOnly)
                {
                    error = label + "target '" + target.Definition.Name +
                            "' is read-only. [Action: pick a writable target parameter]";
                    return null;
                }

                var storage = AgentFamilySpecs.StorageTypeName(target.StorageType);
                var clearFormula = AgentFamilyReader.IsFormulaDriven(target);
                if (clearFormula)
                {
                    plan.ChangeCount++;
                    plan.UndoOps.Add(new FamilyUndoOperation
                    {
                        Kind = "formula",
                        Parameter = target.Definition.Name,
                        Scope = ParameterScope(target),
                        Formula = target.Formula
                    });
                }

                plan.Entries.Add(new MapPlanEntry
                {
                    Source = sourceRef,
                    Target = targetRef,
                    SourceName = source.Definition.Name,
                    TargetName = target.Definition.Name,
                    Scope = ParameterScope(target),
                    Storage = storage,
                    Mode = "values",
                    TypeNames = TypeNameSet(types),
                    ClearTargetFormula = clearFormula
                });

                foreach (var type in types)
                {
                    var sourceValue = FamilyValueSnapshot.ReadFamilyType(type, source);
                    var targetValue = FamilyValueSnapshot.ReadFamilyType(type, target);
                    AddState(expectedByType, type.Name, target, storage, sourceValue.ToRaw());
                    AddState(actualByType, type.Name, target, storage, targetValue.ToRaw());

                    if (AgentFamilyValues.RawEquals(targetValue, sourceValue))
                    {
                        continue;
                    }

                    plan.ChangeCount++;
                    plan.UndoOps.Add(new FamilyUndoOperation
                    {
                        Kind = "value",
                        Parameter = target.Definition.Name,
                        Scope = ParameterScope(target),
                        TypeName = type.Name,
                        Storage = AgentFamilySpecs.StorageTypeName(targetValue.StorageType),
                        Value = targetValue.ToRaw()
                    });
                }

                continue;
            }

            var formula = source.Formula;
            if (string.IsNullOrWhiteSpace(formula))
            {
                // Nothing to copy: the current target values are both expected and actual.
                var currentStorage = AgentFamilySpecs.StorageTypeName(target.StorageType);
                foreach (var type in types)
                {
                    var current = FamilyValueSnapshot.ReadFamilyType(type, target).ToRaw();
                    AddState(expectedByType, type.Name, target, currentStorage, current);
                    AddState(actualByType, type.Name, target, currentStorage, current);
                }

                continue;
            }

            if (!AgentFamilySpecs.SameSpec(source.Definition, target.Definition))
            {
                error = SpecMismatchError(label, source, target);
                return null;
            }

            if (!target.CanAssignFormula)
            {
                error = label + "target '" + target.Definition.Name +
                        "' cannot be driven by a formula. [Action: pick another target parameter]";
                return null;
            }

            if (!AgentFamilyReader.TryFindMissingLookupTables(familyDoc, formula, out var missing))
            {
                error = label + "the source formula references lookup table(s) missing from the family: " +
                        string.Join(", ", missing) + ". [Action: load the lookup tables or map values instead]";
                return null;
            }

            var formulaStorage = AgentFamilySpecs.StorageTypeName(target.StorageType);
            if (!string.Equals(target.Formula, formula, StringComparison.Ordinal))
            {
                plan.ChangeCount++;
                plan.UndoOps.Add(new FamilyUndoOperation
                {
                    Kind = "formula",
                    Parameter = target.Definition.Name,
                    Scope = ParameterScope(target),
                    Formula = target.Formula
                });
            }

            plan.Entries.Add(new MapPlanEntry
            {
                Source = sourceRef,
                Target = targetRef,
                SourceName = source.Definition.Name,
                TargetName = target.Definition.Name,
                Scope = ParameterScope(target),
                Storage = formulaStorage,
                Mode = "formula",
                TypeNames = TypeNameSet(types),
                FormulaToApply = formula
            });

            foreach (var type in types)
            {
                // Once the formula is copied, the target evaluates exactly like the source.
                var expected = FamilyValueSnapshot.ReadFamilyType(type, source).ToRaw();
                var current = FamilyValueSnapshot.ReadFamilyType(type, target).ToRaw();
                AddState(expectedByType, type.Name, target, formulaStorage, expected);
                AddState(actualByType, type.Name, target, formulaStorage, current);
            }
        }

        plan.Expected = BuildState(expectedByType);
        plan.Actual = BuildState(actualByType);
        return plan;
    }

}
