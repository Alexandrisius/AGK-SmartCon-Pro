using System.Globalization;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Owns the lifecycle of one family document (H1d): open through
/// <see cref="Document.EditFamily"/> without the Family Editor, back up the
/// current state, edit inside a family-document transaction (I-03b), load the
/// result back into the project and always close the family document.
/// </summary>
internal sealed partial class AgentFamilySession : IDisposable
{
    private readonly List<SectionReport> _sections = new();
    private readonly List<FamilyReadBackExpectation> _expectations = new();
    private readonly List<FamilyUndoOperation> _undoOperations = new();
    private AgentFamilyFailuresSuppressor? _failures;
    private bool _disposed;

    private AgentFamilySession(Document projectDoc, RevitFamily family, Document familyDoc)
    {
        ProjectDoc = projectDoc;
        FamilyDoc = familyDoc;
        FamilyName = family.Name;
        // Known benign failure prompts («the family no longer matches the
        // properties for the System…») block the write path exactly like the
        // dialogs do; failures never reach DialogBoxShowing, so they get their
        // own scope over every transaction of this session, load-back included
        // (live v18 — a migration job waited minutes for the owner's click).
        _failures = AgentFamilyFailuresSuppressor.Begin(projectDoc);
    }

    public Document ProjectDoc { get; }

    public Document FamilyDoc { get; }

    public string FamilyName { get; }

    public string? BackupPath { get; private set; }

    public string? SourceBackupPath { get; private set; }

    public bool LoadedBack { get; private set; }

    /// <summary>
    /// Element id of the family after LoadFamily. Revit recreates the family
    /// element on reload, so the id captured before the edit becomes stale.
    /// </summary>
    public long? LoadedFamilyId { get; private set; }

    public string? LoadedFamilyUniqueId { get; private set; }

    public IReadOnlyList<SectionReport> Sections => _sections;

    /// <summary>
    /// Error code of a document-level refusal (project read-only or inside another
    /// transaction). It is not a property of the unit: every following unit of the
    /// same job is refused the same way, so the job runner stops the job on it.
    /// </summary>
    internal const string PermissionDenied = "permission_denied";

    /// <summary>
    /// Upper bound for waiting until the project accepts edits again. Revit keeps
    /// a project read-only while one of its family documents is open; a close
    /// normally restores writability at once, so the wait only matters for a close
    /// that is still settling and turns a race into one honest refusal.
    /// </summary>
    private static readonly TimeSpan EditableWait = TimeSpan.FromSeconds(1);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Document OpenWithDialogSuppression(
        Document projectDoc,
        RevitFamily family,
        SmartCon.Core.Services.Interfaces.IRevitContext revitContext,
        WriteOutcome outcome)
    {
        using var dialogs = AgentFamilyDialogSuppressor.Begin(revitContext);
        var familyDoc = projectDoc.EditFamily(family);
        foreach (var dialog in dialogs.Suppressed)
        {
            outcome.Warnings.Add(
                "Revit dialog auto-dismissed while opening the family: '" +
                dialog.DialogId + "' (" + dialog.Answer + ").");
        }

        return familyDoc;
    }

