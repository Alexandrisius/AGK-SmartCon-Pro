using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// #241: auto-assignment integration in the batch import dialog — new
/// rows get their category from the rule evaluation (provenance AutoRule),
/// Existing/Duplicate rows keep the catalog category, ambiguous outcomes
/// surface the conflict notice, rename re-derivation re-evaluates the
/// rules.
/// </summary>
public sealed class FamilyBatchImportAutoAssignTests
{
    private readonly Mock<IFamilyManagerDialogService> _dialogMock = new();
    private readonly Mock<IFamilyManagerViewModelFactory> _factoryMock = new();
    private readonly Mock<IFamilyImportValidationService> _validationMock = new();
    private readonly Mock<IFamilyCatalogProvider> _catalogMock = new();
    private readonly Mock<ICategoryAutoAssignService> _autoAssignMock = new();

    private const string CatSteel = "cat-steel";
    private const string CatFittings = "cat-fittings";

    private Func<FamilySnapshot?, SystemFamilySnapshot?, string?, CategoryAutoAssignResult> _evaluate =
        (_, _, _) => CategoryAutoAssignResult.NoMatch;

    public FamilyBatchImportAutoAssignTests()
    {
        _autoAssignMock
            .Setup(s => s.PreloadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CategoryAutoAssignPreloaded(
                [new AssignmentRuleGroup("g1", CatSteel, 0, true, [])],
                new Dictionary<string, string>(),
                new Dictionary<string, string> { [CatSteel] = "Стальные", [CatFittings] = "Фитинги" },
                true));
        _autoAssignMock
            .Setup(s => s.Evaluate(
                It.IsAny<CategoryAutoAssignPreloaded>(),
                It.IsAny<FamilySnapshot?>(),
                It.IsAny<SystemFamilySnapshot?>(),
                It.IsAny<string?>()))
            .Returns((CategoryAutoAssignPreloaded _, FamilySnapshot? l, SystemFamilySnapshot? s, string? name)
                => _evaluate(l, s, name));
    }

    private static FamilyBatchImportItem MakeItem(
        string fileName,
        FamilyBatchImportStatus status = FamilyBatchImportStatus.New,
        string? categoryId = null,
        string? categoryName = null,
        FamilySnapshot? snapshot = null)
    {
        return new FamilyBatchImportItem(
            FilePath: $@"C:\fake\{fileName}.rfa",
            FileName: fileName,
            RevitMajorVersion: 2025,
            Status: status,
            TargetCategoryId: categoryId,
            TargetCategoryName: categoryName,
            FamilySource: "loadable",
            LoadableSnapshot: snapshot);
    }

    private void SetupGate(string categoryId, bool isValid = true)
    {
        _validationMock
            .Setup(v => v.GetEffectiveRulesAsync(categoryId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new EffectiveValidationRule("Pressure", false,
                    new ValidationRule("r1", "b1", ValidationRuleOperator.HasValue, null, null, null, null, null, 0, true)),
            ]);
        _validationMock
            .Setup(v => v.ValidateFromSnapshots(
                It.IsAny<FamilySnapshot?>(),
                It.IsAny<SystemFamilySnapshot?>(),
                It.IsAny<IReadOnlyList<EffectiveValidationRule>>()))
            .Returns(new FamilyValidationReport(isValid, [], 1));
    }

    [Fact]
    public async Task Vm_NewRowNoCategory_RuleMatches_GetsAutoRuleCategoryAndGateCheck()
    {
        SetupGate(CatSteel);
        _evaluate = (_, _, _) => CategoryAutoAssignResult.Matched(CatSteel);

        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Elbow", snapshot: SnapshotWithParam())],
            _dialogMock.Object,
            _factoryMock.Object,
            validationService: _validationMock.Object,
            autoAssignService: _autoAssignMock.Object);

        var row = vm.Items[0];
        await WaitForAsync(() => row.CategoryProvenance == CategoryProvenance.AutoRule);

        Assert.Equal(CatSteel, row.TargetCategoryId);
        Assert.Equal("Стальные", row.TargetCategoryPath);
        Assert.Equal(CategoryProvenance.AutoRule, row.CategoryProvenance);
        Assert.Null(row.AssignmentConflictCandidates);

