using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.Validation;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public sealed class CategoryChangeGateServiceTests
{
    private readonly Mock<IFamilyImportValidationService> _validationMock = new();
    private readonly Mock<IFamilyManagerDialogService> _dialogMock = new();
    private readonly CategoryChangeGateService _service;

    public CategoryChangeGateServiceTests()
    {
        _service = new CategoryChangeGateService(
            _validationMock.Object, _dialogMock.Object);
    }

    private static IReadOnlyList<EffectiveValidationRule> OneRule() =>
    [
        new EffectiveValidationRule("Pressure", false,
            new ValidationRule("r1", "b1", ValidationRuleOperator.HasValue, null, null, null, null, null, 0, true)),
    ];

    [Fact]
    public async Task NullCategory_Allowed()
    {
        var result = await _service.EnsureFamilyPassesAsync("item1", "Elbow", null, "Без категории");

        Assert.True(result);
        _validationMock.Verify(
            v => v.GetEffectiveRulesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task NoRules_Allowed()
    {
        _validationMock
            .Setup(v => v.GetEffectiveRulesAsync("cat1", It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var result = await _service.EnsureFamilyPassesAsync("item1", "Elbow", "cat1", "Pipes");

        Assert.True(result);
        _validationMock.Verify(
            v => v.ValidateCatalogItemAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<EffectiveValidationRule>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task RulesPassed_Allowed()
    {
        _validationMock
            .Setup(v => v.GetEffectiveRulesAsync("cat1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(OneRule());
        _validationMock
            .Setup(v => v.ValidateCatalogItemAsync("item1", It.IsAny<IReadOnlyList<EffectiveValidationRule>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FamilyValidationReport(true, [], 1));

        var result = await _service.EnsureFamilyPassesAsync("item1", "Elbow", "cat1", "Pipes");

        Assert.True(result);
    }

    [Fact]
    public async Task RulesFailed_Blocked_ReportShown()
    {
        var report = new FamilyValidationReport(false,
            [new RuleViolation("DN50", "Pressure", ValidationRuleOperator.HasValue, null, null, null, null, null)], 1);
        _validationMock
            .Setup(v => v.GetEffectiveRulesAsync("cat1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(OneRule());
        _validationMock
            .Setup(v => v.ValidateCatalogItemAsync("item1", It.IsAny<IReadOnlyList<EffectiveValidationRule>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(report);

        ValidationReportViewModel? shownVm = null;
        _dialogMock
            .Setup(d => d.ShowValidationReport(It.IsAny<object>()))
            .Callback<object>(vm => shownVm = vm as ValidationReportViewModel);

        var result = await _service.EnsureFamilyPassesAsync("item1", "Elbow", "cat1", "Pipes");

        Assert.False(result);
        _dialogMock.Verify(d => d.ShowValidationReport(It.IsAny<object>()), Times.Once);
        Assert.NotNull(shownVm);
        Assert.True(shownVm!.HasBlockedBanner);
        Assert.Contains("Pipes", shownVm.BlockedBannerText);
    }

    [Fact]
    public async Task NoExtractionData_Blocked_InfoShown()
    {
        _validationMock
            .Setup(v => v.GetEffectiveRulesAsync("cat1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(OneRule());
        _validationMock
            .Setup(v => v.ValidateCatalogItemAsync("item1", It.IsAny<IReadOnlyList<EffectiveValidationRule>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyValidationReport?)null);

        var result = await _service.EnsureFamilyPassesAsync("item1", "Elbow", "cat1", "Pipes");

        Assert.False(result);
        _dialogMock.Verify(d => d.ShowInfo(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        _dialogMock.Verify(d => d.ShowValidationReport(It.IsAny<object>()), Times.Never);
    }
}
