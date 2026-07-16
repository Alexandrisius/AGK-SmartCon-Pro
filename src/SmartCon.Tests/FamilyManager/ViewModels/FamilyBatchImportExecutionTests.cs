using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class FamilyBatchImportExecutionTests
{
    private readonly Mock<IFamilyManagerDialogService> _dialogMock = new();
    private readonly Mock<IFamilyManagerViewModelFactory> _factoryMock = new();
    private readonly FakeBatchImportExecutor _executor = new();

    private sealed class FakeBatchImportExecutor : IFamilyBatchImportExecutor
    {
        public Func<IReadOnlyList<FamilyBatchImportItem>, IProgress<FamilyBatchImportProgress>?, PauseGate?, CancellationToken, Task<FamilyBatchImportExecutionResult>>? Handler { get; set; }
        public int CallCount { get; private set; }

        public Task<FamilyBatchImportExecutionResult> ExecuteAsync(
            IReadOnlyList<FamilyBatchImportItem> items,
            string? categoryId,
            IProgress<FamilyBatchImportProgress>? progress,
            PauseGate? pauseGate,
            CancellationToken ct)
        {
            CallCount++;
            if (Handler is not null)
            {
                return Handler(items, progress, pauseGate, ct);
            }
            return Task.FromResult(new FamilyBatchImportExecutionResult(items.Count, 0, 0, false));
        }
    }

    private static FamilyBatchImportItem MakeItem(string fileName)
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
            RevitCategory: null);
    }

    private FamilyBatchImportViewModel CreateVm(params FamilyBatchImportItem[] items)
    {
        return new FamilyBatchImportViewModel(
            items,
            _dialogMock.Object,
            _factoryMock.Object,
            executor: _executor);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
            {
                throw new TimeoutException("Condition was not reached in time");
            }
            await Task.Delay(25);
        }
    }

    private static async Task<FamilyBatchImportExecutionResult> RunAllHandler(
        IReadOnlyList<FamilyBatchImportItem> items,
        IProgress<FamilyBatchImportProgress>? progress,
        PauseGate? gate,
        CancellationToken ct)
    {
        var success = 0;
        for (var i = 0; i < items.Count; i++)
        {
            progress?.Report(new FamilyBatchImportProgress(
                i, items.Count, items[i].FileName, FamilyBatchImportPhase.Importing, null, null, success, 0, 0));
            await Task.Yield();
            success++;
            progress?.Report(new FamilyBatchImportProgress(
                i, items.Count, items[i].FileName, FamilyBatchImportPhase.Importing,
                FamilyBatchImportRowState.Success, null, success, 0, 0));
        }
        return new FamilyBatchImportExecutionResult(success, 0, 0, false);
    }

    [Fact]
    public async Task Import_Completes_ShowsSummary()
    {
        _executor.Handler = RunAllHandler;
        using var vm = CreateVm(MakeItem("a"), MakeItem("b"));
        var closeResults = new List<bool?>();
        vm.RequestClose += r => closeResults.Add(r);

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.True(vm.ImportStarted);
        Assert.True(vm.IsSummary);
        Assert.False(vm.IsImporting);
        Assert.Equal(2, vm.ImportSuccessCount);
        Assert.Contains("2", vm.SummaryMessage);
        Assert.Equal(1, _executor.CallCount);
        Assert.Empty(closeResults);
    }

    [Fact]
    public async Task Import_UpdatesRowStates_FromProgress()
    {
        _executor.Handler = RunAllHandler;
        using var vm = CreateVm(MakeItem("a"), MakeItem("b"));

        await vm.ImportCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.Items.All(r => r.ImportRowState == FamilyBatchImportRowState.Success));

        Assert.Equal(FamilyBatchImportRowState.Success, vm.Items[0].ImportRowState);
        Assert.Equal(FamilyBatchImportRowState.Success, vm.Items[1].ImportRowState);
    }

    [Fact]
    public async Task Import_DisablesGridDuringExecution()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _executor.Handler = async (items, progress, pauseGate, ct) =>
        {
            await gate.Task;
            return new FamilyBatchImportExecutionResult(items.Count, 0, 0, false);
        };
        using var vm = CreateVm(MakeItem("a"));

        var run = vm.ImportCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.IsImporting);

        Assert.False(vm.IsGridEnabled);

        gate.SetResult();
        await run;
        Assert.True(vm.IsSummary);
        Assert.False(vm.IsImporting);
    }

    [Fact]
    public async Task Stop_ThenResume_CompletesAllItems()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _executor.Handler = async (items, progress, gate, ct) =>
        {
            var success = 0;
            for (var i = 0; i < items.Count; i++)
            {
                if (i == 1)
                {
                    started.TrySetResult();
                    await continueSignal.Task;
                }
                if (gate?.IsPaused == true)
                {
                    progress?.Report(new FamilyBatchImportProgress(
                        i, items.Count, items[i].FileName, FamilyBatchImportPhase.Paused,
                        null, null, success, 0, 0));
                    await gate.WaitWhilePausedAsync();
                }
                ct.ThrowIfCancellationRequested();
                success++;
                progress?.Report(new FamilyBatchImportProgress(
                    i, items.Count, items[i].FileName, FamilyBatchImportPhase.Importing,
                    FamilyBatchImportRowState.Success, null, success, 0, 0));
            }
            return new FamilyBatchImportExecutionResult(success, 0, 0, false);
        };
        using var vm = CreateVm(MakeItem("a"), MakeItem("b"), MakeItem("c"));

        var run = vm.ImportCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.CancelCommand.Execute(null);
        continueSignal.SetResult();
        await WaitForAsync(() => vm.IsPaused);
        Assert.True(vm.IsImporting);
        Assert.True(vm.ImportCommand.CanExecute(null));

        await vm.ImportCommand.ExecuteAsync(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(vm.IsSummary);
        Assert.Equal(3, vm.ImportSuccessCount);
    }

    [Fact]
    public async Task Stop_ThenClose_ClosesDialogWithoutSummary()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var continueSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _executor.Handler = async (items, progress, gate, ct) =>
        {
            var success = 0;
            for (var i = 0; i < items.Count; i++)
            {
                if (i == 1)
                {
                    started.TrySetResult();
                    await continueSignal.Task;
                }
                if (gate?.IsPaused == true)
                {
                    progress?.Report(new FamilyBatchImportProgress(
                        i, items.Count, items[i].FileName, FamilyBatchImportPhase.Paused,
                        null, null, success, 0, 0));
                    await gate.WaitWhilePausedAsync();
                }
                if (ct.IsCancellationRequested)
                {
                    return new FamilyBatchImportExecutionResult(success, 0, 0, true);
                }
                success++;
            }
            return new FamilyBatchImportExecutionResult(success, 0, 0, false);
        };
        using var vm = CreateVm(MakeItem("a"), MakeItem("b"), MakeItem("c"));
        var closeResults = new List<bool?>();
        vm.RequestClose += r => closeResults.Add(r);

        var run = vm.ImportCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        vm.CancelCommand.Execute(null);
        continueSignal.SetResult();
        await WaitForAsync(() => vm.IsPaused);

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(vm.IsSummary);
        Assert.Single(closeResults);
        Assert.Null(closeResults[0]);
        var completion = await vm.DialogCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(completion);
    }

    [Fact]
    public async Task ConfirmClose_DuringImport_CancelsCloseAndRequestsStop()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _executor.Handler = async (items, progress, pauseGate, ct) =>
        {
            await gate.Task;
            return new FamilyBatchImportExecutionResult(items.Count, 0, 0, false);
        };
        using var vm = CreateVm(MakeItem("a"));

        var run = vm.ImportCommand.ExecuteAsync(null);
        await WaitForAsync(() => vm.IsImporting);

        var args = new CloseConfirmationArgs();
        vm.ConfirmClose(args);

        Assert.True(args.Cancel);
        Assert.True(vm.IsStopping);

        gate.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Setup_Cancel_ClosesWithFalse()
    {
        using var vm = CreateVm(MakeItem("a"));
        var closeResults = new List<bool?>();
        vm.RequestClose += r => closeResults.Add(r);

        vm.CancelCommand.Execute(null);

        Assert.Single(closeResults);
        Assert.Equal(false, closeResults[0]);
        Assert.False(vm.ImportStarted);
        var completion = await vm.DialogCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(false, completion);
    }

    [Fact]
    public async Task Summary_Ok_ClosesWithTrue()
    {
        _executor.Handler = RunAllHandler;
        using var vm = CreateVm(MakeItem("a"));
        var closeResults = new List<bool?>();
        vm.RequestClose += r => closeResults.Add(r);

        await vm.ImportCommand.ExecuteAsync(null);
        Assert.True(vm.IsSummary);

        await vm.ImportCommand.ExecuteAsync(null);
        Assert.Single(closeResults);
        Assert.Equal(true, closeResults[0]);
        var completion = await vm.DialogCompletion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(true, completion);
    }

    [Fact]
    public async Task Import_WithoutExecutor_LegacyClosesTrue()
    {
        using var vm = new FamilyBatchImportViewModel(
            new[] { MakeItem("a") },
            _dialogMock.Object,
            _factoryMock.Object);
        var closeResults = new List<bool?>();
        vm.RequestClose += r => closeResults.Add(r);

        await vm.ImportCommand.ExecuteAsync(null);

        Assert.Single(closeResults);
        Assert.Equal(true, closeResults[0]);
        Assert.False(vm.ImportStarted);
    }
}
