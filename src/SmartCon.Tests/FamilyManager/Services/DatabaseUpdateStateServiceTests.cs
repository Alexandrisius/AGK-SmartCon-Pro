using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Migrations;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="DatabaseUpdateStateService"/> (database-migrations
/// pattern, Issue #126; actualization engine — ADR-054): state mapping,
/// StateChanged emission, the write-op gate (decline/confirm), and the
/// unified update flow. The engine is a hand-written fake; the dialog fake
/// auto-closes the unified update dialog on the summary screen.
/// </summary>
public sealed class DatabaseUpdateStateServiceTests
{
    private sealed class FakeCatalogActualizationService : ICatalogActualizationService
    {
        public DatabasePendingBreakdown Breakdown { get; set; } = DatabasePendingBreakdown.Empty;
        public DatabaseMigrationResult RunResult { get; set; } = new(
            0, 0,
            Array.Empty<HashRecalculationMissingFile>(),
            Array.Empty<HashRecalculationFailedFile>(),
            WasCancelled: false);
        public Exception? RunException { get; set; }
        public int RunCalls { get; private set; }

        /// <summary>Breakdown returned after a run (default: empty — a completed run clears everything).</summary>
        public DatabasePendingBreakdown? AfterRunBreakdown { get; set; }

        public Task<DatabasePendingBreakdown> CountPendingBreakdownAsync(
            int revitMajorVersion, CancellationToken ct = default)
            => Task.FromResult(Breakdown);

        public Task<DatabaseMigrationResult> RunAllPendingAsync(
            int revitMajorVersion,
            IProgress<DatabaseMigrationProgress>? progress,
            CancellationToken ct = default)
        {
            RunCalls++;
            if (RunException is not null) throw RunException;
            // A completed run clears everything — the real engine leaves no
            // pending records behind, and the state service re-reads the
            // breakdown after the run.
            Breakdown = AfterRunBreakdown ?? DatabasePendingBreakdown.Empty;
            return Task.FromResult(RunResult);
        }

        public Task<(int DeletedItems, int DeletedVersions, int FailedDirectories, int GuardedSkippedItems)> PurgeMissingAsync(
            IReadOnlyList<HashRecalculationMissingFile> missing, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeAccessControl : IDbAccessControlService
    {
        public bool CanEdit { get; set; } = true;
        public bool CanImport => CanEdit;
        public bool CanManageUsers => false;
        public bool CanLoadToProject => true;
        public bool IsEditorRole => CanEdit;
        public bool IsOwner => false;
        public bool IsBanned => false;
        public Task<SmartCon.Core.Models.FamilyManager.DbUserRole> GetCurrentUserRoleAsync(CancellationToken ct = default)
            => Task.FromResult(CanEdit ? SmartCon.Core.Models.FamilyManager.DbUserRole.Owner : SmartCon.Core.Models.FamilyManager.DbUserRole.Engineer);
        public Task<SmartCon.Core.Models.FamilyManager.DbUser> GetCurrentUserAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RefreshCurrentUserAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void InvalidateCache() { }
    }

    private static (DatabaseUpdateStateService sut, FakeFamilyManagerDialogService dialogs, FakeCatalogActualizationService engine, FakeAccessControl access) CreateSut(
        DatabasePendingBreakdown? breakdown = null)
    {
        var engine = new FakeCatalogActualizationService { Breakdown = breakdown ?? DatabasePendingBreakdown.Empty };
        var dialogs = new FakeFamilyManagerDialogService();
        var access = new FakeAccessControl();
        return (new DatabaseUpdateStateService(engine, dialogs, access), dialogs, engine, access);
    }

    [Fact]
    public async Task RefreshAsync_PendingMigration_SetsStateAndRaises()
    {
        var (sut, _, _, _) = CreateSut(new DatabasePendingBreakdown(3, 5, 0, 0, 0, 0));
        var raised = 0;
        sut.StateChanged += (_, _) => raised++;

        await sut.RefreshAsync(2025);

        Assert.True(sut.IsUpdateRequired);
        Assert.Equal(3, sut.PendingCount);
        Assert.Equal(5, sut.OptionalPendingCount);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task RefreshAsync_NoPending_KeepsStateClear_NoEvent()
    {
        var (sut, _, _, _) = CreateSut();
        var raised = 0;
        sut.StateChanged += (_, _) => raised++;

        await sut.RefreshAsync(2025);

        Assert.False(sut.IsUpdateRequired);
        Assert.Equal(0, sut.PendingCount);
        Assert.Equal(0, sut.OptionalPendingCount);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task RefreshAsync_NewerOnlyPending_SetsCount_WithoutGating()
    {
        // OPTIONAL newer-only records: NO banner, NO gate — just the amber
        // indicator count (ADR-054 §3a).
        var (sut, _, _, _) = CreateSut(new DatabasePendingBreakdown(0, 0, 0, 5, 0, 2025));
        await sut.RefreshAsync(2025);

        Assert.False(sut.IsUpdateRequired);
        Assert.Equal(0, sut.PendingCount);
        Assert.Equal(0, sut.OptionalPendingCount);
        Assert.Equal(5, sut.NewerOnlyPendingCount);
    }

    [Fact]
    public async Task EnsureUpToDateAsync_OnlyNewerCriticalPending_GatesWithWarning_NoUpdateOffer()
    {
        // CRITICAL newer-only (ADR-054 §3a): the database stays read-only
        // until perfectly updated — but no immediate update is offered
        // (nothing is fixable in the running Revit).
        var (sut, dialogs, engine, _) = CreateSut(new DatabasePendingBreakdown(0, 0, 1, 0, 2026, 0));
        await sut.RefreshAsync(2025);
        Assert.True(sut.IsUpdateRequired);
        Assert.Equal(0, sut.PendingCount);
        Assert.Equal(1, sut.NewerOnlyCriticalCount);
        Assert.Equal(2026, sut.NewerOnlyRequiredRevitVersion);

        var proceed = await sut.EnsureUpToDateAsync();

        Assert.False(proceed);
        Assert.Equal(1, dialogs.InfoCalls);
        Assert.Contains("2026", dialogs.LastInfoMessage);
        Assert.Equal(0, dialogs.WarningCalls);
        Assert.Equal(0, dialogs.ConfirmationCalls);
        Assert.Equal(0, engine.RunCalls);
    }

    [Fact]
    public async Task EnsureUpToDateAsync_AfterProcessableUpdate_NewerCriticalRemains_StillGated()
    {
        // Full gate (ADR-054 §3a): after fixing the processable part, the
        // newer-only critical remainder keeps the database read-only.
        var (sut, dialogs, engine, _) = CreateSut(new DatabasePendingBreakdown(2, 0, 1, 0, 2026, 0));
        engine.AfterRunBreakdown = new DatabasePendingBreakdown(0, 0, 1, 0, 2026, 0);
        await sut.RefreshAsync(2025);
        dialogs.ConfirmationAnswer = true;

        var proceed = await sut.EnsureUpToDateAsync();

        Assert.False(proceed);
        Assert.Equal(1, engine.RunCalls);
        Assert.True(sut.IsUpdateRequired);
        Assert.Equal(1, sut.NewerOnlyCriticalCount);
    }

    [Fact]
    public async Task Reset_AfterPending_ClearsStateAndRaises()
    {
        var (sut, _, _, _) = CreateSut(new DatabasePendingBreakdown(2, 1, 0, 0, 0, 0));
        await sut.RefreshAsync(2025);
        var raised = 0;
        sut.StateChanged += (_, _) => raised++;

        sut.Reset();

        Assert.False(sut.IsUpdateRequired);
        Assert.Equal(0, sut.PendingCount);
        Assert.Equal(0, sut.OptionalPendingCount);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task EnsureUpToDateAsync_NotRequired_ReturnsTrueWithoutDialog()
    {
        var (sut, dialogs, _, _) = CreateSut();
        await sut.RefreshAsync(2025);

        var proceed = await sut.EnsureUpToDateAsync();

        Assert.True(proceed);
        Assert.Equal(0, dialogs.ConfirmationCalls);
    }

    [Fact]
    public async Task EnsureUpToDateAsync_Declined_ReturnsFalse_EngineNotRun()
    {
        var (sut, dialogs, engine, _) = CreateSut(new DatabasePendingBreakdown(2, 0, 0, 0, 0, 0));
        await sut.RefreshAsync(2025);
        dialogs.ConfirmationAnswer = false;

        var proceed = await sut.EnsureUpToDateAsync();

        Assert.False(proceed);
        Assert.Equal(1, dialogs.ConfirmationCalls);
        Assert.Equal(0, engine.RunCalls);
        Assert.True(sut.IsUpdateRequired);
    }

    [Fact]
    public async Task EnsureUpToDateAsync_Confirmed_RunsEngine_ClearsState_ReturnsTrue()
    {
        var (sut, dialogs, engine, _) = CreateSut(new DatabasePendingBreakdown(2, 0, 0, 0, 0, 0));
        await sut.RefreshAsync(2025);
        dialogs.ConfirmationAnswer = true;

        var proceed = await sut.EnsureUpToDateAsync();

        Assert.True(proceed);
        Assert.Equal(1, dialogs.ConfirmationCalls);
        Assert.Equal(1, engine.RunCalls);
        Assert.False(sut.IsUpdateRequired);
        Assert.Equal(0, sut.PendingCount);
    }

    [Fact]
    public async Task EnsureUpToDateAsync_ProcessablePending_DialogCarriesTeamImpactNote()
    {
        // ADR-058 (#173): the upgrader must make an INFORMED choice — the
        // confirmation tells them that users on older SmartCon versions lose
        // edit access to the shared database after the update.
        var (sut, dialogs, _, _) = CreateSut(new DatabasePendingBreakdown(2, 0, 0, 0, 0, 0));
        await sut.RefreshAsync(2025);

        await sut.EnsureUpToDateAsync();

        Assert.Equal(1, dialogs.ConfirmationCalls);
        Assert.NotNull(dialogs.LastConfirmationMessage);
        Assert.Contains("не смогут редактировать", dialogs.LastConfirmationMessage);
    }

    [Fact]
    public async Task EnsureUpToDateAsync_ReadOnlyRole_GatesWithRoleText_NoOffer_NoRun()
    {
        // Engineer (read-only): the update physically cannot write — no
        // "update now" offer, just the styled info pointing at Owner/BimMaster.
        var (sut, dialogs, engine, access) = CreateSut(new DatabasePendingBreakdown(2, 0, 0, 0, 0, 0));
        access.CanEdit = false;
        await sut.RefreshAsync(2025);
        dialogs.ConfirmationAnswer = true;

        var proceed = await sut.EnsureUpToDateAsync();

        Assert.False(proceed);
        Assert.Equal(1, dialogs.InfoCalls);
        Assert.Equal(0, dialogs.ConfirmationCalls);
        Assert.Equal(0, engine.RunCalls);
        Assert.True(sut.IsUpdateRequired);
    }

    [Fact]
    public async Task UpdateAsync_RunsEngine_RefreshesState()
    {
        var (sut, _, engine, _) = CreateSut(new DatabasePendingBreakdown(1, 4, 0, 0, 0, 0));
        await sut.RefreshAsync(2025);
        Assert.True(sut.IsUpdateRequired);

        await sut.UpdateAsync();

        Assert.Equal(1, engine.RunCalls);
        Assert.False(sut.IsUpdateRequired);
        Assert.False(sut.IsRunning);
    }

    [Fact]
    public async Task UpdateAsync_NothingPending_DoesNotShowDialog()
    {
        var (sut, dialogs, engine, _) = CreateSut();
        await sut.RefreshAsync(2025);

        await sut.UpdateAsync();

        Assert.Equal(0, engine.RunCalls);
        Assert.Empty(dialogs.ShownDatabaseUpdateDialogs);
    }

    [Fact]
    public async Task UpdateAsync_DisplayedStateStale_ResyncsFromFreshBreakdown()
    {
        // Defense in depth for the "banner survives a database switch" class
        // of bugs: the displayed state says "update required", but the fresh
        // breakdown of the CURRENT database is empty — UpdateAsync must
        // resync instead of returning silently with the stale banner up.
        var (sut, dialogs, engine, _) = CreateSut(new DatabasePendingBreakdown(2, 0, 0, 0, 0, 0));
        await sut.RefreshAsync(2025);
        Assert.True(sut.IsUpdateRequired);

        engine.Breakdown = DatabasePendingBreakdown.Empty;

        await sut.UpdateAsync();

        Assert.False(sut.IsUpdateRequired);
        Assert.Equal(0, sut.PendingCount);
        Assert.Equal(0, engine.RunCalls);
        Assert.Empty(dialogs.ShownDatabaseUpdateDialogs);
    }

    [Fact]
    public async Task UpdateAsync_OnlyNewerCriticalPending_ResyncKeepsGate()
    {
        // Newer-only criticals are NOT processable in the running Revit —
        // the early-return resync must KEEP the gate (and the required
        // version) instead of clearing it.
        var (sut, _, engine, _) = CreateSut(new DatabasePendingBreakdown(0, 0, 1, 0, 2026, 0));
        await sut.RefreshAsync(2025);
        Assert.True(sut.IsUpdateRequired);

        await sut.UpdateAsync();

        Assert.True(sut.IsUpdateRequired);
        Assert.Equal(1, sut.NewerOnlyCriticalCount);
        Assert.Equal(2026, sut.NewerOnlyRequiredRevitVersion);
        Assert.Equal(0, engine.RunCalls);
    }

    [Fact]
    public async Task UpdateAsync_EmitsRunningTransitionEvents()
    {
        var (sut, _, _, _) = CreateSut(new DatabasePendingBreakdown(1, 0, 0, 0, 0, 0));
        await sut.RefreshAsync(2025);
        var raised = 0;
        sut.StateChanged += (_, _) => raised++;

        await sut.UpdateAsync();

        // IsRunning=true, then the final refresh flips IsUpdateRequired →
        // at least two notifications; exact count is an implementation
        // detail, but silence would mean the UI misses the run.
        Assert.True(raised >= 2, $"expected >= 2 StateChanged events, got {raised}");
        Assert.False(sut.IsRunning);
    }

    [Fact]
    public async Task UpdateAsync_EngineThrows_IsRunningResetAndNotified()
    {
        var (sut, _, engine, _) = CreateSut(new DatabasePendingBreakdown(1, 0, 0, 0, 0, 0));
        engine.RunException = new InvalidOperationException("engine blew up");
        await sut.RefreshAsync(2025);
        var notifications = new List<bool>();
        sut.StateChanged += (_, _) => notifications.Add(sut.IsRunning);

        await sut.UpdateAsync();

        // The UI must not get stuck at IsRunning=true (dead Update button)
        // even when the update flow fails.
        Assert.False(sut.IsRunning);
        Assert.Contains(false, notifications);
        Assert.True(notifications.Count >= 2, $"expected >= 2 StateChanged events, got {notifications.Count}");
    }
}
