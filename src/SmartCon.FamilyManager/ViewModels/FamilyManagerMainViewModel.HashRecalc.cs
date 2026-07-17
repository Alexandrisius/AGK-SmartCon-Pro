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
    private bool _isDatabaseUpdateRequired;

    [ObservableProperty]
    private int _pendingDatabaseUpdateCount;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UpdateDatabaseCommand))]
    private bool _isDatabaseUpdateRunning;

    private bool CanUpdateDatabase => HasActiveDatabase && !IsDatabaseUpdateRunning;

    private void OnDatabaseUpdateStateChanged(object? sender, EventArgs e) => SyncDatabaseUpdateState();

    private void SyncDatabaseUpdateState()
    {
        IsDatabaseUpdateRequired = _updateState.IsUpdateRequired;
        PendingDatabaseUpdateCount = _updateState.PendingCount;
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
        if (!_updateState.IsUpdateRequired || CurrentRevitVersion <= 0) return;

        using var _scope = SmartConLogger.BeginScope("DbMigration",
            ("Method", nameof(UpdateDatabaseAsync)),
            ("Pending", _updateState.PendingCount));

        await _updateState.UpdateAsync().ConfigureAwait(true);

        // Migrations may have purged catalog rows or re-synced item
        // hashes/names — rebuild the tree to reflect the final state.
        if (!_updateState.IsUpdateRequired)
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
