using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Import Validation Gate: row-level gate status, hard block semantics
/// (forced Skip, AvailableActions reduced) and view-model revalidation
/// on category assignment.
/// </summary>
public sealed class FamilyBatchImportGateTests
{
    private readonly Mock<IFamilyManagerDialogService> _dialogMock = new();
    private readonly Mock<IFamilyManagerViewModelFactory> _factoryMock = new();
    private readonly Mock<IFamilyImportValidationService> _validationMock = new();

    private static FamilyBatchImportItem MakeItem(
        string fileName,
        FamilyHealthReport? healthReport = null,
        string? categoryId = null,
        FamilySnapshot? snapshot = null,
        string? categoryName = null)
    {
        return new FamilyBatchImportItem(
            FilePath: $@"C:\fake\{fileName}.rfa",
            FileName: fileName,
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New,
            TargetCategoryId: categoryId,
            TargetCategoryName: categoryName,
            FamilySource: "loadable",
            LoadableSnapshot: snapshot,
            HealthReport: healthReport);
    }

    private static FamilyHealthReport HealthWithError() =>
        FamilyHealthReport.FromIssues(
        [
            new FamilyHealthIssue("TypeA", FamilyHealthIssueSeverity.Error, "Formula error"),
        ]);

    private static FamilyHealthReport HealthWithWarning() =>
        FamilyHealthReport.FromIssues(
        [
            new FamilyHealthIssue(null, FamilyHealthIssueSeverity.Warning, "slightly off axis"),
        ]);

    private static FamilySnapshot SnapshotWithFilledParam() =>
        new(
            FamilyName: "Elbow",
            Category: "Pipe Fittings",
            Parameters: [],
            Types:
            [
                new FamilyTypeSnapshot("DN50",
                [
                    new FamilyParameterValue("Pressure", "String", true, "16", null, null),
                ]),
            ],
            Geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: []);

