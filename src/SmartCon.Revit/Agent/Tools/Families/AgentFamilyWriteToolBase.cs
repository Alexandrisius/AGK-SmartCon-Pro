using System.Text.Json;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Write pipeline for family-document tools (H1d). Unlike project write tools it
/// does not wrap the work in a project transaction: EditFamily and LoadFamily are
/// forbidden while the project document is modifiable, so each tool owns the whole
/// lifecycle — open, backup, family transaction, load-back, read-back, close.
/// The response envelope is the same as for every write tool:
/// {success, errorCode, message, data:{changed, skipped, failed, sections, changeSetId}}.
/// </summary>
public abstract class AgentFamilyWriteToolBase : AgentWriteToolBase
{
    protected AgentFamilyWriteToolBase(
        IRevitContext revitContext,
        ITransactionService transactions,
        IAgentChangeSetStore changeSets)
        : base(revitContext, transactions, changeSets)
    {
    }

    protected override bool ManagesOwnTransactions => true;

    protected sealed override void Apply(
        Document doc,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct) =>
        throw new InvalidOperationException(
            "Family tools run through ExecuteOutsideTransaction.");

    protected override void ExecuteOutsideTransaction(
        Document document,
        JsonElement args,
        bool dryRun,
        WriteOutcome outcome,
        CancellationToken ct)
    {
        AgentFamilySession? session = null;
        var sections = new List<SectionReport>();
        try
        {
            var family = AgentFamilyReader.ResolveFamily(
                document,
                TryGetLong(args, "familyId"),
                AgentJson.GetString(args, "familyName"),
                AgentJson.GetString(args, "familyUniqueId"),
                out var errorCode,
                out var message);
            if (family is null)
            {
                outcome.Success = false;
                outcome.ErrorCode = errorCode;
                outcome.Message = message;
                return;
            }

            var familyId = family.Id.GetValue();
            var familyUniqueId = family.UniqueId;
            outcome.Data["familyId"] = familyId;
            outcome.Data["familyUniqueId"] = familyUniqueId;

            string? backupPath = null;
            string? sourceBackupPath = null;
            if (!dryRun
                && !AgentFamilySession.TryCreateBackup(
                    document,
                    family,
                    outcome,
                    out backupPath,
                    out sourceBackupPath))
            {
                return;
            }

            // The backup round-trip may invalidate the Family wrapper; re-resolve the
            // element by id before editing (I-05: no Element references across operations).
            var familyForEdit = document.GetElement(
                SmartCon.Core.Compatibility.ElementIdCompat.Create(familyId)) as RevitFamily;
            if (familyForEdit is null)
            {
                outcome.Success = false;
                outcome.ErrorCode = "family_not_found";
                outcome.Message = "The family disappeared from the project while the backup " +
                                  "was being created. Nothing was changed.";
                return;
            }

            session = AgentFamilySession.Open(document, familyForEdit, outcome);
            if (session is null)
                return;

            session.SetBackup(backupPath, sourceBackupPath);

            var transaction = new Transaction(
                session.FamilyDoc,
                OperationName(Definition.Name, dryRun));
            var started = false;
            try
            {
                transaction.Start();
                started = true;
                ApplyInFamilyDocument(session, args, outcome, dryRun, ct);

                // A dry run and any no-op/failed batch must not reach the project:
                // rolling back also discards half-created parameters (for example a
                // failed initialValue) that the tool could not report as changed.
                if (dryRun || outcome.AffectedCount == 0)
                {
                    transaction.RollBack();
                }
                else
                {
                    transaction.Commit();
                }

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

                outcome.Success = false;
                outcome.ErrorCode ??= "transaction_rolled_back";
                outcome.Message =
                    "The family transaction was rolled back (" + ex.GetType().Name + ": " +
                    ex.Message + "). Nothing was changed in the project.";
                MoveChangedToSkipped(outcome);
                outcome.AffectedCount = 0;
                outcome.ModelChanged = false;
                session.MarkEditSection(Definition.Name, dryRun, 0);
                return;
            }

            session.MarkEditSection(Definition.Name, dryRun, outcome.AffectedCount);
            outcome.ModelChanged = false;

            if (dryRun || outcome.AffectedCount == 0)
                return;

            if (!session.TryLoadBack(outcome))
            {
                MoveChangedToSkipped(outcome);
                outcome.AffectedCount = 0;
                outcome.ModelChanged = false;
                outcome.Items.Clear();
                return;
            }

            outcome.ModelChanged = true;

            // LoadFamily deletes and recreates the family element: the id the
            // caller passed is no longer valid. Return the new key together with
            // the previous one so multi-step plans and the model can re-target.
            if (session.LoadedFamilyId.HasValue)
            {
                outcome.Data["previousFamilyId"] = familyId;
                outcome.Data["familyId"] = session.LoadedFamilyId.Value;
                outcome.Data["familyIdChanged"] =
                    session.LoadedFamilyId.Value != familyId;
            }

            if (!string.IsNullOrEmpty(session.LoadedFamilyUniqueId))
            {
                outcome.Data["previousFamilyUniqueId"] = familyUniqueId;
                outcome.Data["familyUniqueId"] = session.LoadedFamilyUniqueId!;
            }

            // Record the undo item before verification: the project already changed,
            // so the audit trail must survive any failure of the read-back check.
            if (outcome.Items.Count == 0)
            {
                outcome.Items.Add(new ChangeItem
                {
                    UniqueId = familyUniqueId,
                    ElementId = familyId,
                    ParameterName = "family",
                    OldValue = session.BuildUndoPayload(),
                    NewValue = session.FamilyName,
                    Status = ChangeStatus.Applied
                });
                outcome.Description = "Family '" + session.FamilyName + "' (" +
                                      Definition.Name + "); backup: " +
                                      (session.BackupPath ?? "unavailable");
            }

            try
            {
                session.VerifyReadBack(outcome);
            }
            catch (Exception ex)
            {
                outcome.Success = false;
                outcome.ErrorCode ??= "read_back_failed";
                outcome.Warnings.Add(
                    "The change was applied, but the read-back check failed: " +
                    ex.GetType().Name + ": " + ex.Message + ". The change set is kept so " +
                    "undo_last can revert the change.");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            outcome.Success = false;
            outcome.ErrorCode ??= "tool_failed";
            outcome.Message = ex.GetType().Name + ": " + ex.Message;
            outcome.ModelChanged = false;
        }
        finally
        {
            if (session is not null)
            {
                session.Dispose();
                sections.AddRange(session.Sections);
                outcome.Data["family"] = session.FamilyName;
                if (session.BackupPath is not null)
                    outcome.Data["backupPath"] = session.BackupPath;
                if (session.SourceBackupPath is not null)
                    outcome.Data["sourceBackupPath"] = session.SourceBackupPath;
            }

            outcome.Data["sections"] = sections;
        }
    }

    /// <summary>
    /// Applies the tool operation inside the family-document transaction. Must add
    /// per-item reports to <see cref="WriteOutcome.Batch"/> and expectations for the
    /// post-load-back verification. A dry run is always rolled back by the base.
    /// </summary>
    private protected abstract void ApplyInFamilyDocument(
        AgentFamilySession session,
        JsonElement args,
        WriteOutcome outcome,
        bool dryRun,
        CancellationToken ct);

    protected static long? TryGetLong(JsonElement args, string property)
    {
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty(property, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt64(out var result))
        {
            return null;
        }

        return result;
    }

    protected static void MoveChangedToSkipped(WriteOutcome outcome)
    {
        foreach (var report in outcome.Batch.Changed)
        {
            report.Reason = "Transaction rolled back — the change was not applied.";
            outcome.Batch.Skipped.Add(report);
        }

        outcome.Batch.Changed.Clear();
    }
}