        // The follow-up gate revalidation must cover the ASSIGNED category.
        await WaitForAsync(() => row.ValidationRulesCount == 1);
        Assert.Equal(FamilyRowGateStatus.Passed, row.GateStatus);
    }

    [Fact]
    public async Task Vm_ExistingRowWithCatalogCategory_NotOverridden()
    {
        SetupGate("cat-existing");
        _evaluate = (_, _, _) => CategoryAutoAssignResult.Matched(CatSteel);

        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Old", FamilyBatchImportStatus.Existing, categoryId: "cat-existing", categoryName: "Существующая")],
            _dialogMock.Object,
            _factoryMock.Object,
            validationService: _validationMock.Object,
            autoAssignService: _autoAssignMock.Object);

        var row = vm.Items[0];
        await Task.Delay(80);

        Assert.Equal(CategoryProvenance.AutoName, row.CategoryProvenance);
        Assert.Equal("cat-existing", row.TargetCategoryId);
        Assert.Equal("Существующая", row.TargetCategoryPath);
    }

    [Fact]
    public async Task Vm_Ambiguous_RowStaysNoCategory_WithConflictNotice()
    {
        _evaluate = (_, _, _) => CategoryAutoAssignResult.Ambiguous([CatSteel, CatFittings]);

        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Elbow", snapshot: SnapshotWithParam())],
            _dialogMock.Object,
            _factoryMock.Object,
            autoAssignService: _autoAssignMock.Object);

        var row = vm.Items[0];
        await WaitForAsync(() => row.HasAssignmentConflict);

        Assert.Null(row.TargetCategoryId);
        Assert.Equal(CategoryProvenance.None, row.CategoryProvenance);
        Assert.Equal(2, row.AssignmentConflictCandidates!.Count);
        Assert.Contains("Стальные", row.AssignmentConflictCandidates);
        Assert.Contains("Фитинги", row.AssignmentConflictCandidates);
        // The conflict surfaces as a clickable problem notice.
        Assert.True(row.HasProblemNotices);
    }

    [Fact]
    public async Task Vm_ManualPick_ClearsConflictNotice()
    {
        _evaluate = (_, _, _) => CategoryAutoAssignResult.Ambiguous([CatSteel, CatFittings]);

        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Elbow", snapshot: SnapshotWithParam())],
            _dialogMock.Object,
            _factoryMock.Object,
            autoAssignService: _autoAssignMock.Object);

        var row = vm.Items[0];
        await WaitForAsync(() => row.HasAssignmentConflict);

        // The user picks a category manually — the conflict notice clears.
        row.CategoryProvenance = CategoryProvenance.Manual;
        row.TargetCategoryId = CatSteel;
        row.TargetCategoryPath = "Стальные";

        await WaitForAsync(() => !row.HasAssignmentConflict);
        Assert.Null(row.AssignmentConflictCandidates);
    }

    [Fact]
    public async Task Vm_NoRules_Configured_RowsUntouched()
    {
        _autoAssignMock
            .Setup(s => s.PreloadAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(CategoryAutoAssignPreloaded.Empty);

        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Elbow", snapshot: SnapshotWithParam())],
            _dialogMock.Object,
            _factoryMock.Object,
            autoAssignService: _autoAssignMock.Object);

        await Task.Delay(80);

        var row = vm.Items[0];
        Assert.Equal(CategoryProvenance.None, row.CategoryProvenance);
        Assert.Null(row.TargetCategoryId);
        Assert.False(row.HasAssignmentConflict);
    }

    [Fact]
    public async Task Vm_NoAutoAssignService_BehavesAsBefore()
    {
        SetupGate(CatSteel);

        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Elbow", categoryId: CatSteel, categoryName: "Стальные", snapshot: SnapshotWithParam())],
            _dialogMock.Object,
            _factoryMock.Object,
            validationService: _validationMock.Object);

        var row = vm.Items[0];
        await WaitForAsync(() => row.GateStatus == FamilyRowGateStatus.Passed);

        Assert.Equal(FamilyRowGateStatus.Passed, row.GateStatus);
    }

    [Fact]
    public async Task Vm_Rename_NewRowWithoutRuleMatch_ClearsAutoRuleCategory()
    {
        _catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);
        _evaluate = (_, _, name) => string.Equals(name, "Отвод А", StringComparison.OrdinalIgnoreCase)
            ? CategoryAutoAssignResult.Matched(CatSteel)
            : CategoryAutoAssignResult.NoMatch;

        using var vm = new FamilyBatchImportViewModel(
            [MakeItem("Отвод А", snapshot: SnapshotWithParam())],
            _dialogMock.Object,
            _factoryMock.Object,
            catalogProvider: _catalogMock.Object,
            autoAssignService: _autoAssignMock.Object);

        var row = vm.Items[0];
        await WaitForAsync(() => row.CategoryProvenance == CategoryProvenance.AutoRule);
        Assert.Equal(CatSteel, row.TargetCategoryId);

        // Rename to a name the rules do not match — the automatic
        // category is re-derived away (AutoRule is NOT locked).
        _evaluate = (_, _, _) => CategoryAutoAssignResult.NoMatch;
        row.FileName = "Отвод Б";

        await WaitForAsync(() => row.CategoryProvenance == CategoryProvenance.None);
        Assert.Null(row.TargetCategoryId);
    }

    private static FamilySnapshot SnapshotWithParam() =>
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
