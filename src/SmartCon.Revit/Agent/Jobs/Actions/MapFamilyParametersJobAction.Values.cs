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

            // Surfaced in the unit result: createTarget definitions are written to the
            // shared parameter file, which the job undo cannot revert.
            var applyWarnings = new List<string>();
            using var transaction = new Transaction(session.FamilyDoc, "SmartCon job: map_family_parameters");
            var started = false;
            try
            {
                transaction.Start();
                started = true;

                var applyError = ApplyPlan(
                    session.FamilyDoc.FamilyManager,
                    context.Document,
                    plan,
                    applyWarnings,
                    context.CancellationToken);
                if (applyError is not null)
                {
                    if (transaction.HasStarted())
                        transaction.RollBack();

                    started = false;
                    return ApplyFail(applyError.Code, applyError.Detail);
                }

                var commitStatus = transaction.Commit();
                started = false;
                if (commitStatus != TransactionStatus.Committed)
                {
                    return ApplyFail(
                        JobErrorCodes.ItemFailed,
                        "Revit rolled back the family transaction (status " + commitStatus +
                        "); nothing was changed [Action: inspect the family and run the preview again]");
                }
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
                UndoJson = finalJson,
                Warnings = applyWarnings
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

    private static PlanApplyError? ApplyPlan(
        RevitFamilyManager manager,
        Document projectDoc,
        FamilyMapPlan plan,
        List<string> warnings,
        CancellationToken ct)
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
                        return ItemFailed(MissingTargetError(entry.TargetName));
                    }

                    manager.SetFormula(formulaTarget, entry.FormulaToApply);
                    continue;
                }

                if (entry.CreateTarget && FindParameter(manager, entry.Target) is null)
                {
                    var createError = EnsureSharedTargetParameter(manager, projectDoc, entry, warnings);
                    if (createError is not null)
                    {
                        return createError;
                    }
                }

                if (!entry.ClearTargetFormula)
                {
                    continue;
                }

                var clearTarget = FindParameter(manager, entry.Target);
                if (clearTarget is null)
                {
                    return ItemFailed(MissingTargetError(entry.TargetName));
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
                        return ItemFailed(
                            "A mapped parameter disappeared while applying. [Action: run the preview again]");
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
            return ItemFailed(
                "Revit refused to apply the mapping (" + ex.GetType().Name + ": " + ex.Message +
                "). [Action: check the mapping spec and run the preview again]");
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
