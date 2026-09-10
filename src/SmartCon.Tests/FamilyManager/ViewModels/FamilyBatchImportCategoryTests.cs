using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Issue #135: category provenance + lock semantics in the batch import
/// dialog after ADR-049 (rename-invariant content hash).
///
/// Covers:
/// 1. «Импорт в категорию» preselection locks the category (Command) —
///    a rename to a unique name must NOT reset it to «Без категории».
/// 2. Category derivation on rename pulls from the hash-matched item
///    (AutoHash), not only from the name lookup.
/// 3. Multi-select category batch-apply propagates the lock
///    (Manual) and never clobbers locked rows from an automatic source.
/// 4. Move warning (P2): locked target category ≠ existing item's real
///    category → the import will move the family between categories.
/// </summary>
public sealed class FamilyBatchImportCategoryTests
{
    private readonly Mock<IFamilyManagerDialogService> _dialogMock = new();
    private readonly Mock<IFamilyManagerViewModelFactory> _factoryMock = new();

    private static FamilyBatchImportItem MakeItem(
        string fileName,
        FamilyBatchImportStatus status = FamilyBatchImportStatus.New,
        string? categoryId = null,
        string? categoryName = null,
        string? existingCatalogItemId = null,
        string? existingCategoryId = null,
        string? existingCategoryPath = null)
    {
        return new FamilyBatchImportItem(
            FilePath: $@"C:\fake\{fileName}.rfa",
            FileName: fileName,
            RevitMajorVersion: 2025,
            Status: status,
            ExistingCatalogItemId: existingCatalogItemId,
            ExistingVersionLabel: null,
            TargetCategoryId: categoryId,
            TargetCategoryName: categoryName,
            FamilySource: "loadable",
            ExistingCategoryId: existingCategoryId,
            ExistingCategoryPath: existingCategoryPath);
    }

    private static FamilyCatalogItem MakeCatalogItem(
        string id,
        string name,
        string? categoryId,
        string? categoryPath,
        string versionLabel = "v1")
    {
        return new FamilyCatalogItem(
            Id: id,
            Name: name,
            NormalizedName: name.ToLowerInvariant().Replace(" ", string.Empty),
            Description: null,
            CategoryPath: categoryPath,
            CategoryId: categoryId,
            Manufacturer: null,
            ContentStatus: ContentStatus.Active,
            CurrentVersionLabel: versionLabel,
            Tags: Array.Empty<string>(),
            PublishedBy: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow);
    }

    private static Mock<IFamilyCatalogProvider> EmptyCatalog()
    {
        var catalogMock = new Mock<IFamilyCatalogProvider>();
        catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);
        return catalogMock;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    [Fact]
    public void Ctor_CommandCategory_LocksRows_WithCommandProvenance()
    {
        var items = new[]
        {
            MakeItem("a", categoryId: "cat-x", categoryName: "Cat X"),
            MakeItem("b", categoryId: "cat-x", categoryName: "Cat X"),
        };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            defaultCategoryId: "cat-x", defaultCategoryName: "Cat X");

