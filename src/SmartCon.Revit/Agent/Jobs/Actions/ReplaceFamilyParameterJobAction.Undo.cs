using System.Globalization;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Read-back and rollback half of the replace action. Verification reads the
/// project-side family symbols: the target parameter must exist on every type
/// with the per-type values captured at preview time (ReplaceParameter keeps
/// them). Rollback restores the pre-edit .rfa backup — a rebind cannot be
/// replayed value-by-value, so the backup is the only honest undo path.
/// </summary>
public sealed partial class ReplaceFamilyParameterJobAction
{
    public override JobUnitVerification Verify(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview)
    {
        var expected = Parse<List<FamilyReplaceEntryState>>(preview.ExpectedJson);
        if (expected is null || expected.Count == 0)
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
        var actual = new List<FamilyReplaceActualState>();
        var mismatches = new List<string>();
        var checkedTargets = 0;

        foreach (var entry in expected)
        {
            if (entry.Source is null || entry.Target is null)
            {
                continue;
            }

            // A project FamilySymbol carries type parameters only; instance-scope
            // rebinds are family-type defaults no project element proves.
            if (string.Equals(entry.Source.Scope, "instance", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var typeState in entry.Types)
            {
                if (string.Equals(typeState.Storage, "elementId", StringComparison.OrdinalIgnoreCase))
                {
                    // Element references get new project-side ids on load-back.
                    continue;
                }

                if (!symbols.TryGetValue(typeState.TypeName, out var symbol))
                {
                    mismatches.Add("type '" + typeState.TypeName + "' is missing from the project");
                    continue;
                }

                var parameter = symbol.LookupParameter(entry.Target.Name);
                if (parameter is null)
                {
                    mismatches.Add("target '" + entry.Target.Name + "' is missing on type '" + typeState.TypeName + "'");
                    continue;
                }

                var current = FamilyValueSnapshot.ReadProjectParameter(parameter).ToRaw();
                actual.Add(new FamilyReplaceActualState
                {
                    Name = entry.Target.Name + "@" + typeState.TypeName,
                    Scope = "type",
                    IsShared = parameter.IsShared
                });

                checkedTargets++;

                if (!ValuesEqual(typeState.Value, current, typeState.Storage))
                {
                    mismatches.Add("target '" + entry.Target.Name + "' of type '" + typeState.TypeName +
                                   "' is '" + current + "' instead of '" + typeState.Value + "'");
                }
            }
        }

        if (checkedTargets == 0)
        {
            // Fail closed: a unit whose targets cannot be read back from the project
            // side (instance scope / element references) is never silently "done".
            return new JobUnitVerification
            {
                Verified = false,
                ActualJson = Json(actual),
                MismatchReason =
                    "No target can be verified from the project side (instance scope or elementId storage)" +
                    " [Action: verify the family in the Family Editor, or replace type-scope parameters only]"
            };
        }

        return new JobUnitVerification
        {
            Verified = mismatches.Count == 0,
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
        var plan = Parse<FamilyReplaceUndoPlan>(undoJson);
        if (plan is null || (plan.Ops.Count == 0 && !HasBackup(plan)))
        {
            var recordedBackup = plan?.BackupPath;
            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackUnavailable,
                ErrorDetail = "The unit records no family operations and no available .rfa backup" +
                              (string.IsNullOrWhiteSpace(recordedBackup)
                                  ? string.Empty
                                  : " (recorded backup: " + recordedBackup + ")") +
                              ". [Action: restore the family from the project backup or edit it manually]"
            };
        }

        if (plan.Ops.Count > 0)
        {
            // The replace action records no value ops; this branch is a future-proof
            // guard so an unexpected payload never silently no-ops.
            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackUnavailable,
                ErrorDetail = "The unit records " + plan.Ops.Count + " operation(s) this action cannot replay. " +
                              "[Action: restore the family from the backup .rfa at " +
                              (plan.BackupPath ?? "(unavailable)") + "]"
            };
        }

        return RestoreFromBackup(context, plan, dryRun);
    }

    private static JobUnitUndo RestoreFromBackup(
        JobActionContext context,
        FamilyReplaceUndoPlan plan,
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
                loaded = backupDoc.LoadFamily(context.Document, new FamilyReplaceLoadOptions());
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
                // after the file while the replaced one stays — never a restore.
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

    private static bool HasBackup(FamilyReplaceUndoPlan plan) =>
        !string.IsNullOrWhiteSpace(plan.BackupPath) && File.Exists(plan.BackupPath);

    private sealed class FamilyReplaceLoadOptions : IFamilyLoadOptions
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
