using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Selectors;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    private void RefreshConnections()
    {
        _suppressConnectionChanged = true;
        try
        {
            var connections = _databaseManager.ListConnections();
            var active = _databaseManager.GetActiveConnection();
            var items = connections.Select(c => BuildListItem(c, active)).ToList();
            Connections = new ObservableCollection<DatabaseListItem>(items);
            SelectedConnection = Connections.FirstOrDefault(c => c.Connection.Id == active?.Id);
            HasActiveDatabase = active is not null;
        }
        finally
        {
            _suppressConnectionChanged = false;
        }
    }

    private DatabaseListItem BuildListItem(DatabaseConnection connection, DatabaseConnection? active)
    {
        // #174: positively unsaved document (empty path received) — every
        // project base is blocked until the file is saved: show the lock
        // (mismatch) icon, not the general one. A null path ("no event yet",
        // startup race) is NOT unsaved and falls through to NotApplicable.
        if (connection.Kind == BaseType.Project && _currentActiveDocumentPath is { Length: 0 })
        {
            var unsavedReason = LanguageManager.GetString(StringLocalization.Keys.FM_PBase_StatusProjectUnsaved)
                ?? "File not saved — project bases are unavailable. Save the file to activate a project base.";
            return new DatabaseListItem(connection, ProjectBaseMatchKind.Mismatch, unsavedReason);
        }

        if (connection.Kind != BaseType.Project || string.IsNullOrEmpty(_currentActiveDocumentPath))
        {
            using var _scope = SmartConLogger.BeginScope("FMVM",
                ("Method", nameof(BuildListItem)),
                ("BaseName", connection.Name),
                ("Kind", connection.Kind),
                ("HasActiveDocPath", !string.IsNullOrEmpty(_currentActiveDocumentPath)));
            SmartConLogger.Debug($"BuildListItem: '{connection.Name}' is not a project base or no active document path -> NotApplicable");
            return new DatabaseListItem(connection, ProjectBaseMatchKind.NotApplicable);
        }

        var filePath = _currentActiveDocumentPath!;
        if (connection.ConnectionEquals(active))
        {
            var kind = _activeBaseMatch?.Kind ?? ProjectBaseMatchKind.NotApplicable;
            using var _scope = SmartConLogger.BeginScope("FMVM",
                ("Method", nameof(BuildListItem)),
                ("BaseName", connection.Name),
                ("IsActive", true),
                ("MatchKind", kind),
                ("Reason", _activeBaseMatch?.Reason ?? string.Empty));
            SmartConLogger.Debug($"BuildListItem: active project base '{connection.Name}' -> {kind}");
            return new DatabaseListItem(connection, kind, _activeBaseMatch?.Reason);
        }

        var evaluation = _projectBaseEvaluator.Evaluate(connection.ProjectBinding, filePath);
        using var _scope2 = SmartConLogger.BeginScope("FMVM",
            ("Method", nameof(BuildListItem)),
            ("BaseName", connection.Name),
            ("IsActive", false),
            ("MatchKind", evaluation.Kind),
            ("Reason", evaluation.Reason ?? string.Empty));
        SmartConLogger.Debug($"BuildListItem: project base '{connection.Name}' evaluated -> {evaluation.Kind}");
        return new DatabaseListItem(connection, evaluation.Kind, evaluation.Reason);
    }

    private void OnActiveDatabaseChanged(object? sender, string? connectionId)
    {
        // D-10: stale cache is per-DB. Snapshot from the previous DB must not leak
        // into the new tree (different catalog items, different versions).
        _staleDetector.InvalidateCache();
        // #259: compliance verdicts are per-DB too (other items, other rules).
        _complianceService.InvalidateCache();
        RecomputeActiveBaseMatch();
        RefreshConnections();
        _ = RefreshTreeViaExternalEventAsync();
        InvalidateLoadAndPlaceCommands();
        // Issue #126: the switched-to database may carry stale hashes.
        _ = RefreshDatabaseUpdateStateAsync();
    }

    partial void OnSelectedConnectionChanged(DatabaseListItem? value)
    {
        DeleteDatabaseCommand.NotifyCanExecuteChanged();
        if (value is null) return;
        if (_suppressConnectionChanged) return;
        var active = _databaseManager.GetActiveConnection();
        if (active?.Id == value.Connection.Id) return;
        _ = SwitchDatabaseAsync(value.Connection.Id);
    }

    private async Task SwitchDatabaseAsync(string connectionId)
    {
        IsLoading = true;
        try
        {
            _databaseManager.ActiveDatabaseChanged -= OnActiveDatabaseChanged;
            try
            {
                var success = await _databaseManager.SwitchDatabaseAsync(connectionId);
                if (success)
                {
                    RecomputeActiveBaseMatch();
                    RefreshConnections();
                    await RefreshAccessAndLoadTreeAsync();
                    RefreshCanLoadToProject();
                    RefreshCanPlaceType();
                    InvalidateLoadAndPlaceCommands();
                    var conn = Connections.FirstOrDefault(c => c.Connection.Id == connectionId);
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitched) ?? "Switched to: {0}",
                        conn?.Name ?? connectionId);
                    // Issue #126: the switched-to database may carry stale hashes.
                    _ = RefreshDatabaseUpdateStateAsync();
                }
                else
                {
                    StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitchError) ?? "Error switching database";
                    RefreshConnections();
                }
            }
            finally
            {
                _databaseManager.ActiveDatabaseChanged += OnActiveDatabaseChanged;
            }
        }
        catch (InvalidOperationException ex)
        {
            SmartConLogger.Error($"SwitchDatabase failed (InvalidOperation): {ex.Message}");
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitchError) ?? "Error switching database",
                ex.Message);
            StatusMessage = $"{LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitchError) ?? "Error switching database"}: {ex.Message}";
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"SwitchDatabase failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitchError) ?? "Error switching database",
                ex.Message);
            StatusMessage = $"{LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitchError) ?? "Error switching database"}: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task ConnectDatabaseAsync()
    {
        var path = _dialogService.ShowFolderBrowserDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbSelectPath) ?? "Select database folder");
        if (string.IsNullOrWhiteSpace(path)) return;

        IsLoading = true;
        try
        {
            _databaseManager.ActiveDatabaseChanged -= OnActiveDatabaseChanged;
            try
            {
                var conn = await _databaseManager.ConnectDatabaseAsync(path!);
                RecomputeActiveBaseMatch();
                RefreshConnections();
                await RefreshAccessAndLoadTreeAsync();
                SelectedConnection = Connections.FirstOrDefault(c => c.Connection.Id == conn.Id);
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitched) ?? "Connected to: {0}",
                    conn.Name);
                // Issue #126: an externally connected database may carry stale hashes.
                _ = RefreshDatabaseUpdateStateAsync();
            }
            finally
            {
                _databaseManager.ActiveDatabaseChanged += OnActiveDatabaseChanged;
            }
        }
        catch (FileNotFoundException ex) when (!string.IsNullOrEmpty(ex.Message))
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbConnectErrorTitle) ?? "Database connection error",
                ex.Message);
            StatusMessage = ex.Message;
        }
        catch (InvalidOperationException ex)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbConnectErrorTitle) ?? "Database connection error",
                ex.Message);
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbConnectError) ?? "Database connection error: {0}",
                ex.Message);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbConnectErrorTitle) ?? "Database connection error",
                ex.Message);
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbConnectError) ?? "Database connection error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task DisconnectDatabaseAsync()
    {
        if (SelectedConnection is null) return;

        IsLoading = true;
        try
        {
            var success = await _databaseManager.DisconnectDatabaseAsync(SelectedConnection.Connection.Id);
            if (success)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleted) ?? "Disconnected: {0}",
                    SelectedConnection.Name);
                RefreshConnections();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_ErrorFormat) ?? "Error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanManageUsersCheck))]
    private async Task DeleteDatabaseAsync()
    {
        if (SelectedConnection is null) return;

        var confirm = _dialogService.ShowInputDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteTitle) ?? "Delete Database",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbDeletePrompt) ?? "Enter \"{0}\" to confirm deletion:",
                SelectedConnection.Name),
            "");

        if (confirm != SelectedConnection.Name) return;

        IsLoading = true;
        try
        {
            var success = await _databaseManager.DeleteDatabaseAsync(SelectedConnection.Connection.Id);
            if (success)
            {
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleted) ?? "Database \"{0}\" deleted",
                    SelectedConnection.Name);
                RefreshConnections();
            }
        }
        catch (Exception ex)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteError) ?? "Error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanManageUsersCheck() => CanManageUsers;

    /// <summary>
    /// ADR-058 (#173): the plugin-compatibility banner's
    /// "Обновить приложение" button — routes to the existing About dialog
    /// (update channel, changelog, update check) instead of duplicating
    /// update logic here.
    /// </summary>
    [RelayCommand]
    private void OpenAbout() => _aboutDialogService.ShowAbout();
}
