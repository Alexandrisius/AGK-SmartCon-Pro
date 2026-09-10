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
        string? filePath = null,
        string? precomputedCatalogItemId = null,
        string? precomputedVersionLabel = null,
        string? precomputedManagedPath = null)
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
            Source: source,
            PrecomputedCatalogItemId: precomputedCatalogItemId,
            PrecomputedVersionLabel: precomputedVersionLabel,
            PrecomputedManagedPath: precomputedManagedPath);
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
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
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

    /// <summary>
    /// v2.0.0 regression: <see cref="FamilyBatchImportViewModel.GetResultItems"/>
    /// MUST round-trip the precomputed
    /// (CatalogItemId, VersionLabel, ManagedPath) triple from the row VM
    /// back into the resulting <see cref="FamilyBatchImportItem"/>. The
    /// post-dialog flow (staging + import) relies on these values to keep
    /// the managed-path invariant consistent — losing them forced
    /// staging to fall back to a fresh GUID and broke every batch
    /// import (UC-3 / UC-4 reported <c>Success = false</c> for every
    /// item). <see cref="FamilyBatchImportRow"/> also exposes them as
    /// read-only properties so a direct reader sees the same values.
    /// </summary>
    [Fact]
    public void GetResultItems_PreservesPrecomputedTriple_OnStagedRow()
    {
        const string catalogId = "staged-catalog-123";
        const string versionLabel = "v1";
        const string managedPath = @"C:\db\files\staged-catalog-123\v1\StagedFamily.rfa";

        var items = new[] { MakeItem(
            "StagedFamily",
            source: null,
            filePath: "loadable://StagedFamily",
            precomputedCatalogItemId: catalogId,
            precomputedVersionLabel: versionLabel,
            precomputedManagedPath: managedPath) };

        using var vm = CreateVm(items);

        var result = vm.GetResultItems().Single();

        Assert.Equal(catalogId, result.PrecomputedCatalogItemId);
        Assert.Equal(versionLabel, result.PrecomputedVersionLabel);
        Assert.Equal(managedPath, result.PrecomputedManagedPath);
    }

    [Fact]
    public void FamilyBatchImportRow_ExposesPrecomputedTriple()
    {
        const string catalogId = "row-catalog-456";
        const string versionLabel = "v7";
        const string managedPath = @"C:\db\files\row-catalog-456\v7\Whatever.rfa";

        var items = new[] { MakeItem(
            "Whatever",
            filePath: "loadable://Whatever",
            precomputedCatalogItemId: catalogId,
            precomputedVersionLabel: versionLabel,
            precomputedManagedPath: managedPath) };
        using var vm = CreateVm(items);
        var row = vm.Items.Single();

        Assert.Equal(catalogId, row.PrecomputedCatalogItemId);
        Assert.Equal(versionLabel, row.PrecomputedVersionLabel);
        Assert.Equal(managedPath, row.PrecomputedManagedPath);
    }

    /// <summary>
    /// v2.0.0 regression (the bug that broke the "Трубы → Трубы новые"
    /// rename in the active-project flow): when the user renames a row
    /// in the dialog to a name that does NOT exist in the catalog, the
    /// row's precomputed triple must be re-derived. Leaving the original
    /// (now-stale) precomputedCatalogItemId on the row would cause
    /// <c>ImportFileAsync</c> to call
    /// <c>InsertCatalogItemAsync(id=staleGuid)</c> and trip
    /// <c>UNIQUE constraint failed: catalog_items.id</c>.
    /// </summary>
    [Fact]
    public async Task RenamingRow_ToUniqueName_RebindsPrecomputedTripleToFreshGuid()
    {
        // Pre-seed the catalog with a family that has a known id; the
        // precomputer must NOT re-use that id when the row is renamed
        // away from it.
        const string seededId = "seeded-existing-id";
        const string seededName = "Existing Family";
        var existing = new FamilyCatalogItem(
            Id: seededId,
            Name: seededName,
            NormalizedName: "existingfamily",
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
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, CancellationToken>((normalized, _, _) =>
                normalized == "existingfamily"
                    ? Task.FromResult<FamilyCatalogItem?>(existing)
                    : Task.FromResult<FamilyCatalogItem?>(null));

        // The precomputer delegates to the catalog for the existing-lookup
        // and to the import service for the path math. We stub both: the
        // catalog says "no match for this name" and the import service
        // hands out a fresh GUID + v1 + a synthetic path on every call.
        var precomputerMock = new Mock<IFamilyImportPrecomputer>();
        precomputerMock
            .Setup(p => p.BuildPrecomputedTripleAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((string displayName, string ext, string _, string? _, CancellationToken __) =>
                new PrecomputedImportTriple(
                    CatalogItemId: Guid.NewGuid().ToString("N"),
                    VersionLabel: "v1",
                    ManagedPath: $@"C:\db\files\{Guid.NewGuid():N}\v1\{displayName}{ext}"));

        var items = new[] { MakeItem(
            seededName,
            status: FamilyBatchImportStatus.Existing,
            precomputedCatalogItemId: seededId,
            precomputedVersionLabel: "v3",
            precomputedManagedPath: $@"C:\db\files\{seededId}\v3\{seededName}.rfa") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object,
            importPrecomputer: precomputerMock.Object);
        var row = vm.Items.Single();

        // Capture the original precomputed triple so the test can prove
        // the rename actually moved the values away from the seed.
        var originalId = row.PrecomputedCatalogItemId;
        var originalVersion = row.PrecomputedVersionLabel;
        var originalPath = row.PrecomputedManagedPath;
        Assert.Equal(seededId, originalId);

        row.FileName = "Трубы новые";

        // Wait for the debounced lookup + recompute. Same 2s deadline
        // the existing rename tests use so we don't accidentally make
        // this test flaky.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (row.Status != FamilyBatchImportStatus.New && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(FamilyBatchImportStatus.New, row.Status);
        // The critical assertions: the precomputed triple is now bound
        // to a fresh GUID, NOT the seeded "existing-id" of the family
        // the row used to be named after. If this fails, ImportFileAsync
        // will INSERT a new catalog_items row with the stale id and
        // trip UNIQUE constraint failed: catalog_items.id.
        Assert.NotEqual(originalId, row.PrecomputedCatalogItemId);
        Assert.False(string.IsNullOrEmpty(row.PrecomputedCatalogItemId));
        Assert.Matches("^[0-9a-f]{32}$", row.PrecomputedCatalogItemId!);
        Assert.Equal("v1", row.PrecomputedVersionLabel);
        Assert.NotEqual(originalVersion, row.PrecomputedVersionLabel);
        Assert.NotEqual(originalPath, row.PrecomputedManagedPath);
        Assert.Contains("Трубы новые", row.PrecomputedManagedPath!);
    }

    /// <summary>
    /// v2.0.0 regression: renaming a row to a name that DOES exist in
    /// the catalog must re-use that existing item's id and the next
    /// version label, not allocate a fresh GUID. The precomputer's
    /// "reuse existing" path must run for renames too, otherwise the
    /// dialog would write a duplicate row instead of re-importing under
    /// the existing catalog item.
    /// </summary>
    [Fact]
    public async Task RenamingRow_ToExistingName_RebindsPrecomputedTripleToExistingId()
    {
        const string existingId = "existing-catalog-id";
        const string existingName = "Renamed Target";
        const string nextVersion = "v4";
        const string existingPath = @"C:\db\files\existing-catalog-id\v4\Renamed Target.rfa";

        var existing = new FamilyCatalogItem(
            Id: existingId,
            Name: existingName,
            NormalizedName: "renamedtarget",
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
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, CancellationToken>((normalized, _, _) =>
                normalized == "renamed target"
                    ? Task.FromResult<FamilyCatalogItem?>(existing)
                    : Task.FromResult<FamilyCatalogItem?>(null));

        var precomputerMock = new Mock<IFamilyImportPrecomputer>();
        precomputerMock
            .Setup(p => p.BuildPrecomputedTripleAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PrecomputedImportTriple(
                CatalogItemId: existingId,
                VersionLabel: nextVersion,
                ManagedPath: existingPath));

        var items = new[] { MakeItem(
            "Original Name",
            status: FamilyBatchImportStatus.New,
            precomputedCatalogItemId: Guid.NewGuid().ToString("N"),
            precomputedVersionLabel: "v1",
            precomputedManagedPath: @"C:\db\files\old-guid\v1\Original Name.rfa") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object,
            importPrecomputer: precomputerMock.Object);
        var row = vm.Items.Single();

        row.FileName = existingName;

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (row.Status != FamilyBatchImportStatus.Existing && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(FamilyBatchImportStatus.Existing, row.Status);
        Assert.Equal(existingId, row.PrecomputedCatalogItemId);
        Assert.Equal(nextVersion, row.PrecomputedVersionLabel);
        Assert.Equal(existingPath, row.PrecomputedManagedPath);

        // Round-trip through GetResultItems so the post-dialog
        // orchestrator sees the same (existingId, nextVersion) triple.
        var emitted = vm.GetResultItems().Single();
        Assert.Equal(existingId, emitted.PrecomputedCatalogItemId);
        Assert.Equal(nextVersion, emitted.PrecomputedVersionLabel);
        Assert.Equal(existingPath, emitted.PrecomputedManagedPath);
    }

    /// <summary>
    /// v2.0.1: when Status flips, AvailableActions must be rebuilt so
    /// OverwriteCurrent appears for Existing and disappears for New.
    /// </summary>
    [Fact]
    public void Status_NewToExisting_AddsOverwriteCurrentToAvailableActions()
    {
        var items = new[] { MakeItem("Family", status: FamilyBatchImportStatus.New) };
        using var vm = CreateVm(items);
        var row = vm.Items.Single();

        Assert.DoesNotContain(FamilyBatchImportAction.OverwriteCurrent, row.AvailableActions);

        row.Status = FamilyBatchImportStatus.Existing;

        Assert.Contains(FamilyBatchImportAction.OverwriteCurrent, row.AvailableActions);
    }

    /// <summary>
    /// v2.0.1: when Status flips Existing → New, AvailableActions must
    /// drop OverwriteCurrent. Action must be reset to a value present
    /// in the new list (otherwise the bound combo box holds an invalid
    /// value).
    /// </summary>
    [Fact]
    public void Status_ExistingToNew_DropsOverwriteCurrentAndResetsAction()
    {
        var items = new[] { MakeItem("Family", status: FamilyBatchImportStatus.Existing) };
        using var vm = CreateVm(items);
        var row = vm.Items.Single();

        row.Action = FamilyBatchImportAction.OverwriteCurrent;
        Assert.Equal(FamilyBatchImportAction.OverwriteCurrent, row.Action);

        row.Status = FamilyBatchImportStatus.New;

        Assert.DoesNotContain(FamilyBatchImportAction.OverwriteCurrent, row.AvailableActions);
        Assert.NotEqual(FamilyBatchImportAction.OverwriteCurrent, row.Action);
    }

    /// <summary>
    /// v2.0.1: when the user renames a row in the dialog to a name that
    /// DOES exist in the catalog, the row must pick up the existing
    /// item's category automatically (only when the user has not
    /// manually picked one in the picker).
    /// </summary>
    [Fact]
    public async Task RenamingRow_ToExistingName_PicksUpExistingCategory()
    {
        var existing = new FamilyCatalogItem(
            Id: "existing-id-cat",
            Name: "ExistingWithCategory",
            NormalizedName: "existingwithcategory",
            Description: null,
            CategoryPath: "HVAC > Ducts",
            CategoryId: "cat-duct",
            Manufacturer: null,
            ContentStatus: ContentStatus.Active,
            CurrentVersionLabel: "v1",
            Tags: Array.Empty<string>(),
            PublishedBy: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        var catalogMock = new Mock<IFamilyCatalogProvider>();
        catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, CancellationToken>((normalized, _, _) =>
                normalized == "existingwithcategory"
                    ? Task.FromResult<FamilyCatalogItem?>(existing)
                    : Task.FromResult<FamilyCatalogItem?>(null));

        var items = new[] { MakeItem("Original", status: FamilyBatchImportStatus.New) };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object);
        var row = vm.Items.Single();
        Assert.Null(row.TargetCategoryId);

        row.FileName = "ExistingWithCategory";

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (row.Status != FamilyBatchImportStatus.Existing && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(FamilyBatchImportStatus.Existing, row.Status);
        Assert.Equal("cat-duct", row.TargetCategoryId);
        Assert.Equal("HVAC > Ducts", row.TargetCategoryPath);
    }

    /// <summary>
    /// v2.0.1: when the user has manually picked a category in the
    /// picker, the rename must NOT clobber it — even if the new name
    /// matches an existing item with a different category. The user
    /// intent is "move this family to my picked category".
    /// </summary>
    [Fact]
    public async Task RenamingRow_ToExistingName_DoesNotClobberManuallyPickedCategory()
    {
        var existing = new FamilyCatalogItem(
            Id: "existing-id-cat2",
            Name: "ExistingWithCategory2",
            NormalizedName: "existingwithcategory2",
            Description: null,
            CategoryPath: "HVAC > Ducts",
            CategoryId: "cat-duct",
            Manufacturer: null,
            ContentStatus: ContentStatus.Active,
            CurrentVersionLabel: "v1",
            Tags: Array.Empty<string>(),
            PublishedBy: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        var catalogMock = new Mock<IFamilyCatalogProvider>();
        catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, CancellationToken>((normalized, _, _) =>
                normalized == "existingwithcategory2"
                    ? Task.FromResult<FamilyCatalogItem?>(existing)
                    : Task.FromResult<FamilyCatalogItem?>(null));

        var items = new[] { MakeItem("Original", status: FamilyBatchImportStatus.New) };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object);
        var row = vm.Items.Single();

        row.TargetCategoryId = "cat-plumbing";
        row.TargetCategoryPath = "Plumbing";
        row.CategoryProvenance = CategoryProvenance.Manual;

        row.FileName = "ExistingWithCategory2";

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (row.Status != FamilyBatchImportStatus.Existing && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(FamilyBatchImportStatus.Existing, row.Status);
        Assert.Equal("cat-plumbing", row.TargetCategoryId);
        Assert.Equal("Plumbing", row.TargetCategoryPath);
    }

    /// <summary>
    /// v2.0.1 hotfix: when the user explicitly picks "Без категории"
    /// in the picker (a deliberate reset), the manual flag must be
    /// cleared so a subsequent rename to another existing family
    /// re-pulls the target family's category. Picking a real
    /// category locks the category (see the matching test above);
    /// picking "Без категории" must NOT — the user intent is
    /// "no manual override, follow the name".
    /// </summary>
    [Fact]
    public async Task RenamingRow_AfterExplicitReset_PullsExistingCategoryOnRename()
    {
        var existing = new FamilyCatalogItem(
            Id: "reset-target-id",
            Name: "RenamedTarget",
            NormalizedName: "renamedtarget",
            Description: null,
            CategoryPath: "HVAC > Ducts",
            CategoryId: "cat-duct",
            Manufacturer: null,
            ContentStatus: ContentStatus.Active,
            CurrentVersionLabel: "v1",
            Tags: Array.Empty<string>(),
            PublishedBy: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
        var catalogMock = new Mock<IFamilyCatalogProvider>();
        catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, CancellationToken>((normalized, _, _) =>
                normalized == "renamedtarget"
                    ? Task.FromResult<FamilyCatalogItem?>(existing)
                    : Task.FromResult<FamilyCatalogItem?>(null));

        var items = new[] { MakeItem("Original") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object);
        var row = vm.Items.Single();

        // Simulate the user opening the picker and clicking
        // "Без категории" (the explicit no-category choice). In the
        // VM this writes null TargetCategoryId and clears the manual
        // flag so a subsequent rename can re-categorize by name.
        row.TargetCategoryId = null;
        row.TargetCategoryPath = "Без категории";
        row.CategoryProvenance = CategoryProvenance.None;
        Assert.Null(row.TargetCategoryId);

        row.FileName = "RenamedTarget";

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (row.Status != FamilyBatchImportStatus.Existing && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(FamilyBatchImportStatus.Existing, row.Status);
        Assert.Equal("cat-duct", row.TargetCategoryId);
        Assert.Equal("HVAC > Ducts", row.TargetCategoryPath);
    }

    /// <summary>
    /// v2.0.1: renaming a row Existing → New (a unique name) must
    /// clear the category inherited from the previous existing item
    /// (when the category was not manually picked by the user).
    /// </summary>
    [Fact]
    public async Task RenamingRow_ExistingToNew_ClearsInheritedCategory()
    {
        var catalogMock = new Mock<IFamilyCatalogProvider>();
        catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        var items = new[] { MakeItem("Original", status: FamilyBatchImportStatus.Existing) };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object);
        var row = vm.Items.Single();

        // Simulate inherited category: TargetCategoryIsManual defaults
        // to false, so the rename handler treats this as inherited and
        // must clear it when the row flips to New.
        row.TargetCategoryId = "cat-duct";
        row.TargetCategoryPath = "HVAC > Ducts";
        Assert.False(row.TargetCategoryIsManual);

        row.FileName = "TotallyUniqueName";

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (row.Status != FamilyBatchImportStatus.New && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(FamilyBatchImportStatus.New, row.Status);
        Assert.Null(row.TargetCategoryId);
    }

    /// <summary>
    /// v2.0.1: when the user has manually picked a category, a rename
    /// Existing → New must NOT clobber the manual choice.
    /// </summary>
    [Fact]
    public async Task RenamingRow_ExistingToNew_KeepsManuallyPickedCategory()
    {
        var catalogMock = new Mock<IFamilyCatalogProvider>();
        catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        var items = new[] { MakeItem("Original", status: FamilyBatchImportStatus.Existing) };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object);
        var row = vm.Items.Single();

        row.TargetCategoryId = "cat-plumbing";
        row.TargetCategoryPath = "Plumbing";
        row.CategoryProvenance = CategoryProvenance.Manual;

        row.FileName = "TotallyUniqueName";

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (row.Status != FamilyBatchImportStatus.New && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(FamilyBatchImportStatus.New, row.Status);
        Assert.Equal("cat-plumbing", row.TargetCategoryId);
        Assert.Equal("Plumbing", row.TargetCategoryPath);
    }

    /// <summary>
    /// v2.0.1: when the precomputer returns null (e.g. no active DB),
    /// the rename handler must clear the stale precomputed triple so
    /// the downstream importer does not register a new row under the
    /// OLD name's GUID.
    /// </summary>
    [Fact]
    public async Task RenamingRow_PrecomputerReturnsNull_ClearsStalePrecomputedTriple()
    {
        var catalogMock = new Mock<IFamilyCatalogProvider>();
        catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        var precomputerMock = new Mock<IFamilyImportPrecomputer>();
        precomputerMock
            .Setup(p => p.BuildPrecomputedTripleAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((PrecomputedImportTriple?)null);

        var items = new[] { MakeItem(
            "Original",
            status: FamilyBatchImportStatus.Existing,
            precomputedCatalogItemId: "stale-existing-id",
            precomputedVersionLabel: "v3",
            precomputedManagedPath: @"C:\db\files\stale-existing-id\v3\Original.rfa") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object,
            importPrecomputer: precomputerMock.Object);
        var row = vm.Items.Single();
        Assert.Equal("stale-existing-id", row.PrecomputedCatalogItemId);

        row.FileName = "BrandNew";

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (row.Status != FamilyBatchImportStatus.New && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(FamilyBatchImportStatus.New, row.Status);
        Assert.Null(row.PrecomputedCatalogItemId);
        Assert.Null(row.PrecomputedVersionLabel);
        Assert.Null(row.PrecomputedManagedPath);
    }
}
