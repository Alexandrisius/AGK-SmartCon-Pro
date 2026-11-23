using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.Revit.Agent.Tools;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Makes a shared parameter file (ФОП) the active one for this Revit session:
/// attaches an existing .txt file by absolute path, or creates a new empty
/// file there first (<c>createIfMissing=true</c>) and attaches it. Without an
/// active file <c>list_shared_parameters</c> and
/// <c>add_project_shared_parameter(createDefinition:true)</c> fail with
/// <c>shared_parameter_file_missing</c>; Revit has no shared parameter file
/// configured by default.
///
/// Deliberately NOT an <see cref="AgentWriteToolBase"/>: the active shared
/// parameter file is an application-level setting of the Revit user, not a
/// document change. The base pipeline requires an open active document and
/// refuses workshared ones, which would be a false refusal for this operation,
/// and its dry run opens a transaction in the document for the sake of a
/// setting that has nothing to do with the document. The dry-run/confirmation
/// contract is the one of <see cref="DiscardChangeSetTool"/>:
/// <c>dryRun=true</c> (the default) changes nothing, <c>dryRun=false</c> needs
/// the user's approval (<c>confirmation_required</c> otherwise). Nothing here
/// is a change set: the setting is not reverted by undo_last, the previous file
/// is restored by calling this tool again with its path.
/// </summary>
public sealed partial class SetSharedParameterFileTool : IAgentTool
{
    public const string ToolName = "set_shared_parameter_file";

    private readonly IRevitContext _revitContext;

