using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyBatchImportRow
{
    /// <summary>
    /// Import Validation Gate: system health report from Phase 1 Prepare
    /// (null for system families and when the check did not run).
    /// </summary>
    public FamilyHealthReport? HealthReport { get; }

    /// <summary>
    /// Import Validation Gate: latest rule-check report for the currently
    /// assigned category (null until a category with rules is assigned).
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GateTooltip))]
    private FamilyValidationReport? _validationReport;

    /// <summary>
    /// Import Validation Gate: number of enabled rules of the currently
    /// assigned category — lets the report dialog distinguish "no rules"
    /// from "rules passed".
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GateTooltip))]
    private int _validationRulesCount;

    /// <summary>
    /// Import Validation Gate: combined gate status shown in the status
    /// column while the row has not been imported yet.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGateBlocked))]
    [NotifyPropertyChangedFor(nameof(GateTooltip))]
    private FamilyRowGateStatus _gateStatus = FamilyRowGateStatus.NotChecked;

    /// <summary><c>true</c> when the gate failed (health errors or rule
    /// violations) — the row is forced to Skip and cannot be imported.</summary>
    public bool IsGateBlocked => GateStatus == FamilyRowGateStatus.Failed;

    /// <summary>
    /// ADR-066 (E1): names of THIS row's dependency children whose gate
    /// failed (they are forced to Skip — the parent will be imported without
    /// them). Computed by the parent view-model after every revalidation;
    /// <c>null</c>/empty when all dependencies passed or the row has none.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFailedDependencies))]
    [NotifyPropertyChangedFor(nameof(FailedDependenciesTooltip))]
    private IReadOnlyList<string>? _failedDependencyNames;

    /// <summary>
    /// #241: categories whose auto-assignment rules match this family
    /// (one for a single match, several for an ambiguous outcome) — the
    /// RULES' recommendation, independent of the row's current category.
    /// <c>null</c>/empty when no rule matches. Evaluated for EVERY row
    /// (New, Existing, Duplicate): we never re-categorize existing
    /// families automatically, but the category-column warning icon keeps
    /// pointing at the recommendation until the current category is one
    /// of the recommended ones.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuleRecommendation))]
    [NotifyPropertyChangedFor(nameof(ShowRuleConflictIcon))]
    [NotifyPropertyChangedFor(nameof(RuleRecommendationTooltip))]
    private IReadOnlyList<string>? _recommendedCategoryIds;

    /// <summary>Display paths parallel to <see cref="RecommendedCategoryIds"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuleRecommendation))]
    [NotifyPropertyChangedFor(nameof(ShowRuleConflictIcon))]
    [NotifyPropertyChangedFor(nameof(RuleRecommendationTooltip))]
    private IReadOnlyList<string>? _recommendedCategoryPaths;

    /// <summary><c>true</c> when the assignment rules recommend at least
    /// one category for this family.</summary>
    public bool HasRuleRecommendation => RecommendedCategoryIds is { Count: > 0 };

    /// <summary>
    /// <c>true</c> when the rules recommend a category that differs from
    /// the row's current one (including «Без категории») — drives the
    /// warning icon in the Category column. The user can pick any category
    /// via the standard picker, but the icon keeps saying "the rules
    /// recommend something else".
    /// </summary>
    public bool ShowRuleConflictIcon =>
        HasRuleRecommendation &&
        (string.IsNullOrEmpty(TargetCategoryId)
         || !RecommendedCategoryIds!.Contains(TargetCategoryId));

    /// <summary>Tooltip of the warning icon: lists the recommended
    /// categories.</summary>
    public string RuleRecommendationTooltip
    {
        get
        {
            if (!HasRuleRecommendation)
                return string.Empty;
            var format = SmartCon.UI.LanguageManager.GetString(
                SmartCon.UI.StringLocalization.Keys.FM_BatchImport_RuleConflict_Tooltip)
                ?? "Правила автоназначения рекомендуют: {0}. Нажмите, чтобы выбрать из подходящих категорий.";
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                format,
                string.Join(", ", RecommendedCategoryPaths ?? Array.Empty<string>()));
        }
    }

    /// <summary>
    /// #241: opens the category picker pre-filtered to the recommended
    /// categories (fired by the warning icon in the Category column).
    /// Caller must await.
    /// </summary>
    [RelayCommand]
    private async Task PickRecommendedCategory()
    {
        var handler = PickRecommendedCategoryRequested;
        if (handler is null) return;
        try
        {
            await handler(this);
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"PickRecommendedCategory failed for '{FileName}': {ex.GetType().Name}: {ex.Message} [Action: закройте batch dialog и повторите, проверьте логи smartcon.log]");
        }
    }

    /// <summary>Raised by the category-column warning icon.</summary>
    public event Func<FamilyBatchImportRow, Task>? PickRecommendedCategoryRequested;

    /// <summary><c>true</c> when at least one dependency child failed the gate.</summary>
    public bool HasFailedDependencies => FailedDependencyNames is { Count: > 0 };

    /// <summary>Localized tooltip listing the failed dependency children.</summary>
    public string FailedDependenciesTooltip
    {
        get
        {
            var format = SmartCon.UI.LanguageManager.GetString(
                SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_DependencyFailed)
                ?? "Зависимости не прошли проверку и будут пропущены: {0}. Элемент будет импортирован без них.";
            return string.Format(
                System.Globalization.CultureInfo.CurrentCulture,
                format,
                string.Join(", ", FailedDependencyNames ?? Array.Empty<string>()));
        }
    }

    /// <summary>Localized short summary of the gate result for the status
    /// icon tooltip.</summary>
    public string GateTooltip
    {
        get
        {
            static string? Loc(string key) => SmartCon.UI.LanguageManager.GetString(key);
            if (ImportRowState == FamilyBatchImportRowState.Success)
            {
                return Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_Imported)
                    ?? "Импорт выполнен — открыть отчёт о проверке";
            }

            return GateStatus switch
            {
                FamilyRowGateStatus.Failed when HealthReport?.IsHealthy == false && ValidationReport?.IsValid == false =>
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_FailedBoth) ?? "System errors: {0}, rule violations: {1}",
                        HealthReport.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Error),
                        ValidationReport.Violations.Count),
                FamilyRowGateStatus.Failed when HealthReport?.IsHealthy == false =>
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_FailedHealth) ?? "System errors in the family: {0}",
                        HealthReport.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Error)),
                FamilyRowGateStatus.Failed when ValidationReport is not null =>
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_FailedRules) ?? "Rule violations: {0}",
                        ValidationReport.Violations.Count),
                FamilyRowGateStatus.Warning =>
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_Warning) ?? "Warnings: {0}",
                        HealthReport?.Issues.Count ?? 0),
                FamilyRowGateStatus.Passed when ValidationRulesCount > 0 =>
                    string.Format(System.Globalization.CultureInfo.CurrentCulture,
                        Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_PassedRules) ?? "Passed {0} rules",
                        ValidationRulesCount),
                FamilyRowGateStatus.Passed =>
                    Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_Passed) ?? "Check passed",
                FamilyRowGateStatus.Checking =>
                    Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_Checking) ?? "Checking…",
                _ => Loc(SmartCon.UI.StringLocalization.Keys.FM_Gate_Tooltip_NotChecked) ?? "Category not assigned — rules not checked",
            };
        }
    }

    // ── Clickable status badges (#210) ─────────────────────────────────
    // The status column shows at most two clickable badges (StatusBadgeButton
    // style): the dependency paperclip (IsDependency) and one problem
    // triangle coloured by the worst notice severity. Both open the status
    // details dialog listing ALL of the row's notices with the full
    // explanations that used to be long tooltips.
}
