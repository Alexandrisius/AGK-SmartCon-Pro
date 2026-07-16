using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;
using SmartCon.FamilyManager.Services.Import;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public sealed class FileFamilyBatchImportExecutorTests
{
    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;
        public SyncProgress(Action<T> handler) => _handler = handler;
        public void Report(T value) => _handler(value);
    }

    private readonly FakeFileFamilyStagingService _staging = new();
    private readonly Mock<IFamilyImportService> _importService = new();
    private readonly Mock<IFamilyDataImportService> _dataImportService = new();
    private readonly Mock<ISharedNestedFamilyRepository> _sharedNestedRepository = new();

    private FileFamilyBatchImportExecutor CreateExecutor() => new(
        _staging,
        _importService.Object,
        _dataImportService.Object,
        _sharedNestedRepository.Object,
        revitVersion: 2025);

    private static FamilyBatchImportItem MakeItem(
        string fileName,
        FamilyBatchImportAction action = FamilyBatchImportAction.IncrementVersion)
    {
        return new FamilyBatchImportItem(
            FilePath: $@"C:\fake\{fileName}.rfa",
            FileName: fileName,
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New,
            ExistingCatalogItemId: null,
            ExistingVersionLabel: null,
            TargetCategoryId: null,
            TargetCategoryName: null,
            FamilySource: "loadable",
            TypeCount: null,
            RevitCategory: null)
        {
            Action = action
        };
    }

    private static FamilyImportResult OkResult(string fileName) => new(
        Success: true,
        CatalogItemId: null,
        VersionId: null,
        FileId: null,
        FileName: fileName,
        VersionLabel: "v1",
        ErrorMessage: null);

    private void SetupImportReturns(FamilyImportResult result)
    {
        _importService
            .Setup(s => s.ImportBatchAsync(
                It.IsAny<IReadOnlyList<FamilyBatchImportItem>>(),
                It.IsAny<string?>(),
                It.IsAny<IProgress<FamilyImportProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<FamilyBatchImportItem> items, string? _, IProgress<FamilyImportProgress>? _, CancellationToken _) =>
                new FamilyBatchImportResult(
                    new[] { result },
                    items.Count,
                    result.Success ? 1 : 0,
                    0,
                    result.Success ? 0 : 1));
    }

    [Fact]
    public async Task ExecuteAsync_AllSucceed_ReportsSuccessPerItem()
    {
        SetupImportReturns(OkResult("a.rfa"));
        var executor = CreateExecutor();
        var items = new[] { MakeItem("a"), MakeItem("b"), MakeItem("c") };
        var reports = new List<FamilyBatchImportProgress>();

        var result = await executor.ExecuteAsync(
            items, null, new SyncProgress<FamilyBatchImportProgress>(reports.Add), null, CancellationToken.None);

        Assert.Equal(3, result.SuccessCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.ErrorCount);
        Assert.False(result.WasStopped);
        Assert.Equal(3, reports.Count(r => r.ItemState == FamilyBatchImportRowState.Success));
        Assert.Equal(1, _staging.CloseAllCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_SkipAction_MarksSkippedWithoutImport()
    {
        var executor = CreateExecutor();
        var items = new[] { MakeItem("a", FamilyBatchImportAction.Skip), MakeItem("b") };
        SetupImportReturns(OkResult("b.rfa"));
        var reports = new List<FamilyBatchImportProgress>();

        var result = await executor.ExecuteAsync(
            items, null, new SyncProgress<FamilyBatchImportProgress>(reports.Add), null, CancellationToken.None);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Single(reports, r => r.ItemState == FamilyBatchImportRowState.Skipped);
        _importService.Verify(
            s => s.ImportBatchAsync(
                It.IsAny<IReadOnlyList<FamilyBatchImportItem>>(),
                It.IsAny<string?>(),
                It.IsAny<IProgress<FamilyImportProgress>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ItemFails_ContinuesWithRemainingItems()
    {
        var failResult = new FamilyImportResult(
            Success: false,
            CatalogItemId: null,
            VersionId: null,
            FileId: null,
            FileName: "a.rfa",
            VersionLabel: null,
            ErrorMessage: "boom");
        _importService
            .SetupSequence(s => s.ImportBatchAsync(
                It.IsAny<IReadOnlyList<FamilyBatchImportItem>>(),
                It.IsAny<string?>(),
                It.IsAny<IProgress<FamilyImportProgress>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FamilyBatchImportResult(new[] { failResult }, 1, 0, 0, 1))
            .ReturnsAsync(new FamilyBatchImportResult(new[] { OkResult("b.rfa") }, 1, 1, 0, 0));

        var executor = CreateExecutor();
        var items = new[] { MakeItem("a"), MakeItem("b") };
        var reports = new List<FamilyBatchImportProgress>();

        var result = await executor.ExecuteAsync(
            items, null, new SyncProgress<FamilyBatchImportProgress>(reports.Add), null, CancellationToken.None);

        Assert.Equal(1, result.SuccessCount);
        Assert.Equal(1, result.ErrorCount);
        var errorReport = Assert.Single(reports, r => r.ItemState == FamilyBatchImportRowState.Error);
        Assert.Equal("boom", errorReport.ItemError);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledAtCheckpoint_StopsBeforeRemainingItems()
    {
        SetupImportReturns(OkResult("x.rfa"));
        var executor = CreateExecutor();
        var items = new[] { MakeItem("a"), MakeItem("b"), MakeItem("c") };
        using var cts = new CancellationTokenSource();

        var progress = new SyncProgress<FamilyBatchImportProgress>(p =>
        {
            if (p.ItemState == FamilyBatchImportRowState.Success)
            {
                cts.Cancel();
            }
        });

        var result = await executor.ExecuteAsync(items, null, progress, null, cts.Token);

        Assert.True(result.WasStopped);
        Assert.Equal(1, result.SuccessCount);
        _importService.Verify(
            s => s.ImportBatchAsync(
                It.IsAny<IReadOnlyList<FamilyBatchImportItem>>(),
                It.IsAny<string?>(),
                It.IsAny<IProgress<FamilyImportProgress>?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
        Assert.Equal(1, _staging.CloseAllCallCount);
    }

    [Fact]
    public async Task ExecuteAsync_PausedThenResumed_ProcessesAllItems()
    {
        SetupImportReturns(OkResult("x.rfa"));
        var executor = CreateExecutor();
        var items = new[] { MakeItem("a"), MakeItem("b") };
        var gate = new PauseGate();
        var reports = new List<FamilyBatchImportProgress>();

        gate.Pause();
        var run = executor.ExecuteAsync(
            items, null, new SyncProgress<FamilyBatchImportProgress>(reports.Add), gate, CancellationToken.None);

        await Task.Delay(200);
        Assert.False(run.IsCompleted);

        gate.Resume();
        var result = await run.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(2, result.SuccessCount);
        Assert.False(result.WasStopped);
        Assert.Contains(reports, r => r.Phase == FamilyBatchImportPhase.Paused);
    }
}
