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
        using var _scope = SmartConLogger.BeginScope("FMVM",
            ("Method", nameof(OnActiveDocumentChanged)),
            ("FilePath", Path.GetFileName(e.FilePath)));

        _currentActiveDocumentPath = e.FilePath;
        _ = ActivateBaseForCurrentDocumentAsync(e.FilePath);
    }

    private async Task ActivateBaseForCurrentDocumentAsync(string filePath)
    {
        try
        {
            await _projectBaseActivator.ActivateForDocumentAsync(filePath);
        }
        catch (System.Exception ex)
        {
            SmartConLogger.Warn($"Project base activation failed for '{Path.GetFileName(filePath)}': {ex.GetType().Name}: {ex.Message}. [Action: check registry.json integrity or re-open the project]");
        }

        RecomputeActiveBaseMatch();
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

        if (active is null)
        {
            _activeBaseMatch = null;
            _activeBaseCompatibleWithCurrentDoc = true;
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_PBase_StatusNoDatabase) ?? "No database connected";
            return;
        }

        if (string.IsNullOrEmpty(filePathNullable))
        {
            _activeBaseMatch = null;
            _activeBaseCompatibleWithCurrentDoc = true; // permissive default for legacy behaviour (no active binding context)
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_StatusGeneral) ?? "General base: {0}",
                active.Name);
            return;
        }

        var filePath = filePathNullable!;

        if (active.Kind == BaseType.General)
        {
            _activeBaseMatch = new ProjectBaseMatch(ProjectBaseMatchKind.NotApplicable);
            _activeBaseCompatibleWithCurrentDoc = true;
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_StatusGeneral) ?? "General base: {0}",
                active.Name);
            return;
        }

        var match = _projectBaseEvaluator.Evaluate(active.ProjectBinding, filePath);
        _activeBaseMatch = match;
        _activeBaseCompatibleWithCurrentDoc = match.Kind == ProjectBaseMatchKind.Match;

        if (_activeBaseCompatibleWithCurrentDoc)
        {
            StatusMessage = string.Format(
                LanguageManager.GetString(StringLocalization.Keys.FM_PBase_StatusProjectMatch) ?? "Project base: {0} → {1}",
                active.Name,
                string.Join(", ", match.ParsedValues?.Select(v => $"{v.Key}={v.Value}") ?? []));
            return;
        }

        StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_PBase_StatusProjectMismatch) ?? "Base does not match project. Loading blocked.";

        using var _scope = SmartConLogger.BeginScope("FMVM",
            ("Method", nameof(RecomputeActiveBaseMatch)),
            ("BaseName", active.Name),
            ("FilePath", Path.GetFileName(filePath)));
        SmartConLogger.Info(
            $"Active project base '{active.Name}' does not match the current document " +
            $"(reason: {match.Reason ?? "unknown"}). Loading and placement are disabled until the user picks a matching base.");
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