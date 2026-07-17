using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Aggregates all registered <see cref="IDatabaseMigration"/> implementations
/// (docs/architecture/database-migrations.md). The FamilyManager main VM uses
/// it to drive the "database update required" badge state and to run pending
/// migrations from the database-tools menu. Pure C# — no Revit API.
/// </summary>
public sealed class DatabaseMigrationCoordinator
{
    private readonly IReadOnlyList<IDatabaseMigration> _migrations;

    public DatabaseMigrationCoordinator(IEnumerable<IDatabaseMigration> migrations)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(migrations);
#else
        if (migrations is null) throw new ArgumentNullException(nameof(migrations));
#endif
        _migrations = migrations.OrderBy(m => m.Order).ToList();
    }

    /// <summary>
    /// Total pending records across all migrations. A migration whose
    /// pending check throws contributes 0 (Warn is logged) so one broken
    /// migration cannot hide the others from the badge state.
    /// </summary>
    public async Task<int> CountTotalPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        var total = 0;
        foreach (var migration in _migrations)
        {
            try
            {
                total += await migration.CountPendingAsync(revitMajorVersion, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Database migration '{migration.Id}': pending check failed: {ex.Message} " +
                    $"[Action: проверьте лог smartcon.log; миграция будет повторно проверена при следующем переключении базы]");
            }
        }
        return total;
    }

    /// <summary>
    /// Runs every migration that still reports pending records, in
    /// <see cref="IDatabaseMigration.Order"/>. Pending is re-checked right
    /// before each run so an already-completed migration is skipped.
    /// </summary>
    public async Task RunPendingAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        foreach (var migration in _migrations)
        {
            ct.ThrowIfCancellationRequested();

            int pending;
            try
            {
                pending = await migration.CountPendingAsync(revitMajorVersion, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn(
                    $"Database migration '{migration.Id}': pending re-check failed: {ex.Message} " +
                    $"[Action: миграция пропущена в этом запуске; повторите «Обновить базу данных»]");
                continue;
            }
            if (pending <= 0) continue;

            using var _scope = SmartConLogger.BeginScope("DbMigration",
                ("Id", migration.Id),
                ("Pending", pending));
            SmartConLogger.Info($"Database migration '{migration.Id}': starting ({pending} pending)");
            await migration.RunAsync(revitMajorVersion, ct).ConfigureAwait(false);
            SmartConLogger.Info($"Database migration '{migration.Id}': finished");
        }
    }
}