    private void SetupRules(string categoryId, IReadOnlyList<EffectiveValidationRule> rules)
    {
        _validationMock
            .Setup(v => v.GetEffectiveRulesAsync(categoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(rules);
    }

    private void SetupEngineReport(FamilyValidationReport report)
    {
        _validationMock
            .Setup(v => v.ValidateFromSnapshots(
                It.IsAny<FamilySnapshot?>(),
                It.IsAny<SystemFamilySnapshot?>(),
                It.IsAny<IReadOnlyList<EffectiveValidationRule>>()))
            .Returns(report);
    }

    [Fact]
    public void Row_HealthError_BlockedImmediately()
    {
        var row = new FamilyBatchImportRow(MakeItem("Elbow", HealthWithError()));

        Assert.Equal(FamilyRowGateStatus.Failed, row.GateStatus);
        Assert.True(row.IsGateBlocked);
        Assert.Equal(FamilyBatchImportAction.Skip, row.Action);
        Assert.Single(row.AvailableActions);
        Assert.False(row.CanImport);
    }

    [Fact]
    public void Row_HealthWarningOnly_StatusWarning_NotBlocked()
    {
        var row = new FamilyBatchImportRow(MakeItem("Elbow", HealthWithWarning()));

        Assert.Equal(FamilyRowGateStatus.Warning, row.GateStatus);
        Assert.False(row.IsGateBlocked);
        Assert.True(row.CanImport);
    }

    [Fact]
    public void Row_NoHealthReport_StatusNotChecked()
    {
        var row = new FamilyBatchImportRow(MakeItem("Elbow"));

        Assert.Equal(FamilyRowGateStatus.NotChecked, row.GateStatus);
        Assert.False(row.IsGateBlocked);
    }

    [Fact]
    public void Row_GateFailForcesSkip_GatePassRestoresActions()
    {
        var row = new FamilyBatchImportRow(MakeItem("Elbow"));

        row.GateStatus = FamilyRowGateStatus.Failed;
        Assert.Equal(FamilyBatchImportAction.Skip, row.Action);
        Assert.Single(row.AvailableActions);

        row.GateStatus = FamilyRowGateStatus.Passed;
        Assert.Equal(FamilyBatchImportAction.IncrementVersion, row.Action);
        Assert.Equal(2, row.AvailableActions.Count);
        Assert.True(row.CanImport);
    }

    [Fact]
    public void Row_GateUnblock_DuplicateRow_StaysSkip()
    {
        // #262: a Duplicate row unblocked after a category change must
        // restore to the Phase 27 default (Skip — identical content adds
        // nothing as a new version), not to IncrementVersion.
        // IncrementVersion stays manually selectable.
        var row = new FamilyBatchImportRow(MakeItem("Elbow"))
        {
            Status = FamilyBatchImportStatus.Duplicate
        };
        Assert.Equal(FamilyBatchImportAction.Skip, row.Action);

        row.GateStatus = FamilyRowGateStatus.Failed;
        Assert.Equal(FamilyBatchImportAction.Skip, row.Action);

        row.GateStatus = FamilyRowGateStatus.Passed;
        Assert.Equal(FamilyBatchImportAction.Skip, row.Action);
        Assert.Contains(FamilyBatchImportAction.IncrementVersion, row.AvailableActions);
        Assert.False(row.CanImport);
    }

    [Fact]
    public void Row_StatusFlipWhileBlocked_StaysSkipped()
    {
        var row = new FamilyBatchImportRow(MakeItem("Elbow", HealthWithError()));

        row.Status = FamilyBatchImportStatus.Existing;

        Assert.Equal(FamilyBatchImportAction.Skip, row.Action);
        Assert.Single(row.AvailableActions);
    }

    [Fact]
    public async Task Vm_RowWithCategoryOnOpen_RuleFail_BlocksRow()
    {
        var rules = new[]
        {
            new EffectiveValidationRule("Pressure", false,
                new ValidationRule("r1", "b1", ValidationRuleOperator.HasValue, null, null, null, null, null, 0, true)),
        };
        SetupRules("cat1", rules);
        SetupEngineReport(new FamilyValidationReport(false,
            [new RuleViolation("DN50", "Pressure", ValidationRuleOperator.HasValue, null, null, null, null, null)], 1));

        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Elbow", categoryId: "cat1", snapshot: SnapshotWithFilledParam())],
            _dialogMock.Object,
            _factoryMock.Object,
            validationService: _validationMock.Object);

        var row = vm.Items[0];
        await WaitForAsync(() => row.GateStatus == FamilyRowGateStatus.Failed);

        Assert.Equal(FamilyRowGateStatus.Failed, row.GateStatus);
        Assert.Equal(FamilyBatchImportAction.Skip, row.Action);
        Assert.NotNull(row.ValidationReport);
        Assert.Equal(1, row.ValidationRulesCount);
    }

    [Fact]
    public async Task Vm_RowWithCategoryOnOpen_RulePass_StatusPassed()
    {
        var rules = new[]
        {
            new EffectiveValidationRule("Pressure", false,
                new ValidationRule("r1", "b1", ValidationRuleOperator.HasValue, null, null, null, null, null, 0, true)),
        };
        SetupRules("cat1", rules);
        SetupEngineReport(new FamilyValidationReport(true, [], 1));

        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Elbow", categoryId: "cat1", snapshot: SnapshotWithFilledParam())],
            _dialogMock.Object,
            _factoryMock.Object,
            validationService: _validationMock.Object);

        var row = vm.Items[0];
        await WaitForAsync(() => row.GateStatus == FamilyRowGateStatus.Passed);

        Assert.Equal(FamilyRowGateStatus.Passed, row.GateStatus);
        Assert.True(row.CanImport);
    }

    [Fact]
    public async Task Vm_HealthFailedRow_KeepsFailedButGetsRuleReport()
    {
        var rules = new[]
        {
            new EffectiveValidationRule("Pressure", false,
                new ValidationRule("r1", "b1", ValidationRuleOperator.HasValue, null, null, null, null, null, 0, true)),
        };
        SetupRules("cat1", rules);
        SetupEngineReport(new FamilyValidationReport(true, [], 1));

        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Elbow", HealthWithError(), "cat1", SnapshotWithFilledParam())],
            _dialogMock.Object,
            _factoryMock.Object,
            validationService: _validationMock.Object);

        var row = vm.Items[0];
        await WaitForAsync(() => row.ValidationReport is not null);