        Assert.All(vm.Items, row =>
        {
            Assert.Equal(CategoryProvenance.Command, row.CategoryProvenance);
            Assert.True(row.TargetCategoryIsManual);
        });
    }

    [Fact]
    public void Ctor_CommandCategory_ItemWithoutCategory_GetsDefaultAndLock()
    {
        var items = new[] { MakeItem("a") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            defaultCategoryId: "cat-x", defaultCategoryName: "Cat X");
        var row = vm.Items.Single();

        Assert.Equal("cat-x", row.TargetCategoryId);
        Assert.Equal(CategoryProvenance.Command, row.CategoryProvenance);
        Assert.True(row.TargetCategoryIsManual);
    }

    [Fact]
    public void Ctor_PlainImport_ExistingCategory_StaysAutoName()
    {
        var items = new[]
        {
            MakeItem("a", status: FamilyBatchImportStatus.Existing, categoryId: "cat-y", categoryName: "Cat Y"),
        };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object);
        var row = vm.Items.Single();

        Assert.Equal(CategoryProvenance.AutoName, row.CategoryProvenance);
        Assert.False(row.TargetCategoryIsManual);
    }

    [Fact]
    public void Ctor_PlainImport_NoCategory_IsNone()
    {
        var items = new[] { MakeItem("a") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object);
        var row = vm.Items.Single();

        Assert.Equal(CategoryProvenance.None, row.CategoryProvenance);
        Assert.False(row.TargetCategoryIsManual);
    }

    /// <summary>
    /// Issue #135 defect 1 (the reported bug): two NEW families imported
    /// via «Импорт в категорию» keep the preselected category when the
    /// user renames them to unique names. Previously the rename handler
    /// reset the category to «Без категории».
    /// </summary>
    [Fact]
    public async Task RenamingRow_ImportToCategory_KeepsPreselectedCategory()
    {
        var catalogMock = EmptyCatalog();
        var items = new[]
        {
            MakeItem("a", categoryId: "cat-x", categoryName: "Cat X"),
            MakeItem("b", categoryId: "cat-x", categoryName: "Cat X"),
        };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            defaultCategoryId: "cat-x", defaultCategoryName: "Cat X",
            catalogProvider: catalogMock.Object);
        var row = vm.Items.First();

        row.FileName = "brand-new-unique-name";

        await WaitForAsync(() => row.Status == FamilyBatchImportStatus.New);

        Assert.Equal("cat-x", row.TargetCategoryId);
        Assert.Equal("Cat X", row.TargetCategoryPath);
        Assert.Equal(CategoryProvenance.Command, row.CategoryProvenance);
        Assert.Equal(0, row.CategoryFlashToken);
    }

    /// <summary>
    /// Issue #135 defect 2: when the content hash matched a catalog item
    /// under a DIFFERENT name (cross-name duplicate, ADR-049), the
    /// category must be pulled from the hash-matched item — the name
    /// lookup misses for a unique new name.
    /// </summary>
    [Fact]
    public async Task RenamingRow_CrossNameDuplicate_PullsCategoryFromHashMatch()
    {
        var catalogMock = EmptyCatalog();
        var hashMatchedItem = MakeCatalogItem("hash-item-id", "OtherName", "cat-hash", "Pipes > Hash", "v2");
        catalogMock
            .Setup(c => c.GetItemAsync("hash-item-id", It.IsAny<CancellationToken>()))
            .ReturnsAsync(hashMatchedItem);

        var dedupMock = new Mock<IContentHashDedupService>();
        dedupMock
            .Setup(d => d.CheckAsync(
                It.IsAny<string>(),
                It.IsAny<FamilyContentHash?>(),
                It.IsAny<string>(),
                It.IsAny<int?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ContentHashDedupResult(
                Status: FamilyBatchImportStatus.Duplicate,
                ExistingCatalogItemId: "hash-item-id",
                ExistingVersionLabel: "v2",
                HashMatch: new ContentHashMatch(
                    CatalogItemId: "hash-item-id",
                    MatchedVersionLabel: "v2",
                    IsCurrentVersion: true,
                    CurrentVersionLabel: "v2",
                    MatchedItemName: "OtherName",
                    MatchedItemNormalizedName: "othername"),
                IsCrossNameDuplicate: true));

        var items = new[] { MakeItem("orig") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object,
            dedupService: dedupMock.Object);
        var row = vm.Items.Single();

        row.FileName = "unique-new-name";

        await WaitForAsync(() => row.Status == FamilyBatchImportStatus.Duplicate);

        Assert.Equal("cat-hash", row.TargetCategoryId);
        Assert.Equal("Pipes > Hash", row.TargetCategoryPath);
        Assert.Equal(CategoryProvenance.AutoHash, row.CategoryProvenance);
        Assert.Equal("cat-hash", row.ExistingCategoryId);
        Assert.Equal(1, row.CategoryFlashToken);
    }

    [Fact]
    public void ApplyCategoryToSelection_SetsManualProvenance_OnAllRows()
    {
        var items = new[] { MakeItem("a"), MakeItem("b"), MakeItem("c") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object);
        var rows = vm.Items.ToArray();
        foreach (var r in rows) r.IsSelected = true;

        // Simulate the picker flow: provenance first, then id/path.
        rows[0].CategoryProvenance = CategoryProvenance.Manual;
        rows[0].TargetCategoryId = "cat-z";
        rows[0].TargetCategoryPath = "HVAC > Z";

        Assert.Equal("cat-z", rows[1].TargetCategoryId);
        Assert.Equal(CategoryProvenance.Manual, rows[1].CategoryProvenance);
        Assert.True(rows[1].TargetCategoryIsManual);
        Assert.Equal(CategoryProvenance.Manual, rows[2].CategoryProvenance);
    }

    /// <summary>
    /// Issue #135 defect 3: an AUTOMATIC category change on one selected
    /// row (rename re-derivation) must not clobber a locked category on
    /// another selected row.
    /// </summary>
    [Fact]
    public void ApplyCategoryToSelection_AutoSourceChange_SkipsLockedRows()
    {
        var items = new[] { MakeItem("a"), MakeItem("b") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object);
        var rows = vm.Items.ToArray();
        foreach (var r in rows) r.IsSelected = true;

        rows[1].CategoryProvenance = CategoryProvenance.Manual;
        rows[1].TargetCategoryId = "cat-locked";
        rows[1].TargetCategoryPath = "Locked";

        // Automatic change on the source row (as ApplyNameChangeResult
        // would do: provenance AutoName first, then id/path).
        rows[0].CategoryProvenance = CategoryProvenance.AutoName;
        rows[0].TargetCategoryId = "cat-auto";
        rows[0].TargetCategoryPath = "Auto";

        Assert.Equal("cat-locked", rows[1].TargetCategoryId);
        Assert.Equal("Locked", rows[1].TargetCategoryPath);
        Assert.Equal(CategoryProvenance.Manual, rows[1].CategoryProvenance);
    }

    [Fact]
    public void ShowCategoryMoveWarning_LockedDifferentFromExisting_IsTrue()
    {
        var items = new[]
        {
            MakeItem("a",
                status: FamilyBatchImportStatus.Existing,
                existingCatalogItemId: "existing-1",
                existingCategoryId: "cat-old",
                existingCategoryPath: "Old Cat"),
        };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object);
        var row = vm.Items.Single();

        row.CategoryProvenance = CategoryProvenance.Manual;
        row.TargetCategoryId = "cat-new";
        row.TargetCategoryPath = "New Cat";

        Assert.True(row.ShowCategoryMoveWarning);
        Assert.Contains("Old Cat", row.CategoryMoveWarningTooltip);
        Assert.Contains("New Cat", row.CategoryMoveWarningTooltip);
    }

    [Fact]
    public void ShowCategoryMoveWarning_SameCategory_IsFalse()
    {
        var items = new[]
        {
            MakeItem("a",
                status: FamilyBatchImportStatus.Existing,
                categoryId: "cat-old",
                categoryName: "Old Cat",
                existingCatalogItemId: "existing-1",
                existingCategoryId: "cat-old",
                existingCategoryPath: "Old Cat"),
        };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object);
        var row = vm.Items.Single();
        row.CategoryProvenance = CategoryProvenance.Manual;

        Assert.False(row.ShowCategoryMoveWarning);
    }

    [Fact]
    public void ShowCategoryMoveWarning_Unlocked_IsFalse()
    {
        var items = new[]
        {
            MakeItem("a",
                status: FamilyBatchImportStatus.Existing,
                categoryId: "cat-new",
                categoryName: "New Cat",
                existingCatalogItemId: "existing-1",
                existingCategoryId: "cat-old",
                existingCategoryPath: "Old Cat"),
        };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object);
        var row = vm.Items.Single();

        Assert.False(row.TargetCategoryIsManual);
        Assert.False(row.ShowCategoryMoveWarning);
    }

    [Fact]
    public void ShowCategoryMoveWarning_ExplicitNoCategoryPick_IsTrue()
    {
        // #261: an explicit «Без категории» pick moves the existing item to
        // no category — the move indicator must fire even though the target
        // id is null (the old predicate required a non-empty id and stayed
        // silent, hiding the move).
        var items = new[]
        {
            MakeItem("a",
                status: FamilyBatchImportStatus.Duplicate,
                existingCatalogItemId: "existing-1",
                existingCategoryId: "cat-old",
                existingCategoryPath: "Old Cat"),
        };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object);
        var row = vm.Items.Single();

        Assert.False(row.ShowCategoryMoveWarning);

        row.ClearCategoryOnImport = true;
        row.TargetCategoryPath = "Без категории";

        Assert.True(row.ShowCategoryMoveWarning);
        Assert.Contains("Old Cat", row.CategoryMoveWarningTooltip);
        Assert.Contains("Без категории", row.CategoryMoveWarningTooltip);
    }

    [Fact]
    public void GetResultItems_ExplicitNoCategoryPick_CarriesClearFlag()
    {
        // #261: the dialog row's explicit «Без категории» pick must reach
        // the import item, or the update branch cannot distinguish "clear
        // the category" from "no explicit choice".
        var items = new[]
        {
            MakeItem("a",
                status: FamilyBatchImportStatus.Duplicate,
                existingCatalogItemId: "existing-1",
                existingCategoryId: "cat-old",
                existingCategoryPath: "Old Cat"),
        };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object);
        var row = vm.Items.Single();

        row.ClearCategoryOnImport = true;
        row.TargetCategoryId = null;
        row.TargetCategoryPath = "Без категории";

        var result = vm.GetResultItems().Single();
        Assert.True(result.ClearCategoryOnImport);
        Assert.Null(result.TargetCategoryId);
    }

    /// <summary>
    /// Issue #135 (P2) integration: a Command-locked row renamed to an
    /// existing family living in a DIFFERENT category keeps its locked
    /// category and arms the move warning — the import will relocate the
    /// existing family into the locked category.
    /// </summary>
    [Fact]
    public async Task RenamingRow_LockedToExistingOtherCategory_KeepsCategory_ArmsMoveWarning()
    {
        var existing = MakeCatalogItem("existing-9", "ExistingFamily", "cat-duct", "HVAC > Ducts", "v3");
        var catalogMock = new Mock<IFamilyCatalogProvider>();
        catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, CancellationToken>((normalized, _, _) =>
                normalized == existing.NormalizedName
                    ? Task.FromResult<FamilyCatalogItem?>(existing)
                    : Task.FromResult<FamilyCatalogItem?>(null));

        var items = new[] { MakeItem("orig", categoryId: "cat-x", categoryName: "Cat X") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            defaultCategoryId: "cat-x", defaultCategoryName: "Cat X",
            catalogProvider: catalogMock.Object);
        var row = vm.Items.Single();

        row.FileName = "ExistingFamily";

        await WaitForAsync(() => row.Status == FamilyBatchImportStatus.Existing);

        Assert.Equal("cat-x", row.TargetCategoryId);
        Assert.Equal(CategoryProvenance.Command, row.CategoryProvenance);
        Assert.Equal("cat-duct", row.ExistingCategoryId);
        Assert.True(row.ShowCategoryMoveWarning);
    }

    [Fact]
    public async Task RenamingRow_AutoCategoryChange_BumpsFlashToken()
    {
        var existing = MakeCatalogItem("existing-cat", "ExistingWithCat", "cat-duct", "HVAC > Ducts");
        var catalogMock = new Mock<IFamilyCatalogProvider>();
        catalogMock
            .Setup(c => c.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .Returns<string, string?, CancellationToken>((normalized, _, _) =>
                normalized == existing.NormalizedName
                    ? Task.FromResult<FamilyCatalogItem?>(existing)
                    : Task.FromResult<FamilyCatalogItem?>(null));

        var items = new[] { MakeItem("orig") };
        using var vm = new FamilyBatchImportViewModel(
            items, _dialogMock.Object, _factoryMock.Object,
            catalogProvider: catalogMock.Object);
        var row = vm.Items.Single();
        Assert.Equal(0, row.CategoryFlashToken);

        row.FileName = "ExistingWithCat";

        await WaitForAsync(() => row.Status == FamilyBatchImportStatus.Existing);

        Assert.Equal("cat-duct", row.TargetCategoryId);
        Assert.Equal(CategoryProvenance.AutoName, row.CategoryProvenance);
        Assert.Equal(1, row.CategoryFlashToken);
    }
}
