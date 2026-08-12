using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// ADR-066 (E1): dependency cascade in the batch import dialog — child rows
/// show their parents, parent rows surface gate-failed children, and the
/// links survive the dialog round-trip into Phase 3.
/// </summary>
public sealed class FamilyBatchImportDependencyTests
{
    private readonly Mock<IFamilyManagerDialogService> _dialogMock = new();
    private readonly Mock<IFamilyManagerViewModelFactory> _factoryMock = new();

    private const string ParentPath = "system://OST_PipeCurves";

    private static FamilyBatchImportItem MakeParentItem(string displayName = "Трубы") =>
        new(
            FilePath: ParentPath,
            FileName: displayName,
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New,
            FamilySource: "system");

    private static FamilyBatchImportItem MakeChildItem(
        string fileName,
        FamilyHealthReport? healthReport = null,
        IReadOnlyList<FamilyDependencyLink>? links = null) =>
        new(
            FilePath: $"loadable://{fileName}",
            FileName: fileName,
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New,
            FamilySource: "loadable",
            HealthReport: healthReport,
            DependencyLinks: links ??
            [
                new FamilyDependencyLink(ParentPath, FamilyDependencyKind.Routing, $"{fileName}:Стандарт"),
            ]);

    private static FamilyHealthReport HealthWithError() =>
        FamilyHealthReport.FromIssues(
        [
            new FamilyHealthIssue("TypeA", FamilyHealthIssueSeverity.Error, "Formula error"),
        ]);