    public static AgentFamilySession? Open(
        Document projectDoc,
        RevitFamily family,
        WriteOutcome outcome,
        SmartCon.Core.Services.Interfaces.IRevitContext? revitContext = null)
    {
        if (!TryEnterEditableProject(projectDoc, family.Name, outcome))
        {
            return null;
        }

        if (family.IsInPlace)
        {
            Fail(
                outcome,
                "in_place_not_supported",
                "The family '" + family.Name + "' is an in-place family. In-place families have " +
                "no family document and cannot be edited by SmartCon.",
                "Edit the in-place family manually in the Family Editor.");
            return null;
        }

        if (!family.IsEditable)
        {
            Fail(
                outcome,
                "family_not_editable",
                "The family '" + family.Name + "' is not editable.",
                "Use list_families and pick a loadable, editable family.");
            return null;
        }

        Document? familyDoc;
        try
        {
            // Family-open dialogs (dependency behaviour warnings, upgrade
            // prompts) hang the write path until a human clicks OK — dismiss
            // and report (live v17, P0 1.1; ShareProjectCommand pattern).
            // The guard keeps RevitAPIUI out of UI-less hosts (integration
            // tests): OpenWithDialogSuppression is only JIT-ed when called
            // (#153 pattern — a FileLoadException here killed the suite).
            familyDoc = revitContext is SmartCon.Revit.Context.IRevitUIContext
                ? OpenWithDialogSuppression(projectDoc, family, revitContext, outcome)
                : projectDoc.EditFamily(family);
        }
        catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
        {
            Fail(
                outcome,
                "family_open_conflict",
                "Revit refused to open the family document: " + ex.Message,
                "Close the Family Editor for '" + family.Name + "' (or the other pending " +
                "SmartCon operation) and retry.");
            return null;
        }
        catch (Autodesk.Revit.Exceptions.ArgumentException ex)
        {
            Fail(
                outcome,
                "family_not_editable",
                "Revit refused to open the family document: " + ex.Message,
                "Use list_families and pick a loadable, editable family.");
            return null;
        }
        catch (Exception ex)
        {
            Fail(
                outcome,
                "family_open_failed",
                "The family document could not be opened: " + ex.GetType().Name + ": " + ex.Message,
                "Close the Family Editor and retry; if the problem repeats, check the family " +
                "in the Family Editor manually.");
            return null;
        }

        if (familyDoc is null)
        {
            Fail(
                outcome,
                "family_open_failed",
                "Revit did not return a family document for '" + family.Name + "'.",
                "Close the Family Editor and retry.");
            return null;
        }

        var session = new AgentFamilySession(projectDoc, family, familyDoc);
        session.AddSection("open", "ok", "family document opened in the background");
        return session;
    }

    public void AddSection(string section, string status, string? reason = null) =>
        _sections.Add(new SectionReport { Section = section, Status = status, Reason = reason });

    public void AddExpectation(FamilyReadBackExpectation expectation) =>
        _expectations.Add(expectation);

    public void AddUndoOperation(FamilyUndoOperation operation) =>
        _undoOperations.Add(operation);

    public string BuildUndoPayload() => new FamilyUndoPlan
    {
        FamilyName = FamilyName,
        BackupPath = BackupPath,
        Ops = _undoOperations
    }.Serialize();

    public void AddExpectations(string typeName, FamilyParameter parameter, FamilyValueSnapshot value)
    {
        AddExpectation(new FamilyReadBackExpectation
        {
            TypeName = typeName,
            ParameterName = parameter.Definition.Name,
            IsTypeParameter = !parameter.IsInstance,
            StorageType = value.StorageType,
            DoubleValue = value.DoubleValue,
            IntegerValue = value.IntegerValue,
            TextValue = value.TextValue,
            ElementIdValue = value.ElementIdValue
        });
    }

    public void AddExistenceExpectation(string typeName, string parameterName, bool isTypeParameter) =>
        AddExpectation(new FamilyReadBackExpectation
        {
            TypeName = typeName,
            ParameterName = parameterName,
            IsTypeParameter = isTypeParameter,
            ExistsOnly = true
        });


    public void SetBackup(string? backupPath, string? sourceBackupPath)
    {
        BackupPath = backupPath;
        SourceBackupPath = sourceBackupPath;
        if (backupPath is not null)
            AddSection("backup", "ok", "backup: " + backupPath);
    }

