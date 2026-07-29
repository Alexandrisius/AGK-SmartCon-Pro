using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using Keys = SmartCon.UI.StringLocalization.Keys;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// One row of the validation report dialog: a health issue or a rule
/// violation, flattened for a single DataGrid (Type / Attribute / Check /
/// Expected / Actual columns; the health-vs-rules split is conveyed by
/// the status lines above the grid, not a column).
/// </summary>
public sealed record ValidationReportIssueRow(
    bool IsError,
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
    public bool IsPassed { get; }

    /// <summary>One-line status of the system health check — always shown,
    /// so "no rule violations" never reads as "nothing was checked".</summary>
    public string HealthStatusText { get; }
    public string HealthStatusIconKind { get; }
    public string HealthStatusBrush { get; }

    /// <summary>One-line status of the category rule check — explicitly
    /// says "not configured" / "not checked" / "passed" / "violated".</summary>
    public string RulesStatusText { get; }
    public string RulesStatusIconKind { get; }
    public string RulesStatusBrush { get; }

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

        var rows = new List<ValidationReportIssueRow>();

        if (healthReport is not null && healthReport.Issues.Count > 0)
        {
            foreach (var issue in healthReport.Issues)
            {
                rows.Add(new ValidationReportIssueRow(
                    IsError: issue.Severity == FamilyHealthIssueSeverity.Error,
                    TypeName: FamilyTypeSnapshot.ResolveDisplayName(issue.TypeName ?? string.Empty, familyName),
                    AttributeName: string.Empty,
                    CheckDescription: issue.Description,
                    ExpectedValue: string.Empty,
                    ActualValue: string.Empty));
            }
        }

        if (validationReport is not null && validationReport.Violations.Count > 0)
        {
            foreach (var violation in validationReport.Violations)
            {
                rows.Add(new ValidationReportIssueRow(
                    IsError: true,
                    TypeName: FamilyTypeSnapshot.ResolveDisplayName(violation.TypeName, familyName),
                    AttributeName: violation.AttributeName,
                    CheckDescription: FormatOperator(violation.Operator),
                    ExpectedValue: FormatExpected(violation),
                    ActualValue: violation.ActualValue
                        ?? (Loc(Keys.FM_ValidationReport_EmptyValue) ?? "<empty>")));
            }
        }

        Issues = rows;

        var healthFailed = healthReport?.IsHealthy == false;
        var rulesFailed = validationReport?.IsValid == false;
        IsPassed = !healthFailed && !rulesFailed;

        (HealthStatusText, HealthStatusIconKind, HealthStatusBrush) = BuildHealthLine(healthReport);
        (RulesStatusText, RulesStatusIconKind, RulesStatusBrush) = BuildRulesLine(validationReport, validationRulesCount);

        _summary = BuildSummary(healthReport, validationReport, validationRulesCount);
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke(true);
    }

    private static (string Text, string Icon, string Brush) BuildHealthLine(FamilyHealthReport? healthReport)
    {
        if (healthReport is null)
        {
            return (Loc(Keys.FM_ValidationReport_HealthSkipped) ?? "Системная проверка: не выполнялась",
                "InformationOutline", "#9E9E9E");
        }

        var errors = healthReport.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Error);
        if (errors > 0)
        {
            return (string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Loc(Keys.FM_ValidationReport_HealthErrorsLine) ?? "Системная проверка: ошибок {0}", errors),
                "CloseCircleOutline", "#F44336");
        }

        var warnings = healthReport.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Warning);
        if (warnings > 0)
        {
            return (string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Loc(Keys.FM_ValidationReport_HealthWarningsLine) ?? "Системная проверка: предупреждений {0}", warnings),
                "AlertCircleOutline", "#FB8C00");
        }

        return (Loc(Keys.FM_ValidationReport_HealthPassedLine) ?? "Системная проверка: пройдена",
            "CheckCircleOutline", "#4CAF50");
    }

    private static (string Text, string Icon, string Brush) BuildRulesLine(
        FamilyValidationReport? validationReport, int rulesCount)
    {
        if (rulesCount == 0)
        {
            return (Loc(Keys.FM_ValidationReport_RulesNone) ?? "Правила категории: не заданы",
                "InformationOutline", "#9E9E9E");
        }

        if (validationReport is null)
        {
            return (Loc(Keys.FM_ValidationReport_RulesNotChecked) ?? "Правила категории: не проверялись",
                "InformationOutline", "#9E9E9E");
        }

        var typesChecked = validationReport.RulesEvaluated / rulesCount;
        if (!validationReport.IsValid)
        {
            return (string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Loc(Keys.FM_ValidationReport_RulesViolationsLine)
                        ?? "Правила категории: нарушений {0} (правил: {1}, типов: {2})",
                    validationReport.Violations.Count, rulesCount, typesChecked),
                "CloseCircleOutline", "#F44336");
        }

        return (string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Loc(Keys.FM_ValidationReport_RulesPassedLine)
                    ?? "Правила категории: пройдены ({0} правил на {1} типах)",
                rulesCount, typesChecked),
            "CheckCircleOutline", "#4CAF50");
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
            var typesChecked = rulesCount > 0 ? validationReport.RulesEvaluated / rulesCount : 0;
            parts.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Loc(Keys.FM_ValidationReport_SummaryRuleViolations) ?? "rule violations: {0} (rules: {1}, types: {2})",
                validationReport.Violations.Count, rulesCount, typesChecked));
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
