using System.Globalization;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Read-back and rollback half of the family job action. Verification reads the
/// project-side family symbols (no family document is reopened); rollback restores
/// the recorded per-type values, with the pre-edit .rfa as the fallback.
/// </summary>
public sealed partial class MapFamilyParametersJobAction
{
    public override JobUnitVerification Verify(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview)
    {
        var expected = Parse<FamilyMapState>(preview.ExpectedJson);
        if (expected is null)
        {
            return new JobUnitVerification
            {
                Verified = false,
                MismatchReason = "The preview carries no expected state. [Action: run the preview again]"
            };
        }

        if (!TryResolveFamily(context.Document, unit.UnitKey, preview.UndoJson, out var family) || family is null)
        {
            return new JobUnitVerification
            {
                Verified = false,
                ErrorCode = JobErrorCodes.ItemFailed,
                ErrorDetail = "The family could not be resolved after load-back. [Action: restore it from the " +
                              "backup and run the preview again]",
                MismatchReason = "The family no longer exists in the project"
            };
        }

        var symbols = CollectSymbols(context.Document, family);
        var actual = new FamilyMapState();
        var mismatches = new List<string>();
        var checkedTargets = 0;
        var skippedUnverifiable = 0;

        foreach (var typeState in expected.Types)
        {
            var actualType = new FamilyMapTypeState { TypeName = typeState.TypeName };
            actual.Types.Add(actualType);

            if (!symbols.TryGetValue(typeState.TypeName, out var symbol))
            {
                mismatches.Add("type '" + typeState.TypeName + "' is missing from the project");
                continue;
            }

            foreach (var value in typeState.Targets)
            {
                // A project FamilySymbol carries type parameters only, and element
                // references get new project-side ids on load-back, so only type
                // values with a stable storage can be read back from the project.
                if (string.Equals(value.Scope, "instance", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value.Storage, "elementId", StringComparison.OrdinalIgnoreCase))
                {
                    skippedUnverifiable++;
                    continue;
                }

                var parameter = symbol.LookupParameter(value.Name);
                if (parameter is null)
                {
                    mismatches.Add("target '" + value.Name + "' is missing on type '" + typeState.TypeName + "'");
                    continue;
                }

                var current = FamilyValueSnapshot.ReadProjectParameter(parameter).ToRaw();
                actualType.Targets.Add(new FamilyMapValueState
                {
                    Name = value.Name,
                    Scope = value.Scope,
                    Storage = value.Storage,
                    Value = current
                });

                checkedTargets++;

                if (!ValuesEqual(value.Value, current, value.Storage))
                {
                    mismatches.Add("target '" + value.Name + "' of type '" + typeState.TypeName + "' is '" +
                                   current + "' instead of '" + value.Value + "'");
                }
            }
        }

        if (checkedTargets == 0)
        {
            if (skippedUnverifiable > 0)
            {
                // #432 (B.6): every target of this unit lives in the family document
                // only (instance scope / element references). Nothing checkable
                // failed, so this is "unknown", not "wrong" — the unit is done with
                // a not-verifiable count instead of a mismatch.
                return new JobUnitVerification
                {
                    Verified = true,
                    NotVerifiable = skippedUnverifiable,
                    ActualJson = Json(actual)
                };
            }

            // Fail closed: a unit with no checkable target and nothing skipped had
            // an empty expected state — that is never silently "done".
            return new JobUnitVerification
            {
                Verified = false,
                ActualJson = Json(actual),
                MismatchReason =
                    "No target can be verified from the project side (instance scope or elementId storage)" +
                    " [Action: verify these values in the Family Editor, or map type-scope targets only]"
            };
        }

        return new JobUnitVerification
        {
            Verified = mismatches.Count == 0,
            NotVerifiable = skippedUnverifiable,
            ActualJson = Json(actual),
            MismatchReason = mismatches.Count == 0 ? null : string.Join("; ", mismatches)
        };
    }

    public override JobUnitUndo Undo(
        JobActionContext context,
        JobUnit unit,
        string? undoJson,
        bool dryRun)
    {
        var plan = Parse<FamilyMapUndoPlan>(undoJson);
        if (plan is null || (plan.Ops.Count == 0 && !HasBackup(plan)))
        {
            var recordedBackup = plan?.BackupPath;
            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackUnavailable,
                ErrorDetail = "The unit records neither family operations nor an available .rfa backup" +
                              (string.IsNullOrWhiteSpace(recordedBackup)
                                  ? string.Empty
                                  : " (recorded backup: " + recordedBackup + ")") +
                              ". [Action: restore the family from the project backup or edit it manually]"
            };
        }

        if (plan.Ops.Count == 0)
        {
            return RestoreFromBackup(context, plan, dryRun);
        }