    /// <summary>
    /// Loads the edited family document back into the project. Revit runs its own
    /// transaction here, so the project document must not be modifiable.
    /// </summary>
    public bool TryLoadBack(WriteOutcome outcome)
    {
        try
        {
            var loaded = FamilyDoc.LoadFamily(ProjectDoc, new AgentFamilyLoadOptions());
            if (loaded is null)
            {
                Fail(
                    outcome,
                    "save_failed",
                    "Revit did not load the edited family back into the project '" +
                    ProjectDoc.Title + "'. The project was not changed.",
                    "Check that the family is still loaded and that no other transaction " +
                    "is open, then retry.");
                AddSection("loadBack", "failed", "Revit returned no family");
                return false;
            }

            LoadedBack = true;
            LoadedFamilyId = loaded.Id.GetValue();
            LoadedFamilyUniqueId = loaded.UniqueId;
            AddSection("loadBack", "ok", "family loaded back into the project");
            // The internal LoadFamily transaction is where the benign system
            // prompts surface; report what the suppressor resolved on the way.
            outcome.Warnings.AddRange(FailureWarnings());
            return true;
        }
        catch (Exception ex)
        {
            Fail(
                outcome,
                "save_failed",
                "The edited family could not be loaded back into the project: " +
                ex.GetType().Name + ": " + ex.Message + ". The project was not changed.",
                "Close other family operations and retry; the backup is kept at " +
                (BackupPath ?? "(no backup)") + ".");
            AddSection("loadBack", "failed", ex.Message);
            return false;
        }
    }

    /// <summary>
    /// Compares the values actually present in the project with the values the tool
    /// wrote into the family document. Instance values live on project instances,
    /// so only type parameters are verified.
    /// </summary>
    public void VerifyReadBack(WriteOutcome outcome)
    {
        if (_expectations.Count == 0)
        {
            AddSection("readBack", "skipped", "no read-back expectations");
            return;
        }

        var snapshot = ProjectFamilySnapshot.Capture(ProjectDoc, FamilyName);
        var checkedCount = 0;
        var mismatches = 0;
        var instanceNotVerifiable = 0;
        var elementIdNotVerifiable = 0;

        foreach (var expectation in _expectations)
        {
            if (!expectation.IsTypeParameter)
            {
                instanceNotVerifiable++;
                continue;
            }

            if (expectation.StorageType == StorageType.ElementId)
            {
                // Material / nested type references get project-side ElementIds on
                // load-back, so comparing the family-document id would be wrong.
                elementIdNotVerifiable++;
                continue;
            }

            string problem;
            if (!snapshot.HasType(expectation.TypeName))
            {
                problem = "type is missing from the project after load-back";
            }
            else if (expectation.ExistsOnly)
            {
                problem = snapshot.HasParameter(expectation.TypeName, expectation.ParameterName)
                    ? string.Empty
                    : "parameter is missing from the project type";
            }
            else if (!snapshot.TryRead(expectation.TypeName, expectation.ParameterName, out var actual))
            {
                problem = "parameter is missing from the project type";
            }
            else if (actual.Matches(expectation, out var actualText))
            {
                problem = string.Empty;
            }
            else
            {
                problem = "expected " + expectation.DoubleValue.ToString("R", CultureInfo.InvariantCulture) +
                          ", actual " + actualText;
                if (expectation.StorageType != StorageType.Double)
                {
                    problem = "value differs after load-back (actual " + actualText + ")";
                }
            }

            if (problem.Length == 0)
            {
                checkedCount++;
                continue;
            }

            mismatches++;
            outcome.Batch.Failed.Add(new WriteItemReport
            {
                // The type name is shown trimmed and quoted: a whitespace-only
                // vendor type name used to render as " .Parameter" and read as
                // corruption (v16 pilot, D1).
                Name = "'" + expectation.TypeName.Trim() + "'." + expectation.ParameterName,
                ErrorCode = "read_back_mismatch",
                Reason = problem
            });
        }

        var notVerifiable = instanceNotVerifiable + elementIdNotVerifiable;
        outcome.Data["readBack"] = new
        {
            expected = _expectations.Count,
            verified = checkedCount,
            mismatched = mismatches,
            notVerifiable,
            notVerifiableReasons = new
            {
                instanceParameter = instanceNotVerifiable,
                elementReference = elementIdNotVerifiable
            }
        };

        if (elementIdNotVerifiable > 0)
        {
            outcome.Warnings.Add(
                elementIdNotVerifiable + " element-reference value(s) are not verifiable: " +
                "Revit assigns new project ElementIds when the family is loaded back.");
        }

        if (instanceNotVerifiable > 0)
        {
            outcome.Warnings.Add(
                instanceNotVerifiable + " instance parameter value(s) are not verifiable from " +
                "the project: they are family type defaults and no project instance carries " +
                "the written value yet.");
        }

        if (mismatches == 0)
        {
            AddSection(
                "readBack",
                "ok",
                checkedCount + " value(s) verified" +
                (notVerifiable > 0 ? ", " + notVerifiable + " not verifiable" : string.Empty));
            return;
        }

        outcome.Success = false;
        outcome.ErrorCode = "read_back_mismatch";
        outcome.Message =
            "The family was saved, but " + mismatches + " of " + _expectations.Count +
            " value(s) did not match after loading the family back into the project. " +
            "The change set keeps the change so it can be undone; restore the backup if the " +
            "result is not acceptable.";
        AddSection("readBack", "failed", mismatches + " mismatch(es)");
    }

