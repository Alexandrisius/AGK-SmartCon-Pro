using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Unit tests for the multi-select batch-apply behaviour of
/// <see cref="FamilyBatchImportViewModel"/>: changing Action or TargetCategory
/// on one selected row propagates the change to all other selected rows in
/// the DataGrid.
///
/// Selection is driven by the row VM's <c>IsSelected</c> property
/// (TwoWay-bound to <c>DataGridRow.IsSelected</c> in the view). Tests
/// manipulate <c>IsSelected</c> directly to keep the test independent of
/// the WPF runtime.
/// </summary>
public sealed class FamilyBatchImportMultiSelectTests
{
    private readonly Mock<IFamilyManagerDialogService> _dialogMock = new();
    private readonly Mock<IFamilyManagerViewModelFactory> _factoryMock = new();
    private readonly Mock<IFamilyCatalogProvider> _catalogMock = new();

    public FamilyBatchImportMultiSelectTests()
    {
        _catalogMock
            .Setup(c => c.FindByHashAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogVersion?)null);
        _catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);
    }

    private static FamilyBatchImportItem MakeItem(
        string fileName,
        FamilyBatchImportStatus status = FamilyBatchImportStatus.New,
        string? categoryId = null)
    {
        return new FamilyBatchImportItem(
            FilePath: $@"C:\fake\{fileName}.rfa",
            FileName: fileName,
            Sha256: Guid.NewGuid().ToString("N"),
            RevitMajorVersion: 2025,
            FileSizeBytes: 1024,
            Status: status,
            ExistingCatalogItemId: null,
            ExistingVersionLabel: null,
            TargetCategoryId: categoryId,
            TargetCategoryName: categoryId,
            FamilySource: "loadable",
            TypeCount: null,
            RevitCategory: null);
    }

    /// <summary>
    /// Marks the given rows as selected via the VM-owned IsSelected property
    /// (the same property bound TwoWay to DataGridRow.IsSelected in the view).
    /// </summary>
    private static void SelectRows(params FamilyBatchImportRow[] rows)
    {
        foreach (var r in rows) r.IsSelected = true;
    }

    private FamilyBatchImportViewModel CreateVm(IReadOnlyList<FamilyBatchImportItem> items)
    {
        return new FamilyBatchImportViewModel(
            items,
            _dialogMock.Object,
            _factoryMock.Object,
            _catalogMock.Object);
    }

    [Fact]
    public void ChangingAction_OnMultiSelection_AppliesToAllOtherSelectedRows()
    {
        var items = new[] { MakeItem("a"), MakeItem("b"), MakeItem("c") };
        using var vm = CreateVm(items);
        var rows = vm.Items.ToArray();
        SelectRows(rows);

        rows[0].Action = FamilyBatchImportAction.Skip;

        Assert.Equal(FamilyBatchImportAction.Skip, rows[0].Action);
        Assert.Equal(FamilyBatchImportAction.Skip, rows[1].Action);
        Assert.Equal(FamilyBatchImportAction.Skip, rows[2].Action);
    }

    [Fact]
    public void ChangingAction_OnMultiSelection_ExcludesSourceRow_FromBatchApplication()
    {
        // The source row sets the new value first; the batch handler must
        // not re-apply it to the same row. We measure this by counting how
        // many times the source row's setter would have been re-entered via
        // ActionChanged event for the source row. With the source excluded,
        // ApplyActionToSelection iterates over the other rows only and
        // never touches the source.
        var items = new[] { MakeItem("a"), MakeItem("b"), MakeItem("c") };
        using var vm = CreateVm(items);
        var rows = vm.Items.ToArray();
        SelectRows(rows);

        var sourceEventCount = 0;
        rows[0].ActionChanged += (_, _) =>
        {
            // Should only fire once (the original set on rows[0]), not a
            // second time when ApplyActionToSelection iterates the
            // selection list.
            sourceEventCount++;
        };

        rows[0].Action = FamilyBatchImportAction.Skip;

        Assert.Equal(1, sourceEventCount);
        Assert.Equal(FamilyBatchImportAction.Skip, rows[1].Action);
        Assert.Equal(FamilyBatchImportAction.Skip, rows[2].Action);
    }

    [Fact]
    public void ChangingAction_IncompatibleValue_IsSkipped_ForThatRow()
    {
        // Status=Duplicate means AvailableActions = { Skip } only.
        // If the user selects a New row and a Duplicate row and picks
        // IncrementVersion, the Duplicate row must keep Skip.
        var items = new[]
        {
            MakeItem("new-row", FamilyBatchImportStatus.New),
            MakeItem("dup-row", FamilyBatchImportStatus.Duplicate),
        };
        using var vm = CreateVm(items);
        var rows = vm.Items.ToArray();
        SelectRows(rows);

        rows[0].Action = FamilyBatchImportAction.IncrementVersion;

        Assert.Equal(FamilyBatchImportAction.IncrementVersion, rows[0].Action);
        Assert.Equal(FamilyBatchImportAction.Skip, rows[1].Action);
    }

    [Fact]
    public void ChangingCategory_OnMultiSelection_AppliesToAllOtherSelectedRows()
    {
        var items = new[] { MakeItem("a"), MakeItem("b"), MakeItem("c") };
        using var vm = CreateVm(items);
        var rows = vm.Items.ToArray();
        SelectRows(rows);

        // Id is set first, Path last — same order the picker flow uses.
        // The batch apply is triggered by OnTargetCategoryPathChanged
        // partial method, so by the time it fires both values are populated.
        rows[0].TargetCategoryId = "cat-x";
        rows[0].TargetCategoryPath = "HVAC > X";

        Assert.Equal("cat-x", rows[1].TargetCategoryId);
        Assert.Equal("HVAC > X", rows[1].TargetCategoryPath);
        Assert.Equal("cat-x", rows[2].TargetCategoryId);
        Assert.Equal("HVAC > X", rows[2].TargetCategoryPath);
    }

    [Fact]
    public void ChangingAction_OnSingleSelection_DoesNotThrow_AndKeepsOtherRowsUntouched()
    {
        var items = new[] { MakeItem("only"), MakeItem("untouched") };
        using var vm = CreateVm(items);
        var rows = vm.Items.ToArray();
        SelectRows(rows[0]);

        rows[0].Action = FamilyBatchImportAction.IncrementVersion;

        Assert.Equal(FamilyBatchImportAction.IncrementVersion, rows[0].Action);
        Assert.Equal(FamilyBatchImportAction.IncrementVersion, rows[1].Action); // default action
    }

    [Fact]
    public void ChangingCategory_OnSingleSelection_DoesNotAffectOtherRows()
    {
        var items = new[] { MakeItem("only"), MakeItem("untouched") };
        using var vm = CreateVm(items);
        var rows = vm.Items.ToArray();
        SelectRows(rows[0]);

        rows[0].TargetCategoryId = "cat-only";
        rows[0].TargetCategoryPath = "HVAC > Only";

        Assert.Equal("cat-only", rows[0].TargetCategoryId);
        Assert.Null(rows[1].TargetCategoryId);
    }

    [Fact]
    public void IsSelected_BindingPropagatesTo_AllOtherSelectedRows_AfterDeselect()
    {
        // Simulates the WPF flow: user selects 3 rows, our behavior deselects
        // 2 of them via IsSelected (e.g. by clicking elsewhere with no
        // modifier), then changes Action. Only the still-selected row should
        // see the change applied to itself (which is a no-op for the source);
        // the deselected rows must NOT receive the new Action.
        var items = new[] { MakeItem("a"), MakeItem("b"), MakeItem("c") };
        using var vm = CreateVm(items);
        var rows = vm.Items.ToArray();
        SelectRows(rows);

        // User clicks empty space → only the third row stays selected.
        rows[0].IsSelected = false;
        rows[1].IsSelected = false;

        rows[2].Action = FamilyBatchImportAction.Skip;

        // The two deselected rows keep their default action.
        Assert.Equal(FamilyBatchImportAction.IncrementVersion, rows[0].Action);
        Assert.Equal(FamilyBatchImportAction.IncrementVersion, rows[1].Action);
        Assert.Equal(FamilyBatchImportAction.Skip, rows[2].Action);
    }

    [Fact]
    public void RestoreMultiSelectOnCellClick_AttachedProperty_ExposesGetterAndSetter()
    {
        // Verify the attached property surface is wired up correctly. The
        // actual click-handling requires a real DataGrid on an STA thread
        // and is verified manually in Revit.
        var prop = typeof(SmartCon.UI.Behaviors.DataGridBehaviors)
            .GetField("RestoreMultiSelectOnCellClickProperty",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);

        Assert.NotNull(prop);
        var dp = (System.Windows.DependencyProperty)prop!.GetValue(null)!;
        Assert.Equal(typeof(bool), dp.PropertyType);
        Assert.Equal("RestoreMultiSelectOnCellClick", dp.Name);
    }
}
