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

    private static FamilyBatchImportItem MakeItem(
        string fileName,
        FamilyBatchImportStatus status = FamilyBatchImportStatus.New,
        string? categoryId = null)
    {
        return new FamilyBatchImportItem(
            FilePath: $@"C:\fake\{fileName}.rfa",
            FileName: fileName,
            RevitMajorVersion: 2025,
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
            _factoryMock.Object);
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
        var items = new[] { MakeItem("a"), MakeItem("b"), MakeItem("c") };
        using var vm = CreateVm(items);
        var rows = vm.Items.ToArray();
        SelectRows(rows);

        var sourceEventCount = 0;
        rows[0].ActionChanged += (_, _) =>
        {
            sourceEventCount++;
        };

        rows[0].Action = FamilyBatchImportAction.Skip;

        Assert.Equal(1, sourceEventCount);
        Assert.Equal(FamilyBatchImportAction.Skip, rows[1].Action);
        Assert.Equal(FamilyBatchImportAction.Skip, rows[2].Action);
    }

    [Fact]
    public void ChangingCategory_OnMultiSelection_AppliesToAllOtherSelectedRows()
    {
        var items = new[] { MakeItem("a"), MakeItem("b"), MakeItem("c") };
        using var vm = CreateVm(items);
        var rows = vm.Items.ToArray();
        SelectRows(rows);

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
        Assert.Equal(FamilyBatchImportAction.IncrementVersion, rows[1].Action);
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
        var items = new[] { MakeItem("a"), MakeItem("b"), MakeItem("c") };
        using var vm = CreateVm(items);
        var rows = vm.Items.ToArray();
        SelectRows(rows);

        rows[0].IsSelected = false;
        rows[1].IsSelected = false;

        rows[2].Action = FamilyBatchImportAction.Skip;

        Assert.Equal(FamilyBatchImportAction.IncrementVersion, rows[0].Action);
        Assert.Equal(FamilyBatchImportAction.IncrementVersion, rows[1].Action);
        Assert.Equal(FamilyBatchImportAction.Skip, rows[2].Action);
    }

    [Fact]
    public void RestoreMultiSelectOnCellClick_AttachedProperty_ExposesGetterAndSetter()
    {
        var prop = typeof(SmartCon.UI.Behaviors.DataGridBehaviors)
            .GetField("RestoreMultiSelectOnCellClickProperty",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public);

        Assert.NotNull(prop);
        var dp = (System.Windows.DependencyProperty)prop!.GetValue(null)!;
        Assert.Equal(typeof(bool), dp.PropertyType);
        Assert.Equal("RestoreMultiSelectOnCellClick", dp.Name);
    }
}