    public void MarkEditSection(string toolName, bool dryRun, int affected)
    {
        AddSection(
            "edit",
            affected > 0 ? "ok" : "skipped",
            toolName + (dryRun ? " (dry run)" : string.Empty) + ": " + affected + " change(s)");
        if (dryRun)
            AddSection("backup", "skipped", "dry run does not create a backup");
        if (dryRun)
            AddSection("loadBack", "skipped", "dry run does not modify the project");
        if (dryRun)
            AddSection("readBack", "skipped", "dry run does not modify the project");
    }

    /// <summary>
    /// Warning lines for the failures auto-resolved during this session (edit
    /// transaction + load-back). Job actions copy them into the unit result;
    /// write tools get them through <see cref="WriteOutcome.Warnings"/>.
    /// </summary>
    internal List<string> FailureWarnings() =>
        _failures?.WarningLines() ?? new List<string>();

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _failures?.Dispose();
        _failures = null;
        // FamilyManager batch pattern (#96 / REVIT-237190): close + synchronous
        // COM release + render-thread nudge after every family document cycle.
        // The close is confirmed, not assumed: a family document that survives
        // keeps the project read-only for every later family operation.
        try
        {
            if (AgentFamilyDocCloser.Close(FamilyDoc))
            {
                AddSection("close", "ok", "family document closed without saving");
            }
            else
            {
                // AgentFamilyDocCloser logged the cause together with the action.
                AddSection(
                    "close",
                    "failed",
                    "the family document is still open after Close(false); the project stays " +
                    "read-only until it is closed");
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "Agent family document '" + FamilyName + "' could not be closed: " +
                ex.GetType().Name + ": " + ex.Message +
                " [Action: close the family document in Revit (or reload the project) so the " +
                "family is not left open]");
            AddSection("close", "failed", ex.Message);
        }
    }

    /// <summary>
    /// Waits (bounded) for a project that accepts edits again and reports the
    /// refusal with its real cause when it does not.
    /// </summary>
    private static bool TryEnterEditableProject(
        Document projectDoc,
        string familyName,
        WriteOutcome outcome)
    {
        var deadline = DateTime.UtcNow + EditableWait;
        while (true)
        {
            var inOtherTransaction = projectDoc.IsModifiable;
            var readOnly = projectDoc.IsReadOnly;
            if (!inOtherTransaction && !readOnly)
            {
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                FailProjectRefusal(inOtherTransaction, familyName, outcome);
                return false;
            }

            Thread.Sleep(50);
        }
    }

    private static void FailProjectRefusal(
        bool inOtherTransaction,
        string familyName,
        WriteOutcome outcome)
    {
        if (inOtherTransaction)
        {
            Fail(
                outcome,
                PermissionDenied,
                "The project document is being modified by another transaction, so the family '" +
                familyName + "' cannot be edited.");
            return;
        }

        Fail(
            outcome,
            PermissionDenied,
            "The project document is read-only (a family document of this project is still open, " +
            "another operation is pending, or the file is read-only), so the family '" +
            familyName + "' cannot be edited.",
            "No family of this project can be edited until it accepts edits again.");
    }

    private static void Fail(
        WriteOutcome outcome,
        string errorCode,
        string message,
        string? hint = null)
    {
        outcome.Success = false;
        outcome.ErrorCode = errorCode;
        outcome.Message = string.IsNullOrEmpty(hint) ? message : message + " " + hint;
    }

    private sealed class AgentFamilyLoadOptions : IFamilyLoadOptions
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