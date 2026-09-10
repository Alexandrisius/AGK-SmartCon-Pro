using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// RevitMiniProjectActualizationService (Issue #189): backfill of the ES
/// marker into an existing staged .rvt — open → mark → Document.Save() in
/// place → Revit backup cleanup → read-only restore → close. Covers:
/// Marked outcome with the marker persisting after reopen, backup files
/// (name.NNNN.rvt) deleted, read-only attribute restored, idempotency
/// (AlreadyMarked without rewrite), and the Missing outcome.
/// </summary>
public sealed class MiniProjectActualizationServiceTests : RevitApiTest
{
    private Document? _contextDoc;
    private RevitMiniProjectActualizationService? _service;
    private RevitMiniProjectMarker? _marker;
    private string? _tempDirToCleanup;

    private RevitMiniProjectActualizationService Service => _service!;
    private RevitMiniProjectMarker Marker => _marker!;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void CreateService()
    {
        _contextDoc = Application.NewProjectDocument(UnitSystem.Metric);
        var transactions = new RevitTransactionService(new StubRevitContext(_contextDoc));
        _marker = new RevitMiniProjectMarker(transactions, new SystemClock());
        _service = new RevitMiniProjectActualizationService(
            new InlineAwaitableEvent(Application), _marker);
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Cleanup()
    {
        _contextDoc?.Close(false);
        _contextDoc = null;
        if (_tempDirToCleanup is not null && Directory.Exists(_tempDirToCleanup))
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(_tempDirToCleanup, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                Directory.Delete(_tempDirToCleanup, recursive: true);
            }
            catch
            {
                // Temp cleanup is best-effort — a locked file must not fail the suite.
            }
            _tempDirToCleanup = null;
        }
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task MarkManagedFile_UnmarkedStagedFile_MarkedPersistsAfterReopen_BackupsDeleted()
    {
        var path = CreateUnmarkedStagedFile();

        var outcome = await Service.MarkManagedFileAsync(path, "item-42");
        await Assert.That(outcome.Status).IsEqualTo(MiniProjectMarkFileStatus.Marked);

        using (var reopened = Application.OpenDocumentFile(path))
        {
            await Assert.That(Marker.IsMiniProject(reopened)).IsTrue();
            await Assert.That(Marker.ReadCatalogItemId(reopened)).IsEqualTo("item-42");
            reopened.Close(false);
        }

        var directory = Path.GetDirectoryName(path)!;
        var backups = Directory.GetFiles(directory, "Провода.*.rvt")
            .Where(f => !string.Equals(f, path, StringComparison.OrdinalIgnoreCase))
            .ToList();
        await Assert.That(backups.Count).IsEqualTo(0);
        await Assert.That(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly)).IsTrue();
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task MarkManagedFile_AlreadyMarked_SkipsRewrite()
    {
        var path = CreateUnmarkedStagedFile();

        var first = await Service.MarkManagedFileAsync(path, "item-42");
        await Assert.That(first.Status).IsEqualTo(MiniProjectMarkFileStatus.Marked);

        var second = await Service.MarkManagedFileAsync(path, "item-42");
        await Assert.That(second.Status).IsEqualTo(MiniProjectMarkFileStatus.AlreadyMarked);
    }

    [Test]
    [HookExecutor<RevitThreadExecutor>]
    public async Task MarkManagedFile_MissingFile_ReturnsMissing()
    {
        var outcome = await Service.MarkManagedFileAsync(
            Path.Combine(Path.GetTempPath(), $"smartcon-missing-{Guid.NewGuid().ToString("N")}.rvt"), "item-42");

        await Assert.That(outcome.Status).IsEqualTo(MiniProjectMarkFileStatus.Missing);
    }

    /// <summary>
    /// Creates a staged-looking project file WITHOUT the marker (a legacy
    /// pre-#188 staged .rvt): new project document saved to a temp dir and
    /// made read-only like managed storage (I-16).
    /// </summary>
    private string CreateUnmarkedStagedFile()
    {
        _tempDirToCleanup = Path.Combine(Path.GetTempPath(), $"smartcon-mka-{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDirToCleanup);
        var path = Path.Combine(_tempDirToCleanup, "Провода.rvt");

        var newDoc = Application.NewProjectDocument(UnitSystem.Metric);
        newDoc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
        newDoc.Close(false);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        return path;
    }

    /// <summary>
    /// Tests run on the Revit thread (RevitThreadExecutor) — the awaitable
    /// event is faked by invoking the action inline with the live
    /// ApplicationServices.Application (the test host has no UIApplication —
    /// the production service accepts both context types), mirroring
    /// FakeFamilyManagerAwaitableEvent of the unit suite without a
    /// cross-assembly dependency.
    /// </summary>
    private sealed class InlineAwaitableEvent : IFamilyManagerAwaitableEvent
    {
        private readonly Autodesk.Revit.ApplicationServices.Application _app;

        public InlineAwaitableEvent(Autodesk.Revit.ApplicationServices.Application app)
        {
            _app = app;
        }

        public Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default)
        {
            actionWithApp(_app);
            return Task.CompletedTask;
        }

        public Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default)
            => Task.FromResult(funcWithApp(_app));

        public Task RaiseAsyncTask(Func<object, Task> asyncActionWithApp, CancellationToken ct = default)
            => asyncActionWithApp(_app);

        public void ProcessQueue(object revitApp) { }
        public void Initialize(Action onRaise) { }
    }
}
