using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Value-mapping half of the family job action: the read-only preview, the
/// family-document transaction and the type-major apply.
/// </summary>
public sealed partial class MapFamilyParametersJobAction
{
    public override JobUnitPreview Preview(JobActionContext context, JobUnit unit)
    {
        var args = Parse<MapFamilyUnitArgs>(unit.ArgsJson);
        if (args is null)
        {
            return Fail(JobErrorCodes.InvalidSpec, "Unit arguments are unreadable. [Action: run the preview again]");
        }

        if (args.Error is not null)
        {
            return Fail(FamilyMappingInvalid, args.Error!);
        }

        if (!TryResolveFamily(context.Document, unit.UnitKey, null, out var family) || family is null)
        {
            return Fail(
                JobErrorCodes.ItemFailed,
                "The family '" + args.FamilyName + "' no longer exists in the project. [Action: list the " +
                "loaded families and run the preview again]");
        }

        var outcome = new WriteOutcome();
        AgentFamilySession? session = null;
        try
        {
            session = AgentFamilySession.Open(context.Document, family, outcome);
            if (session is null)
            {
                return Fail(JobErrorCodes.ItemFailed, DescribeOutcome(outcome));
            }

            var plan = BuildPlan(
                session.FamilyDoc,
                session.FamilyDoc.FamilyManager,
                args.Mappings,
                context.CancellationToken,
                out var error);
            if (plan is null)
            {
                return Fail(FamilyMappingInvalid, error!);
            }

            return new JobUnitPreview
            {
                Skipped = plan.ChangeCount == 0,
                ExpectedJson = Json(plan.Expected),
                ActualJson = Json(plan.Actual),
                UndoJson = Json(new FamilyMapUndoPlan
                {
                    FamilyName = session.FamilyName,
                    BackupPath = null,
                    Ops = plan.UndoOps
                }),
                Label = session.FamilyName,
                Warnings = plan.Entries.Any(entry => entry.Scope == "instance")
                    ? new[]
                    {
                        "Instance-scope targets become family-type defaults and are not verifiable from the " +
                        "project after load-back."
                    }
                    : Array.Empty<string>()
            };
        }
        finally
        {
            session?.Dispose();
        }
    }

