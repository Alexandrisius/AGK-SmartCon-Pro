using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Read-back and rollback half of the add action. Verification reads the
/// project-side family symbols: a type parameter must exist on every type of
/// the loaded family; instance-scope additions are family-type defaults no
/// project element proves, so they are marked unverifiable instead of silently
/// green. Rollback restores the pre-edit .rfa backup — a reload changes family
/// ids, so the backup is the only honest undo path.
/// </summary>
public sealed partial class AddFamilyParametersJobAction
{
    public override JobUnitVerification Verify(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview)
    {
        var expected = Parse<List<AddFamilyEntryState>>(preview.ExpectedJson);
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
        var mismatches = new List<string>();
        var checkedParameters = 0;
        var skippedUnverifiable = 0;

        foreach (var entry in expected)
        {
            // A project FamilySymbol carries type parameters only; instance-scope
            // additions are family-type defaults no project element proves.
            if (string.Equals(entry.Scope, "instance", StringComparison.OrdinalIgnoreCase))
            {
                skippedUnverifiable++;
                continue;
            }

            var missingOn = new List<string>();
            foreach (var pair in symbols)
            {
                var hasName = false;
                foreach (Parameter parameter in pair.Value.Parameters)
                {
                    if (string.Equals(
                            parameter.Definition?.Name,
                            entry.Name,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        hasName = true;
                        break;
                    }
                }

                if (!hasName)
                    missingOn.Add(pair.Key);
            }

            if (missingOn.Count > 0)
            {
                mismatches.Add(
                    "'" + entry.Name + "' is missing on " + missingOn.Count + " of " + symbols.Count +
                    " type(s)");
            }
            else
            {
                checkedParameters++;
            }
        }

        if (mismatches.Count > 0)
        {
            return new JobUnitVerification
            {
                Verified = false,
                MismatchReason = string.Join("; ", mismatches) +
                                 ". [Action: inspect the family with get_family_parameters]"
            };
        }

        return new JobUnitVerification
        {
            Verified = true,
            NotVerifiable = skippedUnverifiable,
            ActualJson = Value(checkedParameters + " type parameter(s) verified on every type" +
                            (skippedUnverifiable > 0
                                ? "; " + skippedUnverifiable + " instance parameter(s) not project-verifiable"
                                : string.Empty))
        };
    }

    public override JobUnitUndo Undo(
        JobActionContext context,
        JobUnit unit,
        string? undoJson,
        bool dryRun)
    {
        var plan = Parse<AddFamilyUndoPlan>(undoJson);
        var backupPath = plan?.BackupPath;
        if (string.IsNullOrWhiteSpace(backupPath) || !File.Exists(backupPath!))
        {
            return new JobUnitUndo
            {
                ErrorCode = JobErrorCodes.RollbackUnavailable,
                ErrorDetail = "The unit records no available .rfa backup" +
                              (string.IsNullOrWhiteSpace(backupPath)
                                  ? string.Empty
                                  : " (recorded backup: " + backupPath + ")") +
                              ". [Action: restore the family from the project backup or edit it manually]"
            };
        }

        if (dryRun)
        {
            return new JobUnitUndo
            {
                Success = true,
                ActualJson = Value("would restore family '" + (plan!.FamilyName ?? "(unknown)") +
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
                    && !string.IsNullOrEmpty(plan!.FamilyName)
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
                    plan!.FamilyName ?? backupFamilyName ?? "family");
                loaded = backupDoc.LoadFamily(context.Document, new AddFamilyLoadOptions());
            }
            finally
            {
                AgentFamilyDocCloser.Close(backupDoc);
            }

            if (loaded is null)
            {
                throw new InvalidOperationException("Revit did not load the family from the backup.");
            }

            if (!string.IsNullOrEmpty(plan!.FamilyName)
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
                symbols.Add(symbol.Name, symbol);
        }

        return symbols;
    }

    private sealed class AddFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(
            bool familyInUse,
            out bool overwriteParameterValues)
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
