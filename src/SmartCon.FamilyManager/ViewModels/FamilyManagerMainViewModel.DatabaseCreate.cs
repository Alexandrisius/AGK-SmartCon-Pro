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
    [RelayCommand]
    private async Task CreateGeneralDatabaseAsync()
    {
        var path = _dialogService.ShowFolderBrowserDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbSelectPath) ?? "Select parent folder for database");
        if (string.IsNullOrWhiteSpace(path)) return;

        var name = _dialogService.ShowInputDialog(
            LanguageManager.GetString(StringLocalization.Keys.FM_DbNewTitle) ?? "New Database",
            LanguageManager.GetString(StringLocalization.Keys.FM_DbNewPrompt) ?? "Enter database name:",
            placeholderText: LanguageManager.GetString(StringLocalization.Keys.FM_DbNewPlaceholder) ?? "Database name");

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
                // A freshly created database is up to date by construction,
                // but the singleton update-state may still carry the
                // PREVIOUS database's verdict (critical banner + write gate
                // stuck on the new empty DB). ActiveDatabaseChanged is
                // detached for this branch and OnSelectedConnectionChanged
                // early-returns (the new DB is already active) — recompute
                // explicitly, same contract as Switch/ConnectDatabaseAsync.
                // Isolated: a refresh failure must not mask the successful
                // creation with the error dialog.
                try
                {
                    await RefreshDatabaseUpdateStateAsync();
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"DbMigration: update-state refresh after database creation failed: {ex.Message} " +
                        "[Action: переключите базу туда-обратно — состояние баннера пересчитается]");
                }
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
            placeholderText: LanguageManager.GetString(StringLocalization.Keys.FM_DbNewPlaceholder) ?? "Database name");

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
                // Same contract as CreateGeneralDatabaseAsync: the singleton
                // update-state must be recomputed for the freshly created
                // database (the switch/create event handler is detached here).
                try
                {
                    await RefreshDatabaseUpdateStateAsync();
                }
                catch (Exception ex)
                {
                    SmartConLogger.Warn(
                        $"DbMigration: update-state refresh after database creation failed: {ex.Message} " +
                        "[Action: переключите базу туда-обратно — состояние баннера пересчитается]");
                }
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

    [RelayCommand(CanExecute = nameof(CanConfigureSelectedProjectBase))]
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

    /// <summary>
    /// Visibility + CanExecute for «Настроить правила проектной базы» — shown
    /// only when the selected connection is a project base and the role can
    /// edit (same contextual-visibility pattern as the convert commands).
    /// </summary>
    public bool CanConfigureSelectedProjectBase =>
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
}
