using System.Runtime.CompilerServices;
using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Models.Agent;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Family part of undo_last. Family write tools record one change item with
/// <c>ParameterName = "family"</c>, a JSON undo plan in OldValue (operation list
/// plus the backup path) and the family name in NewValue. The plan is reverted in
/// a family-document transaction after the project transaction, because
/// EditFamily/LoadFamily cannot run while the project document is modifiable.
/// Change sets written before the undo plan existed fall back to reloading the
/// backup .rfa (Revit keeps parameters that the reloaded file no longer has, so
/// precise plans are the primary path).
/// </summary>
public sealed partial class UndoLastTool
{
    private static readonly ConditionalWeakTable<WriteOutcome, List<FamilyRestore>> PendingFamilyRestores =
        new();

    internal static bool HasPendingFamilyRestores(WriteOutcome outcome) =>
        PendingFamilyRestores.TryGetValue(outcome, out var list) && list.Count > 0;

    internal static void RegisterFamilyRestore(WriteOutcome outcome, ChangeItem item)
    {
        var list = PendingFamilyRestores.GetOrCreateValue(outcome);
        list.Add(new FamilyRestore
        {
            Payload = item.OldValue,
            FamilyName = item.NewValue
        });
    }

    protected override void AfterTransaction(
        Document document,
        JsonElement args,
        bool dryRun,
        WriteOutcome outcome,
        CancellationToken ct)
    {
        if (!PendingFamilyRestores.TryGetValue(outcome, out var restores) || restores.Count == 0)
            return;

        PendingFamilyRestores.Remove(outcome);

        foreach (var restore in restores)
        {
            ct.ThrowIfCancellationRequested();

            var plan = FamilyUndoPlan.Parse(restore.Payload);
            var report = new WriteItemReport
            {
                Name = restore.FamilyName,
                OldValue = plan?.BackupPath ?? restore.Payload
            };

            if (plan is not null && plan.Ops.Count > 0)
            {
                if (dryRun)
                {
                    report.NewValue = "would revert " + plan.Ops.Count + " family operation(s)";
                    outcome.Batch.Changed.Add(report);
                    continue;
                }

                ApplyPlan(document, plan, outcome, report, ct);
                continue;
            }

            RestoreFromBackup(document, restore, outcome, report, dryRun);
        }

        if (dryRun)
        {
            outcome.AffectedCount = outcome.Batch.Changed.Count;
            outcome.ModelChanged = false;
            return;
        }

        if (outcome.Batch.Failed.Count > 0 && outcome.AffectedCount == 0)
        {
            // Nothing was reverted, so the change set must stay available for a retry.
            outcome.RollBackChangeSetId = null;
        }
    }

