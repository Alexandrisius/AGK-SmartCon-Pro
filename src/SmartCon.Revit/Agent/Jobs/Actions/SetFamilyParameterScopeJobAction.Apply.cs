using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Preview, apply, verify and rollback halves of the scope action. The preview
/// is read-only; the apply backs the .rfa up first, persists the undo anchor
/// before the first write (J-04) and runs MakeType/MakeInstance inside one
/// family-document transaction per family. Rollback restores the pre-edit
/// backup.
/// </summary>
public sealed partial class SetFamilyParameterScopeJobAction
{
    public override JobUnitPreview Preview(JobActionContext context, JobUnit unit)
    {
        var args = Parse<SetScopeUnitArgs>(unit.ArgsJson);
        if (args is null)
        {
            return Fail(JobErrorCodes.InvalidSpec, "Unit arguments are unreadable. [Action: run the preview again]");
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
                return Fail(FamilyJobOutcome.CodeOf(outcome), FamilyJobOutcome.Describe(outcome));
            }

            var parameter = AgentFamilyReader.FindParameter(
                session.FamilyDoc.FamilyManager,
                args.ParameterRef!.Name,
                args.ParameterRef.SharedParameterGuid,
                null,
                out _,
                out _);
            if (parameter is null)
            {
                return Fail(
                    FamilyScopeInvalid,
                    "The family '" + session.FamilyName + "' has no parameter matching '" +
                    (args.ParameterRef.Name ?? args.ParameterRef.SharedParameterGuid) + "'. [Action: check " +
                    "get_family_parameters; scope=family families only — a project binding is not a family " +
                    "parameter]");
            }

            var current = parameter.IsInstance ? "instance" : "type";
            var target = args.IsInstance ? "instance" : "type";
            return new JobUnitPreview
            {
                Skipped = string.Equals(current, target, StringComparison.Ordinal),
                ExpectedJson = Json(new SetScopeEntryState
                {
                    Name = parameter.Definition.Name,
                    Scope = target
                }),
                ActualJson = Json(new SetScopeEntryState
                {
                    Name = parameter.Definition.Name,
                    Scope = current
                }),
                UndoJson = Json(new SetScopeUndoPlan
                {
                    FamilyName = session.FamilyName,
                    BackupPath = null,
                    OriginalScope = current
                }),
                Label = session.FamilyName
            };
        }
        finally
        {
            session?.Dispose();
        }
    }

    private static bool TryResolveFamily(
        Document document,
        string unitKey,
        string? undoJson,
        out RevitFamily? family)
    {
        var plan = Parse<SetScopeUndoPlan>(undoJson);
        return FamilyJobSelectors.TryResolveFamily(
            document,
            unitKey,
            plan?.NewFamilyUniqueId,
            plan?.FamilyName,
            out family);
    }

    public override JobUnitApply Apply(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview,
        int attempt,
        Action<string>? persistUndo)
    {
        var args = Parse<SetScopeUnitArgs>(unit.ArgsJson);
        if (args is null)
        {
            return ApplyFail(
                JobErrorCodes.InvalidSpec,
                "Unit arguments are unreadable. [Action: run the preview again]");
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
                return ApplyFail(FamilyJobOutcome.CodeOf(outcome), FamilyJobOutcome.Describe(outcome));
            }

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
                return ApplyFail(FamilyJobOutcome.CodeOf(outcome), FamilyJobOutcome.Describe(outcome));
            }

            session.SetBackup(backupPath, sourceBackupPath);

            var manager = session.FamilyDoc.FamilyManager;
            var parameter = AgentFamilyReader.FindParameter(
                manager,
                args.ParameterRef!.Name,
                args.ParameterRef.SharedParameterGuid,
                null,
                out _,
                out _);
            if (parameter is null)
            {
                return ApplyFail(
                    FamilyScopeInvalid,
                    "The family '" + session.FamilyName + "' has no parameter matching '" +
                    (args.ParameterRef.Name ?? args.ParameterRef.SharedParameterGuid) + "'. [Action: run the preview " +
                    "again]");
            }

            if (parameter.IsInstance == args.IsInstance)
            {
                return new JobUnitApply
                {
                    Success = true,
                    Skipped = true,
                    ExpectedJson = preview.ExpectedJson,
                    UndoJson = preview.UndoJson
                };
            }

            var undo = new SetScopeUndoPlan
            {
                FamilyName = session.FamilyName,
                BackupPath = backupPath,
                OriginalScope = parameter.IsInstance ? "instance" : "type"
            };

            // J-04: the crash anchor (backup path) must be durable before the first write.
            persistUndo?.Invoke(Json(undo));

            using var transaction = new Transaction(
                session.FamilyDoc,
                "SmartCon job: set_family_parameter_scope");
            var started = false;
            try
            {
                transaction.Start();
                started = true;

                if (args.IsInstance)
                    manager.MakeInstance(parameter);
                else
                    manager.MakeType(parameter);

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
                    FamilyScopeInvalid,
                    "Revit refused the scope switch of '" + parameter.Definition.Name + "' in family '" +
                    session.FamilyName + "': " + ex.Message +
                    " A type parameter referenced by another parameter's formula cannot become instance " +
                    "(and image parameters cannot switch at all). [Action: break the formula dependency first, " +
                    "or use replace_family_parameter which rebuilds the parameter]");
            }

            if (!session.TryLoadBack(outcome))
            {
                return ApplyFail(FamilyJobOutcome.CodeOf(outcome), FamilyJobOutcome.Describe(outcome));
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
                ExpectedJson = preview.ExpectedJson,
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

    public override JobUnitVerification Verify(
        JobActionContext context,
        JobUnit unit,
        JobUnitPreview preview)
    {
        var expected = Parse<SetScopeEntryState>(preview.ExpectedJson);
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

        // A project FamilySymbol proves TYPE parameters only; an instance-scope
        // switch is a family-type default no project element carries.
        if (string.Equals(expected.Scope, "instance", StringComparison.OrdinalIgnoreCase))
        {
            return new JobUnitVerification
            {
                Verified = true,
                NotVerifiable = 1,
                ActualJson = Value("instance scope is not project-verifiable; check with " +
                                  "get_family_parameter_values")
            };
        }

        var anySymbol = false;
        foreach (var typeId in family.GetFamilySymbolIds())
        {
            if (context.Document.GetElement(typeId) is not FamilySymbol symbol)
                continue;

            anySymbol = true;
            foreach (Parameter parameter in symbol.Parameters)
            {
                if (string.Equals(
                        parameter.Definition?.Name,
                        expected.Name,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return new JobUnitVerification { Verified = true };
                }
            }
        }

        return new JobUnitVerification
        {
            Verified = false,
            MismatchReason = "The parameter '" + expected.Name + "' is not visible as a type parameter" +
                             (anySymbol ? string.Empty : " (no types found)") +
                             ". [Action: inspect the family with get_family_parameters]"
        };
    }

    public override JobUnitUndo Undo(
        JobActionContext context,
        JobUnit unit,
        string? undoJson,
        bool dryRun)
    {
        var plan = Parse<SetScopeUndoPlan>(undoJson);
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
                                   "' (parameter back to " + (plan.OriginalScope ?? "?") + ") from " +
                                   Path.GetFileName(backupPath!))
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

                AgentFamilyReloadPoke.TryPokeLocal(
                    backupDoc,
                    plan!.FamilyName ?? backupFamilyName ?? "family");
                loaded = backupDoc.LoadFamily(context.Document, new SetScopeLoadOptions());
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
                ActualJson = Value("family '" + (plan!.FamilyName ?? loaded.Name) +
                                   "' restored from the backup (parameter back to " +
                                   (plan.OriginalScope ?? "?") + ")")
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

    private static JobUnitApply ApplyFail(string code, string detail) => new()
    {
        Success = false,
        ErrorCode = code,
        ErrorDetail = detail
    };

    private sealed class SetScopeLoadOptions : IFamilyLoadOptions
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