        if (!TryResolveFamily(context.Document, unit.UnitKey, undoJson, out var family) || family is null)
        {
            if (HasBackup(plan))
            {
                return RestoreFromBackup(context, plan, dryRun);
            }

            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackFailed,
                ErrorDetail = "The family '" + plan.FamilyName + "' is no longer loaded, so the recorded values " +
                              "cannot be restored and no backup is available. [Action: load the family again " +
                              "from the project or edit it manually]"
            };
        }

        if (dryRun)
        {
            return new JobUnitUndo
            {
                Success = true,
                ActualJson = Value(
                    "would restore " + plan.Ops.Count + " value(s) in family '" +
                    (plan.FamilyName ?? family.Name) + "'" +
                    (HasBackup(plan) ? "; backup available: " + Path.GetFileName(plan.BackupPath!) : string.Empty))
            };
        }

        Document? familyDoc = null;
        var restored = 0;
        var problems = new List<string>();
        try
        {
            try
            {
                familyDoc = context.Document.EditFamily(family);
            }
            catch (Exception ex)
            {
                problems.Add("the family document could not be opened (" + ex.GetType().Name + ": " +
                             ex.Message + ")");
            }

            if (familyDoc is not null)
            {
                using var transaction = new Transaction(familyDoc, "SmartCon job undo: map_family_parameters");
                var started = false;
                try
                {
                    transaction.Start();
                    started = true;

                    var manager = familyDoc.FamilyManager;
                    var originalType = manager.CurrentType;
                    try
                    {
                        for (var index = plan.Ops.Count - 1; index >= 0; index--)
                        {
                            context.CancellationToken.ThrowIfCancellationRequested();
                            if (TryUndoOperation(manager, plan.Ops[index], out var changed, out var problem))
                            {
                                if (changed)
                                {
                                    restored++;
                                }
                            }
                            else if (!string.IsNullOrEmpty(problem))
                            {
                                problems.Add(problem!);
                            }
                        }
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
                                // The original type may have been removed by the undo itself.
                            }
                        }
                    }

                    var commitStatus = transaction.Commit();
                    started = false;
                    if (commitStatus != TransactionStatus.Committed)
                    {
                        return new JobUnitUndo
                        {
                            ErrorCode = JobErrorCodes.RollbackFailed,
                            ErrorDetail = "Revit rolled back the family undo transaction (status " +
                                          commitStatus + "); nothing was restored"
                        };
                    }
                }
                catch (OperationCanceledException)
                {
                    if (started && transaction.HasStarted())
                    {
                        transaction.RollBack();
                    }

                    throw;
                }
                catch (Exception ex)
                {
                    if (started && transaction.HasStarted())
                    {
                        transaction.RollBack();
                    }

                    restored = 0;
                    problems.Add("the family transaction was rolled back (" + ex.GetType().Name + ": " +
                                 ex.Message + ")");
                }

                if (restored > 0)
                {
                    var loaded = familyDoc.LoadFamily(context.Document, new FamilyMapLoadOptions());
                    if (loaded is null)
                    {
                        problems.Add("Revit did not load the reverted family back into the project");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            problems.Add("the family restore failed (" + ex.GetType().Name + ": " + ex.Message + ")");
        }
        finally
        {
            if (familyDoc is not null && familyDoc.IsValidObject)
                AgentFamilyDocCloser.Close(familyDoc);
        }

        if (problems.Count > 0)
        {
            if (restored == 0 && HasBackup(plan))
            {
                return RestoreFromBackup(context, plan, dryRun);
            }

            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackFailed,
                ErrorDetail = "The family restore was partial: " + string.Join("; ", problems) +
                              ". [Action: restore the family from the backup .rfa at " +
                              (plan.BackupPath ?? "(unavailable)") + " or edit it manually]",
                ActualJson = Value(restored + " value(s) restored")
            };
        }

        if (restored == 0)
        {
            return new JobUnitUndo
            {
                Success = true,
                Skipped = true,
                ActualJson = Value("family '" + (plan.FamilyName ?? family.Name) +
                                   "' already matches the recorded state")
            };
        }

        return new JobUnitUndo
        {
            Success = true,
            ActualJson = Value(restored + " value(s) restored in family '" +
                               (plan.FamilyName ?? family.Name) + "'")
        };
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
                    {
                        // Idempotent: the parameter is already gone — the recorded
                        // state is restored, nothing to remove.
                        return true;
                    }

                    manager.RemoveParameter(parameter);
                    changed = true;
                    return true;
                }
                case "value":
                {
                    var parameter = AgentFamilyUndo.FindParameter(
                        manager, operation.Parameter, operation.Scope);
                    if (parameter is null)
                    {
                        problem = "parameter '" + operation.Parameter + "' is missing; value restore skipped";
                        return false;
                    }

                    var type = AgentFamilyUndo.FindType(manager, operation.TypeName);
                    if (type is null)
                    {
                        problem = "type '" + operation.TypeName + "' is missing; value restore skipped";
                        return false;
                    }

                    manager.CurrentType = type;
                    if (!AgentFamilyUndo.SetRaw(manager, parameter, operation.Storage, operation.Value))
                    {
                        problem = "the stored value for '" + operation.Parameter + "' could not be parsed";
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
                        problem = "parameter '" + operation.Parameter + "' is missing; formula restore skipped";
                        return false;
                    }

                    manager.SetFormula(
                        parameter,
                        string.IsNullOrEmpty(operation.Formula) ? null : operation.Formula);
                    changed = true;
                    return true;
                }
                default:
                    problem = "unknown undo operation '" + operation.Kind + "'";
                    return false;
            }
        }
        catch (Exception ex)
        {
            problem = "operation '" + operation.Kind + "' on '" + operation.Parameter + "' failed: " + ex.Message;
            return false;
        }
    }

    private static JobUnitUndo RestoreFromBackup(
        JobActionContext context,
        FamilyMapUndoPlan plan,
        bool dryRun)
    {
        var backupPath = plan.BackupPath;
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath!))
        {
            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackUnavailable,
                ErrorDetail = "The family backup '" + (backupPath ?? "(none)") + "' does not exist. [Action: " +
                              "restore the family from the project backup or edit it manually]"
            };
        }

        if (dryRun)
        {
            return new JobUnitUndo
            {
                Success = true,
                ActualJson = Value("would restore family '" + (plan.FamilyName ?? "(unknown)") +
                                   "' from " + Path.GetFileName(backupPath!))
            };
        }

        try
        {
            var backupDoc = context.Document.Application.OpenDocumentFile(backupPath!);
            RevitFamily? loaded;
            try
            {
                var backupFamilyName = backupDoc.OwnerFamily?.Name;
                if (!string.IsNullOrEmpty(backupFamilyName)
                    && !string.IsNullOrEmpty(plan.FamilyName)
                    && !string.Equals(backupFamilyName, plan.FamilyName, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "The backup contains family '" + backupFamilyName + "', not '" + plan.FamilyName + "'.");
                }

                // #209: a freshly opened document has no edits, so Revit can decide
                // "the family has not changed" from the dirty stamp and silently
                // no-op the reload. The net-zero poke flips the stamp first.
                AgentFamilyReloadPoke.TryPokeLocal(
                    backupDoc,
                    plan.FamilyName ?? backupFamilyName ?? "family");
                loaded = backupDoc.LoadFamily(context.Document, new FamilyMapLoadOptions());
            }
            finally
            {
                AgentFamilyDocCloser.Close(backupDoc);
            }

            if (loaded is null)
            {
                throw new InvalidOperationException("Revit did not load the family from the backup.");
            }

            if (!string.IsNullOrEmpty(plan.FamilyName)
                && !string.Equals(loaded.Name, plan.FamilyName, StringComparison.OrdinalIgnoreCase))
            {
                // A backup saved under another file name loads as a NEW family named
                // after the file while the replaced one stays — never a restore (#430).
                throw new InvalidOperationException(
                    "Revit loaded the backup as a new family '" + loaded.Name + "' instead of replacing '" +
                    plan.FamilyName + "'.");
            }

            return new JobUnitUndo
            {
                Success = true,
                ActualJson = Value("family '" + (plan.FamilyName ?? loaded.Name) + "' restored from the backup")
            };
        }
        catch (Exception ex)
        {
            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackFailed,
                ErrorDetail = "The family could not be restored from the backup: " + ex.GetType().Name + ": " +
                              ex.Message + " [Action: restore the family manually from " +
                              Path.GetFileName(backupPath!) + "]"
            };
        }
    }

    private static Dictionary<string, FamilySymbol> CollectSymbols(Document document, RevitFamily family)
    {
        var symbols = new Dictionary<string, FamilySymbol>(StringComparer.OrdinalIgnoreCase);
        foreach (var typeId in family.GetFamilySymbolIds())
        {
            if (document.GetElement(typeId) is FamilySymbol symbol && !symbols.ContainsKey(symbol.Name))
            {
                symbols.Add(symbol.Name, symbol);
            }
        }

        return symbols;
    }

    private static bool ValuesEqual(string expected, string actual, string storage)
    {
        if (string.Equals(expected, actual, StringComparison.Ordinal))
        {
            return true;
        }

        if (!string.Equals(storage, "double", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!double.TryParse(expected, NumberStyles.Float, CultureInfo.InvariantCulture, out var left)
            || !double.TryParse(actual, NumberStyles.Float, CultureInfo.InvariantCulture, out var right))
        {
            return false;
        }

        var tolerance = Math.Max(1e-9, Math.Abs(left) * 1e-9);
        return Math.Abs(left - right) <= tolerance;
    }

    private static bool HasBackup(FamilyMapUndoPlan plan) =>
        !string.IsNullOrWhiteSpace(plan.BackupPath) && File.Exists(plan.BackupPath);

    private sealed class FamilyMapLoadOptions : IFamilyLoadOptions
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
