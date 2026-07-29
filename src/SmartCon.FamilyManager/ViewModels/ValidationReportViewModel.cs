using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using Keys = SmartCon.UI.StringLocalization.Keys;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// One row of the validation report dialog: a health issue or a rule
/// violation, flattened for a single DataGrid (Section / Type / Attribute
/// / Check / Expected / Actual columns).
/// </summary>
public sealed record ValidationReportIssueRow(
    bool IsHealthSection,
    bool IsError,
    string SectionName,
    string TypeName,
    string AttributeName,
    string CheckDescription,
    string ExpectedValue,
    string ActualValue);

/// <summary>
/// Read-only detail report for one batch import row: why the family
/// passed/failed the import validation gate. Opened by clicking the
/// status icon in the batch dialog's status column.
/// </summary>
public sealed partial class ValidationReportViewModel : ObservableObject, IObservableRequestClose
{
    public event Action<bool?>? RequestClose;

    public string FamilyName { get; }
    public string CategoryPath { get; }

    [ObservableProperty]
    private string _summary;

    public IReadOnlyList<ValidationReportIssueRow> Issues { get; }
    public bool HasIssues => Issues.Count > 0;
    public bool HasHealthSection { get; }
    public bool HasRulesSection { get; }
    public bool IsPassed { get; }

    public string HeaderIconKind => IsPassed ? "CheckCircleOutline" : "CloseCircleOutline";
    public string HeaderIconBrush => IsPassed ? "#4CAF50" : "#F44336";

    /// <summary>
    /// Optional banner shown on top when the report doubles as a BLOCK
    /// dialog (category-change gate): explains that the action was
    /// cancelled. Empty for the batch-dialog use case.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBlockedBanner))]
    private string _blockedBannerText = string.Empty;

    public bool HasBlockedBanner => !string.IsNullOrEmpty(BlockedBannerText);

    public ValidationReportViewModel(
        string familyName,
        string categoryPath,
        FamilyHealthReport? healthReport,
        FamilyValidationReport? validationReport,
        int validationRulesCount)
    {
        FamilyName = familyName;
        CategoryPath = categoryPath;

                var healthSectionName = Loc(Keys.FM_ValidationReport_SectionHealth) ?? "System check";
        var rulesSectionName = Loc(Keys.FM_ValidationReport_SectionRules) ?? "Category rules";

        var rows = new List<ValidationReportIssueRow>();

        if (healthReport is not null && healthReport.Issues.Count > 0)
        {
            foreach (var issue in healthReport.Issues)
            {
                rows.Add(new ValidationReportIssueRow(
                    IsHealthSection: true,
                    IsError: issue.Severity == FamilyHealthIssueSeverity.Error,
                    SectionName: healthSectionName,
                    TypeName: issue.TypeName ?? string.Empty,
                    AttributeName: string.Empty,
                    CheckDescription: issue.Description,
                    ExpectedValue: string.Empty,
                    ActualValue: string.Empty));
            }
        }
        HasHealthSection = rows.Count > 0;

        if (validationReport is not null && validationReport.Violations.Count > 0)
        {
            foreach (var violation in validationReport.Violations)
            {
                rows.Add(new ValidationReportIssueRow(
                    IsHealthSection: false,
                    IsError: true,
                    SectionName: rulesSectionName,
                    TypeName: violation.TypeName,
                    AttributeName: violation.AttributeName,
                    CheckDescription: FormatOperator(violation.Operator),
                    ExpectedValue: FormatExpected(violation),
                    ActualValue: violation.ActualValue
                        ?? (Loc(Keys.FM_ValidationReport_EmptyValue) ?? "<empty>")));
            }
        }
        HasRulesSection = validationReport?.Violations.Count > 0;

        Issues = rows;

        var healthFailed = healthReport?.IsHealthy == false;
        var rulesFailed = validationReport?.IsValid == false;
        IsPassed = !healthFailed && !rulesFailed;

        _summary = BuildSummary(healthReport, validationReport, validationRulesCount);
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke(true);
    }

    private string BuildSummary(FamilyHealthReport? healthReport, FamilyValidationReport? validationReport, int rulesCount)
    {
        
        if (IsPassed)
        {
            return rulesCount > 0
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Loc(Keys.FM_ValidationReport_SummaryPassedRules) ?? "All checks passed ({0} rules)", rulesCount)
                : Loc(Keys.FM_ValidationReport_SummaryPassed) ?? "All checks passed (no rules configured for the category)";
        }

        var parts = new List<string>();
        var healthErrors = healthReport?.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Error) ?? 0;
        if (healthErrors > 0)
        {
            parts.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Loc(Keys.FM_ValidationReport_SummaryHealthErrors) ?? "system errors: {0}", healthErrors));
        }
        var healthWarnings = healthReport?.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Warning) ?? 0;
        if (healthWarnings > 0)
        {
            parts.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Loc(Keys.FM_ValidationReport_SummaryHealthWarnings) ?? "warnings: {0}", healthWarnings));
        }
        if (validationReport is not null && validationReport.Violations.Count > 0)
        {
            parts.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Loc(Keys.FM_ValidationReport_SummaryRuleViolations) ?? "rule violations: {0} (of {1} rules)",
                validationReport.Violations.Count, rulesCount));
        }

        return string.Format(System.Globalization.CultureInfo.CurrentCulture,
            Loc(Keys.FM_ValidationReport_SummaryFailed) ?? "Check failed — {0}",
            string.Join(", ", parts));
    }

    private static string? Loc(string key) => SmartCon.UI.LanguageManager.GetString(key);

    private static string FormatOperator(ValidationRuleOperator op)
    {
                return op switch
        {
            ValidationRuleOperator.IsPresent => Loc(Keys.FM_RuleOp_IsPresent) ?? "Parameter exists",
            ValidationRuleOperator.HasValue => Loc(Keys.FM_RuleOp_HasValue) ?? "Has value",
            ValidationRuleOperator.IsEmpty => Loc(Keys.FM_RuleOp_IsEmpty) ?? "Is empty",
            ValidationRuleOperator.Equals => Loc(Keys.FM_RuleOp_Equals) ?? "Equals",
            ValidationRuleOperator.NotEquals => Loc(Keys.FM_RuleOp_NotEquals) ?? "Not equals",
            ValidationRuleOperator.Contains => Loc(Keys.FM_RuleOp_Contains) ?? "Contains",
            ValidationRuleOperator.NotContains => Loc(Keys.FM_RuleOp_NotContains) ?? "Not contains",
            ValidationRuleOperator.GreaterThan => ">",
            ValidationRuleOperator.GreaterOrEqual => "≥",
            ValidationRuleOperator.LessThan => "<",
            ValidationRuleOperator.LessOrEqual => "≤",
            ValidationRuleOperator.Between => Loc(Keys.FM_RuleOp_Between) ?? "Between",
            _ => op.ToString(),
        };
    }

    private static string FormatExpected(RuleViolation violation)
    {
        if (violation.Operator == ValidationRuleOperator.Between)
        {
            var min = violation.ExpectedMin?.ToString("G6", System.Globalization.CultureInfo.CurrentCulture) ?? "?";
            var max = violation.ExpectedMax?.ToString("G6", System.Globalization.CultureInfo.CurrentCulture) ?? "?";
            return $"{min} — {max}";
        }

        return violation.ExpectedValue ?? string.Empty;
    }
}