    [Fact]
    public void Vm_ChildRow_ShowsParentDisplayName()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeChildItem("ADSK_Отвод")],
            _dialogMock.Object,
            _factoryMock.Object);

        var child = vm.Items[1];
        Assert.True(child.IsDependency);
        var parentName = Assert.Single(child.DependencyParentNames!);
        Assert.Equal("Трубы", parentName);
        Assert.Contains("Трубы", child.DependencyOfTooltip);
    }

    [Fact]
    public void Vm_TopLevelRow_IsNotDependency()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem()],
            _dialogMock.Object,
            _factoryMock.Object);

        Assert.False(vm.Items[0].IsDependency);
        Assert.Null(vm.Items[0].DependencyParentNames);
    }

    [Fact]
    public void Vm_GateFailedChild_ParentShowsFailedDependencyIndicator()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeChildItem("ADSK_Отвод", HealthWithError())],
            _dialogMock.Object,
            _factoryMock.Object);

        var parent = vm.Items[0];
        Assert.True(parent.HasFailedDependencies);
        var failedName = Assert.Single(parent.FailedDependencyNames!);
        Assert.Equal("ADSK_Отвод", failedName);
        Assert.Contains("ADSK_Отвод", parent.FailedDependenciesTooltip);
    }

    [Fact]
    public void Vm_HealthyChild_ParentHasNoFailedIndicator()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeChildItem("ADSK_Отвод")],
            _dialogMock.Object,
            _factoryMock.Object);

        Assert.False(vm.Items[0].HasFailedDependencies);
        Assert.Null(vm.Items[0].FailedDependencyNames);
    }

    // ---- E2 (#209): outdated-nested detection + parent import block ----

    private static FamilyBatchImportItem MakeOutdatedChildItem(
        string fileName,
        FamilyBatchImportAction action = FamilyBatchImportAction.Skip) =>
        new(
            FilePath: $"loadable://{fileName}",
            FileName: fileName,
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.Duplicate,
            ExistingCatalogItemId: "existing-1",
            // Active version of the existing item is v2, the embedded copy
            // hash-matched v1 → the parent embeds an OUTDATED nested.
            ExistingVersionLabel: "v2",
            MatchedVersionLabel: "v1",
            FamilySource: "loadable",
            DependencyLinks:
            [
                new FamilyDependencyLink(ParentPath, FamilyDependencyKind.SharedNested, null),
            ])
        {
            Action = action,
        };

    [Fact]
    public void Row_DuplicateMatchedNonActive_IsOutdatedNested()
    {
        var row = new FamilyBatchImportRow(MakeOutdatedChildItem("Фланец"));

        Assert.True(row.IsOutdatedNested);
        Assert.Contains("v1", row.OutdatedNestedTooltip);
        Assert.Contains("v2", row.OutdatedNestedTooltip);
    }

    [Fact]
    public void Row_DuplicateMatchedActive_IsNotOutdatedNested()
    {
        var item = MakeOutdatedChildItem("Фланец") with { MatchedVersionLabel = "v2" };
        var row = new FamilyBatchImportRow(item);

        Assert.False(row.IsOutdatedNested);
    }

    [Fact]
    public void Vm_OutdatedNestedChild_ParentBlockedForcedSkip()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeOutdatedChildItem("Фланец")],
            _dialogMock.Object,
            _factoryMock.Object);

        var parent = vm.Items[0];
        Assert.True(parent.HasOutdatedDependencyBlock);
        Assert.Equal(FamilyBatchImportAction.Skip, parent.Action);
        Assert.DoesNotContain(FamilyBatchImportAction.IncrementVersion, parent.AvailableActions);
        Assert.Contains("Фланец", parent.OutdatedDependencyBlockTooltip);
    }

    [Fact]
    public void Vm_MakeActiveOnOutdatedChild_LiftsParentBlock()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeOutdatedChildItem("Фланец")],
            _dialogMock.Object,
            _factoryMock.Object);

        var parent = vm.Items[0];
        Assert.True(parent.HasOutdatedDependencyBlock);

        // Escape hatch: MakeActive switches the catalog back to the
        // embedded version — the parent's content becomes consistent.
        vm.Items[1].Action = FamilyBatchImportAction.MakeActive;

        Assert.False(parent.HasOutdatedDependencyBlock);
        Assert.Contains(FamilyBatchImportAction.IncrementVersion, parent.AvailableActions);
    }

    [Fact]
    public void Vm_HealthyNestedChild_NoParentBlock()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeChildItem("ADSK_Отвод")],
            _dialogMock.Object,
            _factoryMock.Object);

        Assert.False(vm.Items[0].HasOutdatedDependencyBlock);
        Assert.Null(vm.Items[0].OutdatedDependencyBlockNames);
    }

    [Fact]
    public void Vm_ChildGateFlipsToFailed_ParentIndicatorRefreshes()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeChildItem("ADSK_Отвод")],
            _dialogMock.Object,
            _factoryMock.Object);

        Assert.False(vm.Items[0].HasFailedDependencies);

        // Rule-check failure lands asynchronously in production; the VM
        // recomputes indicators inside the revalidation mutation — simulate
        // by flipping the gate directly and re-running the refresh.
        vm.Items[1].GateStatus = FamilyRowGateStatus.Failed;
        vm.RefreshDependencyIndicators();

        Assert.True(vm.Items[0].HasFailedDependencies);
    }

    [Fact]
    public void Vm_GetResultItems_PreservesDependencyLinks()
    {
        using var vm = new FamilyBatchImportViewModel(
            [MakeParentItem(), MakeChildItem("ADSK_Отвод")],
            _dialogMock.Object,
            _factoryMock.Object);

        var result = vm.GetResultItems();

        var childResult = Assert.Single(result, i => i.FamilySource == "loadable");
        var link = Assert.Single(childResult.DependencyLinks!);
        Assert.Equal(ParentPath, link.ParentSourcePath);
        Assert.Equal(FamilyDependencyKind.Routing, link.Kind);
        Assert.Equal("ADSK_Отвод:Стандарт", link.PartName);
    }

    // ---- #180: marker-resolved version origin annotation ----

    [Fact]
    public void Row_MarkerResolvedVersion_TooltipExplainsOrigin()
    {
        // The nested update healed the marker to the ACTIVE version (v2),
        // but the embedded identity hash keeps matching v1 (parameter
        // groups never propagate on merge) — the row must disclose that
        // the shown version is marker-resolved, not content-matched.
        var item = MakeOutdatedChildItem("Фланец") with
        {
            MatchedVersionLabel = "v2",
            IsMarkerResolvedVersion = true,
        };
        var row = new FamilyBatchImportRow(item);

        Assert.True(row.IsMarkerResolvedVersion);
        Assert.NotNull(row.MarkerResolvedTooltip);
        Assert.Contains("v2", row.MarkerResolvedTooltip);
    }

    [Fact]
    public void Row_HashResolvedVersion_NoMarkerAnnotation()
    {
        var row = new FamilyBatchImportRow(MakeOutdatedChildItem("Фланец"));

        Assert.False(row.IsMarkerResolvedVersion);
        Assert.Null(row.MarkerResolvedTooltip);
    }
}
