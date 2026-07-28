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
        RecomputeActiveBaseMatch();
        RefreshConnections();
        _ = RefreshTreeViaExternalEventAsync();
        InvalidateLoadAndPlaceCommands();
        // Issue #126: the switched-to database may carry stale hashes.
        _ = RefreshDatabaseUpdateStateAsync();
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
                RecomputeActiveBaseMatch();
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
                RecomputeActiveBaseMatch();
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
            // #169: cross-DB write switched LocalCatalogDatabase paths and reset
            // write access to writable — re-resolve the role so the active base
            // returns to its role-correct (possibly read-only) mode.
            await _accessControl.RefreshCurrentUserAsync();
            UpdateAccessProperties();
            RecomputeActiveBaseMatch();
            RefreshConnections();
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

    private bool CanConfigureProjectBase() =>
        SelectedConnection is not null && SelectedConnection.Kind == BaseType.Project && CanEdit;

    [RelayCommand(CanExecute = nameof(CanConvertSelectedToProject))]
    private async Task ConvertToProjectBaseAsync()
    {
        var selected = SelectedConnection;
        if (selected is null || selected.Kind != BaseType.General) return;

        var editorVm = _viewModelFactory.CreateProjectBaseRulesEditorViewModel(null, _currentActiveDocumentPath ?? string.Empty);
        var ok = _dialogService.ShowProjectBaseRulesEditor(editorVm);
        if (ok != true) return;
        var binding = editorVm.BuildBinding();
        if (binding is null)
        {
            _dialogService.ShowWarning(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_ConvertToProjectBaseTitle) ?? "Convert to project base",
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_BindingNotConfiguredConvert) ?? "Project binding is not configured. Cannot convert to project base.");
            return;
        }

        IsLoading = true;
        try
        {
            await _databaseManager.ConfigureProjectBaseAsync(selected.Connection.Id, binding);
            // #169: cross-DB write switched LocalCatalogDatabase paths and reset
            // write access to writable — re-resolve the role so the active base
            // returns to its role-correct (possibly read-only) mode.
            await _accessControl.RefreshCurrentUserAsync();
            UpdateAccessProperties();
            RecomputeActiveBaseMatch();
            RefreshConnections();
            InvalidateLoadAndPlaceCommands();
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_ConvertedToProject) ?? "Database \"{0}\" converted to project base",
                selected.Name);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_ConvertErrorTitle) ?? "Database conversion error",
                ex.Message);
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_ConvertError) ?? "Error converting database: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// #168: visibility + CanExecute for «Сделать проектной базой» — shown only
    /// when the selected connection is a general base and the role can edit.
    /// </summary>
    public bool CanConvertSelectedToProject =>
        SelectedConnection is not null && SelectedConnection.Kind == BaseType.General && CanEdit;

    [RelayCommand(CanExecute = nameof(CanConvertSelectedToGeneral))]
    private async Task ConvertToGeneralBaseAsync()
    {
        var selected = SelectedConnection;
        if (selected is null || selected.Kind != BaseType.Project) return;

        var confirmed = _dialogService.ShowConfirmation(
            LanguageManager.GetString(StringLocalization.Keys.FM_PBase_ConvertToGeneralConfirmTitle) ?? "Convert to general base",
            string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_ConvertToGeneralConfirm) ?? "Database \"{0}\" will stop auto-activating by project file name. The binding template will be deleted. Continue?",
                selected.Name));
        if (!confirmed) return;

        IsLoading = true;
        try
        {
            await _databaseManager.ConvertToGeneralBaseAsync(selected.Connection.Id);
            // #169: cross-DB write switched LocalCatalogDatabase paths and reset
            // write access to writable — re-resolve the role so the active base
            // returns to its role-correct (possibly read-only) mode.
            await _accessControl.RefreshCurrentUserAsync();
            UpdateAccessProperties();
            RecomputeActiveBaseMatch();
            RefreshConnections();
            InvalidateLoadAndPlaceCommands();
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_ConvertedToGeneral) ?? "Database \"{0}\" converted to general base",
                selected.Name);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_ConvertErrorTitle) ?? "Database conversion error",
                ex.Message);
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_ConvertError) ?? "Error converting database: {0}",
                ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>
    /// #168: visibility + CanExecute for «Сделать общей базой» — shown only
    /// when the selected connection is a project base and the role can edit.
    /// </summary>
    public bool CanConvertSelectedToGeneral =>
        SelectedConnection is not null && SelectedConnection.Kind == BaseType.Project && CanEdit;

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
}
