using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Implementation;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// File side of <see cref="SetSharedParameterFileTool"/>: argument parsing,
/// the dry-run inspection of an existing file (read through the Core parser,
/// no Revit API, nothing written), the inventory of the file Revit opened, and
/// the restoration of the previous setting after a failed apply.
/// </summary>
public sealed partial class SetSharedParameterFileTool
{
    private const int MaxGroupNames = 50;

    /// <summary>
    /// Restores the previous application setting and removes a file this call
    /// created. Returns true when the previous setting is active again.
    /// </summary>
    private static bool RollBack(
        Application application,
        string previousPath,
        string path,
        bool createdFile,
        string reason)
    {
        SmartConLogger.Warn(
            "set_shared_parameter_file failed for '" + Path.GetFileName(path) + "': " + reason +
            " [Action: the previous shared parameter file setting is being restored; retry " +
            "with another path or check the file]");

        var restored = false;
        try
        {
            application.SharedParametersFilename = previousPath ?? string.Empty;
            restored = true;
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                "set_shared_parameter_file could not restore the previous shared parameter " +
                "file setting: " + Describe(ex) +
                " [Action: set the shared parameter file manually in Manage > Shared Parameters]");
        }

        if (createdFile)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    "set_shared_parameter_file could not delete the file it created ('" +
                    Path.GetFileName(path) + "'): " + Describe(ex) +
                    " [Action: delete the empty file manually; it is not the active shared " +
                    "parameter file]");
            }
        }

        return restored;
    }

    /// <summary>
    /// Groups and definitions of the file Revit now holds as the active one:
    /// the proof that Revit accepted the file, read with the Revit API.
    /// </summary>
    private static Inventory ReadInventory(Application application)
    {
        var inventory = new Inventory();
        try
        {
            var file = application.OpenSharedParameterFile();
            if (file is null)
            {
                inventory.ErrorCode = "shared_parameter_file_invalid";
                inventory.Message = "OpenSharedParameterFile returned null.";
                return inventory;
            }

            foreach (DefinitionGroup group in file.Groups)
            {
                inventory.GroupNames.Add(group.Name ?? string.Empty);
                inventory.DefinitionsCount += group.Definitions.Size;
            }
        }
        catch (Exception ex)
        {
            inventory.ErrorCode = "shared_parameter_file_invalid";
            inventory.Message = Describe(ex) + ".";
        }

        return inventory;
    }

    /// <summary>
    /// What the dry run can state about an existing file without Revit and
    /// without writing: the read-only attribute, the size and - through the
    /// Core parser - the groups and definitions it contains.
    /// </summary>
    private static FilePreview InspectFile(string path, List<string> warnings)
    {
        var preview = new FilePreview();
        var fileName = Path.GetFileName(path);
        try
        {
            preview.ReadOnly = (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0;
            preview.IsEmpty = new FileInfo(path).Length == 0;
        }
        catch (Exception ex)
        {
            warnings.Add(
                "The file '" + fileName + "' could not be checked on the dry run: " +
                Describe(ex) + ". Revit checks it on apply.");
            return preview;
        }

        if (preview.IsEmpty)
        {
            warnings.Add(
                "The file '" + fileName + "' is empty: it has no groups and no definitions " +
                "yet (a valid new shared parameter file).");
            return preview;
        }

        try
        {
            var entries = new SharedParameterFileParser().ParseFile(path);
            preview.DefinitionsCount = entries.Count;
            preview.GroupNames = entries
                .Select(entry => entry.GroupName ?? string.Empty)
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(MaxGroupNames)
                .ToList();
        }
        catch (InvalidDataException)
        {
            warnings.Add(
                "The file '" + fileName +
                "' does not look like a Revit shared parameter file (no parameter section " +
                "found). Revit checks it on apply; the previous setting is restored when " +
                "Revit refuses it.");
        }
        catch (Exception ex)
        {
            warnings.Add(
                "The file '" + fileName + "' could not be read on the dry run: " + Describe(ex) +
                ". Revit checks it on apply.");
        }

        return preview;
    }

    /// <summary>
    /// The DB <see cref="Application"/> behind the object the awaitable event
    /// hands over. No direct UIApplication cast and no RevitAPIUI type here:
    /// JIT of those crashes the headless integration-test host.
    /// </summary>
    private Application? ResolveApplication(object app)
    {
        if (app is Application application)
            return application;

        var property = app.GetType().GetProperty("Application");
        if (property?.GetValue(app) is Application owned)
            return owned;

        try
        {
            return _revitContext.TryGetDocument()?.Application;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? TryGetFullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Describe(Exception ex) =>
        ex.GetType().Name + ": " + ex.Message;

    private sealed class Inventory
    {
        public string? ErrorCode { get; set; }

        public string? Message { get; set; }

        public List<string> GroupNames { get; } = new();

        public int DefinitionsCount { get; set; }
    }

    private sealed class FilePreview
    {
        public bool ReadOnly { get; set; }

        public bool IsEmpty { get; set; }

        public List<string> GroupNames { get; } = new();

        public int DefinitionsCount { get; set; }
    }

    private sealed class Request
    {
        public string FullPath { get; private set; } = string.Empty;

        public bool CreateIfMissing { get; private set; }

        public string? ErrorCode { get; private set; }

        public string? Message { get; private set; }

        public static Request Parse(JsonElement args)
        {
            var request = new Request();
            var raw = AgentJson.GetString(args, "path");
            if (string.IsNullOrWhiteSpace(raw))
            {
                return request.Rejected(
                    "Pass path: the absolute path of the shared parameter file (.txt).");
            }

            var text = raw!.Trim().Trim('"');
            if (text.IndexOf('%') >= 0 && Environment.ExpandEnvironmentVariables(text) != text)
            {
                return request.Rejected(
                    "The path '" + text + "' contains an environment variable. Pass the " +
                    "expanded absolute path (for example the real user folder instead of " +
                    "%USERPROFILE%).");
            }

            if (!Path.IsPathRooted(text))
            {
                return request.Rejected(
                    "The path '" + text + "' is not absolute. Pass the full path of the " +
                    "shared parameter file, for example D:\\Project\\shared-parameters.txt.");
            }

            try
            {
                request.FullPath = Path.GetFullPath(text);
            }
            catch (Exception ex)
            {
                return request.Rejected(
                    "The path '" + text + "' is not a valid file path: " + Describe(ex) + ".");
            }

            if (Directory.Exists(request.FullPath))
            {
                return request.Rejected(
                    "The path '" + request.FullPath + "' is a folder. Pass the .txt file " +
                    "itself, not its folder.");
            }

            if (!string.Equals(
                    Path.GetExtension(request.FullPath),
                    ".txt",
                    StringComparison.OrdinalIgnoreCase))
            {
                return request.Rejected(
                    "The shared parameter file must be a .txt file; '" +
                    Path.GetFileName(request.FullPath) + "' has another extension.");
            }

            request.CreateIfMissing = AgentJson.GetBool(args, "createIfMissing", false);
            return request;
        }

        private Request Rejected(string message)
        {
            ErrorCode = "invalid_arguments";
            Message = message;
            return this;
        }
    }
}