    public override JobUnitApply Apply(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview,
        int attempt,
        Action<string>? persistUndo)
    {
        var args = Parse<MapFamilyUnitArgs>(unit.ArgsJson);
        if (args is null)
        {
            return ApplyFail(
                JobErrorCodes.InvalidSpec,
                "Unit arguments are unreadable. [Action: run the preview again]");
        }

        if (args.Error is not null)
        {
            return ApplyFail(FamilyMappingInvalid, args.Error!);
        }

        if (preview.Skipped)
        {
            return new JobUnitApply
            {
                Success = true,
                Skipped = true,
                ExpectedJson = preview.ExpectedJson,
                UndoJson = preview.UndoJson
            };
        }

        if (!TryResolveFamily(context.Document, unit.UnitKey, preview.UndoJson, out var family) || family is null)
        {
            return ApplyFail(
                JobErrorCodes.ItemFailed,
                "The family '" + args.FamilyName + "' no longer exists in the project. [Action: run the preview " +
                "again]");
        }

        var outcome = new WriteOutcome();
        AgentFamilySession? session = null;
        try
        {
            var familyId = family.Id.GetValue();
            if (!AgentFamilySession.TryCreateBackup(
                    context.Document,
                    family,
                    outcome,
                    out var backupPath,
                    out var sourceBackupPath))
            {
                return ApplyFail(JobErrorCodes.ItemFailed, DescribeOutcome(outcome));
            }

            // The backup round-trip may invalidate the Family wrapper; re-resolve by id.
            var familyForEdit = context.Document.GetElement(ElementIdCompat.Create(familyId)) as RevitFamily;
            if (familyForEdit is null)
            {
                return ApplyFail(
                    JobErrorCodes.ItemFailed,
                    "The family disappeared from the project while the backup was being created; nothing was " +
                    "changed. [Action: run the preview again]");
            }

            session = AgentFamilySession.Open(context.Document, familyForEdit, outcome);
            if (session is null)
            {
                return ApplyFail(JobErrorCodes.ItemFailed, DescribeOutcome(outcome));
            }

            session.SetBackup(backupPath, sourceBackupPath);

            var plan = BuildPlan(
                session.FamilyDoc,
                session.FamilyDoc.FamilyManager,
                args.Mappings,
                context.CancellationToken,
                out var error);
            if (plan is null)
            {
                return ApplyFail(FamilyMappingInvalid, error!);
            }

            if (plan.ChangeCount == 0)
            {
                return new JobUnitApply
                {
                    Success = true,
                    Skipped = true,
                    ExpectedJson = Json(plan.Expected),
                    UndoJson = preview.UndoJson
                };
            }

            var undo = new FamilyMapUndoPlan
            {
                FamilyName = session.FamilyName,
                BackupPath = backupPath,
                Ops = plan.UndoOps
            };

            // J-04: the crash anchor (backup path + old values) must be durable before the first write.
            persistUndo?.Invoke(Json(undo));

            var transaction = new Transaction(session.FamilyDoc, "SmartCon job: map_family_parameters");
            var started = false;
            try
            {
                transaction.Start();
                started = true;

                var applyError = ApplyPlan(session.FamilyDoc.FamilyManager, plan, context.CancellationToken);
                if (applyError is not null)
                {
                    if (transaction.HasStarted())
                        transaction.RollBack();

                    started = false;
                    return ApplyFail(JobErrorCodes.ItemFailed, applyError);
                }

                transaction.Commit();
                started = false;
            }
            catch (OperationCanceledException)
            {
                if (started && transaction.HasStarted())
                    transaction.RollBack();

                throw;
            }
            catch (Exception ex)
            {
                if (started && transaction.HasStarted())
                    transaction.RollBack();

                return ApplyFail(
                    JobErrorCodes.ItemFailed,
                    "The family transaction was rolled back (" + ex.GetType().Name + ": " + ex.Message +
                    "); nothing was changed. [Action: run the preview again]");
            }

            if (!session.TryLoadBack(outcome))
            {
                return ApplyFail(JobErrorCodes.ItemFailed, DescribeOutcome(outcome));
            }

            undo.NewFamilyUniqueId = session.LoadedFamilyUniqueId;
            var finalJson = Json(undo);
            try
            {
                persistUndo?.Invoke(finalJson);
            }
            catch (Exception)
            {
                // The change is already loaded back; the family-name fallback keeps undo working.
            }

            return new JobUnitApply
            {
                Success = true,
                ExpectedJson = Json(plan.Expected),
                UndoJson = finalJson
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return ApplyFail(
                JobErrorCodes.ItemFailed,
                "The family edit failed (" + ex.GetType().Name + ": " + ex.Message +
                "); the family document was closed. [Action: inspect the family and run the preview again]");
        }
        finally
        {
            session?.Dispose();
        }
    }

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

    private static string? ApplyPlan(RevitFamilyManager manager, FamilyMapPlan plan, CancellationToken ct)
    {
        var originalType = manager.CurrentType;
        try
        {
            foreach (var entry in plan.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Mode == "formula")
                {
                    var formulaTarget = FindParameter(manager, entry.Target);
                    if (formulaTarget is null)
                    {
                        return MissingTargetError(entry.TargetName);
                    }

                    manager.SetFormula(formulaTarget, entry.FormulaToApply);
                    continue;
                }

                if (!entry.ClearTargetFormula)
                {
                    continue;
                }

                var clearTarget = FindParameter(manager, entry.Target);
                if (clearTarget is null)
                {
                    return MissingTargetError(entry.TargetName);
                }

                manager.SetFormula(clearTarget, null);
            }

            foreach (var type in AgentFamilyReader.GetTypes(manager))
            {
                ct.ThrowIfCancellationRequested();
                var switched = false;
                foreach (var entry in plan.Entries)
                {
                    if (entry.Mode != "values" || !entry.TypeNames.Contains(type.Name))
                    {
                        continue;
                    }

                    var source = FindParameter(manager, entry.Source);
                    var target = FindParameter(manager, entry.Target);
                    if (source is null || target is null)
                    {
                        return "A mapped parameter disappeared while applying. [Action: run the preview again]";
                    }

                    var sourceValue = FamilyValueSnapshot.ReadFamilyType(type, source);
                    var targetValue = FamilyValueSnapshot.ReadFamilyType(type, target);
                    if (AgentFamilyValues.RawEquals(targetValue, sourceValue))
                    {
                        continue;
                    }

                    if (!switched
                        || manager.CurrentType is null
                        || !string.Equals(manager.CurrentType.Name, type.Name, StringComparison.Ordinal))
                    {
                        manager.CurrentType = type;
                        switched = true;
                    }

                    AgentFamilyValues.Set(manager, target, sourceValue);
                }
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return "Revit refused to apply the mapping (" + ex.GetType().Name + ": " + ex.Message +
                   "). [Action: check the mapping spec and run the preview again]";
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
                    // The original type may have been removed by the operation itself.
                }
            }
        }
    }
}
