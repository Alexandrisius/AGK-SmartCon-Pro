using System.Text.Json;
using Autodesk.Revit.ApplicationServices;
using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Services.Interfaces.Agent;
using SmartCon.IntegrationTests.Agent.Support;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.Agent;
using SmartCon.Revit.Agent.Tools.Families;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.IntegrationTests.Agent;

/// <summary>
/// set_shared_parameter_file (night run 2, C5-02/C7-01): dry run checks the
/// path without touching the file system, apply attaches or creates the file
/// and reports the inventory Revit read from it, an unapproved apply is
/// refused, a file Revit refuses restores the previous setting, and the
/// shared_parameter_file_missing errors of the neighbouring tools name this
/// tool in their [Action]. The user's real shared parameter file setting is
/// snapshotted before every test and restored after it.
/// </summary>
public sealed class AgentSharedParameterFileToolTests : RevitApiTest
{
    private Document? _document;
    private RevitThreadEvent? _event;
    private string? _previousSharedFile;

    private Document Doc => _document!;

    private RevitThreadEvent Event => _event!;

    [Before(Test)]
    public void Seed()
    {
        _document = Application.NewProjectDocument(UnitSystem.Metric);
        _event = new RevitThreadEvent(Application);
        _previousSharedFile = SafeReadSharedFile(Application);
    }

    [After(Test)]
    public void Close()
    {
        RestoreSharedFile(Application, _previousSharedFile);
        TryCloseDocument();
        TryDeleteDirectory(TempDirectory());
    }

    [Test]
    public async Task DryRun_MissingFileWithoutCreateIfMissing_FailsTypedAndCreatesNothing()
    {
        var path = TempPath("missing-" + Guid.NewGuid().ToString("N") + ".txt");

        var result = await Run(new SetSharedParameterFileTool(new StubRevitContext(Doc)),
            new { path }, writeApproved: false);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("shared_parameter_file_not_found");
        await Assert.That(result.Message ?? string.Empty).Contains("createIfMissing=true");
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(SafeReadSharedFile(Application)).IsEqualTo(_previousSharedFile);
    }

    [Test]
    public async Task DryRun_NewFileWithCreateIfMissing_PlansCreationWithoutTouchingDisk()
    {
        var path = TempPath("planned-" + Guid.NewGuid().ToString("N") + ".txt");

        var result = await Run(new SetSharedParameterFileTool(new StubRevitContext(Doc)),
            new { path, createIfMissing = true }, writeApproved: false);

        await Assert.That(result.Success).IsTrue();
        var payload = result.Data!.Value;
        await Assert.That(payload.GetProperty("dryRun").GetBoolean()).IsTrue();
        await Assert.That(payload.GetProperty("action").GetString()).IsEqualTo("create_new");
        await Assert.That(payload.GetProperty("changed").GetBoolean()).IsFalse();
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(SafeReadSharedFile(Application)).IsEqualTo(_previousSharedFile);
        // The dry run states that write permission is verified only on apply.
        await Assert.That(JoinWarnings(result)).Contains("write permission is verified only on apply");
    }

    [Test]
    public async Task DryRun_RelativePath_IsRejectedAsInvalidArguments()
    {
        var result = await Run(new SetSharedParameterFileTool(new StubRevitContext(Doc)),
            new { path = "folder/shared.txt" }, writeApproved: false);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("invalid_arguments");
        await Assert.That(result.Message ?? string.Empty).Contains("not absolute");
    }

    [Test]
    public async Task Apply_WithoutApproval_IsRefusedAndChangesNothing()
    {
        var path = TempPath("unapproved-" + Guid.NewGuid().ToString("N") + ".txt");

        var result = await Run(new SetSharedParameterFileTool(new StubRevitContext(Doc)),
            new { path, createIfMissing = true, dryRun = false }, writeApproved: false);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("confirmation_required");
        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(SafeReadSharedFile(Application)).IsEqualTo(_previousSharedFile);
    }

    [Test]
    public async Task Apply_CreateIfMissing_CreatesEmptyFileAndAttachesIt()
    {
        var path = TempPath("created-" + Guid.NewGuid().ToString("N") + ".txt");

        var result = await Run(new SetSharedParameterFileTool(new StubRevitContext(Doc)),
            new { path, createIfMissing = true, dryRun = false }, writeApproved: true);

        await Assert.That(result.Success).IsTrue();
        var payload = result.Data!.Value;
        await Assert.That(payload.GetProperty("action").GetString())
            .IsEqualTo("created_and_attached");
        await Assert.That(payload.GetProperty("changed").GetBoolean()).IsTrue();
        await Assert.That(payload.GetProperty("fileCreated").GetBoolean()).IsTrue();
        await Assert.That(payload.GetProperty("groupsCount").GetInt32()).IsEqualTo(0);
        await Assert.That(payload.GetProperty("undoSupported").GetBoolean()).IsFalse();
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(
            string.Equals(Application.SharedParametersFilename, path, StringComparison.OrdinalIgnoreCase))
            .IsTrue();

        // The attached empty file is a valid shared parameter file for Revit.
        var opened = Application.OpenSharedParameterFile();
        await Assert.That(opened is not null).IsTrue();
    }