        Assert.Equal(FamilyRowGateStatus.Failed, row.GateStatus);
        Assert.NotNull(row.ValidationReport);
    }

    [Fact]
    public async Task Vm_NoValidationService_GateInert()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Elbow", categoryId: "cat1")],
            _dialogMock.Object,
            _factoryMock.Object);

        await Task.Delay(50);

        Assert.Equal(FamilyRowGateStatus.NotChecked, vm.Items[0].GateStatus);
    }

    [Fact]
    public void Row_GateForcedSkip_DoesNotBroadcastToSelection()
    {
        var failing = new FamilyBatchImportRow(MakeItem("Failing"));
        var bystander = new FamilyBatchImportRow(MakeItem("Bystander"));
        var broadcastCount = 0;
        failing.ActionChanged += (_, _) => broadcastCount++;

        bystander.Action = FamilyBatchImportAction.IncrementVersion;
        failing.GateStatus = FamilyRowGateStatus.Failed;

        Assert.Equal(0, broadcastCount);
        Assert.Equal(FamilyBatchImportAction.IncrementVersion, bystander.Action);
    }

    [Fact]
    public async Task Vm_RuleBlockedRow_UnblocksWhenCategoryCleared()
    {
        var rules = new[]
        {
            new EffectiveValidationRule("Pressure", false,
                new ValidationRule("r1", "b1", ValidationRuleOperator.HasValue, null, null, null, null, null, 0, true)),
        };
        SetupRules("cat1", rules);
        SetupEngineReport(new FamilyValidationReport(false,
            [new RuleViolation("DN50", "Pressure", ValidationRuleOperator.HasValue, null, null, null, null, null)], 1));

        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Elbow", categoryId: "cat1", snapshot: SnapshotWithFilledParam(), categoryName: "Pipes")],
            _dialogMock.Object,
            _factoryMock.Object,
            validationService: _validationMock.Object);

        var row = vm.Items[0];
        await WaitForAsync(() => row.GateStatus == FamilyRowGateStatus.Failed);

        // Clearing the category (user picks "Без категории") must unblock
        // a RULE-blocked row — the health check never failed.
        row.CategoryProvenance = CategoryProvenance.None;
        row.TargetCategoryId = null;
        row.TargetCategoryPath = "Без категории";

        await WaitForAsync(() => row.GateStatus == FamilyRowGateStatus.NotChecked);
        Assert.Null(row.ValidationReport);
        Assert.Equal(0, row.ValidationRulesCount);
        Assert.True(row.CanImport);
    }

    [Fact]
    public async Task Vm_RuleBlockedDuplicateRow_CategoryCleared_StaysSkip()
    {
        // #262 repro (owner log 2026-09-10): a Duplicate row blocked by the
        // category rules, then reset to «Без категории», must NOT silently
        // become IncrementVersion — the identical content would be posted
        // as a new version. The row unblocks but stays Skip; forcing a new
        // version remains a manual choice.
        var rules = new[]
        {
            new EffectiveValidationRule("Pressure", false,
                new ValidationRule("r1", "b1", ValidationRuleOperator.HasValue, null, null, null, null, null, 0, true)),
        };
        SetupRules("cat1", rules);
        SetupEngineReport(new FamilyValidationReport(false,
            [new RuleViolation("DN50", "Pressure", ValidationRuleOperator.HasValue, null, null, null, null, null)], 1));

        var item = new FamilyBatchImportItem(
            FilePath: @"C:\fake\Ducts.rvt",
            FileName: "Ducts",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.Duplicate,
            ExistingCatalogItemId: "item-1",
            TargetCategoryId: "cat1",
            TargetCategoryName: "Трубы",
            FamilySource: "system",
            LoadableSnapshot: SnapshotWithFilledParam(),
            ExistingCategoryId: "cat1",
            ExistingCategoryPath: "Трубы");
        using var vm = new FamilyBatchImportViewModel(
            [item],
            _dialogMock.Object,
            _factoryMock.Object,
            validationService: _validationMock.Object);

        var row = vm.Items[0];
        await WaitForAsync(() => row.GateStatus == FamilyRowGateStatus.Failed);
        Assert.Equal(FamilyBatchImportAction.Skip, row.Action);

        row.ClearCategoryOnImport = true;
        row.CategoryProvenance = CategoryProvenance.None;
        row.TargetCategoryId = null;
        row.TargetCategoryPath = "Без категории";

        await WaitForAsync(() => row.GateStatus == FamilyRowGateStatus.NotChecked);
        Assert.Equal(FamilyBatchImportAction.Skip, row.Action);
        Assert.False(row.CanImport);
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 3000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "Condition was not met within the timeout");
    }
}
