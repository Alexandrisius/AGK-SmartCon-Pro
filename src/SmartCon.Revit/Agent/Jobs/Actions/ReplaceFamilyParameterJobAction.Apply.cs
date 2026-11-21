using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Preview and apply half of the replace action. The preview stays read-only (no
/// backup); the apply backs the .rfa up first, re-derives the plan (preview-stale
/// guard), persists the undo anchor before the first write (J-04) and runs the
/// two-step replace inside one family-document transaction: a direct
/// ReplaceParameter onto a same-named definition throws, so the parameter is
/// first detached onto a temporary family parameter, then rebound to the target
/// shared definition — values, formulas and labels survive both steps.
/// </summary>
public sealed partial class ReplaceFamilyParameterJobAction
{
    public override JobUnitPreview Preview(JobActionContext context, JobUnit unit)
    {
        var args = Parse<ReplaceFamilyUnitArgs>(unit.ArgsJson);
        if (args is null)
        {
            return Fail(JobErrorCodes.InvalidSpec, "Unit arguments are unreadable. [Action: run the preview again]");
        }

        if (args.Error is not null)
        {
            return Fail(FamilyReplaceInvalid, args.Error!);
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
                context.Document,
                session.FamilyDoc.FamilyManager,
                args.Replacements,
                context.CancellationToken,
                out var error);
            if (plan is null)
            {
                return Fail(FamilyReplaceInvalid, error!);
            }

            return new JobUnitPreview
            {
                Skipped = plan.ChangeCount == 0,
                ExpectedJson = Json(plan.Expected),
                ActualJson = Json(plan.Actual),
                UndoJson = Json(new FamilyReplaceUndoPlan
                {
                    FamilyName = session.FamilyName,
                    BackupPath = null,
                    Ops = new List<FamilyUndoOperation>()
                }),
                Label = session.FamilyName,
                Warnings = plan.Warnings
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
        var args = Parse<ReplaceFamilyUnitArgs>(unit.ArgsJson);
        if (args is null)
        {
            return ApplyFail(
                JobErrorCodes.InvalidSpec,
                "Unit arguments are unreadable. [Action: run the preview again]");
        }

        if (args.Error is not null)
        {
            return ApplyFail(FamilyReplaceInvalid, args.Error!);
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
                context.Document,
                session.FamilyDoc.FamilyManager,
                args.Replacements,
                context.CancellationToken,
                out var error);
            if (plan is null)
            {
                return ApplyFail(FamilyReplaceInvalid, error!);
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

            var undo = new FamilyReplaceUndoPlan
            {
                FamilyName = session.FamilyName,
                BackupPath = backupPath,
                Ops = new List<FamilyUndoOperation>()
            };

            // J-04: the crash anchor (backup path) must be durable before the first write.
            persistUndo?.Invoke(Json(undo));

            using var transaction = new Transaction(session.FamilyDoc, "SmartCon job: replace_family_parameter");
            var started = false;
            try
            {
                transaction.Start();
                started = true;

                var applyError = ApplyReplacements(
                    session.FamilyDoc.FamilyManager,
                    plan,
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
                    "replace_failed",
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
                Warnings = plan.Warnings
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

    private static PlanApplyError? ApplyReplacements(
        RevitFamilyManager manager,
        FamilyReplacePlan plan,
        CancellationToken ct)
    {
        var currentName = "(unknown)";
        try
        {
            foreach (var entry in plan.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.AlreadyOnTarget)
                {
                    continue;
                }

                currentName = entry.SourceName;
                var source = AgentFamilyReader.FindParameter(
                    manager,
                    entry.Source.Name,
                    entry.Source.SharedParameterGuid,
                    null,
                    out _,
                    out _);
                if (source is null)
                {
                    return ItemFailed(
                        "The source parameter '" + entry.SourceName + "' disappeared while applying. " +
                        "[Action: run the preview again]");
                }

                if (AgentFamilyReader.IsBuiltIn(source))
                {
                    return ItemFailed(
                        "The parameter '" + entry.SourceName + "' became a built-in parameter while applying. " +
                        "[Action: run the preview again]");
                }

                var replaceError = ReplaceOne(manager, entry, source);
                if (replaceError is not null)
                {
                    return replaceError;
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
            return new PlanApplyError(
                "replace_failed",
                "Revit refused the replacement of '" + currentName + "': " + ex.GetType().Name + ": " +
                ex.Message + ". [Action: check the target definition (group/spec) and run the preview again]");
        }
    }

    /// <summary>
    /// Direct rebind when the names differ. A same-name target definition collides
    /// with the existing parameter name, so the collision is broken first: a shared
    /// source is detached onto a temporary family parameter (ReplaceParameter with
    /// a name), a family source is simply renamed — ReplaceParameter with a name
    /// refuses family sources ("use RenameParameter() instead").
    /// </summary>
    private static PlanApplyError? ReplaceOne(
        RevitFamilyManager manager,
        ReplacePlanEntry entry,
        FamilyParameter source)
    {
        var sameName = string.Equals(
            source.Definition.Name,
            entry.TargetDefinition.Name,
            StringComparison.OrdinalIgnoreCase);
        if (!sameName)
        {
            manager.ReplaceParameter(source, entry.TargetDefinition, entry.GroupTypeId, entry.IsInstance);
            return null;
        }

        var temporaryName = BuildTemporaryName(manager, entry);
        FamilyParameter? temporary;
        if (source.IsShared)
        {
            temporary = manager.ReplaceParameter(
                source,
                temporaryName,
                entry.GroupTypeId,
                entry.IsInstance);
        }
        else
        {
            // RenameParameter is void and throws on conflicts; the surrounding
            // try/catch turns a refusal into an honest replace_failed.
            manager.RenameParameter(source, temporaryName);
            temporary = FindByExactName(manager, temporaryName, entry.IsInstance);
            if (temporary is null)
            {
                return ItemFailed(
                    "The temporary parameter '" + temporaryName + "' disappeared while applying. " +
                    "[Action: run the preview again]");
            }
        }

        manager.ReplaceParameter(
            temporary!,
            entry.TargetDefinition,
            entry.GroupTypeId,
            entry.IsInstance);
        return null;
    }

    private static FamilyParameter? FindByExactName(
        RevitFamilyManager manager,
        string name,
        bool isInstance)
    {
        foreach (var parameter in manager.GetParameters())
        {
            if (parameter.IsInstance == isInstance
                && string.Equals(parameter.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return parameter;
            }
        }

        return null;
    }

    /// <summary>
    /// A unique, character-safe temporary name for the detach step: Revit rejects
    /// ReplaceParameter onto a definition whose name duplicates an existing
    /// parameter, so the rebind goes through a scratch family parameter first.
    /// </summary>
    private static string BuildTemporaryName(RevitFamilyManager manager, ReplacePlanEntry entry)
    {
        var guidStamp = entry.TargetGuid.ToString("N").Substring(0, 8);
        var baseName = new string(
            entry.SourceName.Where(char.IsLetterOrDigit).ToArray());
        if (baseName.Length > 60)
        {
            baseName = baseName.Substring(0, 60);
        }

        var prefix = "__SmartConMig_" + guidStamp + "_";
        var candidate = prefix + baseName;
        var suffix = 2;
        while (NameExists(manager, candidate))
        {
            candidate = prefix + baseName + "_" + suffix;
            suffix++;
        }

        return candidate;
    }

    private static bool NameExists(RevitFamilyManager manager, string name)
    {
        foreach (var parameter in manager.GetParameters())
        {
            if (string.Equals(parameter.Definition.Name, name, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private sealed record PlanApplyError(string Code, string Detail);

    private static PlanApplyError ItemFailed(string detail) =>
        new(JobErrorCodes.ItemFailed, detail);
}
