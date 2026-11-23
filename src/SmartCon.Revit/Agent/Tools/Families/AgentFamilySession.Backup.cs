using System.Globalization;
using Autodesk.Revit.DB;
using RevitFamily = Autodesk.Revit.DB.Family;
using SmartCon.Core.Logging;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Families;

/// <summary>
/// Backup half of the family session: the pre-edit snapshot is created in a
/// separate family document, because SaveAs on the editing document makes
/// LoadFamily reload the stale file instead of the edited contents.
/// </summary>
internal sealed partial class AgentFamilySession
{
    /// <summary>
    /// Creates the pre-edit backup in a separate family document. The editing
    /// document is opened after this method returns: SaveAs on the editing document
    /// would switch its path to the backup and Revit would then load the stale file
    /// contents back into the project.
    /// </summary>
    public static bool TryCreateBackup(
        Document projectDoc,
        RevitFamily family,
        WriteOutcome outcome,
        out string? backupPath,
        out string? sourceBackupPath)
    {
        backupPath = null;
        sourceBackupPath = null;
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

        if (!TryEnterEditableProject(projectDoc, family.Name, outcome))
        {
            return false;
        }

        if (family.IsInPlace)
        {
            Fail(outcome, "in_place_not_supported",
                "The family '" + family.Name + "' is an in-place family. In-place families " +
                "have no family document and cannot be edited by SmartCon.");
            return false;
        }

        if (!family.IsEditable)
        {
            Fail(outcome, "family_not_editable",
                "The family '" + family.Name + "' is not editable.");
            return false;
        }

        Document? backupDoc = null;
        try
        {
            try
            {
                backupDoc = projectDoc.EditFamily(family);
            }
            catch (Autodesk.Revit.Exceptions.InvalidOperationException ex)
            {
                Fail(outcome, "family_open_conflict",
                    "Revit refused to open the family document: " + ex.Message,
                    "Close the Family Editor for '" + family.Name + "' and retry.");
                return false;
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException ex)
            {
                Fail(outcome, "family_not_editable",
                    "Revit refused to open the family document: " + ex.Message);
                return false;
            }

            if (backupDoc is null)
            {
                Fail(outcome, "family_open_failed",
                    "Revit did not return a family document for '" + family.Name + "'.");
                return false;
            }

            var sourcePath = ResolveSourcePath(backupDoc);
            if (sourcePath is not null)
            {
                try
                {
                    var sourceBackup = UniquePath(sourcePath + ".bak-" + stamp);
                    File.Copy(sourcePath, sourceBackup, overwrite: false);
                    sourceBackupPath = sourceBackup;
                    outcome.Data["sourceBackupPath"] = sourceBackup;
                }
                catch (Exception ex)
                {
                    outcome.Warnings.Add(
                        "The source .rfa copy could not be written next to the original (" +
                        ex.GetType().Name + ": " + ex.Message + "); the in-memory family " +
                        "snapshot is used as the backup instead.");
                }
            }

            try
            {
                // The backup file must be named exactly as the family: a doc-to-doc
                // LoadFamily from a differently named file creates a NEW family named
                // after the file instead of replacing the loaded one (#430), so the
                // timestamp lives in the directory, not in the file name.
                var root = BackupDirectory();
                var directory = UniqueDirectory(Path.Combine(root, stamp));
                Directory.CreateDirectory(directory);
                CleanupOldBackups(root);
                var fileName = AgentFamilySpecs.SanitizeFileName(family.Name) + ".rfa";
                var path = Path.Combine(directory, fileName);
                backupDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
                backupPath = path;
                return true;
            }
            catch (Exception ex)
            {
                if (sourceBackupPath is not null)
                {
                    backupPath = sourceBackupPath;
                    outcome.Warnings.Add(
                        "The in-memory family snapshot failed (" + ex.GetType().Name + ": " +
                        ex.Message + "); undo will restore the .rfa file copy.");
                    return true;
                }

                Fail(
                    outcome,
                    "backup_failed",
                    "A backup of the family could not be created, so the change was not " +
                    "applied: " + ex.GetType().Name + ": " + ex.Message,
                    "Check write access to " + BackupDirectory() + " and retry.");
                return false;
            }
        }
        finally
        {
            if (backupDoc is not null && backupDoc.IsValidObject)
                AgentFamilyDocCloser.Close(backupDoc);
        }
    }

    private static string? ResolveSourcePath(Document familyDoc)
    {
        try
        {
            var path = familyDoc.PathName;
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (!string.Equals(Path.GetExtension(path), ".rfa", StringComparison.OrdinalIgnoreCase))
                return null;

            return File.Exists(path) ? path : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string BackupDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SmartCon",
        "Agent",
        "family-backups");

    private static void CleanupOldBackups(string root)
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddDays(-30);
            // Legacy layout: timestamped .rfa files directly under the root.
            foreach (var file in Directory.GetFiles(root, "*.rfa"))
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
            }

            // Current layout: one directory per backup, the .rfa named as the family.
            foreach (var directory in Directory.GetDirectories(root))
            {
                if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
                    Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception)
        {
            // Cleanup is best-effort; a stale backup never blocks the edit.
        }
    }

    private static string UniqueDirectory(string path)
    {
        if (!Directory.Exists(path))
            return path;

        var parent = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileName(path);
        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(parent, name + "-" + index);
            if (!Directory.Exists(candidate))
                return candidate;
        }

        return Path.Combine(parent, name + "-" + Guid.NewGuid().ToString("N"));
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
            return path;

        var directory = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var index = 2; index < 1000; index++)
        {
            var candidate = Path.Combine(directory, name + "-" + index + extension);
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(directory, name + "-" + Guid.NewGuid().ToString("N") + extension);
    }

}
