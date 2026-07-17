using SmartCon.Core.Logging;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.Services.Migrations;

/// <summary>
/// Default <see cref="IDatabaseUpdateStateService"/>: aggregates pending
/// counts via <see cref="DatabaseMigrationCoordinator"/>, shows the gate
/// dialog and runs migrations. UI texts come from LanguageManager, so this
/// implementation lives in FamilyManager (Core stays UI-free, I-09).
/// </summary>
public sealed class DatabaseUpdateStateService : IDatabaseUpdateStateService
{
    private readonly DatabaseMigrationCoordinator _coordinator;
    private readonly IFamilyManagerDialogService _dialogService;
    private int _currentRevitVersion;

    public DatabaseUpdateStateService(
        DatabaseMigrationCoordinator coordinator,
        IFamilyManagerDialogService dialogService)
    {
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
    }

    public bool IsUpdateRequired { get; private set; }
    public int PendingCount { get; private set; }
    public bool IsRunning { get; private set; }

    public event EventHandler? StateChanged;

    public async Task RefreshAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        _currentRevitVersion = revitMajorVersion;
        var pending = await _coordinator
            .CountTotalPendingAsync(revitMajorVersion, ct)
            .ConfigureAwait(true);
        SetState(pending);
    }

    public void Reset()
    {
        SetState(0);
    }

    public async Task<bool> EnsureUpToDateAsync()
    {
        if (!IsUpdateRequired) return true;
        if (IsRunning)
        {
            SmartConLogger.Info("DbMigration: write op gated — update already running");
            return false;
        }

        var confirmed = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_LoadBlockedTitle)
                ?? "Требуется обновление базы",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_LoadBlockedBody)
                    ?? "Действие временно недоступно: база данных создана в старой версии SmartCon и требует обновления ({0} записей). До завершения обновления база работает в режиме просмотра.\n\nОбновить сейчас?",
                PendingCount));
        if (!confirmed)
        {
            SmartConLogger.Info("DbMigration: write op gated — database update declined by user");
            return false;
        }

        await UpdateAsync().ConfigureAwait(true);
        return !IsUpdateRequired;
    }

    public async Task UpdateAsync()
    {
        if (IsRunning || _currentRevitVersion <= 0) return;

        IsRunning = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            await _coordinator
                .RunPendingAsync(_currentRevitVersion)
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"DbMigration update flow failed: {ex.GetType().Name}: {ex.Message} " +
                $"[Action: проверьте лог smartcon.log; закоммиченные пачки сохранены, повторный запуск продолжит с места остановки]");
        }
        finally
        {
            IsRunning = false;
        }

        await RefreshAsync(_currentRevitVersion).ConfigureAwait(true);
    }

    private void SetState(int pending)
    {
        var required = pending > 0;
        if (IsUpdateRequired == required && PendingCount == pending) return;
        IsUpdateRequired = required;
        PendingCount = pending;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