    private static void ApplyPlan(
        Document projectDoc,
        FamilyUndoPlan plan,
        WriteOutcome outcome,
        WriteItemReport report,
        CancellationToken ct)
    {
        var family = ResolveFamilyByName(projectDoc, plan.FamilyName);
        if (family is null)
        {
            report.ErrorCode = "family_not_found";
            report.Reason = "The family '" + plan.FamilyName +
                            "' is no longer loaded, so its operations cannot be reverted.";
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            outcome.ErrorCode ??= "family_not_found";
            return;
        }

        Document? familyDoc = null;
        var applied = 0;
        var problems = new List<string>();

        try
        {
            familyDoc = projectDoc.EditFamily(family);
            if (familyDoc is null)
            {
                throw new InvalidOperationException("Revit did not return a family document.");
            }

            var transaction = new Transaction(familyDoc, "SmartCon undo family");
            var started = false;
            try
            {
                transaction.Start();
                started = true;
                var manager = familyDoc.FamilyManager;
                var originalType = manager.CurrentType;

                for (var index = plan.Ops.Count - 1; index >= 0; index--)
                {
                    ct.ThrowIfCancellationRequested();
                    if (TryUndoOperation(manager, plan.Ops[index], out var changed, out var problem))
                    {
                        if (changed)
                            applied++;
                    }
                    else if (!string.IsNullOrEmpty(problem))
                    {
                        problems.Add(problem!);
                    }
                }

                if (originalType is not null)
                {
                    try
                    {
                        manager.CurrentType = originalType;
                    }
                    catch (Exception)
                    {
                        // The original type may have been removed by the undo itself.
                    }
                }

                transaction.Commit();
                started = false;
            }
            catch (Exception)
            {
                if (started && transaction.HasStarted())
                    transaction.RollBack();
                throw;
            }

            var loaded = familyDoc.LoadFamily(projectDoc, new FamilyRestoreLoadOptions());
            if (loaded is null)
                throw new InvalidOperationException(
                    "Revit did not load the reverted family back into the project.");
        }
        catch (Exception ex)
        {
            report.ErrorCode = "family_restore_failed";
            report.Reason = "The family operations could not be reverted: " +
                            ex.GetType().Name + ": " + ex.Message;
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            outcome.ErrorCode ??= "family_restore_failed";
            return;
        }
        finally
        {
            if (familyDoc is not null && familyDoc.IsValidObject)
            {
                try
                {
                    familyDoc.Close(false);
                }
                catch (Exception)
                {
                    // The family document is already closed.
                }
            }
        }

        if (applied > 0)
        {
            report.NewValue = "reverted " + applied + " family operation(s)";
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount += applied;
            outcome.ModelChanged = true;
        }

        if (problems.Count > 0)
        {
            var problemReport = new WriteItemReport
            {
                Name = report.Name,
                ErrorCode = "family_undo_partial",
                Reason = string.Join("; ", problems)
            };
            outcome.Batch.Failed.Add(problemReport);
            outcome.Success = false;
            outcome.ErrorCode ??= "family_undo_partial";
            // Partially applied undo must keep the change set available: the failed
            // operations can still be retried, and the applied ones are recorded here.
            outcome.RollBackChangeSetId = null;
            return;
        }

        if (applied == 0)
        {
            report.ErrorCode = "nothing_to_undo";
            report.Reason = "The family already matches the recorded previous state.";
            outcome.Batch.Skipped.Add(report);
        }
    }

    private static bool TryUndoOperation(
        RevitFamilyManager manager,
        FamilyUndoOperation operation,
        out bool changed,
        out string? problem)
    {
        changed = false;
        problem = null;
        try
        {
            switch (operation.Kind)
            {
                case "add":
                {
                    var parameter = AgentFamilyUndo.FindParameter(
                        manager, operation.Parameter, operation.Scope);
                    if (parameter is null)
                        return true;

                    manager.RemoveParameter(parameter);
                    changed = true;
                    return true;
                }
                case "rename":
                {
                    var parameter = AgentFamilyUndo.FindParameter(
                                      manager, operation.NewName, operation.Scope)
                                  ?? AgentFamilyUndo.FindParameter(
                                      manager, operation.Parameter, operation.Scope);
                    if (parameter is null)
                    {
                        problem = "Parameter '" + (operation.NewName ?? operation.Parameter) +
                                  "' is missing; rename skipped.";
                        return false;
                    }

                    if (string.Equals(
                            parameter.Definition.Name,
                            operation.Parameter,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }

                    manager.RenameParameter(parameter, operation.Parameter!);
                    changed = true;
                    return true;
                }
                case "value":
                {
                    var parameter = AgentFamilyUndo.FindParameter(
                        manager, operation.Parameter, operation.Scope);
                    if (parameter is null)
                    {
                        problem = "Parameter '" + operation.Parameter +
                                  "' is missing; value restore skipped.";
                        return false;
                    }

                    var type = AgentFamilyUndo.FindType(manager, operation.TypeName);
                    if (type is null)
                    {
                        problem = "Type '" + operation.TypeName +
                                  "' is missing; value restore skipped.";
                        return false;
                    }

                    manager.CurrentType = type;
                    if (!AgentFamilyUndo.SetRaw(
                            manager,
                            parameter,
                            operation.Storage,
                            operation.Value))
                    {
                        problem = "Stored value for '" + operation.Parameter +
                                  "' could not be parsed.";
                        return false;
                    }

                    changed = true;
                    return true;
                }
                case "formula":
                {
                    var parameter = AgentFamilyUndo.FindParameter(
                        manager, operation.Parameter, operation.Scope);
                    if (parameter is null)
                    {
                        problem = "Parameter '" + operation.Parameter +
                                  "' is missing; formula restore skipped.";
                        return false;
                    }

                    manager.SetFormula(
                        parameter,
                        string.IsNullOrEmpty(operation.Formula) ? null : operation.Formula);
                    changed = true;
                    return true;
                }
                default:
                    problem = "Unknown undo operation '" + operation.Kind + "'.";
                    return false;
            }
        }
        catch (Exception ex)
        {
            problem = "Operation '" + operation.Kind + "' on '" + operation.Parameter +
                      "' failed: " + ex.Message;
            return false;
        }
    }

    private static void RestoreFromBackup(
        Document document,
        FamilyRestore restore,
        WriteOutcome outcome,
        WriteItemReport report,
        bool dryRun)
    {
        var backupPath = report.OldValue;
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath))
        {
            report.ErrorCode = "backup_unavailable";
            report.Reason =
                "Neither an undo plan nor a family backup is available for '" +
                restore.FamilyName + "'.";
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            outcome.ErrorCode ??= "backup_unavailable";
            return;
        }

