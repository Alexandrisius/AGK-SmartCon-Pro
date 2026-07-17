using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.Core.Services;

/// <summary>
/// Tests for <see cref="DatabaseMigrationCoordinator"/> (database-migrations
/// pattern, Issue #126): pending aggregation, execution order, resilience to
/// a broken migration, and cancellation.
/// </summary>
public sealed class DatabaseMigrationCoordinatorTests
{
    [Fact]
    public void Constructor_NullMigrations_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new DatabaseMigrationCoordinator(null!));
    }

    [Fact]
    public async Task CountTotalPendingAsync_MultipleMigrations_SumsPending()
    {
        var a = new FakeDatabaseMigration("a", order: 1, 3);
        var b = new FakeDatabaseMigration("b", order: 2, 4);
        var c = new FakeDatabaseMigration("c", order: 3, 0);
        var sut = new DatabaseMigrationCoordinator(new IDatabaseMigration[] { a, b, c });

        var total = await sut.CountTotalPendingAsync(2025);

        Assert.Equal(7, total);
        Assert.Equal(1, a.CountCalls);
        Assert.Equal(1, b.CountCalls);
        Assert.Equal(1, c.CountCalls);
    }

    [Fact]
    public async Task CountTotalPendingAsync_FailingMigration_ContributesZeroAndDoesNotThrow()
    {
        var broken = new FakeDatabaseMigration("broken", order: 1) { CountException = new InvalidOperationException("db locked") };
        var healthy = new FakeDatabaseMigration("healthy", order: 2, 5);
        var sut = new DatabaseMigrationCoordinator(new IDatabaseMigration[] { broken, healthy });

        var total = await sut.CountTotalPendingAsync(2025);

        Assert.Equal(5, total);
        Assert.Equal(1, healthy.CountCalls);
    }

    [Fact]
    public async Task RunPendingAsync_MixedPending_RunsOnlyPendingMigrations()
    {
        var pending = new FakeDatabaseMigration("pending", order: 1, 2);
        var idle = new FakeDatabaseMigration("idle", order: 2, 0);
        var sut = new DatabaseMigrationCoordinator(new IDatabaseMigration[] { pending, idle });

        await sut.RunPendingAsync(2025);

        Assert.Equal(1, pending.RunCalls);
        Assert.Equal(0, idle.RunCalls);
    }

    [Fact]
    public async Task RunPendingAsync_RegistrationOutOfOrder_ExecutesByOrderAscending()
    {
        var runOrder = new List<string>();
        var high = new FakeDatabaseMigration("high", order: 20, 1) { SharedRunLog = runOrder };
        var low = new FakeDatabaseMigration("low", order: 5, 1) { SharedRunLog = runOrder };
        var sut = new DatabaseMigrationCoordinator(new IDatabaseMigration[] { high, low });

        await sut.RunPendingAsync(2025);

        Assert.Equal(new[] { "low", "high" }, runOrder);
    }

    [Fact]
    public async Task RunPendingAsync_RecheckFindsZero_SkipsRun()
    {
        // First count (from CountTotalPendingAsync) reports work, but the
        // re-check inside RunPendingAsync sees 0 — e.g. another instance
        // already finished the migration.
        var migration = new FakeDatabaseMigration("flaky", order: 1, 1, 0);
        var sut = new DatabaseMigrationCoordinator(new IDatabaseMigration[] { migration });

        await sut.CountTotalPendingAsync(2025);
        await sut.RunPendingAsync(2025);

        Assert.Equal(0, migration.RunCalls);
    }

    [Fact]
    public async Task RunPendingAsync_FailingRecheck_SkipsMigrationAndContinues()
    {
        var broken = new FakeDatabaseMigration("broken", order: 1) { CountException = new InvalidOperationException("io") };
        var healthy = new FakeDatabaseMigration("healthy", order: 2, 3);
        var sut = new DatabaseMigrationCoordinator(new IDatabaseMigration[] { broken, healthy });

        await sut.RunPendingAsync(2025);

        Assert.Equal(0, broken.RunCalls);
        Assert.Equal(1, healthy.RunCalls);
    }

    [Fact]
    public async Task RunPendingAsync_CancelledToken_ThrowsBeforeAnyRun()
    {
        var migration = new FakeDatabaseMigration("a", order: 1, 1);
        var sut = new DatabaseMigrationCoordinator(new IDatabaseMigration[] { migration });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sut.RunPendingAsync(2025, cts.Token));
        Assert.Equal(0, migration.RunCalls);
    }
}
