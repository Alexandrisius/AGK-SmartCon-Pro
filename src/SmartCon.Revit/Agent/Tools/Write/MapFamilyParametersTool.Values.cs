using Autodesk.Revit.DB;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Value/formula mapping half of map_family_parameters: plans are prepared per
/// mapping and applied type-major (one CurrentType switch per type).
/// </summary>
public sealed partial class MapFamilyParametersTool
{
    private static ValueMapPlan? PrepareValuePlan(
        AgentFamilySession session,
        RevitFamilyManager manager,
        WriteOutcome outcome,
        FamilyParameter source,
        FamilyParameter target,
        IReadOnlyList<FamilyType> types,
        string label,
        ref int changedCount)
    {
        if (!AgentFamilySpecs.SameSpec(source.Definition, target.Definition))
        {
            Failed(
                outcome,
                label,
                "spec_mismatch",
                "Source '" + source.Definition.Name + "' has spec '" +
                AgentFamilySpecs.SpecName(source.Definition) + "', target '" +
                target.Definition.Name + "' has spec '" +
                AgentFamilySpecs.SpecName(target.Definition) + "'. Values of different " +
                "specs cannot be copied without conversion.");
            return null;
        }

        if (target.IsDeterminedByFormula)
        {
            var oldFormula = target.Formula;
            try
            {
                manager.SetFormula(target, null);
                outcome.Data["formulaCleared"] = true;
            }
            catch (Exception ex)
            {
                Failed(
                    outcome,
                    label,
                    "formula_not_allowed",
                    "The target formula could not be cleared: " + ex.Message);
                return null;
            }

            outcome.Batch.Changed.Add(new WriteItemReport
            {
                Name = target.Definition.Name,
                Detail = label + ", formula",
                OldValue = oldFormula ?? "(none)",
                NewValue = "(cleared)"
            });
            session.AddUndoOperation(new FamilyUndoOperation
            {
                Kind = "formula",
                Parameter = target.Definition.Name,
                Scope = target.IsInstance ? "instance" : "type",
                Formula = oldFormula
            });
            changedCount++;
        }

        var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in types)
            typeNames.Add(type.Name);

