using System.IO;
using System.Reflection;
using SmartCon.FamilyManager.Services;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public class ActiveImportCleanupServiceTests : IDisposable
{
    private readonly string _sandboxRoot;
    private readonly ActiveImportCleanupService _sut = new();

    public ActiveImportCleanupServiceTests()
    {
        _sandboxRoot = Path.Combine(
            Path.GetTempPath(),
            "SmartCon.Tests.Cleanup." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_sandboxRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandboxRoot, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Invokes the production cleanup code against the test sandbox.
    /// Uses reflection because <c>CleanupImpl</c> is <c>internal</c>;
    /// this avoids exposing the seam in the public surface area while
    /// keeping the test honest (it runs the real implementation, not a mock).
    /// </summary>
    private void RunCleanup()
    {
        var method = typeof(ActiveImportCleanupService).GetMethod(
            "CleanupImpl",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        method!.Invoke(_sut, new object?[] { _sandboxRoot, CancellationToken.None });
    }

    private void StageFMLoad(string folderName, params (string File, string Content)[] files)
    {
        var dir = Path.Combine(_sandboxRoot, "FMLoad", folderName);
        Directory.CreateDirectory(dir);
        foreach (var (file, content) in files)
        {
            File.WriteAllText(Path.Combine(dir, file), content);
        }
    }

    private void StageSystemFamily(string folderName, params (string File, string Content)[] files)
    {
        var dir = Path.Combine(_sandboxRoot, "SystemFamilyLoadFromProject", folderName);
        Directory.CreateDirectory(dir);
        foreach (var (file, content) in files)
        {
            File.WriteAllText(Path.Combine(dir, file), content);
        }
    }

    [Fact]
    public void Cleanup_RemovesFMLoadStagingSubfolders()
    {
        StageFMLoad(Guid.NewGuid().ToString(), ("Test.rfa", "fake-rfa"), ("Test.txt", "fake-txt"));

        RunCleanup();

        Assert.True(Directory.Exists(Path.Combine(_sandboxRoot, "FMLoad")),
            "FMLoad root itself must stay (only its children are staging entries).");
        Assert.Empty(Directory.GetDirectories(Path.Combine(_sandboxRoot, "FMLoad")));
    }

    [Fact]
    public void Cleanup_RemovesSystemFamilyLoadFromProjectSubfolders()
    {
        StageSystemFamily(Guid.NewGuid().ToString(),
            ("Pipes.rvt", "fake-rvt"),
            ("Pipes.rvt.types.json", "[]"));

        RunCleanup();

        Assert.True(Directory.Exists(Path.Combine(_sandboxRoot, "SystemFamilyLoadFromProject")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_sandboxRoot, "SystemFamilyLoadFromProject")));
    }

    [Fact]
    public void Cleanup_RemovesNestedFilesRecursively()
    {
        var dir = Path.Combine(_sandboxRoot, "FMLoad", Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        var nested = Path.Combine(dir, "nested", "deep");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "deep.txt"), "deep");

        RunCleanup();

        Assert.Empty(Directory.GetDirectories(Path.Combine(_sandboxRoot, "FMLoad")));
    }

    [Fact]
    public void Cleanup_LeavesNonStagingSubfoldersAlone()
    {
        var keep = Path.Combine(_sandboxRoot, "logs");
        Directory.CreateDirectory(keep);
        File.WriteAllText(Path.Combine(keep, "app.log"), "keep me");

        StageFMLoad(Guid.NewGuid().ToString(), ("Test.rfa", "fake"));

        RunCleanup();

        Assert.True(Directory.Exists(keep), "non-staging subfolder should NOT be removed");
        Assert.True(File.Exists(Path.Combine(keep, "app.log")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_sandboxRoot, "FMLoad")));
    }

    [Fact]
    public void Cleanup_HandlesMissingRoot()
    {
        // Sandbox is empty (no SmartCon tree) — should be a no-op, no throw.
        RunCleanup();

        Assert.False(Directory.Exists(Path.Combine(_sandboxRoot, "FMLoad")));
        Assert.False(Directory.Exists(Path.Combine(_sandboxRoot, "SystemFamilyLoadFromProject")));
    }

    [Fact]
    public void Cleanup_HandlesMissingStagingSubfolders()
    {
        // Sandbox exists, but no FMLoad / SystemFamilyLoadFromProject under it.
        RunCleanup();

        Assert.False(Directory.Exists(Path.Combine(_sandboxRoot, "FMLoad")));
    }

    [Fact]
    public void Cleanup_HandlesEmptyStagingSubfolders()
    {
        Directory.CreateDirectory(Path.Combine(_sandboxRoot, "FMLoad"));
        Directory.CreateDirectory(Path.Combine(_sandboxRoot, "SystemFamilyLoadFromProject"));

        RunCleanup();

        Assert.True(Directory.Exists(Path.Combine(_sandboxRoot, "FMLoad")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_sandboxRoot, "FMLoad")));
        Assert.True(Directory.Exists(Path.Combine(_sandboxRoot, "SystemFamilyLoadFromProject")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_sandboxRoot, "SystemFamilyLoadFromProject")));
    }

    [Fact]
    public void Cleanup_IsIdempotent()
    {
        StageFMLoad(Guid.NewGuid().ToString(), ("Test.rfa", "fake"));

        RunCleanup();
        RunCleanup();
        RunCleanup();

        Assert.Empty(Directory.GetDirectories(Path.Combine(_sandboxRoot, "FMLoad")));
    }

    [Fact]
    public void Cleanup_RemovesMultipleStagingSubfoldersInOnePass()
    {
        for (var i = 0; i < 3; i++)
            StageFMLoad(Guid.NewGuid().ToString(), ($"Test{i}.rfa", "fake"));
        for (var i = 0; i < 2; i++)
            StageSystemFamily(Guid.NewGuid().ToString(), ($"Sys{i}.rvt", "fake"));

        RunCleanup();

        Assert.Empty(Directory.GetDirectories(Path.Combine(_sandboxRoot, "FMLoad")));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_sandboxRoot, "SystemFamilyLoadFromProject")));
    }

    [Fact]
    public void Cleanup_RemovesReadOnlyFiles()
    {
        var folder = Path.Combine(_sandboxRoot, "FMLoad", Guid.NewGuid().ToString());
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "Locked.rfa");
        File.WriteAllText(file, "fake");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        RunCleanup();

        Assert.Empty(Directory.GetDirectories(Path.Combine(_sandboxRoot, "FMLoad")));
    }

    [Fact]
    public async Task CleanupAsync_RespectsCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _sut.CleanupAfterImportAsync(cts.Token));
    }

    [Fact]
    public void Cleanup_DoesNotTouchUnrelatedTempFiles()
    {
        // A real user might have other folders under %TEMP% that we
        // must NEVER touch. Simulate that by creating a sibling folder
        // at the same level as our staging roots.
        var unrelated = Path.Combine(_sandboxRoot, "SomeOtherApp", "cache");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "data.bin"), "do not touch");

        StageFMLoad(Guid.NewGuid().ToString(), ("Test.rfa", "fake"));

        RunCleanup();

        Assert.True(Directory.Exists(unrelated), "unrelated folder must NOT be removed");
        Assert.True(File.Exists(Path.Combine(unrelated, "data.bin")));
    }
}
