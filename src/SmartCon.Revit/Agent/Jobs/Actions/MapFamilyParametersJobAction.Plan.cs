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
                if (mapping.SkipIfSourceMissing)
                {
                    // One universal mapping list must survive families with
                    // different parameter signatures: the missing source is a
                    // reported skip, not a unit failure. No entry and no change
                    // count — the mapping simply does not apply to this family.
                    plan.Warnings.Add(
                        "mapping " + (index + 1) + " skipped: this family has no source parameter '" +
                        (sourceRef.Name ?? sourceRef.SharedParameterGuid) + "'");
                    continue;
                }

                error = label + "source: " + (sourceMessage ?? "parameter not found") +
                        " [Action: check the parameter name/scope with get_family_parameters, or pass " +
                        "skipIfSourceMissing=true when the mapping list is intentionally wider than one " +
                        "family's signature]";
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

            var target = FindParameter(manager, targetRef, out var targetMessage);
            if (target is null)
            {
                if (!mapping.CreateTarget)
                {
                    error = label + "target: " + (targetMessage ?? "parameter not found") +
                            " [Action: pass createTarget=true in the mapping to create it from the shared " +
                            "parameter file, use add_family_parameter, or fix its name]";
                    return null;
                }

                error = PlanCreateTarget(
                    mapping,
                    familyDoc,
                    source,
                    manager,
                    allTypes,
                    label,
                    plan,
                    expectedByType,
                    actualByType,
                    ct);
                if (error is not null)
                {
                    return null;
                }

                continue;
            }

            if (source.IsInstance != target.IsInstance)
            {
                error = label + "source '" + source.Definition.Name + "' is " + ParameterScope(source) +
                        ", target '" + target.Definition.Name + "' is " + ParameterScope(target) +
                        ". [Action: map to a target parameter of the same scope]";
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
        if (plan.Creates.Count > 0)
        {
            plan.Expected.WouldCreateTargets = plan.Creates.ToList();
        }

        return plan;
    }

    /// <summary>
    /// Plans a createTarget mapping (#431): the target parameter does not exist, so
    /// the apply will create it from a shared definition with the source spec/group
    /// and then map the per-type values or copy the source formula (#431: the
    /// formula mode is planned together with the creation instead of being
    /// rejected, so one job does both). Nothing is written here — the preview
    /// stays read-only and never touches the shared parameter file.
    /// </summary>
    private static string? PlanCreateTarget(
        MapFamilyMappingSpec mapping,
        Document familyDoc,
        FamilyParameter source,
        RevitFamilyManager manager,
        IReadOnlyList<FamilyType> allTypes,
        string label,
        FamilyMapPlan plan,
        Dictionary<string, List<FamilyMapValueState>> expectedByType,
        Dictionary<string, List<FamilyMapValueState>> actualByType,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var mode = string.IsNullOrWhiteSpace(mapping.Mode)
            ? "values"
            : mapping.Mode!.Trim().ToLowerInvariant();
        var formula = mode == "formula" ? source.Formula : null;
        if (mode == "formula")
        {
            if (string.IsNullOrWhiteSpace(formula))
            {
                return label + "the source parameter has no formula, so mode=formula has nothing " +
                       "to copy. [Action: use mode=values to transfer the per-type values instead]";
            }

            if (!AgentFamilyReader.TryFindMissingLookupTables(familyDoc, formula, out var missing))
            {
                return label + "the source formula references lookup table(s) missing from the " +
                       "family: " + string.Join(", ", missing) +
                       ". [Action: load the lookup tables or map values instead]";
            }
        }

        var targetRef = mapping.Target!;
        var targetName = targetRef.Name!.Trim();
        var targetIsInstance = source.IsInstance;
        if (!string.IsNullOrWhiteSpace(targetRef.Scope))
        {
            var parsed = AgentFamilyReader.ParseScope(targetRef.Scope, out var scopeMessage);
            if (parsed is null)
            {
                return label + "target: " + (scopeMessage ?? "scope must be 'instance' or 'type'.") +
                       " [Action: pass 'instance' or 'type', or omit target.scope to inherit the source scope]";
            }

            if (parsed.Value != source.IsInstance)
            {
                return label + "source '" + source.Definition.Name + "' is " + ParameterScope(source) +
                       ", the target to create would be " + (parsed.Value ? "instance" : "type") +
                       ". [Action: omit target.scope so the created target inherits the source scope]";
            }

            targetIsInstance = parsed.Value;
        }

        if (!string.IsNullOrWhiteSpace(targetRef.SharedParameterGuid)
            && !Guid.TryParse(targetRef.SharedParameterGuid, out _))
        {
            return label + "target: '" + targetRef.SharedParameterGuid + "' is not a valid GUID. " +
                   "[Action: fix the guid or omit it]";
        }

        var types = ResolveTypes(manager, allTypes, mapping.TypeNames, out var typeError);
        if (types is null)
        {
            return label + typeError + " [Action: list the family types and fix typeNames]";
        }

        var targetScope = targetIsInstance ? "instance" : "type";
        var storage = AgentFamilySpecs.StorageTypeName(source.StorageType);

        // A family cannot hold two parameters with the same name: when the target
        // name is already taken (any scope — the same-name lookup above missed it
        // because of a guid reference or an explicit other scope), AddParameter
        // would refuse at apply time. Fail the unit already in the preview with
        // the scope-mismatch wording of the regular path (#461).
        foreach (var existing in manager.GetParameters())
        {
            if (!string.Equals(existing.Definition.Name, targetName, StringComparison.OrdinalIgnoreCase))
                continue;

            var existingScope = existing.IsInstance ? "instance" : "type";
            return label + "target: a parameter named '" + targetName + "' already exists in this " +
                   "family with scope '" + existingScope + "' (this mapping would create '" +
                   targetScope + "'), so createTarget cannot proceed — a family cannot hold " +
                   "two parameters with the same name. [Action: map to the existing parameter " +
                   "with scope='" + existingScope + "', or choose another target name]";
        }

        // J-04: the add op is recorded before any write; Apply rebuilds this plan
        // before opening the transaction, so the op reaches persistUndo first.
        plan.UndoOps.Add(new FamilyUndoOperation
        {
            Kind = "add",
            Parameter = targetName,
            Scope = targetScope
        });

        if (mode == "formula")
        {
            // The created target starts without a formula: clearing it back to
            // null is the undo of this step, and the formula is one change for
            // every type at once.
            plan.UndoOps.Add(new FamilyUndoOperation
            {
                Kind = "formula",
                Parameter = targetName,
                Scope = targetScope,
                Formula = null
            });
        }

        plan.Entries.Add(new MapPlanEntry
        {
            Source = mapping.Source!,
            Target = targetRef,
            SourceName = source.Definition.Name,
            TargetName = targetName,
            Scope = targetScope,
            Storage = storage,
            Mode = mode,
            FormulaToApply = formula,
            TypeNames = TypeNameSet(types),
            CreateTarget = true,
            TargetIsInstance = targetIsInstance
        });

        plan.Creates.Add(new FamilyMapCreateState
        {
            Name = targetName,
            Scope = targetScope,
            Spec = AgentFamilySpecs.SpecName(source.Definition)
        });

        // Creating the parameter is itself a change, so the unit can never preview
        // as skipped; the per-type writes (values) or the formula are counted on top.
        plan.ChangeCount++;
        if (mode == "formula")
            plan.ChangeCount++;

        foreach (var type in types)
        {
            ct.ThrowIfCancellationRequested();
            var sourceValue = FamilyValueSnapshot.ReadFamilyType(type, source).ToRaw();
            AddState(expectedByType, type.Name, targetName, targetScope, storage, sourceValue);
            AddState(
                actualByType,
                type.Name,
                targetName,
                targetScope,
                storage,
                "target parameter will be created");

            if (mode == "values"
                && (source.StorageType != StorageType.String || !string.IsNullOrEmpty(sourceValue)))
            {
                plan.ChangeCount++;
            }
        }

        return null;
    }

}
