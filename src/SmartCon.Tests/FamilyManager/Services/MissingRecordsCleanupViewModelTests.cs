using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for the "Очистить недоступные записи" dialog view model (Issue
/// #133): the always-on disk scan (candidates arrive incrementally via
/// progress), the delete-selected/delete-all flows through PurgeMissingAsync,
/// confirmation gating, cancellation and the purge-result bookkeeping.
/// </summary>
public sealed class MissingRecordsCleanupViewModelTests : IDisposable
{
    private readonly SynchronizationContext? _previousContext = SynchronizationContext.Current;

    public MissingRecordsCleanupViewModelTests()
    {
        // The VM drives its list/status through Progress<T>, which captures
        // the current SynchronizationContext. In Revit that is the WPF
        // dispatcher; in xUnit there is none, so callbacks would race with
        // the assertions on the thread pool. Install an inline context.
        SynchronizationContext.SetSynchronizationContext(new InlineSynchronizationContext());
    }

    public void Dispose() => SynchronizationContext.SetSynchronizationContext(_previousContext);

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => d(state);
        public override void Send(SendOrPostCallback d, object? state) => d(state);
    }

    private sealed class FakeActualization : ICatalogActualizationService
    {
        /// <summary>Candidates returned by the -2 reload (after purge).</summary>
        public List<MissingRecordCandidate> Marked { get; } = new();

        /// <summary>Candidates reported by the on-disk scan (incrementally).</summary>
        public List<MissingRecordCandidate> ScanResults { get; } = new();

        public List<HashRecalculationMissingFile> PurgeCalls { get; } = new();
        public PurgeMissingResult PurgeResult { get; set; } =
            new(1, 1, 0, Array.Empty<ResetRoutingLinkInfo>(), Array.Empty<SwitchedActiveVersionInfo>());
        public Exception? LoadException { get; set; }
        public Exception? ScanException { get; set; }
        public Exception? PurgeException { get; set; }
        public int ScanCalls { get; private set; }

        public Task<DatabasePendingBreakdown> CountPendingBreakdownAsync(int revitMajorVersion, CancellationToken ct = default)
            => Task.FromResult(DatabasePendingBreakdown.Empty);

        public Task<DatabaseMigrationResult> RunAllPendingAsync(
            int revitMajorVersion, IProgress<DatabaseMigrationProgress>? progress, CancellationToken ct = default)
            => Task.FromResult(new DatabaseMigrationResult(
                0, 0, Array.Empty<HashRecalculationMissingFile>(), Array.Empty<HashRecalculationFailedFile>(), false));

        public Task<PurgeMissingResult> PurgeMissingAsync(
            IReadOnlyList<HashRecalculationMissingFile> missing, CancellationToken ct = default)
        {
            PurgeCalls.AddRange(missing);
            if (PurgeException is not null) throw PurgeException;
            var purgedKeys = missing
                .Select(m => m.CatalogItemId + "|" + m.VersionLabel)
                .ToHashSet(StringComparer.Ordinal);
            Marked.RemoveAll(m => purgedKeys.Contains(m.CatalogItemId + "|" + m.VersionLabel));
            return Task.FromResult(PurgeResult);
        }

        public Task<IReadOnlyList<MissingRecordCandidate>> LoadMissingRecordCandidatesAsync(CancellationToken ct = default)
        {
            if (LoadException is not null) throw LoadException;
            return Task.FromResult<IReadOnlyList<MissingRecordCandidate>>(Marked.ToList());
        }

        public Task<int> ScanForMissingFilesAsync(
            IProgress<MissingRecordScanProgress>? progress, CancellationToken ct = default)
        {
            ScanCalls++;
            var results = ScanResults.ToList();
            // Report first, throw after — mirrors the real engine where an
            // interrupted scan keeps everything reported before the stop.
            for (var i = 0; i < results.Count; i++)
            {
                progress?.Report(new MissingRecordScanProgress(i + 1, results.Count, results[i].FileName, results[i]));
            }
            if (ScanException is not null) throw ScanException;
            return Task.FromResult(results.Count);
        }
    }

    private static MissingRecordCandidate Candidate(
        string item = "FamA", string label = "v1", MissingRecordReason reason = MissingRecordReason.MarkedMissing)
        => new("id-" + item, item, label, item + ".rfa", $"files/id-{item}/{label}/{item}.rfa", 2025, reason);

    private static (MissingRecordsCleanupViewModel vm, FakeActualization engine, FakeFamilyManagerDialogService dialogs) CreateSut()
    {
        var engine = new FakeActualization();
        var dialogs = new FakeFamilyManagerDialogService();
        return (new MissingRecordsCleanupViewModel(engine, dialogs), engine, dialogs);
    }

    [Fact]
    public async Task RunScan_FillsRowsIncrementally_AndShowsTotal()
    {
        var (vm, engine, _) = CreateSut();
        engine.ScanResults.Add(Candidate("FamA"));
        engine.ScanResults.Add(Candidate("FamOrphan", reason: MissingRecordReason.FileMissing));

        await vm.RunScanAsync();

        Assert.Equal(2, vm.Rows.Count);
        Assert.True(vm.HasRows);
        Assert.True(vm.IsScanDone);
        Assert.Equal(2, vm.ProgressMaximum);
        Assert.Equal(2, vm.ProgressValue);
        Assert.Contains("2", vm.StatusText);
        var orphan = vm.Rows.Single(r => r.ItemName == "FamOrphan");
        Assert.Equal(MissingRecordReason.FileMissing, orphan.Candidate.Reason);
    }

    [Fact]
    public async Task RunScan_NothingFound_StatusAndEmptyList()
    {
        var (vm, _, _) = CreateSut();

        await vm.RunScanAsync();

        Assert.Empty(vm.Rows);
        Assert.False(vm.HasRows);
        Assert.True(vm.IsScanDone);
        Assert.False(string.IsNullOrEmpty(vm.StatusText));
    }

    [Fact]
    public async Task RunScan_Failure_ShowsErrorAndUnblocks()
    {
        var (vm, engine, dialogs) = CreateSut();
        engine.ScanException = new InvalidOperationException("boom");

        await vm.RunScanAsync();

        Assert.Equal(1, dialogs.ErrorCalls);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsScanRunning);
    }

    [Fact]
    public async Task RunScan_Cancelled_KeepsPartialResults()
    {
        var (vm, engine, _) = CreateSut();
        engine.ScanResults.Add(Candidate("FamA"));
        // The fake reports the candidate, THEN cancels — the row must survive.
        engine.ScanException = new OperationCanceledException();

        await vm.RunScanAsync();

        Assert.Single(vm.Rows);
        Assert.False(vm.IsBusy);
        Assert.False(vm.IsScanDone);
        Assert.False(string.IsNullOrEmpty(vm.StatusText));
    }

    [Fact]
    public async Task CheckDisk_MergesNewFindings_SkipsDuplicates()
    {
        var (vm, engine, _) = CreateSut();
        engine.ScanResults.Add(Candidate("FamA"));
        await vm.RunScanAsync();

        // A later re-check discovers the same record plus a new orphan.
        engine.ScanResults.Add(Candidate("FamA"));
        engine.ScanResults.Add(Candidate("FamOrphan", reason: MissingRecordReason.FileMissing));
        await vm.CheckDiskCommand.ExecuteAsync(null);

        Assert.Equal(2, vm.Rows.Count);
        Assert.Single(vm.Rows, r => r.ItemName == "FamOrphan");
    }

    [Fact]
    public async Task DeleteSelected_NotConfirmed_DoesNotPurge()
    {
        var (vm, engine, dialogs) = CreateSut();
        engine.ScanResults.Add(Candidate("FamA"));
        dialogs.ConfirmationAnswer = false;
        await vm.RunScanAsync();
        vm.Rows[0].IsSelected = true;

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Empty(engine.PurgeCalls);
        Assert.False(vm.PurgedAny);
        Assert.Single(vm.Rows);
    }

    [Fact]
    public async Task DeleteSelected_PurgesSelectedOnly_AndReloadsSurvivors()
    {
        var (vm, engine, dialogs) = CreateSut();
        engine.ScanResults.Add(Candidate("FamA"));
        engine.ScanResults.Add(Candidate("FamB"));
        // The reload after purge returns the -2 survivors (FamB).
        engine.Marked.Add(Candidate("FamB"));
        await vm.RunScanAsync();
        vm.Rows.Single(r => r.ItemName == "FamA").IsSelected = true;

        Assert.True(vm.DeleteSelectedCommand.CanExecute(null));

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Single(engine.PurgeCalls);
        Assert.Equal("id-FamA", engine.PurgeCalls[0].CatalogItemId);
        Assert.True(vm.PurgedAny);
        // the surviving candidate stays in the list after the reload
        Assert.Single(vm.Rows);
        Assert.Equal("FamB", vm.Rows[0].ItemName);
    }

    [Fact]
    public async Task DeleteSelected_NoSelection_CannotExecute()
    {
        var (vm, engine, _) = CreateSut();
        engine.ScanResults.Add(Candidate("FamA"));
        await vm.RunScanAsync();

        Assert.False(vm.DeleteSelectedCommand.CanExecute(null));
        Assert.Empty(engine.PurgeCalls);
    }

    [Fact]
    public async Task DeleteAll_PurgesEverything()
    {
        var (vm, engine, dialogs) = CreateSut();
        engine.ScanResults.Add(Candidate("FamA"));
        engine.ScanResults.Add(Candidate("FamB"));
        await vm.RunScanAsync();

        await vm.DeleteAllCommand.ExecuteAsync(null);

        Assert.Equal(2, engine.PurgeCalls.Count);
        Assert.True(vm.PurgedAny);
        Assert.Empty(vm.Rows);
    }

    [Fact]
    public async Task Delete_ResetLinksSwitchedActiveAndFailedDirs_AreReflectedInStatus()
    {
        var (vm, engine, _) = CreateSut();
        engine.ScanResults.Add(Candidate("FamA"));
        engine.PurgeResult = new PurgeMissingResult(
            0, 0, 1,
            new[] { new ResetRoutingLinkInfo("Кран", "Труба Р50") },
            new[] { new SwitchedActiveVersionInfo("Тройник", "v2") });
        await vm.RunScanAsync();

        await vm.DeleteAllCommand.ExecuteAsync(null);

        Assert.False(vm.PurgedAny);
        Assert.Contains("1", vm.StatusText);          // failed directories count
        Assert.Contains("Труба Р50", vm.StatusText);  // reset-link parent name
        Assert.Contains("Кран", vm.StatusText);       // purged fitting name
        Assert.Contains("Тройник", vm.StatusText);    // switched-active family
        Assert.Contains("v2", vm.StatusText);         // new active label
    }

    [Fact]
    public async Task SelectAll_MarksEveryRow_AndEnablesDeleteSelected()
    {
        var (vm, engine, _) = CreateSut();
        engine.ScanResults.Add(Candidate("FamA"));
        engine.ScanResults.Add(Candidate("FamB"));
        await vm.RunScanAsync();

        vm.SelectAllCommand.Execute(null);

        Assert.All(vm.Rows, r => Assert.True(r.IsSelected));
        Assert.Contains("2", vm.DeleteSelectedButtonText);

        vm.ClearSelectionCommand.Execute(null);

        Assert.All(vm.Rows, r => Assert.False(r.IsSelected));
    }

    [Fact]
    public void ConfirmClose_WhenIdle_CompletesDialogTask()
    {
        var (vm, _, _) = CreateSut();
        var args = new CloseConfirmationArgs();
        vm.ConfirmClose(args);
        Assert.True(args.DialogResult);
        Assert.True(vm.DialogCompletion.IsCompleted);
    }

    [Fact]
    public void ConfirmClose_DuringBusy_CancelsInsteadOfClosing()
    {
        var (vm, _, _) = CreateSut();
        vm.IsBusy = true;
        var args = new CloseConfirmationArgs();

        vm.ConfirmClose(args);

        Assert.True(args.Cancel);
        Assert.False(vm.DialogCompletion.IsCompleted);
    }

    [Fact]
    public async Task Delete_FailureInPurge_ShowsErrorAndStaysConsistent()
    {
        var (vm, engine, dialogs) = CreateSut();
        engine.PurgeException = new System.IO.IOException("disk");
        engine.ScanResults.Add(Candidate("FamA"));
        await vm.RunScanAsync();

        await vm.DeleteAllCommand.ExecuteAsync(null);

        Assert.Equal(1, dialogs.ErrorCalls);
        Assert.False(vm.PurgedAny);
        Assert.False(vm.IsBusy);
    }
}