    [Test]
    public async Task Apply_FileRevitRefuses_RestoresPreviousSetting()
    {
        // A file with content that is not a Revit shared parameter file: the
        // assignment succeeds, OpenSharedParameterFile refuses, and the tool
        // must restore the previous setting instead of leaving the session
        // with a broken active file.
        var path = TempPath("garbage-" + Guid.NewGuid().ToString("N") + ".txt");
        await File.WriteAllTextAsync(path, "this is not a Revit shared parameter file");

        var result = await Run(new SetSharedParameterFileTool(new StubRevitContext(Doc)),
            new { path, dryRun = false }, writeApproved: true);

        await Assert.That(result.Success).IsFalse();
        await Assert.That(result.ErrorCode).IsEqualTo("shared_parameter_file_invalid");
        await Assert.That(result.Message ?? string.Empty).Contains("previous setting was restored");
        await Assert.That(SafeReadSharedFile(Application)).IsEqualTo(_previousSharedFile);
    }

    [Test]
    public async Task Apply_AlreadyActiveFile_ReportsNoChange()
    {
        var path = TempPath("already-" + Guid.NewGuid().ToString("N") + ".txt");
        var attached = await Run(new SetSharedParameterFileTool(new StubRevitContext(Doc)),
            new { path, createIfMissing = true, dryRun = false }, writeApproved: true);
        if (!attached.Success)
            Skip.Test("Не удалось подключить файл: " + attached.Message);

        var again = await Run(new SetSharedParameterFileTool(new StubRevitContext(Doc)),
            new { path, dryRun = false }, writeApproved: true);

        await Assert.That(again.Success).IsTrue();
        var payload = again.Data!.Value;
        await Assert.That(payload.GetProperty("action").GetString()).IsEqualTo("none");
        await Assert.That(payload.GetProperty("changed").GetBoolean()).IsFalse();
        await Assert.That(payload.GetProperty("noChanges").GetBoolean()).IsTrue();
    }

    [Test]
    public async Task SharedParameterFileMissing_ErrorsNameTheToolInAction()
    {
        // list_shared_parameters without an active file points the model at
        // set_shared_parameter_file instead of only at the Revit UI.
        var previous = Application.SharedParametersFilename;
        Application.SharedParametersFilename = string.Empty;
        try
        {
            var listed = await Run(new ListSharedParametersTool(new StubRevitContext(Doc)),
                new { }, writeApproved: false);
            await Assert.That(listed.Success).IsFalse();
            await Assert.That(listed.ErrorCode).IsEqualTo("shared_parameter_file_missing");
            await Assert.That(listed.Message ?? string.Empty).Contains("set_shared_parameter_file");
        }
        finally
        {
            Application.SharedParametersFilename = previous;
        }
    }

    // ── Support ────────────────────────────────────────────────────────

    private async Task<ToolResult> Run(IAgentTool tool, object args, bool writeApproved)
    {
        var context = new AgentToolContext(
            Event,
            AgentDocumentIdentity.GetDocumentId(Doc),
            Guid.Empty)
        {
            WriteApproved = writeApproved
        };
        return await tool.ExecuteAsync(context, Parse(args), CancellationToken.None);
    }

    private static JsonElement Parse(object args)
    {
        var json = JsonSerializer.Serialize(args);
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string SafeReadSharedFile(Application application)
    {
        try
        {
            return application.SharedParametersFilename ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static void RestoreSharedFile(Application application, string? previous)
    {
        try
        {
            application.SharedParametersFilename = previous ?? string.Empty;
        }
        catch (Exception)
        {
            // Restoring the shared parameter file is best-effort.
        }
    }

    private static string TempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "SmartConAgentSetSharedFile");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string TempPath(string fileName) =>
        Path.Combine(TempDirectory(), fileName);

    private static string JoinWarnings(ToolResult result) =>
        result.Warnings is null ? string.Empty : string.Join(" ", result.Warnings);

    private void TryCloseDocument()
    {
        try
        {
            _document?.Close(false);
        }
        catch (Exception)
        {
            // The document may already be closed by a test scenario.
        }
    }

    private static void TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
        catch (Exception)
        {
            // Temp cleanup is best-effort.
        }
    }
}
