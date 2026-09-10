using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;
using Keys = SmartCon.UI.StringLocalization.Keys;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// #210: clickable status badges in the catalog tree. Two commands, two
/// separate dialogs: ShowStatusDetails (PROBLEM view — warning/error
/// notices + update actions) opened by the warning badges (leaf problem
/// triangle, category stale roll-up), and ShowStatusInfoDetails
/// (RELATED-ELEMENTS view — info notices only, no actions) opened by the
/// paperclip. The notices come from the node itself; the update actions
/// mirror the node's context menu including the CanExecute guards. The
/// type presence dot lives in LoadPlace.cs (PlaceTypeFromIndicator).
/// </summary>
public sealed partial class FamilyManagerMainViewModel
{
    /// <summary>
    /// #210: the PROBLEM view (warning/error notices + update actions) —
    /// opened by the warning badges (stale / outdated-nested triangle on a
    /// leaf, stale roll-up on a category).
    /// </summary>
    [RelayCommand]
    private void ShowStatusDetails(CatalogTreeNodeViewModel? node)
    {
        if (node is null) return;

        try
        {
            var notices = GetNodeNotices(node)
                .Where(n => n.Severity >= StatusNoticeSeverity.Warning).ToList();
            if (notices.Count == 0) return;

            var (actions, hasReportAction) = BuildStatusDetailsActions(node);
            // The split button repeats the node's tree context menu: leaf →
            // «Обновить все типы» (FM_UpdateAllTypes), category → «Обновить».
            // #259: a mixed set (update actions + «Открыть отчёт о проверке»)
            // gets the neutral «Действия» label — the report is not an update.
            var menuLabelKey = node is FamilyLeafNodeViewModel ? Keys.FM_UpdateAllTypes : Keys.FM_Update;
            var detailsVm = new StatusDetailsViewModel(
                node.DisplayName,
                GetNodeSubtitle(node),
                notices,
                actions,
                actionsMenuLabel: actions.Count > 1
                    ? hasReportAction
                        ? LanguageManager.GetString(Keys.FM_StatusDetails_ActionsMenu) ?? "Действия"
                        : LanguageManager.GetString(menuLabelKey) ?? "Обновить"
                    : null);
            _dialogService.ShowStatusDetails(detailsVm);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"Tree.Badges: failed to open status details for '{node.DisplayName}': {ex.Message}");
        }
    }

    /// <summary>
    /// #210: the RELATED-ELEMENTS view (info notices only, NO warnings and
    /// NO actions) — opened by the paperclip badge. Relationship info and
    /// problems are two separate dialogs by design.
    /// </summary>
    [RelayCommand]
    private void ShowStatusInfoDetails(CatalogTreeNodeViewModel? node)
    {
        if (node is null) return;

        try
        {
            var notices = GetNodeNotices(node)
                .Where(n => n.Severity == StatusNoticeSeverity.Info).ToList();
            if (notices.Count == 0) return;

            var detailsVm = new StatusDetailsViewModel(
                node.DisplayName,
                GetNodeSubtitle(node),
                notices);
            _dialogService.ShowStatusDetails(detailsVm);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error(
                $"Tree.Badges: failed to open info details for '{node.DisplayName}': {ex.Message}");
        }
    }

    private static IReadOnlyList<StatusNotice> GetNodeNotices(CatalogTreeNodeViewModel node) =>
        node switch
        {
            FamilyLeafNodeViewModel leaf => leaf.StatusNotices,
            CategoryNodeViewModel category => category.StatusNotices,
            _ => Array.Empty<StatusNotice>(),
        };

    private static string? GetNodeSubtitle(CatalogTreeNodeViewModel node) =>
        node switch
        {
            FamilyLeafNodeViewModel leaf => leaf.CategoryPath,
            CategoryNodeViewModel category => category.FullPath,
            _ => null,
        };

    /// <summary>
    /// Follow-up actions of the details dialog = the node's UPDATE commands
    /// only (the dialog explains a verdict; re-running the check that
    /// produced it is pointless) PLUS the #259 compliance report («Открыть
    /// отчёт о проверке») for a leaf with a live Fail verdict. Predicates
    /// mirror the commands' CanExecute — evaluated here against the specific
    /// node (RelayCommand.Execute skips CanExecute, so an unguarded action
    /// would bypass e.g. the banned-user check or the incompatible-Revit
    /// guard). Two update variants render as a split button («Обновить ▾»)
    /// whose menu items repeat the tree context menu exactly.
    /// </summary>
    /// <returns>The action list and whether it contains the compliance-report
    /// action (drives the neutral split-button label for mixed sets).</returns>
    private (List<StatusDetailsAction> Actions, bool HasReportAction) BuildStatusDetailsActions(CatalogTreeNodeViewModel node)
    {
        static string Loc(string key, string fallback) =>
            LanguageManager.GetString(key) ?? fallback;

        var actions = new List<StatusDetailsAction>();
        var hasReportAction = false;
        switch (node)
        {
            case FamilyLeafNodeViewModel leaf:
                // The UpdateStale* commands are selection-based (CanLoadToProject
                // reads SelectedItem): the action selects the leaf first, the
                // guard below is the same predicate evaluated for THIS leaf.
                if (leaf.IsStale && CanUpdateStaleLeaf(leaf))
                {
                    if (leaf.FamilySource == "system")
                    {
                        // Issue #104: system families have a single overwrite semantics.
                        actions.Add(new StatusDetailsAction(
                            Loc(Keys.FM_UpdateAllTypes, "Обновить все типы"),
                            () => { leaf.IsSelected = true; UpdateStaleCommand.Execute(null); }));
                    }
                    else
                    {
                        actions.Add(new StatusDetailsAction(
                            Loc(Keys.FM_UpdateKeepParams, "Сохранить параметры"),
                            () => { leaf.IsSelected = true; UpdateStaleKeepParamsCommand.Execute(null); }));
                        actions.Add(new StatusDetailsAction(
                            Loc(Keys.FM_UpdateOverwriteParams, "Перезаписать параметры"),
                            () => { leaf.IsSelected = true; UpdateStaleCommand.Execute(null); }));
                    }
                }
                // #259: the rule-violation report — the fix path is edit +
                // re-import, so no update action is offered for this verdict.
                if (HasComplianceFailVerdict(leaf))
                {
                    hasReportAction = true;
                    actions.Add(new StatusDetailsAction(
                        Loc(Keys.FM_StatusDetails_OpenValidationReport, "Открыть отчёт о проверке"),
                        () => OpenComplianceReport(leaf)));
                }
                break;

            case CategoryNodeViewModel category:
                if (CanUpdateCategoryKeep(category))
                {
                    actions.Add(new StatusDetailsAction(
                        Loc(Keys.FM_UpdateKeepParams, "Сохранить параметры"),
                        () => UpdateCategoryKeepParamsCommand.Execute(category)));
                }
                if (CanUpdateCategoryOverwrite(category))
                {
                    actions.Add(new StatusDetailsAction(
                        Loc(Keys.FM_UpdateOverwriteParams, "Перезаписать параметры"),
                        () => UpdateCategoryOverwriteParamsCommand.Execute(category)));
                }
                break;
        }
        return (actions, hasReportAction);
    }

    /// <summary>
    /// The <c>UpdateStale*</c> command guard (<see cref="RefreshCanLoadToProject"/>)
    /// evaluated for a specific leaf instead of the current selection.
    /// </summary>
    private bool CanUpdateStaleLeaf(FamilyLeafNodeViewModel leaf) =>
        CanLoadLeafToProject(leaf);
}
