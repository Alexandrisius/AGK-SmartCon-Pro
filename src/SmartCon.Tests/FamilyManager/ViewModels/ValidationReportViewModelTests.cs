using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// <see cref="ValidationReportViewModel"/> — the unified report: health
/// issues map to cards (never table rows), violations to grid rows,
/// section visibility/titles, clipboard text.
/// </summary>
public sealed class ValidationReportViewModelTests
{
    private static FamilyHealthReport Health(params FamilyHealthIssue[] issues) =>
        FamilyHealthReport.FromIssues(issues);

    private static FamilyHealthIssue HealthError(string? type, string description) =>
        new(type, FamilyHealthIssueSeverity.Error, description);

    private static FamilyHealthIssue HealthWarning(string? type, string description) =>
        new(type, FamilyHealthIssueSeverity.Warning, description);

    private static FamilyValidationReport Violations(params RuleViolation[] violations) =>
        new(false, violations, violations.Length);

    private static RuleViolation Violation(string type = "Тип", string attribute = "ADSK_Марка") =>
        new(type, attribute, ValidationRuleOperator.HasValue, null, null, null, null, null);

    [Fact]
    public void Ctor_HealthIssues_BecomeCards_NotTableRows()
    {
        var vm = new ValidationReportViewModel(
            "Вентилятор", "Фитинги > Отводы",
            Health(HealthError("Тип 1", "InvalidOperationException: file is corrupt"),
                   HealthWarning(null, "Element is slightly off axis")),
            null, 0);

        Assert.Equal(2, vm.HealthCards.Count);
        Assert.True(vm.HealthCards[0].IsError);
        Assert.Equal("Тип 1", vm.HealthCards[0].TypeName);
        Assert.Equal("InvalidOperationException: file is corrupt", vm.HealthCards[0].Description);
        Assert.False(vm.HealthCards[1].IsError);
        // Long free text must NOT leak into the violations grid.
        Assert.Empty(vm.Issues);
        Assert.True(vm.HasHealthCards);
        Assert.False(vm.HasViolations);
    }

    [Fact]
    public void Ctor_Violations_BecomeTableRows()
    {
        var vm = new ValidationReportViewModel(
            "Вентилятор", "Категория", null,
            Violations(Violation("Ф200"), Violation("Ф250")), 1);

        Assert.Equal(2, vm.Issues.Count);
        Assert.Equal("Ф200", vm.Issues[0].TypeName);
        Assert.True(vm.HasViolations);
        Assert.False(vm.HasHealthCards);
    }

    [Fact]
    public void SectionTitles_CarryCounts()
    {
        var vm = new ValidationReportViewModel(
            "F", "C",
            Health(HealthWarning(null, "w")),
            Violations(Violation(), Violation()), 1);

        Assert.Contains("1", vm.HealthSectionTitle);
        Assert.Contains("2", vm.ViolationsSectionTitle);
    }

    [Fact]
    public void BuildClipboardText_ContainsBothSections()
    {
        var vm = new ValidationReportViewModel(
            "Вентилятор", "Отводы",
            Health(HealthError("Тип 1", "cannot open file")),
            Violations(Violation("Ф200")), 1);

        var text = vm.BuildClipboardText();

        Assert.Contains("Вентилятор", text);
        Assert.Contains("Отводы", text);
        Assert.Contains("Тип 1", text);
        Assert.Contains("cannot open file", text);
        Assert.Contains("Ф200", text);
        Assert.Contains("ADSK_Марка", text);
    }

    [Fact]
    public void BuildClipboardText_NoSections_WhenNothingToReport()
    {
        var vm = new ValidationReportViewModel("F", "C", null, null, 0);

        var text = vm.BuildClipboardText();

        Assert.DoesNotContain("ADSK", text);
        Assert.False(vm.HasHealthCards);
        Assert.False(vm.HasViolations);
    }

    [Fact]
    public void Header_WarningOnly_ShowsOrangeAlert_NotGreenCheck()
    {
        var vm = new ValidationReportViewModel(
            "F", "C", Health(HealthWarning(null, "doc warning")), null, 0);

        Assert.Equal("AlertCircleOutline", vm.HeaderIconKind);
        Assert.Equal("#FB8C00", vm.HeaderIconBrush);
    }

    [Fact]
    public void Header_CleanReport_ShowsGreenCheck()
    {
        var vm = new ValidationReportViewModel("F", "C", null, null, 0);

        Assert.Equal("CheckCircleOutline", vm.HeaderIconKind);
    }

    [Fact]
    public void Header_ErrorOrViolation_ShowsRedCross()
    {
        var healthError = new ValidationReportViewModel(
            "F", "C", Health(HealthError(null, "boom")), null, 0);
        var violation = new ValidationReportViewModel(
            "F", "C", null, Violations(Violation()), 1);

        Assert.Equal("CloseCircleOutline", healthError.HeaderIconKind);
        Assert.Equal("CloseCircleOutline", violation.HeaderIconKind);
    }

    [Fact]
    public void Summary_WarningOnlyPassed_SaysWarningsPresent()
    {
        var vm = new ValidationReportViewModel(
            "F", "C", Health(HealthWarning(null, "doc warning")), null, 0);

        Assert.Contains("1", vm.Summary);
        Assert.DoesNotContain("Все проверки пройдены", vm.Summary);
    }

    [Fact]
    public void Card_FamilyLevelIssue_HasNoTypeName()
    {
        var vm = new ValidationReportViewModel(
            "F", "C", Health(HealthWarning(null, "doc warning")), null, 0);

        Assert.False(vm.HealthCards[0].HasTypeName);
        Assert.True(vm.HealthCards[0].TypeName.Length == 0);
    }
}
