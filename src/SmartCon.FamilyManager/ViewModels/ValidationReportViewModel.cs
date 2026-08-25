using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using Keys = SmartCon.UI.StringLocalization.Keys;

namespace SmartCon.FamilyManager.ViewModels;

/// <summary>
/// One card of the system-issues section: a problem Revit reported inside
/// the family document (broken formula, regeneration failure, file-open
/// error, document warning). Unlike a rule violation the payload is a
/// single free-text description — rendered as a wrapped, selectable card,
/// never squeezed into a table cell.
/// </summary>
public sealed record HealthIssueCard(
    bool IsError,
    string TypeName,
    string Description)
{
    /// <summary>Family-level issues (document warnings) carry no type —
    /// the card collapses the type line entirely.</summary>
    public bool HasTypeName => !string.IsNullOrEmpty(TypeName);
}

/// <summary>
/// One row of the rule-violations table (Type / Attribute / Check /
/// Expected / Actual columns).
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
/// status icon in the batch dialog's status column. ONE dialog carries
/// both result kinds: system-level issues as wrapped cards on top,
/// rule violations as a table below — each section appears only when
/// it has content.
/// </summary>
public sealed partial class ValidationReportViewModel : ObservableObject, IObservableRequestClose
{
    public event Action<bool?>? RequestClose;

    public string FamilyName { get; }
    public string CategoryPath { get; }

    [ObservableProperty]
    private string _summary;

    /// <summary>Rule violations — the DataGrid rows.</summary>
    public IReadOnlyList<ValidationReportIssueRow> Issues { get; }

    /// <summary>System-level issues — the wrapped cards above the grid.</summary>
    public IReadOnlyList<HealthIssueCard> HealthCards { get; }

    public bool HasHealthCards => HealthCards.Count > 0;
    public bool HasViolations => Issues.Count > 0;
    public string HealthSectionTitle { get; }
    public string ViolationsSectionTitle { get; }
    public bool IsPassed { get; }

    /// <summary>Header mark: red ✗ on any error/violation, orange ⚠ when
    /// only warnings remain, green ✓ only for a truly clean report — a
    /// green check above a warning card reads as a contradiction.</summary>
    public string HeaderIconKind { get; }
    public string HeaderIconBrush { get; }

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

        var cards = new List<HealthIssueCard>();
        if (healthReport is not null)
        {
            foreach (var issue in healthReport.Issues)
            {
                cards.Add(new HealthIssueCard(
                    IsError: issue.Severity == FamilyHealthIssueSeverity.Error,
                    TypeName: FamilyTypeSnapshot.ResolveDisplayName(issue.TypeName ?? string.Empty, familyName),
                    Description: issue.Description));
            }
        }

        HealthCards = cards;

        var rows = new List<ValidationReportIssueRow>();
        if (validationReport is not null)
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

        HealthSectionTitle = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Loc(Keys.FM_ValidationReport_HealthSection) ?? "Системные проблемы ({0})",
            cards.Count);
        ViolationsSectionTitle = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            Loc(Keys.FM_ValidationReport_ViolationsSection) ?? "Нарушения правил ({0})",
            rows.Count);

        var healthFailed = healthReport?.IsHealthy == false;
        var rulesFailed = validationReport?.IsValid == false;
        IsPassed = !healthFailed && !rulesFailed;

        var healthErrors = healthReport?.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Error) ?? 0;
        var healthWarnings = healthReport?.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Warning) ?? 0;
        (HeaderIconKind, HeaderIconBrush) =
            healthErrors > 0 || rulesFailed ? ("CloseCircleOutline", "#F44336")
            : healthWarnings > 0 ? ("AlertCircleOutline", "#FB8C00")
            : ("CheckCircleOutline", "#4CAF50");

        _summary = BuildSummary(healthReport, validationReport, validationRulesCount);
    }

    [RelayCommand]
    private void Copy()
    {
        try
        {
            System.Windows.Clipboard.SetText(BuildClipboardText());
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"ValidationReport.Copy: clipboard unavailable: {ex.Message} " +
                "[Action: повторите копирование — буфер обмена был занят другим приложением]");
        }
    }

    /// <summary>Plain-text rendering of the whole report for the clipboard —
    /// extracted so unit tests can verify the content without an STA thread.</summary>
    public string BuildClipboardText()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(FamilyName);
        if (!string.IsNullOrEmpty(CategoryPath))
        {
            sb.Append(" — ").Append(CategoryPath);
        }

        sb.AppendLine();
        sb.Append(Summary);

        if (HasHealthCards)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(HealthSectionTitle);
            foreach (var card in HealthCards)
            {
                sb.AppendLine();
                sb.Append(card.IsError
                    ? Loc(Keys.FM_HealthReport_ErrorLabel) ?? "[Ошибка] "
                    : Loc(Keys.FM_HealthReport_WarningLabel) ?? "[Предупреждение] ");
                if (!string.IsNullOrEmpty(card.TypeName))
                {
                    sb.Append(card.TypeName).Append(": ");
                }

                sb.Append(card.Description);
            }
        }

        if (HasViolations)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.Append(ViolationsSectionTitle);
            foreach (var row in Issues)
            {
                sb.AppendLine();
                sb.Append(row.TypeName).Append(" | ")
                    .Append(row.AttributeName).Append(" | ")
                    .Append(row.CheckDescription).Append(" | ")
                    .Append(row.ExpectedValue).Append(" | ")
                    .Append(row.ActualValue);
            }
        }

        return sb.ToString();
    }

    [RelayCommand]
    private void Close()
    {
        RequestClose?.Invoke(true);
    }

    private string BuildSummary(FamilyHealthReport? healthReport, FamilyValidationReport? validationReport, int rulesCount)
    {
        var healthWarnings = healthReport?.Issues.Count(i => i.Severity == FamilyHealthIssueSeverity.Warning) ?? 0;
        if (IsPassed)
        {
            return healthWarnings > 0
                ? string.Format(System.Globalization.CultureInfo.CurrentCulture,
                    Loc(Keys.FM_ValidationReport_SummaryPassedWithWarnings)
                        ?? "Проверки пройдены, есть предупреждения: {0}", healthWarnings)
                : rulesCount > 0
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
        if (healthWarnings > 0)
        {
            parts.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Loc(Keys.FM_ValidationReport_SummaryHealthWarnings) ?? "warnings: {0}", healthWarnings));
        }
        if (validationReport is not null && validationReport.Violations.Count > 0)
        {
            parts.Add(string.Format(System.Globalization.CultureInfo.CurrentCulture,
                Loc(Keys.FM_ValidationReport_SummaryRuleViolations) ?? "нарушений правил: {0}",
                validationReport.Violations.Count));
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