        return new ValueMapPlan
        {
            Label = label,
            Source = source,
            Target = target,
            TypeNames = typeNames
        };
    }

    private static void ApplyValuePlans(
        AgentFamilySession session,
        RevitFamilyManager manager,
        WriteOutcome outcome,
        IReadOnlyList<ValueMapPlan> plans,
        IReadOnlyList<FamilyType> allTypes,
        ref int changedCount,
        ref int failedCount,
        CancellationToken ct)
    {
        foreach (var type in allTypes)
        {
            ct.ThrowIfCancellationRequested();

            var plansForType = new List<ValueMapPlan>();
            foreach (var plan in plans)
            {
                if (plan.TypeNames.Contains(type.Name))
                    plansForType.Add(plan);
            }

            if (plansForType.Count == 0)
                continue;

            var switched = false;
            foreach (var plan in plansForType)
            {
                var sourceValue = FamilyValueSnapshot.ReadFamilyType(type, plan.Source);
                var targetOld = FamilyValueSnapshot.ReadFamilyType(type, plan.Target);
                var oldDisplay = AgentFamilyReader.DisplayValue(type, plan.Target);

                if (AgentFamilyValues.RawEquals(targetOld, sourceValue))
                {
                    outcome.Batch.Skipped.Add(new WriteItemReport
                    {
                        Name = plan.Target.Definition.Name,
                        Detail = type.Name,
                        Reason = "The target already has this value."
                    });
                    continue;
                }

                try
                {
                    if (!switched
                        || manager.CurrentType is null
                        || !string.Equals(manager.CurrentType.Name, type.Name,
                            StringComparison.Ordinal))
                    {
                        manager.CurrentType = type;
                        switched = true;
                    }

                    AgentFamilyValues.Set(manager, plan.Target, sourceValue);
                }
                catch (Exception ex)
                {
                    Failed(
                        outcome,
                        plan.Label + ", type " + type.Name,
                        "invalid_value",
                        "Revit refused to set '" + plan.Target.Definition.Name + "': " +
                        ex.Message);
                    failedCount++;
                    continue;
                }

                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Name = plan.Target.Definition.Name,
                    Detail = type.Name,
                    OldValue = oldDisplay,
                    NewValue = AgentFamilyReader.DisplayValue(type, plan.Target) ??
                               sourceValue.ToRaw()
                });
                changedCount++;

                session.AddUndoOperation(new FamilyUndoOperation
                {
                    Kind = "value",
                    Parameter = plan.Target.Definition.Name,
                    Scope = plan.Target.IsInstance ? "instance" : "type",
                    TypeName = type.Name,
                    Storage = AgentFamilySpecs.StorageTypeName(targetOld.StorageType),
                    Value = targetOld.ToRaw()
                });

                session.AddExpectations(
                    type.Name,
                    plan.Target,
                    FamilyValueSnapshot.ReadFamilyType(type, plan.Target));
            }
        }
    }

    private static bool TryMapFormula(
        AgentFamilySession session,
        RevitFamilyManager manager,
        WriteOutcome outcome,
        FamilyParameter source,
        FamilyParameter target,
        IReadOnlyList<FamilyType> types,
        string label,
        out int changedCount)
    {
        changedCount = 0;

        if (string.IsNullOrWhiteSpace(source.Formula))
        {
            outcome.Batch.Skipped.Add(new WriteItemReport
            {
                Name = target.Definition.Name,
                Detail = label,
                Reason = "The source parameter has no formula; nothing to copy."
            });
            return true;
        }

        if (!AgentFamilySpecs.SameSpec(source.Definition, target.Definition))
        {
            Failed(
                outcome,
                label,
                "spec_mismatch",
                "Source '" + source.Definition.Name + "' has spec '" +
                AgentFamilySpecs.SpecName(source.Definition) + "', target '" +
                target.Definition.Name + "' has spec '" +
                AgentFamilySpecs.SpecName(target.Definition) + "'. A formula of a different " +
                "spec would produce numerically wrong values.");
            return false;
        }

        if (!target.CanAssignFormula)
        {
            Failed(
                outcome,
                label,
                "formula_not_allowed",
                "The target parameter '" + target.Definition.Name +
                "' cannot be determined by a formula.");
            return false;
        }

        if (string.Equals(target.Formula, source.Formula, StringComparison.Ordinal))
        {
            outcome.Batch.Skipped.Add(new WriteItemReport
            {
                Name = target.Definition.Name,
                Detail = label,
                Reason = "The target already has this formula."
            });
            return true;
        }

        if (!AgentFamilyReader.TryFindMissingLookupTables(
                session.FamilyDoc,
                source.Formula,
                out var missingTables))
        {
            Failed(
                outcome,
                label,
                "lookup_table_missing",
                "The source formula references lookup table(s) that are not present in the " +
                "family: " + string.Join(", ", missingTables) + ".");
            return false;
        }

        var before = new List<string?>(types.Count);
        foreach (var type in types)
            before.Add(AgentFamilyReader.DisplayValue(type, target));

        var oldTargetFormula = target.Formula;
        try
        {
            manager.SetFormula(target, source.Formula);
        }
        catch (Exception ex)
        {
            Failed(
                outcome,
                label,
                "formula_not_allowed",
                "Revit refused to assign the formula: " + ex.Message);
            return false;
        }

        outcome.Batch.Changed.Add(new WriteItemReport
        {
            Name = target.Definition.Name,
            Detail = label + ", formula",
            OldValue = oldTargetFormula,
            NewValue = source.Formula
        });
        session.AddUndoOperation(new FamilyUndoOperation
        {
            Kind = "formula",
            Parameter = target.Definition.Name,
            Scope = target.IsInstance ? "instance" : "type",
            Formula = oldTargetFormula
        });
        changedCount = 1;

        for (var index = 0; index < types.Count; index++)
        {
            var type = types[index];
            var after = AgentFamilyReader.DisplayValue(type, target);
            if (!string.Equals(before[index], after, StringComparison.Ordinal))
            {
                outcome.Batch.Changed.Add(new WriteItemReport
                {
                    Name = target.Definition.Name,
                    Detail = label + ", type " + type.Name,
                    OldValue = before[index],
                    NewValue = after
                });
                changedCount++;
            }

            session.AddExpectations(
                type.Name,
                target,
                FamilyValueSnapshot.ReadFamilyType(type, target));
        }

        return true;
    }

    private static List<FamilyType>? ResolveTypes(
        RevitFamilyManager manager,
        IReadOnlyList<FamilyType> allTypes,
        IReadOnlyList<string>? requested,
        out string? error)
    {
        error = null;
        if (requested is null || requested.Count == 0)
            return new List<FamilyType>(allTypes);

        var result = new List<FamilyType>();
        foreach (var name in requested)
        {
            var type = AgentFamilyReader.ResolveType(manager, name, out var errorCode, out var message);
            if (type is null)
            {
                error = message ?? ("Type '" + name + "' not found (" + errorCode + ").");
                return null;
            }

            if (!result.Contains(type))
                result.Add(type);
        }

        return result;
    }

    private sealed class ValueMapPlan
    {
        public required string Label { get; init; }

        public required FamilyParameter Source { get; init; }

        public required FamilyParameter Target { get; init; }

        public required HashSet<string> TypeNames { get; init; }
    }
}
