using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Core.Services.Agent;
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

        // formula_repointed rewrites source references to the TARGET names of
        // the whole mapping list, so the pairs are collected once up front.
        var repointPairs = mappings
            .Where(mapping => mapping.Source?.Name is not null && mapping.Target?.Name is not null)
            .Select(mapping => new KeyValuePair<string, string>(
                mapping.Source!.Name!.Trim(), mapping.Target!.Name!.Trim()))
            .ToList();

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

            var source = FindParameter(manager, sourceRef, out var sourceMessage, out var sourceErrorCode);
            if (source is null)
            {
                if (mapping.SkipIfSourceMissing)
                {
                    // One universal mapping list must survive families with
                    // different parameter signatures: the missing source is a
                    // reported skip, not a unit failure. The verbatim reason
                    // matters: "no parameter named X" and "X is an instance
                    // parameter, but scope='type' was requested" need different
                    // follow-ups (live v18 — instance-table sources were
                    // silently lost behind a wrong "no source" wording).
                    var reason = sourceMessage
                        ?? "this family has no source parameter '" +
                           (sourceRef.Name ?? sourceRef.SharedParameterGuid) + "'";
                    if (string.Equals(sourceErrorCode, "scope_mismatch", StringComparison.Ordinal))
                    {
                        reason += " [Action: add a mapping with scope:instance (or scope:type) to cover it]";
                    }

                    plan.Warnings.Add("mapping " + (index + 1) + " skipped: " + reason);
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
            if (mode != "values" && mode != "formula" && mode != "formula_repointed")
            {
                error = label + "mode must be 'values', 'formula' or 'formula_repointed'. [Action: fix the mode]";
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
                    repointPairs,
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
                if (mode == "formula_repointed")
                {
                    // Honest instead of silent: the source value is manual, so
                    // there is no formula to re-author over the target name.
                    plan.Warnings.Add(
                        "mapping " + (index + 1) + " skipped: source '" + source.Definition.Name +
                        "' has no formula — nothing to repoint (its manual values carry the data; " +
                        "use mode=values for them)");
                }

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

            if (mode == "formula_repointed")
            {
                var buildError = BuildRepointedFormula(
                    label, mapping, source, target, repointPairs, plan.Warnings, out formula);
                if (buildError is not null)
                {
                    error = buildError;
                    return null;
                }
            }
            else if (!AgentFamilySpecs.SameSpec(source.Definition, target.Definition))
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
        IReadOnlyList<KeyValuePair<string, string>> repointPairs,
        Dictionary<string, List<FamilyMapValueState>> expectedByType,
        Dictionary<string, List<FamilyMapValueState>> actualByType,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var mode = string.IsNullOrWhiteSpace(mapping.Mode)
            ? "values"
            : mapping.Mode!.Trim().ToLowerInvariant();
        var formula = mode is "formula" or "formula_repointed" ? source.Formula : null;
        if (mode is "formula" or "formula_repointed")
        {
            if (string.IsNullOrWhiteSpace(formula))
            {
                return label + "the source parameter has no formula, so mode=" + mode + " has nothing " +
                       "to copy. [Action: use mode=values to transfer the per-type values instead]";
            }

            if (mode == "formula_repointed")
            {
                // The created target inherits the source spec, so there is no
                // mismatch to wrap — only the reference rewrite.
                var buildError = BuildRepointedFormula(
                    label, mapping, source, null, repointPairs, plan.Warnings, out formula);
                if (buildError is not null)
                {
                    return buildError;
                }
            }

            if (!AgentFamilyReader.TryFindMissingLookupTables(familyDoc, formula, out var missing))
            {
                return label + "the formula references lookup table(s) missing from the " +
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

        if (mode is "formula" or "formula_repointed")
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
            // The apply routes every formula-bearing entry through SetFormula;
            // how the formula was derived (verbatim or repointed) lives in the
            // preview warnings.
            Mode = mode == "formula_repointed" ? "formula" : mode,
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
        if (mode is "formula" or "formula_repointed")
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

    /// <summary>How a formula result can participate in arithmetic across a
    /// spec mismatch.</summary>
    private enum FormulaArithmetic
    {
        Numeric,
        UnitTyped,
        NonArithmetic
    }

    /// <summary>
    /// Builds the formula for mode=formula_repointed: the source formula with
    /// every mapped source name rewritten to its target name (live v18 — the
    /// migration duplicates must be driven by the SAME formulas, not chained
    /// to the sources). A spec mismatch is closed with the Autodesk ×1/÷1
    /// unit trick via mapping.formulaUnit; anything the trick cannot do is an
    /// honest refusal with the next action. Self-references are left as-is:
    /// replacing them would make the target circular, and following the old
    /// parameter is hybrid-safe.
    /// </summary>
    private static string? BuildRepointedFormula(
        string label,
        MapFamilyMappingSpec mapping,
        FamilyParameter source,
        FamilyParameter? target,
        IReadOnlyList<KeyValuePair<string, string>> repointPairs,
        List<string> warnings,
        out string formula)
    {
        formula = source.Formula ?? string.Empty;

        // The mapping's own source→target pair must not be rewritten: a
        // formula referencing its own parameter would become self-circular.
        var ownName = source.Definition.Name;
        var pairsForThis = repointPairs
            .Where(pair => !string.Equals(pair.Key, ownName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        formula = AgentFormulaRepointer.Repoint(formula, pairsForThis, out var replaced);
        if (replaced.Count > 0)
        {
            var replacements = pairsForThis
                .Where(pair => replaced.Contains(pair.Key, StringComparer.Ordinal))
                .Select(pair => pair.Key + "→" + pair.Value);
            warnings.Add(label + "formula repointed: " + string.Join(", ", replacements) + ".");
        }
        else
        {
            warnings.Add(label + "formula kept as-is: no mapped source references inside.");
        }

        if (ReferencesParameter(formula, ownName))
        {
            warnings.Add(
                label + "the formula still references its own source '" + ownName +
                "' — left as-is to keep it valid (the value follows the old parameter until " +
                "it is removed; Revit then keeps the last computed value).");
        }

        if (target is not null && !AgentFamilySpecs.SameSpec(source.Definition, target.Definition))
        {
            var sourceKind = ClassifySpec(AgentFamilySpecs.SpecName(source.Definition));
            var targetKind = ClassifySpec(AgentFamilySpecs.SpecName(target.Definition));
            if (sourceKind == FormulaArithmetic.NonArithmetic || targetKind == FormulaArithmetic.NonArithmetic)
            {
                return label + "the target spec differs from the source (text/boolean/material take " +
                       "no arithmetic), so the formula cannot be re-authored automatically. " +
                       "[Action: use mode=values to transfer the computed values instead]";
            }

            if (sourceKind == FormulaArithmetic.UnitTyped && targetKind == FormulaArithmetic.Numeric)
            {
                var stripError = WrapByUnit(label, mapping, formula, addUnits: false, warnings, out var stripped);
                if (stripError is not null)
                {
                    return stripError;
                }

                formula = stripped;
            }
            else if (sourceKind == FormulaArithmetic.Numeric && targetKind == FormulaArithmetic.UnitTyped)
            {
                var addError = WrapByUnit(label, mapping, formula, addUnits: true, warnings, out var added);
                if (addError is not null)
                {
                    return addError;
                }

                formula = added;
            }
            else
            {
                return label + "the target spec differs from the source in dimension (e.g. Length → " +
                       "Area); one ×1/÷1 wrap cannot convert that. [Action: pick a same-spec target, " +
                       "or use mode=values]";
            }
        }

        return null;
    }

    /// <summary>
    /// The Autodesk unit trick with an explicit constant (a bare 1 makes Revit
    /// guess the units — developer guide "Formulas and Constants"): a
    /// dimensioned formula wrapped as "(F) / 1 mm" yields a bare number of
    /// millimetres; a bare number wrapped as "(F) * 1 mm" yields a length.
    /// </summary>
    private static string? WrapByUnit(
        string label,
        MapFamilyMappingSpec mapping,
        string formula,
        bool addUnits,
        List<string> warnings,
        out string wrapped)
    {
        var unit = mapping.FormulaUnit?.Trim() ?? string.Empty;
        if (unit.Length == 0)
        {
            wrapped = formula;
            return label + "the target spec differs from the source: the formula needs a unit wrap. " +
                   "[Action: pass formulaUnit on this mapping — e.g. formulaUnit:\"mm\" wraps the " +
                   "formula as '(F) / 1 mm' (a number of millimetres) or '(F) * 1 mm' (a length)]";
        }

        if (!AgentFormulaRepointer.IsValidUnitToken(unit))
        {
            wrapped = formula;
            return label + "formulaUnit '" + unit + "' is not a usable unit constant. " +
                   "[Action: use a Revit unit symbol like mm, m, m2, kg, MPa, °C]";
        }

        wrapped = AgentFormulaRepointer.WrapWithUnit(formula, unit, addUnits);
        warnings.Add(
            label + "unit wrap applied: the formula is now '" + wrapped + "' — check that the " +
            "numeric meaning of the unit (" + unit + ") matches the customer's intent.");
        return null;
    }

    private static FormulaArithmetic ClassifySpec(string specName) =>
        specName is "number" or "integer"
            ? FormulaArithmetic.Numeric
            : specName is "text" or "yesno" or "material" or "unknown"
                ? FormulaArithmetic.NonArithmetic
                : FormulaArithmetic.UnitTyped;

    private static bool ReferencesParameter(string formula, string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        var pattern = @"(?<![\p{L}\p{N}_])" + System.Text.RegularExpressions.Regex.Escape(name) +
                      @"(?![\p{L}\p{N}_])";
        return System.Text.RegularExpressions.Regex.IsMatch(
            formula, pattern,
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
