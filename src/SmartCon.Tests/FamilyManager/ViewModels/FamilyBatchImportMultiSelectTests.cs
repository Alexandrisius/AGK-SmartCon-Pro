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
        string? categoryId = null,
        FamilyImportSource? source = null,
        string? filePath = null)
    {
        return new FamilyBatchImportItem(
            FilePath: filePath ?? $@"C:\fake\{fileName}.rfa",
            FileName: fileName,
            RevitMajorVersion: 2025,
            Status: status,
            ExistingCatalogItemId: null,
            ExistingVersionLabel: null,
            TargetCategoryId: categoryId,
            TargetCategoryName: categoryId,
            FamilySource: "loadable",
            TypeCount: null,
            RevitCategory: null,
            Source: source);
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

    /// <summary>
    /// v2.0.0 hotfix: renaming a row whose name now matches nothing in the
    /// catalog must flip the row's Status to <c>New</c>. The debounced
    /// lookup runs on a background task; we wait synchronously for the
    /// result by giving the dispatcher / task pool a chance to complete.
    /// </summary>
    [Fact]
    public async Task RenamingRow_ToUniqueName_UpdatesStatusToNew()
    {
        var catalogMock = new Mock<IFamilyCatalogProvider>();
        catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        var items = new[] { MakeItem("orig") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object);
        var row = vm.Items.Single();

        // The row starts as New; the test sets the pre-condition to
        // Existing to verify the rename flips it back.
        row.Status = FamilyBatchImportStatus.Existing;
        row.ExistingCatalogItemId = "old-id";
        row.ExistingVersionLabel = "v1";

        row.FileName = "completely-different-name";

        // Wait for the debounced lookup (250ms in the VM) plus a small
        // buffer for the dispatcher to apply the result.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (row.Status != FamilyBatchImportStatus.New && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(FamilyBatchImportStatus.New, row.Status);
        Assert.Null(row.ExistingCatalogItemId);
        Assert.Null(row.ExistingVersionLabel);
    }

    /// <summary>
    /// v2.0.0 hotfix: renaming a row to a name that already exists in the
    /// catalog must flip the row's Status to <c>Existing</c> and update
    /// <c>ExistingCatalogItemId</c> + <c>ExistingVersionLabel</c> to the
    /// matched record.
    /// </summary>
    [Fact]
    public async Task RenamingRow_ToExistingName_UpdatesStatusToExisting()
    {
        var existing = new FamilyCatalogItem(
            Id: "catalog-id-1",
            Name: "TargetFamily",
            NormalizedName: "targetfamily",
            Description: null,
            CategoryPath: null,
            CategoryId: null,
            Manufacturer: null,
            ContentStatus: ContentStatus.Active,
            CurrentVersionLabel: "v3",
            Tags: Array.Empty<string>(),
            PublishedBy: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        var catalogMock = new Mock<IFamilyCatalogProvider>();
        catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existing);

        var items = new[] { MakeItem("orig") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object);
        var row = vm.Items.Single();
        row.Status = FamilyBatchImportStatus.New;

        row.FileName = "TargetFamily";

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (row.Status != FamilyBatchImportStatus.Existing && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(FamilyBatchImportStatus.Existing, row.Status);
        Assert.Equal("catalog-id-1", row.ExistingCatalogItemId);
        Assert.Equal("v3", row.ExistingVersionLabel);
    }

    /// <summary>
    /// v2.0.0 hotfix: when the view-model is constructed without a
    /// catalog provider (older callers), the rename handler must not
    /// throw. The Status / ExistingCatalogItemId remain at their
    /// pre-rename values.
    /// </summary>
    [Fact]
    public void RenamingRow_WithoutCatalogProvider_DoesNotThrow()
    {
        var items = new[] { MakeItem("orig") };
        using var vm = CreateVm(items);
        var row = vm.Items.Single();
        row.Status = FamilyBatchImportStatus.New;

        var ex = Record.Exception(() => row.FileName = "new-name");
        Assert.Null(ex);
        Assert.Equal(FamilyBatchImportStatus.New, row.Status);
    }

    /// <summary>
    /// v2.0.0 regression: <see cref="FamilyBatchImportRow"/> must surface
    /// the <see cref="FamilyImportSource"/> payload from the input item,
    /// and <see cref="FamilyBatchImportViewModel.GetResultItems"/> must
    /// re-emit it on the resulting <see cref="FamilyBatchImportItem"/>.
    /// Without this, the post-dialog staging flow in
    /// <c>ProcessProjectImportAsync</c> sees <c>Source = null</c> on
    /// every row and skips all staging, leaving the placeholder
    /// <c>FilePath</c> ("system://..." / "loadable://...") in place.
    /// The orchestrator then calls <c>ImportFileAsync</c> with a
    /// non-existent path and reports <c>Success = false</c> for every
    /// item. This was the root cause of the v2.0.0 batch-import
    /// regression (UC-3/UC-4 imported zero families).
    /// </summary>
    [Fact]
    public void GetResultItems_PreservesSource_OnLoadableRow()
    {
        var source = new FamilyImportSource.LoadableSource(
            FamilyName: "TestFamily",
            FamilyUniqueId: "uid-123",
            CategoryName: "TestCategory");
        var placeholder = "loadable://TestFamily";
        var items = new[] { MakeItem("TestFamily", source: source, filePath: placeholder) };
        using var vm = CreateVm(items);

        var result = vm.GetResultItems();

        Assert.Single(result);
        Assert.Same(source, result[0].Source);
        Assert.Equal(placeholder, result[0].FilePath);
    }

    /// <summary>
    /// v2.0.0 regression: same as <see cref="GetResultItems_PreservesSource_OnLoadableRow"/>
    /// but for the system-family path. The system <see cref="FamilyImportSource.SystemSource"/>
    /// carries the type unique ids needed by
    /// <c>StageSystemFamiliesFromMetadataAsync</c>.
    /// </summary>
    [Fact]
    public void GetResultItems_PreservesSource_OnSystemRow()
    {
        var source = new FamilyImportSource.SystemSource(
            DisplayName: "Трубы",
            CategoryId: 41, // OST_Pipes
            TypeUniqueIds: new[] { "uid-1", "uid-2" },
            TypeNames: new[] { "Тип 1", "Тип 2" });
        var placeholder = "system://active-project/Трубы";
        var items = new[] { new FamilyBatchImportItem(
            FilePath: placeholder,
            FileName: "Трубы",
            RevitMajorVersion: 2025,
            Status: FamilyBatchImportStatus.New,
            FamilySource: "system",
            TypeCount: 2,
            RevitCategory: "Трубы",
            Source: source) };
        using var vm = CreateVm(items);

        var result = vm.GetResultItems();

        Assert.Single(result);
        Assert.Same(source, result[0].Source);
        Assert.Equal(placeholder, result[0].FilePath);
    }

    /// <summary>
    /// v2.0.0 regression: <see cref="FamilyBatchImportRow"/> must
    /// expose the input <see cref="FamilyBatchImportItem.Source"/> via
    /// its <c>Source</c> property. Without it, callers that need the
    /// payload (e.g. custom VM code) have no way to read it back.
    /// </summary>
    [Fact]
    public void FamilyBatchImportRow_ExposesSource()
    {
        var source = new FamilyImportSource.LoadableSource(
            FamilyName: "X", FamilyUniqueId: "u", CategoryName: "C");
        var items = new[] { MakeItem("X", source: source, filePath: "loadable://X") };
        using var vm = CreateVm(items);
        var row = vm.Items.Single();

        Assert.Same(source, row.Source);
    }
}
