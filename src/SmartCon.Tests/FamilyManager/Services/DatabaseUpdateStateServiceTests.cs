using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.Migrations;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="DatabaseUpdateStateService"/> (database-migrations
/// pattern, Issue #126): state mapping, StateChanged emission, the write-op
/// gate (decline/confirm), and the unconditional update flow. Coordinator is
/// real with fake migrations; the dialog service is a hand-written fake.
/// </summary>
public sealed class DatabaseUpdateStateServiceTests
{
    private static (DatabaseUpdateStateService sut, FakeFamilyManagerDialogService dialogs) CreateSut(
        params FakeDatabaseMigration[] migrations)
    {
        var coordinator = new DatabaseMigrationCoordinator(migrations);
        var dialogs = new FakeFamilyManagerDialogService();
        return (new DatabaseUpdateStateService(coordinator, dialogs), dialogs);
    }

    [Fact]
    public async Task RefreshAsync_PendingMigration_SetsStateAndRaises()
    {
        var (sut, _) = CreateSut(new FakeDatabaseMigration("a", order: 1, 3));
        var raised = 0;
        sut.StateChanged += (_, _) => raised++;

        await sut.RefreshAsync(2025);

        Assert.True(sut.IsUpdateRequired);
        Assert.Equal(3, sut.PendingCount);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task RefreshAsync_NoPending_KeepsStateClear_NoEvent()
    {
        var (sut, _) = CreateSut(new FakeDatabaseMigration("a", order: 1, 0));
        var raised = 0;
        sut.StateChanged += (_, _) => raised++;

        await sut.RefreshAsync(2025);

        Assert.False(sut.IsUpdateRequired);
        Assert.Equal(0, sut.PendingCount);
        Assert.Equal(0, raised);
    }

    [Fact]
    public async Task Reset_AfterPending_ClearsStateAndRaises()
    {
        var (sut, _) = CreateSut(new FakeDatabaseMigration("a", order: 1, 2));
        await sut.RefreshAsync(2025);
        var raised = 0;
        sut.StateChanged += (_, _) => raised++;

        sut.Reset();

        Assert.False(sut.IsUpdateRequired);
        Assert.Equal(0, sut.PendingCount);
        Assert.Equal(1, raised);
    }

    [Fact]
    public async Task EnsureUpToDateAsync_NotRequired_ReturnsTrueWithoutDialog()
    {
        var (sut, dialogs) = CreateSut(new FakeDatabaseMigration("a", order: 1, 0));
        await sut.RefreshAsync(2025);

        var proceed = await sut.EnsureUpToDateAsync();

        Assert.True(proceed);
        Assert.Equal(0, dialogs.ConfirmationCalls);
    }

    [Fact]
    public async Task EnsureUpToDateAsync_Declined_ReturnsFalse_MigrationNotRun()
    {
        var migration = new FakeDatabaseMigration("a", order: 1, 2);
        var (sut, dialogs) = CreateSut(migration);
        await sut.RefreshAsync(2025);
        dialogs.ConfirmationAnswer = false;

        var proceed = await sut.EnsureUpToDateAsync();

        Assert.False(proceed);
        Assert.Equal(1, dialogs.ConfirmationCalls);
        Assert.Equal(0, migration.RunCalls);
        Assert.True(sut.IsUpdateRequired);
    }

    [Fact]
    public async Task EnsureUpToDateAsync_Confirmed_RunsMigration_ClearsState_ReturnsTrue()
    {
        var migration = new FakeDatabaseMigration("a", order: 1, 2);
        var (sut, dialogs) = CreateSut(migration);
        await sut.RefreshAsync(2025);
        dialogs.ConfirmationAnswer = true;

        var proceed = await sut.EnsureUpToDateAsync();

        Assert.True(proceed);
        Assert.Equal(1, dialogs.ConfirmationCalls);
        Assert.Equal(1, migration.RunCalls);
        Assert.False(sut.IsUpdateRequired);
        Assert.Equal(0, sut.PendingCount);
    }

    [Fact]
    public async Task UpdateAsync_RunsPendingMigrations_RefreshesState()
    {
        var a = new FakeDatabaseMigration("a", order: 1, 1);
        var b = new FakeDatabaseMigration("b", order: 2, 4);
        var (sut, _) = CreateSut(a, b);
        await sut.RefreshAsync(2025);
        Assert.True(sut.IsUpdateRequired);

        await sut.UpdateAsync();

        Assert.Equal(1, a.RunCalls);
        Assert.Equal(1, b.RunCalls);
        Assert.False(sut.IsUpdateRequired);
        Assert.False(sut.IsRunning);
    }

    [Fact]
    public async Task UpdateAsync_EmitsRunningTransitionEvents()
    {
        var (sut, _) = CreateSut(new FakeDatabaseMigration("a", order: 1, 1));
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
    public async Task UpdateAsync_MigrationThrows_IsRunningResetAndNotified()
    {
        var broken = new FakeDatabaseMigration("broken", order: 1, 1)
        {
            RunException = new InvalidOperationException("migration blew up")
        };
        var (sut, _) = CreateSut(broken);
        await sut.RefreshAsync(2025);
        var notifications = new List<bool>();
        sut.StateChanged += (_, _) => notifications.Add(sut.IsRunning);

        await sut.UpdateAsync();

        // The UI must not get stuck at IsRunning=true (dead Update button)
        // even when the migration flow fails.
        Assert.False(sut.IsRunning);
        Assert.Contains(false, notifications);
        Assert.True(notifications.Count >= 2, $"expected >= 2 StateChanged events, got {notifications.Count}");
    }
}
