using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent.Jobs;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>
/// Preview and apply half of the add action. The preview stays read-only (no
/// backup) and reports same-name parameters as skips; the apply backs the .rfa
/// up first, re-derives the plan (preview-stale guard), persists the undo
/// anchor before the first write (J-04) and runs every add + authored formula
/// inside one family-document transaction per family. Rollback restores the
/// pre-edit backup — a reload changes family ids, so value replay is not a
/// honest undo path.
/// </summary>
public sealed partial class AddFamilyParametersJobAction
{
    public override JobUnitPreview Preview(JobActionContext context, JobUnit unit)
    {
        var args = Parse<AddFamilyUnitArgs>(unit.ArgsJson);
        if (args is null)
        {
            return Fail(JobErrorCodes.InvalidSpec, "Unit arguments are unreadable. [Action: run the preview again]");
        }

        if (args.Error is not null)
        {
            return Fail(FamilyAddInvalid, args.Error!);
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
            session = AgentFamilySession.Open(context.Document, family, outcome, context.RevitContext);
            if (session is null)
            {
                return Fail(FamilyJobOutcome.CodeOf(outcome), FamilyJobOutcome.Describe(outcome));
            }

            var plan = BuildPlan(session.FamilyDoc.FamilyManager, args.Parameters!);
            return new JobUnitPreview
            {
                Skipped = plan.ChangeCount == 0,
                ExpectedJson = Json(plan.Entries
                    .Where(entry => string.Equals(entry.Action, "add", StringComparison.Ordinal))
                    .Select(entry => new AddFamilyEntryState
                    {
                        Name = entry.Name,
                        Scope = entry.IsInstance ? "instance" : "type",
                        Formula = entry.Formula
                    })
                    .ToList()),
                ActualJson = Json(plan.Entries),
                UndoJson = Json(new AddFamilyUndoPlan
                {
                    FamilyName = session.FamilyName,
                    BackupPath = null
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

    private static AddFamilyPlan BuildPlan(
        RevitFamilyManager manager,
        IReadOnlyList<AddFamilyParameterRequest> parameters)
    {
        var plan = new AddFamilyPlan();
        foreach (var request in parameters)
        {
            var name = request.Name!.Trim();
            var existing = FindByName(manager, name);
            if (existing is not null)
            {
                plan.Entries.Add(new AddFamilyPlanEntry
                {
                    Name = name,
                    Action = "skip",
                    Reason = "the family already has a parameter named '" + name + "' (" +
                             (existing.IsInstance ? "instance" : "type") + ")"
                });
                continue;
            }

            plan.ChangeCount++;
            plan.Entries.Add(new AddFamilyPlanEntry
            {
                Name = name,
                StorageType = request.StorageType,
                Group = request.Group,
                IsInstance = request.IsInstance,
                IsShared = request.IsShared,
                Formula = string.IsNullOrWhiteSpace(request.Formula) ? null : request.Formula!.Trim()
            });
        }

        return plan;
    }

    private static Autodesk.Revit.DB.FamilyParameter? FindByName(
        RevitFamilyManager manager,
        string name)
    {
        foreach (var parameter in manager.GetParameters())
        {
            if (string.Equals(parameter.Definition.Name, name, StringComparison.OrdinalIgnoreCase))
                return parameter;
        }

        return null;
    }

    private static bool TryResolveFamily(
        Document document,
        string unitKey,
        string? undoJson,
        out RevitFamily? family)
    {
        var plan = Parse<AddFamilyUndoPlan>(undoJson);
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
        var args = Parse<AddFamilyUnitArgs>(unit.ArgsJson);
        if (args is null)
        {
            return ApplyFail(
                JobErrorCodes.InvalidSpec,
                "Unit arguments are unreadable. [Action: run the preview again]");
        }

        if (args.Error is not null)
        {
            return ApplyFail(FamilyAddInvalid, args.Error!);
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

            // The backup round-trip may invalidate the Family wrapper; re-resolve by id.
            var familyForEdit = context.Document.GetElement(ElementIdCompat.Create(familyId)) as RevitFamily;
            if (familyForEdit is null)
            {
                return ApplyFail(
                    JobErrorCodes.ItemFailed,
                    "The family disappeared from the project while the backup was being created; nothing was " +
                    "changed. [Action: run the preview again]");
            }

            session = AgentFamilySession.Open(context.Document, familyForEdit, outcome, context.RevitContext);
            if (session is null)
            {
                return ApplyFail(FamilyJobOutcome.CodeOf(outcome), FamilyJobOutcome.Describe(outcome));
            }

            session.SetBackup(backupPath, sourceBackupPath);

            var plan = BuildPlan(session.FamilyDoc.FamilyManager, args.Parameters!);
            if (plan.ChangeCount == 0)
            {
                return new JobUnitApply
                {
                    Success = true,
                    Skipped = true,
                    ExpectedJson = preview.ExpectedJson,
                    UndoJson = preview.UndoJson
                };
            }

            var undo = new AddFamilyUndoPlan
            {
                FamilyName = session.FamilyName,
                BackupPath = backupPath
            };

            // J-04: the crash anchor (backup path) must be durable before the first write.
            persistUndo?.Invoke(Json(undo));

            using var transaction = new Transaction(session.FamilyDoc, "SmartCon job: add_family_parameter");
            var started = false;
            try
            {
                transaction.Start();
                started = true;

                var applyError = ApplyAdds(session, plan, context.CancellationToken);
                if (applyError is not null)
                {
                    if (transaction.HasStarted())
                        transaction.RollBack();

                    started = false;
                    return ApplyFail(applyError.Value.Code, applyError.Value.Detail);
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
                    "add_failed",
                    "The family transaction was rolled back (" + ex.GetType().Name + ": " + ex.Message +
                    "); nothing was changed. [Action: run the preview again]");
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

    private static (string Code, string Detail)? ApplyAdds(
        AgentFamilySession session,
        AddFamilyPlan plan,
        CancellationToken ct)
    {
        var manager = session.FamilyDoc.FamilyManager;
        foreach (var entry in plan.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(entry.Action, "add", StringComparison.Ordinal))
                continue;

            var request = new AddFamilyParameterRequest
            {
                Name = entry.Name,
                StorageType = entry.StorageType,
                Group = entry.Group,
                IsInstance = entry.IsInstance,
                IsShared = entry.IsShared
            };

            var name = entry.Name;
            try
            {
                if (!AgentFamilySpecs.TryResolveSpec(
                        entry.StorageType,
                        out var spec,
                        out _,
                        out var specMessage))
                {
                    return (FamilyAddInvalid,
                        "parameter '" + name + "': " + (specMessage ?? "Unknown storageType."));
                }

                if (!AgentFamilySpecs.TryResolveGroup(entry.Group, out var group, out var groupMessage))
                {
                    return (FamilyAddInvalid,
                        "parameter '" + name + "': " + (groupMessage ?? "Unknown parameter group."));
                }

                Autodesk.Revit.DB.FamilyParameter created;
                if (entry.IsShared)
                {
                    var definition = FindSharedDefinition(session, name);
                    if (definition is null)
                    {
                        return (FamilyAddInvalid,
                            "parameter '" + name + "': the shared definition does not exist in the active shared " +
                            "parameter file. [Action: create it once with add_family_parameter" +
                            "(createDefinition=true) or list_shared_parameters to pick the exact name]");
                    }

                    if (!definition.GetDataType().Equals(spec!))
                    {
                        return (FamilyAddInvalid,
                            "parameter '" + name + "': the shared definition exists with a different data type. " +
                            "[Action: match storageType to the definition or use another name]");
                    }

                    created = manager.AddParameter(definition, group!, entry.IsInstance);
                }
                else
                {
                    created = manager.AddParameter(name, group!, spec!, entry.IsInstance);
                }

                if (!string.IsNullOrEmpty(entry.Formula))
                {
                    if (!AgentFamilyReader.TryFindMissingLookupTables(
                            session.FamilyDoc,
                            entry.Formula!,
                            out var missingTables))
                    {
                        return (FamilyAddInvalid,
                            "parameter '" + name + "': the formula references lookup table(s) that are not " +
                            "present in the family: " + string.Join(", ", missingTables) + ".");
                    }

                    manager.SetFormula(created, entry.Formula!);
                }
            }
            catch (Exception ex)
            {
                return (FamilyAddInvalid,
                    "Revit refused to add the parameter '" + name + "': " + ex.Message +
                    " [Action: check the name, group, storage type and formula]");
            }
        }

        return null;
    }

    /// <summary>
    /// Shared definitions must already exist in the active shared parameter file
    /// (a job must not pollute the customer's external ФОП file); the single
    /// add_family_parameter(createDefinition=true) creates a missing definition
    /// exactly once.
    /// </summary>
    private static ExternalDefinition? FindSharedDefinition(
        AgentFamilySession session,
        string name)
    {
        try
        {
            var file = session.ProjectDoc.Application.OpenSharedParameterFile();
            if (file is null)
                return null;

            foreach (DefinitionGroup candidate in file.Groups)
            {
                foreach (Definition definition in candidate.Definitions)
                {
                    if (definition is ExternalDefinition external
                        && string.Equals(external.Name, name, StringComparison.OrdinalIgnoreCase))
                    {
                        return external;
                    }
                }
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
