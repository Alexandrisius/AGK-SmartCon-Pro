using System.Globalization;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using RevitFamilyManager = Autodesk.Revit.DB.FamilyManager;
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
    private bool _disposed;

    private AgentFamilySession(Document projectDoc, RevitFamily family, Document familyDoc)
    {
        ProjectDoc = projectDoc;
        Family = family;
        FamilyDoc = familyDoc;
        FamilyName = family.Name;
    }

    public Document ProjectDoc { get; }

    public RevitFamily Family { get; }

    public Document FamilyDoc { get; }

    public string FamilyName { get; }

    public string? BackupPath { get; private set; }

    public string? SourceBackupPath { get; private set; }

    public bool LoadedBack { get; private set; }

    public IReadOnlyList<SectionReport> Sections => _sections;

    public static AgentFamilySession? Open(
        Document projectDoc,
        RevitFamily family,
        WriteOutcome outcome)
    {
        if (projectDoc.IsModifiable)
        {
            Fail(
                outcome,
                "permission_denied",
                "The project document is modified by another transaction. Close it and retry.",
                "Wait until the current operation finishes, then repeat the request.");
            return null;
        }

        if (projectDoc.IsReadOnly)
        {
            Fail(
                outcome,
                "permission_denied",
                "The project document is read-only, so its families cannot be edited.",
                "Open a writable copy of the project.");
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
            familyDoc = projectDoc.EditFamily(family);
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
            AddSection("loadBack", "ok", "family loaded back into the project");
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
        var instanceSkipped = 0;

        foreach (var expectation in _expectations)
        {
            if (!expectation.IsTypeParameter)
            {
                instanceSkipped++;
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
                Name = expectation.TypeName + "." + expectation.ParameterName,
                ErrorCode = "read_back_mismatch",
                Reason = problem
            });
        }

        outcome.Data["readBack"] = new
        {
            expected = _expectations.Count,
            verified = checkedCount,
            mismatched = mismatches,
            instanceSkipped
        };

        if (instanceSkipped > 0)
        {
            outcome.Warnings.Add(
                instanceSkipped + " instance parameter value(s) were written into the family " +
                "type defaults but cannot be verified from the project without instances.");
        }

        if (mismatches == 0)
        {
            AddSection("readBack", "ok", checkedCount + " value(s) verified");
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

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        try
        {
            if (!FamilyDoc.IsReadOnly)
                FamilyDoc.Close(false);
            AddSection("close", "ok", "family document closed without saving");
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