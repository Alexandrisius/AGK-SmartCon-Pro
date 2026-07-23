using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// Database-update feature (docs/architecture/database-migrations.md,
/// Issue #126). The shared <see cref="IDatabaseUpdateStateService"/> is
/// refreshed silently after the initial database connection and after
/// every database switch — no dialogs are shown unprompted. While the
/// update is required the database is read-only: a red badge + banner are
/// shown, an "Update database" command is available in the database-tools
/// popup, and every write command gates through
/// <see cref="EnsureDatabaseUpToDateAsync"/>.
/// </summary>
public sealed partial class FamilyManagerMainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyDatabaseUpdate))]
    [NotifyPropertyChangedFor(nameof(DatabaseUpdateBannerText))]
    [NotifyPropertyChangedFor(nameof(HasDatabaseUpdateIndicator))]
    [NotifyPropertyChangedFor(nameof(DatabaseUpdateBadgeTooltip))]
    private bool _isDatabaseUpdateRequired;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatabaseUpdateBannerText))]
    [NotifyPropertyChangedFor(nameof(HasProcessableCriticalPending))]
    private int _pendingDatabaseUpdateCount;

    /// <summary>
    /// Pending OPTIONAL migrations (ADR-054). Drives (together with the
    /// critical state) the visibility of the single unified "Обновить
    /// базу" menu command — never the banner, badge or write-op gate.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnyDatabaseUpdate))]
    [NotifyPropertyChangedFor(nameof(HasOptionalPendingIndicator))]
    [NotifyPropertyChangedFor(nameof(HasDatabaseUpdateIndicator))]
    [NotifyPropertyChangedFor(nameof(DatabaseUpdateBadgeTooltip))]
    private int _optionalDatabaseUpdateCount;

    /// <summary>
    /// Visibility of the single unified "Обновить базу" menu command
    /// (ADR-054): actual work exists in the RUNNING Revit AND the current
    /// role may write (Owner/BimMaster — Engineer connections are
    /// read-only, the update would fail on write anyway).
    /// </summary>
    public bool HasAnyDatabaseUpdate =>
        CanEdit && (PendingDatabaseUpdateCount > 0 || OptionalDatabaseUpdateCount > 0);

    /// <summary>
    /// Pending records whose files all require a NEWER Revit and are
    /// OPTIONAL (ADR-054 §3a). Non-blocking: drives only the amber
    /// indicator on the database-tools toggle — these records cannot be
    /// fixed in the running Revit and do not affect write integrity.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOptionalPendingIndicator))]
    [NotifyPropertyChangedFor(nameof(HasDatabaseUpdateIndicator))]
    [NotifyPropertyChangedFor(nameof(DatabaseUpdateBadgeTooltip))]
    private int _newerOnlyDatabaseUpdateCount;

    /// <summary>
    /// Visibility of the SINGLE indicator dot on the database-tools toggle
    /// (ADR-054 §3a): red when CRITICAL pending gates the database
    /// (processable or newer-only — the gate holds until perfectly
    /// updated), amber when only non-blocking OPTIONAL records remain
    /// (recommended, not required — processable here or newer-only).
    /// </summary>
    public bool HasDatabaseUpdateIndicator => IsDatabaseUpdateRequired || HasOptionalPendingIndicator;

    /// <summary>
    /// True when ANY optional (non-blocking) pending exists — processable
    /// in the running Revit or newer-only. Drives the amber indicator:
    /// "рекомендовано обновить" — the menu command (when processable here)
    /// or a newer-Revit pass.
    /// </summary>
    public bool HasOptionalPendingIndicator => OptionalDatabaseUpdateCount > 0 || NewerOnlyDatabaseUpdateCount > 0;

    /// <summary>
    /// True when processable CRITICAL pending exists AND the current role
    /// may write — the banner's "Обновить" button makes sense (an
    /// immediate update lifts the gate). Hidden when only newer-Revit
    /// critical records gate or for read-only roles.
    /// </summary>
    public bool HasProcessableCriticalPending => PendingDatabaseUpdateCount > 0 && CanEdit;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatabaseUpdateBannerText))]
    [NotifyPropertyChangedFor(nameof(HasAnyDatabaseUpdate))]
    private int _newerOnlyCriticalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatabaseUpdateBannerText))]
    private int _newerOnlyRequiredRevitVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatabaseUpdateBadgeTooltip))]
    private int _newerOnlyOptionalRequiredRevitVersion;

    /// <summary>
    /// Tooltip of the database-tools toggle — mirrors the indicator state
    /// (ADR-054 §3a): critical gate text, or the recommended-update text
    /// with the Revit version where it can run.
    /// </summary>
    public string DatabaseUpdateBadgeTooltip
    {
        get
        {
            if (IsDatabaseUpdateRequired)
            {
                return LanguageManager.GetString(StringLocalization.Keys.FM_PBase_UpdateDatabaseBadgeTooltip)
                    ?? "Требуется обновление базы данных";
            }
            if (NewerOnlyDatabaseUpdateCount > 0)
            {
                return string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_PBase_OptionalBadgeTooltipNewerRevit)
                        ?? "Есть рекомендуемые обновления базы — требуется Revit {0} или новее.",
                    NewerOnlyOptionalRequiredRevitVersion);
            }
            if (OptionalDatabaseUpdateCount > 0)
            {
                return LanguageManager.GetString(StringLocalization.Keys.FM_PBase_OptionalBadgeTooltip)
                    ?? "Есть рекомендуемые обновления базы — выполните «Обновить базу» в текущей версии Revit.";
            }
            return LanguageManager.GetString(StringLocalization.Keys.FM_PBase_DatabaseTools)
                ?? "Инструменты базы";
        }
    }

    /// <summary>
    /// Banner text (ADR-054 §3a): the default read-only explanation when
    /// processable critical pending exists; the "update in Revit {0}+"
    /// variant when ONLY newer-Revit critical records gate the database.
    /// </summary>
    public string DatabaseUpdateBannerText
    {
        get
        {
            if (!CanEdit)
            {
                return LanguageManager.GetString(StringLocalization.Keys.FM_DbUpdate_BannerTextReadOnlyRole)
                    ?? "База данных требует обновления и работает в режиме просмотра. Обновление может выполнить пользователь с ролью Owner или BIM-мастер — обратитесь к нему.";
            }
            if (PendingDatabaseUpdateCount > 0 || NewerOnlyCriticalCount <= 0)
            {
                return LanguageManager.GetString(StringLocalization.Keys.FM_DbUpdate_BannerText)
                    ?? "База данных создана в старой версии SmartCon и работает в режиме просмотра. Обновите её, чтобы импортировать и изменять семейства.";
            }
            return string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbUpdate_BannerTextNewerRevit)
                    ?? "База данных требует обновления в Revit {0} или новее и работает в режиме просмотра. Откройте её в Revit {0}+ и выполните «Обновить базу» — тогда всё обновится за один раз.",
                NewerOnlyRequiredRevitVersion);
        }
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateDatabaseCommand))]
    private bool _isDatabaseUpdateRunning;

    private bool CanUpdateDatabase => HasActiveDatabase && !IsDatabaseUpdateRunning;

    private void OnDatabaseUpdateStateChanged(object? sender, EventArgs e) => SyncDatabaseUpdateState();

    private void SyncDatabaseUpdateState()
    {
        IsDatabaseUpdateRequired = _updateState.IsUpdateRequired;
        PendingDatabaseUpdateCount = _updateState.PendingCount;
        OptionalDatabaseUpdateCount = _updateState.OptionalPendingCount;
        NewerOnlyCriticalCount = _updateState.NewerOnlyCriticalCount;
        NewerOnlyRequiredRevitVersion = _updateState.NewerOnlyRequiredRevitVersion;
        NewerOnlyOptionalRequiredRevitVersion = _updateState.NewerOnlyOptionalRequiredRevitVersion;
        NewerOnlyDatabaseUpdateCount = _updateState.NewerOnlyPendingCount;
        IsDatabaseUpdateRunning = _updateState.IsRunning;
        UpdateDatabaseCommand.NotifyCanExecuteChanged();
    }

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
            _updateState.Reset();
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
            await _updateState.RefreshAsync(CurrentRevitVersion).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"DbMigration pending check failed: {ex.Message} [Action: повторите при следующем запуске; если ошибка повторяется — проверьте целостность catalog.db]");
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateDatabase))]
    private async Task UpdateDatabaseAsync()
    {
        if (CurrentRevitVersion <= 0)
        {
            DetectRevitVersion();
        }
        if (CurrentRevitVersion <= 0) return;
        if (!_updateState.IsUpdateRequired && _updateState.OptionalPendingCount <= 0) return;

        using var _scope = SmartConLogger.BeginScope("DbMigration",
            ("Method", nameof(UpdateDatabaseAsync)),
            ("Pending", _updateState.PendingCount + _updateState.OptionalPendingCount));

        await _updateState.UpdateAsync().ConfigureAwait(true);

        // Migrations may have purged catalog rows or re-written
        // types/attributes/hashes — rebuild the tree to reflect the final
        // state.
        if (!_updateState.IsUpdateRequired && _updateState.OptionalPendingCount <= 0)
        {
            await RefreshTreeViaExternalEventAsync().ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Gate for write commands (imports, loads into project, edits,
    /// deletes, version management): while the database has pending
    /// migrations the write is blocked with an explanation and an offer to
    /// run the update immediately. Returns true when the write may proceed.
    /// </summary>
    private async Task<bool> EnsureDatabaseUpToDateAsync()
    {
        if (!_updateState.IsUpdateRequired) return true;

        var proceeded = await _updateState.EnsureUpToDateAsync().ConfigureAwait(true);
        if (proceeded && !_updateState.IsUpdateRequired)
        {
            // The user just completed the update — the catalog content may
            // have changed (purge / hash re-sync), so rebuild the tree.
            await RefreshTreeViaExternalEventAsync().ConfigureAwait(true);
        }
        return proceeded;
    }
}
