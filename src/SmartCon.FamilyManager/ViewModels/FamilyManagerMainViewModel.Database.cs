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
        if (connection.Kind != BaseType.Project || string.IsNullOrEmpty(_currentActiveDocumentPath))
            return new DatabaseListItem(connection, ProjectBaseMatchKind.NotApplicable);

        var filePath = _currentActiveDocumentPath!;
        if (connection.ConnectionEquals(active))
        {
            var kind = _activeBaseMatch?.Kind ?? ProjectBaseMatchKind.NotApplicable;
            return new DatabaseListItem(connection, kind, _activeBaseMatch?.Reason);
        }

        var evaluation = _projectBaseEvaluator.Evaluate(connection.ProjectBinding, filePath);
        return new DatabaseListItem(connection, evaluation.Kind, evaluation.Reason);
    }

    private void OnActiveDatabaseChanged(object? sender, string connectionId)
    {
        // D-10: stale cache is per-DB. Snapshot from the previous DB must not leak
        // into the new tree (different catalog items, different versions).
        _staleDetector.InvalidateCache();
        RefreshConnections();
        _ = RefreshTreeViaExternalEventAsync();
        RecomputeActiveBaseMatch();
        InvalidateLoadAndPlaceCommands();
    }

    partial void OnSelectedConnectionChanged(DatabaseListItem? value)
    {
        ConfigureProjectBaseCommand.NotifyCanExecuteChanged();
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
                    RefreshConnections();
                    await RefreshAccessAndLoadTreeAsync();
                    var conn = Connections.FirstOrDefault(c => c.Connection.Id == connectionId);
                    StatusMessage = string.Format(
                        LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitched) ?? "Switched to: {0}",
                        conn?.Name ?? connectionId);
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
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitchError) ?? "Error switching database",
                ex.Message);
            StatusMessage = $"{LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitchError) ?? "Error switching database"}: {ex.Message}";
        }
        catch (Exception ex)
        {
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
    private async Task CreateGeneralDatabaseAsync()
    {
        var path = _dialogService.ShowFolderBrowserDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbSelectPath) ?? "Select parent folder for database");
        if (string.IsNullOrWhiteSpace(path)) return;

        var name = _dialogService.ShowInputDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbNewTitle) ?? "New Database",
            LanguageManager.GetString(StringLocalization.Keys.FM_DbNewPrompt) ?? "Enter database name:",
            LanguageManager.GetString(StringLocalization.Keys.FM_DbNewDefault) ?? "New Catalog");

        if (string.IsNullOrWhiteSpace(name)) return;

        IsLoading = true;
        try
        {
            _databaseManager.ActiveDatabaseChanged -= OnActiveDatabaseChanged;
            try
            {
                var conn = await _databaseManager.CreateDatabaseAsync(name!.Trim(), path!);
                RefreshConnections();
                await RefreshAccessAndLoadTreeAsync();
                SelectedConnection = Connections.FirstOrDefault(c => c.Connection.Id == conn.Id);
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DbCreated) ?? "Database \"{0}\" created at {1}",
                    conn.Name, conn.Path);
            }
            finally
            {
                _databaseManager.ActiveDatabaseChanged += OnActiveDatabaseChanged;
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbCreateErrorTitle) ?? "Database creation error",
                ex.Message);
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbCreateError) ?? "Error creating database: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task CreateProjectDatabaseAsync()
    {
        var path = _dialogService.ShowFolderBrowserDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbSelectPath) ?? "Select parent folder for database");
        if (string.IsNullOrWhiteSpace(path)) return;

        var name = _dialogService.ShowInputDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbNewTitle) ?? "New Project Database",
            LanguageManager.GetString(StringLocalization.Keys.FM_DbNewPrompt) ?? "Enter database name:",
            LanguageManager.GetString(StringLocalization.Keys.FM_DbNewDefault) ?? "New Project Catalog");

        if (string.IsNullOrWhiteSpace(name)) return;

        var editorVm = _viewModelFactory.CreateProjectBaseRulesEditorViewModel(null, _currentActiveDocumentPath ?? string.Empty);
        var ok = _dialogService.ShowProjectBaseRulesEditor(editorVm);
        if (ok != true) return;

        var binding = editorVm.BuildBinding();
        if (binding is null)
        {
            _dialogService.ShowWarning(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbNewTitle) ?? "New Project Database",
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_BindingNotConfigured) ?? "Project binding is not configured. Cannot create project base.");
            return;
        }

        IsLoading = true;
        try
        {
            _databaseManager.ActiveDatabaseChanged -= OnActiveDatabaseChanged;
            try
            {
                var conn = await _databaseManager.CreateProjectDatabaseAsync(name!.Trim(), path!, binding);
                RefreshConnections();
                await RefreshAccessAndLoadTreeAsync();
                SelectedConnection = Connections.FirstOrDefault(c => c.Connection.Id == conn.Id);
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DbCreated) ?? "Project database \"{0}\" created at {1}",
                    conn.Name, conn.Path);
            }
            finally
            {
                _databaseManager.ActiveDatabaseChanged += OnActiveDatabaseChanged;
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbCreateErrorTitle) ?? "Database creation error",
                ex.Message);
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbCreateError) ?? "Error creating database: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanConfigureProjectBase))]
    private async Task ConfigureProjectBaseAsync()
    {
        var selected = SelectedConnection;
        if (selected is null || selected.Kind != BaseType.Project) return;

        var editorVm = _viewModelFactory.CreateProjectBaseRulesEditorViewModel(selected.Connection.ProjectBinding, _currentActiveDocumentPath ?? string.Empty);
        var ok = _dialogService.ShowProjectBaseRulesEditor(editorVm);
        if (ok != true) return;
        var binding = editorVm.BuildBinding();
        if (binding is null) return;

        IsLoading = true;
        try
        {
            await _databaseManager.ConfigureProjectBaseAsync(selected.Connection.Id, binding);
            RefreshConnections();
            RecomputeActiveBaseMatch();
            InvalidateLoadAndPlaceCommands();
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitched) ?? "Project binding for \"{0}\" updated",
                selected.Name);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbCreateErrorTitle) ?? "Project base error",
                ex.Message);
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbCreateError) ?? "Error: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private bool CanConfigureProjectBase() => SelectedConnection is not null && SelectedConnection.Kind == BaseType.Project;

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
                RefreshConnections();
                await RefreshAccessAndLoadTreeAsync();
                SelectedConnection = Connections.FirstOrDefault(c => c.Connection.Id == conn.Id);
                StatusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_DbSwitched) ?? "Connected to: {0}",
                    conn.Name);
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

        var connections = _databaseManager.ListConnections();
        if (connections.Count <= 1)
        {
            _dialogService.ShowWarning(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteTitle) ?? "Disconnect",
                LanguageManager.GetString(StringLocalization.Keys.FM_CannotDisconnectOnlyDatabase) ?? "Cannot disconnect the only database.");
            return;
        }

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

        var isActive = _databaseManager.GetActiveConnection()?.Id == SelectedConnection.Connection.Id;
        var connections = _databaseManager.ListConnections();
        if (isActive && connections.Count <= 1)
        {
            _dialogService.ShowWarning(
                LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteTitle) ?? "Delete Database",
                LanguageManager.GetString(StringLocalization.Keys.FM_DbDeleteSingle) ?? "Cannot delete the only database.");
            return;
        }

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
}
