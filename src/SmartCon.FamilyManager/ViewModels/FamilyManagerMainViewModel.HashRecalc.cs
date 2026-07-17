using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Database-update feature (docs/architecture/database-migrations.md,
/// Issue #126). All registered <c>IDatabaseMigration</c> implementations are
/// checked silently after the initial database connection and after every
/// database switch — no dialogs are shown unprompted. When anything is
/// pending, a red badge appears on the database-tools button and an
/// "Update database" command becomes available; loading families into the
/// project is gated until the update completes (e.g. v1 hashes cannot
/// match v2, so dedup would silently degrade to name-only matching).
/// </summary>
public sealed partial class FamilyManagerMainViewModel
{
    [ObservableProperty]
    private bool _isDatabaseUpdateRequired;

    [ObservableProperty]
    private int _pendingDatabaseUpdateCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateDatabaseCommand))]
    private bool _isDatabaseUpdateRunning;

    private bool CanUpdateDatabase => HasActiveDatabase && !IsDatabaseUpdateRunning;

    private async Task RefreshDatabaseUpdateStateAsync()
    {
        // Re-detect here: at VM construction the Revit context may not be
        // fully wired yet, leaving CurrentRevitVersion = 0. Callers invoke
        // this after an ExternalEvent round-trip, so the context is ready.
        if (CurrentRevitVersion <= 0)
        {
            DetectRevitVersion();
        }

        if (!HasActiveDatabase)
        {
            SetDatabaseUpdateState(0);
            return;
        }
        if (CurrentRevitVersion <= 0)
        {
            SmartConLogger.Debug(
                "DbMigration: update-state check skipped — Revit version not detected yet");
            return;
        }

        try
        {
            var pending = await _migrationCoordinator
                .CountTotalPendingAsync(CurrentRevitVersion)
                .ConfigureAwait(true);
            SetDatabaseUpdateState(pending);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"DbMigration pending check failed: {ex.Message} [Action: повторите при следующем запуске; если ошибка повторяется — проверьте целостность catalog.db]");
        }
    }

    private void SetDatabaseUpdateState(int pending)
    {
        PendingDatabaseUpdateCount = pending;
        IsDatabaseUpdateRequired = pending > 0;
        UpdateDatabaseCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanUpdateDatabase))]
    private async Task UpdateDatabaseAsync()
    {
        if (CurrentRevitVersion <= 0)
        {
            DetectRevitVersion();
        }
        if (PendingDatabaseUpdateCount <= 0 || CurrentRevitVersion <= 0) return;

        IsDatabaseUpdateRunning = true;
        using var _scope = SmartConLogger.BeginScope("DbMigration",
            ("Method", nameof(UpdateDatabaseAsync)),
            ("Pending", PendingDatabaseUpdateCount));

        try
        {
            await _migrationCoordinator
                .RunPendingAsync(CurrentRevitVersion)
                .ConfigureAwait(true);

            // Migrations may have purged catalog rows or re-synced item
            // hashes/names — rebuild the tree to reflect the final state.
            await RefreshTreeViaExternalEventAsync().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"DbMigration update flow failed: {ex.GetType().Name}: {ex.Message} " +
                $"[Action: проверьте лог smartcon.log; закоммиченные пачки сохранены, повторный запуск продолжит с места остановки]");
        }
        finally
        {
            IsDatabaseUpdateRunning = false;
            await RefreshDatabaseUpdateStateAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Gate for load-into-project commands: while the database has pending
    /// migrations, loading is blocked with an explanation and an offer to
    /// run the update immediately. Returns true when loading may proceed.
    /// </summary>
    private async Task<bool> EnsureDatabaseUpToDateForLoadAsync()
    {
        if (!IsDatabaseUpdateRequired) return true;

        var confirmed = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_LoadBlockedTitle)
                ?? "Требуется обновление базы",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_LoadBlockedBody)
                    ?? "Загрузка семейств в проект временно недоступна: база данных создана в старой версии SmartCon и требует обновления контрольных сумм ({0} записей).\n\nОбновить сейчас?",
                PendingDatabaseUpdateCount));
        if (!confirmed)
        {
            SmartConLogger.Info("DbMigration: load to project postponed — database update declined by user");
            return false;
        }

        await UpdateDatabaseAsync().ConfigureAwait(true);
        return !IsDatabaseUpdateRequired;
    }
}