        if (!string.Equals(
                Path.GetExtension(backupPath),
                ".rfa",
                StringComparison.OrdinalIgnoreCase))
        {
            report.ErrorCode = "backup_unavailable";
            report.Reason = "The recorded backup path is not a .rfa family file.";
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            outcome.ErrorCode ??= "backup_unavailable";
            return;
        }

        if (dryRun)
        {
            report.NewValue = "would restore from backup";
            outcome.Batch.Changed.Add(report);
            return;
        }

        try
        {
            var backupDoc = document.Application.OpenDocumentFile(backupPath);
            RevitFamily? loaded;
            try
            {
                var backupFamilyName = backupDoc.OwnerFamily?.Name;
                if (!string.IsNullOrEmpty(backupFamilyName)
                    && !string.Equals(
                        backupFamilyName,
                        restore.FamilyName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The backup contains family '" + backupFamilyName + "', not '" +
                        restore.FamilyName + "'.");
                }

                loaded = backupDoc.LoadFamily(document, new FamilyRestoreLoadOptions());
            }
            finally
            {
                backupDoc.Close(false);
            }

            if (loaded is null)
                throw new InvalidOperationException("Revit did not load the family from the backup.");

            report.NewValue = "restored from backup";
            outcome.Batch.Changed.Add(report);
            outcome.AffectedCount++;
            outcome.ModelChanged = true;
        }
        catch (Exception ex)
        {
            report.ErrorCode = "family_restore_failed";
            report.Reason = "The family could not be restored from the backup: " +
                            ex.GetType().Name + ": " + ex.Message;
            outcome.Batch.Failed.Add(report);
            outcome.Success = false;
            outcome.ErrorCode ??= "family_restore_failed";
        }
    }

    private static RevitFamily? ResolveFamilyByName(Document document, string? familyName)
    {
        if (string.IsNullOrWhiteSpace(familyName))
            return null;

        var collector = new FilteredElementCollector(document).OfClass(typeof(RevitFamily));
        foreach (var element in collector)
        {
            if (element is RevitFamily family &&
                string.Equals(family.Name, familyName, StringComparison.OrdinalIgnoreCase))
            {
                return family;
            }
        }

        return null;
    }

    private sealed class FamilyRestore
    {
        public string? Payload { get; init; }

        public string? FamilyName { get; init; }
    }

    private sealed class FamilyRestoreLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(
            RevitFamily sharedFamily,
            bool familyInUse,
            out FamilySource source,
            out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }
}