    public SetSharedParameterFileTool(IRevitContext revitContext)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
    }

    public AgentToolDefinition Definition { get; } = new()
    {
        Name = ToolName,
        Description =
            "Sets the active shared parameter file (ФОП) of this Revit session: attaches an " +
            "existing .txt file by absolute path, or with createIfMissing=true creates a new " +
            "empty file at that path first and attaches it (an existing file is never " +
            "overwritten). Use it when list_shared_parameters or add_project_shared_parameter " +
            "fail with shared_parameter_file_missing (Revit has no shared parameter file " +
            "configured by default). Returns the absolute path, the previous file, the number " +
            "of groups and definitions. It is an application-level setting of the current Revit " +
            "user, not a document change: it stays after the document is closed, creates no " +
            "change set and is not reverted by undo_last; to restore the previous file call this " +
            "tool again with its path. dryRun=true (default) checks the path without touching " +
            "anything (file or folder, read-only attribute, groups and definitions of an " +
            "existing file) and changes nothing - write permission is verified on apply. " +
            "dryRun=false requires user confirmation.",
        Risk = AgentToolRisk.Write,
        SupportsDryRun = true,
        Category = "Write",
        InputSchemaJson = @"{""type"":""object"",""properties"":{""path"":{""type"":""string"",""description"":""Absolute path of the shared parameter file (.txt): an existing file to attach, or the file to create when createIfMissing=true""},""createIfMissing"":{""type"":""boolean"",""default"":false,""description"":""Create a new empty shared parameter file at path when no file exists there; an existing file is never overwritten""},""dryRun"":{""type"":""boolean"",""default"":true}},""required"":[""path""],""additionalProperties"":false}"
    };

    public async Task<ToolResult> ExecuteAsync(
        AgentToolContext context,
        JsonElement args,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Default-safe: nothing is applied unless the caller asks for it.
        var dryRun = AgentJson.GetBool(args, "dryRun", true);
        var request = Request.Parse(args);
        if (request.ErrorCode is not null)
            return ToolResult.Fail(request.ErrorCode, request.Message!);

        if (!dryRun && !context.WriteApproved)
        {
            return ToolResult.Fail(
                "confirmation_required",
                "Changing the active shared parameter file requires explicit user " +
                "confirmation. Call the tool with dryRun=true first, show the planned change, " +
                "and only then repeat the call with dryRun=false — the user will be asked to " +
                "approve it.");
        }

        return await context.Revit
            .RaiseAsync(app => ExecuteOnRevitThread(app, request, dryRun), ct)
            .ConfigureAwait(false);
    }

    private ToolResult ExecuteOnRevitThread(object app, Request request, bool dryRun)
    {
        var application = ResolveApplication(app);
        if (application is null)
        {
            return ToolResult.Fail(
                "no_active_document",
                "The Revit application is not available in this session. Open a project " +
                "and try again.");
        }

        var path = request.FullPath;
        using var _scope = SmartConLogger.BeginScope(
            "AgentTool",
            ("Tool", ToolName),
            ("Method", nameof(ExecuteOnRevitThread)),
            ("FilePath", Path.GetFileName(path)),
            ("DryRun", dryRun));

        var previousPath = ReadCurrentSetting(application, out var settingFailure);
        if (settingFailure is not null)
            return settingFailure;

        var previousFullPath = TryGetFullPath(previousPath);
        if (previousFullPath is not null
            && string.Equals(previousFullPath, path, StringComparison.OrdinalIgnoreCase))
        {
            return AlreadyActive(application, path, dryRun);
        }

        bool fileExists;
        try
        {
            fileExists = File.Exists(path);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail(
                "invalid_arguments",
                "The path '" + path + "' could not be checked: " + Describe(ex) + ".");
        }

        if (!fileExists && !request.CreateIfMissing)
        {
            return ToolResult.Fail(
                "shared_parameter_file_not_found",
                "No file exists at '" + path + "' and createIfMissing is false. Nothing was " +
                "changed. [Action: point path at the existing file, or pass " +
                "createIfMissing=true when creating a new shared parameter file there was " +
                "agreed with the user]");
        }

        if (!fileExists)
        {
            var folder = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                return ToolResult.Fail(
                    "directory_not_found",
                    "The folder '" + folder + "' does not exist, so the shared parameter file '" +
                    Path.GetFileName(path) + "' cannot be created there. Nothing was changed. " +
                    "[Action: create the folder first or pass a path inside an existing folder]");
            }
        }

        var warnings = new List<string>();
        var preview = fileExists ? InspectFile(path, warnings) : null;

        return dryRun
            ? Preview(request, path, previousPath, fileExists, preview, warnings)
            : Apply(application, path, previousPath, fileExists, preview, warnings);
    }

    private static string ReadCurrentSetting(Application application, out ToolResult? failure)
    {
        failure = null;
        try
        {
            var current = application.SharedParametersFilename;
            if (string.IsNullOrWhiteSpace(current))
                return string.Empty;

            return current.Trim().Trim('"');
        }
        catch (Exception ex)
        {
            failure = ToolResult.Fail(
                "tool_failed",
                "The current shared parameter file setting could not be read: " + Describe(ex) +
                ". [Action: check the shared parameter file in Manage > Shared Parameters " +
                "(Revit UI)]");
            return string.Empty;
        }
    }

    private static ToolResult AlreadyActive(Application application, string path, bool dryRun)
    {
        var fileName = Path.GetFileName(path);
        var inventory = ReadInventory(application);
        if (inventory.ErrorCode is not null)
        {
            return ToolResult.Fail(
                inventory.ErrorCode,
                "The shared parameter file '" + fileName + "' is already the active one, but " +
                "Revit could not read it: " + inventory.Message +
                " [Action: point set_shared_parameter_file at another file, or check the file " +
                "in Manage > Shared Parameters]");
        }

        SmartConLogger.Info(
            "set_shared_parameter_file: '" + fileName + "' is already active" +
            (dryRun ? " (dry run)" : string.Empty));

        return ToolResult.Ok(
            AgentJson.Serialize(new
            {
                dryRun,
                action = "none",
                changed = false,
                noChanges = true,
                path,
                groupsCount = inventory.GroupNames.Count,
                definitionsCount = inventory.DefinitionsCount,
                groups = inventory.GroupNames,
                undoSupported = false
            }),
            "The shared parameter file '" + fileName + "' is already active: " +
            inventory.GroupNames.Count + " group(s), " + inventory.DefinitionsCount +
            " definition(s). Nothing was changed.");
    }

    private static ToolResult Preview(
        Request request,
        string path,
        string previousPath,
        bool fileExists,
        FilePreview? preview,
        List<string> warnings)
    {
        var fileName = Path.GetFileName(path);

        if (!fileExists)
        {
            warnings.Add(
                "A NEW empty shared parameter file will be created at '" + path +
                "': it has no groups and no definitions yet.");
        }
        else if (request.CreateIfMissing)
        {
            warnings.Add(
                "A file already exists at '" + path + "': it will be attached as is and not " +
                "overwritten (createIfMissing has no effect).");
        }

        if (!string.IsNullOrEmpty(previousPath))
        {
            warnings.Add(
                "The active file '" + Path.GetFileName(previousPath) +
                "' will be replaced for this Revit session; to restore it later call " +
                "set_shared_parameter_file(path='" + previousPath + "', dryRun=false).");
        }

        warnings.Add(
            "This changes the Revit application-level shared parameter file setting of the " +
            "current user, not the document: it stays after the document is closed, creates " +
            "no change set and is not reverted by undo_last.");
        warnings.Add(
            "The dry run does not touch the file system: write permission is verified only " +
            "on apply.");

        var payload = new Dictionary<string, object?>
        {
            ["dryRun"] = true,
            ["action"] = fileExists ? "attach_existing" : "create_new",
            ["changed"] = false,
            ["path"] = path,
            ["previousPath"] = string.IsNullOrEmpty(previousPath) ? null : previousPath,
            ["createIfMissing"] = request.CreateIfMissing,
            ["fileExists"] = fileExists,
            ["fileReadOnly"] = preview is { ReadOnly: true },
            ["applicationSetting"] = true,
            ["wouldChangeApplicationSetting"] = true
        };
        if (preview is not null)
        {
            payload["fileIsEmpty"] = preview.IsEmpty;
            payload["groupsCount"] = preview.GroupNames.Count;
            payload["definitionsCount"] = preview.DefinitionsCount;
            payload["groups"] = preview.GroupNames;
        }

        var message = fileExists
            ? "Dry run: the existing shared parameter file '" + fileName + "' would become " +
              "the active one (" + (preview?.GroupNames.Count ?? 0) + " group(s), " +
              (preview?.DefinitionsCount ?? 0) + " definition(s)). Nothing was modified."
            : "Dry run: a new empty shared parameter file '" + fileName +
              "' would be created and become the active one. Nothing was modified.";

        SmartConLogger.Info(
            "set_shared_parameter_file: dry run, would " +
            (fileExists ? "attach" : "create") + " '" + fileName + "'");

        return ToolResult.Ok(AgentJson.Serialize(payload), message, warnings: warnings);
    }

    private ToolResult Apply(
        Application application,
        string path,
        string previousPath,
        bool fileExists,
        FilePreview? preview,
        List<string> warnings)
    {
        var fileName = Path.GetFileName(path);
        var createdFile = false;

        if (preview is { ReadOnly: true })
        {
            warnings.Add(
                "The file '" + fileName + "' has the read-only attribute: new definitions " +
                "cannot be added to it (createDefinition will fail).");
        }

        try
        {
            if (!fileExists)
            {
                using (new FileStream(path, FileMode.CreateNew, FileAccess.Write))
                {
                }

                createdFile = true;
            }

            application.SharedParametersFilename = path;

            var inventory = ReadInventory(application);
            if (inventory.ErrorCode is not null)
            {
                var reason = inventory.Message ?? "OpenSharedParameterFile failed";
                RollBack(application, previousPath, path, createdFile, reason);
                return ToolResult.Fail(
                    inventory.ErrorCode,
                    "Revit refused '" + fileName + "' as a shared parameter file: " + reason +
                    " The previous setting was restored; nothing else was changed. " +
                    "[Action: check that the file is a Revit shared parameter file and pass " +
                    "another path]");
            }

            var hadPrevious = !string.IsNullOrEmpty(previousPath);
            var payload = new Dictionary<string, object?>
            {
                ["dryRun"] = false,
                ["action"] = createdFile ? "created_and_attached" : "attached",
                ["changed"] = true,
                ["path"] = path,
                ["previousPath"] = hadPrevious ? previousPath : null,
                ["fileCreated"] = createdFile,
                ["groupsCount"] = inventory.GroupNames.Count,
                ["definitionsCount"] = inventory.DefinitionsCount,
                ["groups"] = inventory.GroupNames,
                ["applicationSetting"] = true,
                ["undoSupported"] = false,
                ["revertPath"] = hadPrevious ? previousPath : null
            };

            if (createdFile)
            {
                warnings.Add(
                    "A new shared parameter file was created at '" + path +
                    "'. Definitions are added to it later, for example by " +
                    "add_project_shared_parameter(createDefinition=true).");
            }

            SmartConLogger.Info(
                "set_shared_parameter_file: " + (createdFile ? "created and attached '" : "attached '") +
                fileName + "' (" + inventory.GroupNames.Count + " group(s), " +
                inventory.DefinitionsCount + " definition(s))");

            var message = (createdFile
                    ? "Created a new shared parameter file and made it active: '" + fileName + "'"
                    : "The shared parameter file '" + fileName + "' is now active") +
                " (" + inventory.GroupNames.Count + " group(s), " + inventory.DefinitionsCount +
                " definition(s))." +
                (hadPrevious
                    ? " It replaces '" + Path.GetFileName(previousPath) +
                      "'; to restore it call set_shared_parameter_file(path='" + previousPath +
                      "', dryRun=false)."
                    : " There was no active shared parameter file before this call.") +
                " This is a Revit application setting, not a document change: undo_last does " +
                "not revert it.";

            return ToolResult.Ok(
                AgentJson.Serialize(payload),
                message,
                affectedCount: 1,
                warnings: warnings);
        }
        catch (Exception ex)
        {
            // Any failure after the file was created or the setting was changed
            // leaves the session exactly as it was before the call.
            var restored = RollBack(application, previousPath, path, createdFile, Describe(ex));
            var what = !fileExists ? "could not be created" : "could not be attached";
            return ToolResult.Fail(
                !fileExists ? "file_not_writable" : "tool_failed",
                "The shared parameter file '" + fileName + "' " + what + ": " + Describe(ex) +
                "." +
                (restored
                    ? " The previous setting was restored; nothing was changed."
                    : string.Empty) +
                " [Action: check that the folder exists and is writable for the current user, " +
                "and that the file is a Revit shared parameter file]");
        }
    }
}
