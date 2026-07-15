using System.IO;
using System.Threading.Tasks;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    private void OnActiveDocumentChanged(object? sender, ActiveDocumentChangedEventArgs e)
    {
        _currentActiveDocumentPath = e.FilePath;
        _ = ActivateBaseForCurrentDocumentAsync(e.FilePath);
    }

    private void OnActiveDocumentPathChanged(object? sender, ActiveDocumentPathChangedEventArgs e)
    {
        SmartConLogger.Info($"Active document path changed via {e.Reason} to '{Path.GetFileName(e.FilePath)}' — re-evaluating project base");
        _currentActiveDocumentPath = e.FilePath;
        _ = ActivateBaseForCurrentDocumentAsync(e.FilePath);
    }

    private async Task ActivateBaseForCurrentDocumentAsync(string filePath)
    {
        using var _scope = SmartConLogger.BeginScope("FMVM",
            ("Method", nameof(ActivateBaseForCurrentDocumentAsync)),
            ("FilePath", Path.GetFileName(filePath) ?? "(none)"));
        try
        {
            await _projectBaseActivator.ActivateForDocumentAsync(filePath);
        }
        catch (System.Exception ex)
        {
            SmartConLogger.Warn($"Project base activation failed for '{Path.GetFileName(filePath) ?? "(none)"}': {ex.GetType().Name}: {ex.Message}. [Action: check registry.json integrity or re-open the project]");
        }

        RecomputeActiveBaseMatch();
        RefreshConnections();
        InvalidateLoadAndPlaceCommands();
    }

    /// <summary>
    /// Re-evaluate the "active base == matches current document" flag based
    /// on the cached <c>_currentActiveDocumentPath</c> and the currently
    /// active connection. Called from <see cref="OnActiveDocumentChanged"/>
    /// (after the activator ran) and from
    /// <see cref="ViewModels.FamilyManagerMainViewModel.Database.OnActiveDatabaseChanged"/>
    /// (after a manual pick in the ComboBox). Updates <see cref="_activeBaseMatch"/>
    /// and <see cref="_activeBaseCompatibleWithCurrentDoc"/> in place.
    /// </summary>
    internal void RecomputeActiveBaseMatch()
    {
        var active = _databaseManager.GetActiveConnection();
        var filePathNullable = _currentActiveDocumentPath;
        string? statusMessage;

        using var _scope = SmartConLogger.BeginScope("FMVM",
            ("Method", nameof(RecomputeActiveBaseMatch)),
            ("ActiveBaseName", active?.Name ?? "(none)"),
            ("ActiveBaseKind", active?.Kind.ToString() ?? "(none)"),
            ("FilePath", Path.GetFileName(filePathNullable ?? string.Empty)));

        if (active is null)
        {
            _activeBaseMatch = null;
            _activeBaseCompatibleWithCurrentDoc = true;
            statusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_PBase_StatusNoDatabase) ?? "No database connected";
            SmartConLogger.Debug("RecomputeActiveBaseMatch: no active database");
        }
        else if (string.IsNullOrEmpty(filePathNullable))
        {
            _activeBaseMatch = null;
            _activeBaseCompatibleWithCurrentDoc = true; // permissive default for legacy behaviour (no active binding context)
            statusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_StatusGeneral) ?? "General base: {0}",
                active.Name);
            SmartConLogger.Debug($"RecomputeActiveBaseMatch: no active document path for base '{active.Name}'");
        }
        else if (active.Kind == BaseType.General)
        {
            _activeBaseMatch = new ProjectBaseMatch(ProjectBaseMatchKind.NotApplicable);
            _activeBaseCompatibleWithCurrentDoc = true;
            statusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_StatusGeneral) ?? "General base: {0}",
                active.Name);
            SmartConLogger.Debug($"RecomputeActiveBaseMatch: base '{active.Name}' is general");
        }
        else
        {
            var filePath = filePathNullable!;
            var match = _projectBaseEvaluator.Evaluate(active.ProjectBinding, filePath);
            _activeBaseMatch = match;
            _activeBaseCompatibleWithCurrentDoc = match.Kind == ProjectBaseMatchKind.Match;

            if (_activeBaseCompatibleWithCurrentDoc)
            {
                statusMessage = string.Format(
                    LanguageManager.GetString(StringLocalization.Keys.FM_PBase_StatusProjectMatch) ?? "Project base: {0} → {1}",
                    active.Name,
                    string.Join(", ", match.ParsedValues?.Select(v => $"{v.Key}={v.Value}") ?? []));
                SmartConLogger.Info($"RecomputeActiveBaseMatch: active project base '{active.Name}' matches current document");
            }
            else
            {
                statusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_PBase_StatusProjectMismatch) ?? "Base does not match project. Loading blocked.";
                SmartConLogger.Warn(
                    $"Active project base '{active.Name}' does not match the current document " +
                    $"(reason: {match.Reason ?? "unknown"}). Loading and placement are disabled until the user picks a matching base. [Action: select a project base that matches the current file name or reconfigure the binding rules]");
            }
        }

        StatusMessage = statusMessage ?? string.Empty;
        RefreshCanLoadToProject();
        RefreshCanPlaceType();
        NotifyCheckCommands();
    }

    /// <summary>
    /// Force WPF to re-query the CanExecute of every command gated by
    /// <see cref="_activeBaseCompatibleWithCurrentDoc"/> so the visible state
    /// matches the new information. After a manual DB pick in the ComboBox
    /// (which fires <c>OnSelectedConnectionChanged</c>) the active tree is
    /// already rebuilt, but the gating commands themselves are not
    /// invalidated; this method picks them up explicitly.
    /// </summary>
    private void InvalidateLoadAndPlaceCommands()
    {
        LoadToProjectCommand.NotifyCanExecuteChanged();
        LoadToProjectKeepParamsCommand.NotifyCanExecuteChanged();
        PlaceTypeCommand.NotifyCanExecuteChanged();
        StartPlacementDragCommand.NotifyCanExecuteChanged();
    }
}