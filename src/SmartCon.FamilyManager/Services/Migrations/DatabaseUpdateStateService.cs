using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.Services.Migrations;

/// <summary>
/// Default <see cref="IDatabaseUpdateStateService"/>: pending counts come
/// from the actualization engine (<see cref="ICatalogActualizationService"/>),
/// shows the gate dialog and runs the unified update dialog. UI texts come
/// from LanguageManager, so this implementation lives in FamilyManager
/// (Core stays UI-free, I-09).
/// </summary>
public sealed class DatabaseUpdateStateService : IDatabaseUpdateStateService
{
    private readonly ICatalogActualizationService _actualization;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly IDbAccessControlService _accessControl;
    private int _currentRevitVersion;

    public DatabaseUpdateStateService(
        ICatalogActualizationService actualization,
        IFamilyManagerDialogService dialogService,
        IDbAccessControlService accessControl)
    {
        _actualization = actualization ?? throw new ArgumentNullException(nameof(actualization));
        _dialogService = dialogService ?? throw new ArgumentNullException(nameof(dialogService));
        _accessControl = accessControl ?? throw new ArgumentNullException(nameof(accessControl));
    }

    public bool IsUpdateRequired { get; private set; }
    public int PendingCount { get; private set; }
    public int OptionalPendingCount { get; private set; }
    public int NewerOnlyCriticalCount { get; private set; }
    public int NewerOnlyRequiredRevitVersion { get; private set; }
    public int NewerOnlyOptionalRequiredRevitVersion { get; private set; }
    public int NewerOnlyPendingCount { get; private set; }
    public bool IsRunning { get; private set; }

    public event EventHandler? StateChanged;

    public async Task RefreshAsync(int revitMajorVersion, CancellationToken ct = default)
    {
        _currentRevitVersion = revitMajorVersion;
        var breakdown = await _actualization
            .CountPendingBreakdownAsync(revitMajorVersion, ct)
            .ConfigureAwait(true);
        SetState(breakdown);
    }

    public void Reset()
    {
        SetState(DatabasePendingBreakdown.Empty);
    }

    public async Task<bool> EnsureUpToDateAsync()
    {
        if (!IsUpdateRequired) return true;
        if (IsRunning)
        {
            SmartConLogger.Info("DbMigration: write op gated — update already running");
            return false;
        }

        if (!_accessControl.CanEdit)
        {
            // Read-only role (Engineer): the update physically cannot write
            // (SQLite Mode=ReadOnly) — do not offer it. The banner already
            // explains that only Owner/BimMaster can run the update.
            _dialogService.ShowInfo(
                LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_LoadBlockedTitle)
                    ?? "Требуется обновление базы",
                LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_LoadBlockedBodyReadOnlyRole)
                    ?? "База данных требует обновления и работает в режиме просмотра. Обновление может выполнить пользователь с ролью Owner или BIM-мастер — обратитесь к нему.");
            SmartConLogger.Info("DbMigration: write op gated — update required, but the current role is read-only");
            return false;
        }

        if (PendingCount <= 0)
        {
            // Only NEWER-Revit critical pending (ADR-054 §3a): it cannot be
            // fixed in the running Revit — block with the required version
            // in the styled info dialog, no immediate-update offer.
            _dialogService.ShowInfo(
                LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_LoadBlockedTitle)
                    ?? "Требуется обновление базы",
                string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_LoadBlockedBodyNewerRevit)
                        ?? "База данных требует обновления в Revit {0} или новее и работает в режиме просмотра. Откройте её в Revit {0}+ и выполните «Обновить базу» — тогда всё обновится за один раз.",
                    NewerOnlyRequiredRevitVersion));
            SmartConLogger.Info(
                $"DbMigration: write op gated — newer-Revit-only critical pending ({NewerOnlyCriticalCount}), requires Revit {NewerOnlyRequiredRevitVersion}+");
            return false;
        }

        var confirmed = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_LoadBlockedTitle)
                ?? "Требуется обновление базы",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_LoadBlockedBody)
                    ?? "Действие временно недоступно: база данных создана в старой версии SmartCon и требует обновления ({0} записей). До завершения обновления база работает в режиме просмотра.\n\nОбновить сейчас?",
                PendingCount)
            + "\n\n"
            + (LanguageManager.GetString(StringLocalization.Keys.FM_HashRecalc_LoadBlockedBodyTeamNote)
                ?? "Обратите внимание: после обновления пользователи со старыми версиями SmartCon не смогут редактировать эту базу (просмотр и загрузка в проект останутся доступны). Убедитесь, что команда обновилась."));
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

        var breakdown = await _actualization
            .CountPendingBreakdownAsync(_currentRevitVersion)
            .ConfigureAwait(true);
        if (breakdown.TotalProcessable <= 0) return;

        IsRunning = true;
        StateChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            // Unified update (ADR-054): ONE dialog runs the actualization
            // engine over every pending family — one file open, all
            // pending tasks applied.
            var vm = new ViewModels.DatabaseUpdateProgressViewModel(
                _actualization, _dialogService, _currentRevitVersion);
            _dialogService.ShowDatabaseUpdateProgressDialog(vm);
            try
            {
                await vm.RunAsync().ConfigureAwait(true);
                await vm.DialogCompletion.ConfigureAwait(true);
            }
            finally
            {
                vm.Dispose();
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"DbMigration update flow failed: {ex.GetType().Name}: {ex.Message} " +
                $"[Action: проверьте лог smartcon.log; закоммиченные пачки сохранены, повторный запуск продолжит с места остановки]");
        }
        finally
        {
            // Notify BEFORE the refresh: if the refresh below throws, the UI
            // would otherwise stay stuck at IsRunning=true (dead Update button)
            // until the next database switch.
            IsRunning = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }

        try
        {
            await RefreshAsync(_currentRevitVersion).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"DbMigration post-update refresh failed: {ex.Message} [Action: переключите базу туда-сюда для повторной проверки состояния]");
        }
    }

    private void SetState(DatabasePendingBreakdown breakdown)
    {
        var required = breakdown.TotalCritical > 0;
        if (IsUpdateRequired == required
            && PendingCount == breakdown.Critical
            && OptionalPendingCount == breakdown.Optional
            && NewerOnlyCriticalCount == breakdown.NewerOnlyCritical
            && NewerOnlyRequiredRevitVersion == breakdown.NewerOnlyCriticalRequiredRevitVersion
            && NewerOnlyOptionalRequiredRevitVersion == breakdown.NewerOnlyOptionalRequiredRevitVersion
            && NewerOnlyPendingCount == breakdown.NewerOnlyOptional) return;
        IsUpdateRequired = required;
        PendingCount = breakdown.Critical;
        OptionalPendingCount = breakdown.Optional;
        NewerOnlyCriticalCount = breakdown.NewerOnlyCritical;
        NewerOnlyRequiredRevitVersion = breakdown.NewerOnlyCriticalRequiredRevitVersion;
        NewerOnlyOptionalRequiredRevitVersion = breakdown.NewerOnlyOptionalRequiredRevitVersion;
        NewerOnlyPendingCount = breakdown.NewerOnlyOptional;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
