using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public class ContentHashDedupServiceTests
{
    private readonly Mock<IFamilyCatalogProvider> _mockProvider = new();
    private readonly ContentHashDedupService _sut;

    public ContentHashDedupServiceTests()
    {
        _sut = new ContentHashDedupService(_mockProvider.Object);
    }

    private static FamilyCatalogItem CreateCatalogItem(
        string id = "item-1",
        string name = "TestFamily",
        string? currentVersion = "v1",
        string familySource = "loadable")
    {
        return new FamilyCatalogItem(
            Id: id,
            Name: name,
            NormalizedName: name.ToLowerInvariant(),
            Description: null,
            CategoryPath: null,
            CategoryId: null,
            Manufacturer: null,
            ContentStatus: ContentStatus.Active,
            CurrentVersionLabel: currentVersion,
            Tags: Array.Empty<string>(),
            PublishedBy: null,
            CreatedAtUtc: DateTimeOffset.UtcNow,
            UpdatedAtUtc: DateTimeOffset.UtcNow,
            FamilySource: familySource,
            RevitCategory: null);
    }

    private static ContentHashMatch CreateMatch(
        string itemId = "item-1",
        string matchedLabel = "v2",
        bool isCurrent = true,
        string? currentLabel = "v2",
        string itemName = "TestFamily")
    {
        return new ContentHashMatch(
            CatalogItemId: itemId,
            MatchedVersionLabel: matchedLabel,
            IsCurrentVersion: isCurrent,
            CurrentVersionLabel: currentLabel,
            MatchedItemName: itemName,
            MatchedItemNormalizedName: itemName.ToLowerInvariant());
    }

    private static FamilyContentHash CreateHash(string hex = "ABC123")
    {
        return new FamilyContentHash(hex, FamilyContentHashFormat.CurrentVersion, "loadable");
    }

    private void SetupHashSearch(string hex, string source, ContentHashMatch? match)
    {
        _mockProvider
            .Setup(p => p.FindByContentHashAcrossVersionsAsync(
                hex, FamilyContentHashFormat.CurrentVersion, source,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(match);
    }

    [Fact]
    public async Task CheckAsync_HashNotMatched_NameNotFound_ReturnsNew()
    {
        SetupHashSearch("ABC123", "loadable", null);
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("testfamily", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        var result = await _sut.CheckAsync("testfamily", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.New, result.Status);
        Assert.Null(result.ExistingCatalogItemId);
        Assert.Null(result.HashMatch);
        Assert.False(result.IsCrossNameDuplicate);
    }

    [Fact]
    public async Task CheckAsync_NameFound_NoHash_ReturnsExisting()
    {
        var item = CreateCatalogItem();
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("testfamily", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        var result = await _sut.CheckAsync("testfamily", null, "loadable");

        Assert.Equal(FamilyBatchImportStatus.Existing, result.Status);
        Assert.Equal("item-1", result.ExistingCatalogItemId);
        Assert.Equal("v1", result.ExistingVersionLabel);
        Assert.Null(result.HashMatch);
    }

    [Fact]
    public async Task CheckAsync_HashMatchesCurrentVersion_SameName_ReturnsDuplicate()
    {
        var item = CreateCatalogItem(currentVersion: "v2");
        var match = CreateMatch(currentLabel: "v2");

        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("testfamily", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        SetupHashSearch("ABC123", "loadable", match);

        var result = await _sut.CheckAsync("testfamily", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.Duplicate, result.Status);
        Assert.Equal("item-1", result.ExistingCatalogItemId);
        Assert.Equal("v2", result.ExistingVersionLabel);
        Assert.NotNull(result.HashMatch);
        Assert.Equal("v2", result.HashMatch!.MatchedVersionLabel);
        Assert.True(result.HashMatch.IsCurrentVersion);
        Assert.False(result.IsCrossNameDuplicate);
    }

    [Fact]
    public async Task CheckAsync_HashMatchesArchivedVersion_SameName_ReturnsDuplicate()
    {
        var item = CreateCatalogItem(currentVersion: "v3");
        var match = CreateMatch(matchedLabel: "v2", isCurrent: false, currentLabel: "v3");

        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("testfamily", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        SetupHashSearch("ABC123", "loadable", match);

        var result = await _sut.CheckAsync("testfamily", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.Duplicate, result.Status);
        Assert.Equal("item-1", result.ExistingCatalogItemId);
        Assert.Equal("v3", result.ExistingVersionLabel);
        Assert.NotNull(result.HashMatch);
        Assert.Equal("v2", result.HashMatch!.MatchedVersionLabel);
        Assert.False(result.HashMatch.IsCurrentVersion);
        Assert.False(result.IsCrossNameDuplicate);
    }

    [Fact]
    public async Task CheckAsync_NameFound_HashNotMatched_ReturnsExisting()
    {
        var item = CreateCatalogItem(currentVersion: "v1");

        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("testfamily", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        SetupHashSearch("ABC123", "loadable", null);

        var result = await _sut.CheckAsync("testfamily", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.Existing, result.Status);
        Assert.Equal("item-1", result.ExistingCatalogItemId);
        Assert.Equal("v1", result.ExistingVersionLabel);
        Assert.Null(result.HashMatch);
    }

    [Fact]
    public async Task CheckAsync_EmptyName_ReturnsError()
    {
        var result = await _sut.CheckAsync("", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.Error, result.Status);
        Assert.Null(result.ExistingCatalogItemId);
    }

    [Fact]
    public async Task CheckAsync_NullName_ReturnsError()
    {
        var result = await _sut.CheckAsync(null!, CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.Error, result.Status);
    }

    [Fact]
    public async Task CheckAsync_NameNotFound_HashSearchedFirst()
    {
        // Issue #126: hash-first order — the hash lookup ALWAYS runs when
        // a hash is available, independent of the name. Only when the
        // hash misses does the name lookup run.
        SetupHashSearch("ABC123", "loadable", null);
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("unique", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        var result = await _sut.CheckAsync("unique", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.New, result.Status);
        _mockProvider.Verify(
            p => p.FindByContentHashAcrossVersionsAsync(
                "ABC123", FamilyContentHashFormat.CurrentVersion, "loadable",
                It.IsAny<CancellationToken>()),
            Times.Once,
            "Hash search MUST run first, even when the name is not in the catalog");
    }

    [Fact]
    public async Task CheckAsync_NoHash_DoesNotSearchByHash()
    {
        var item = CreateCatalogItem();
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("testfamily", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);

        var result = await _sut.CheckAsync("testfamily", null, "loadable");

        Assert.Equal(FamilyBatchImportStatus.Existing, result.Status);
        _mockProvider.Verify(
            p => p.FindByContentHashAcrossVersionsAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "Hash search must NOT be called when no hash is available");
    }

    [Fact]
    public async Task CheckAsync_Duplicate_PassesFamilySourceToHashSearch()
    {
        var match = CreateMatch(itemName: "Трубы", matchedLabel: "v1", currentLabel: "v1");
        SetupHashSearch("DEF456", "system", match);
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("трубы", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCatalogItem(familySource: "system"));

        var hash = new FamilyContentHash("DEF456", FamilyContentHashFormat.CurrentVersion, "system");
        var result = await _sut.CheckAsync("трубы", hash, "system");

        Assert.Equal(FamilyBatchImportStatus.Duplicate, result.Status);
        _mockProvider.Verify(
            p => p.FindByContentHashAcrossVersionsAsync(
                "DEF456", FamilyContentHashFormat.CurrentVersion, "system",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CheckAsync_HashMatchesDifferentName_ReturnsCrossNameDuplicate()
    {
        // Issue #126 core scenario: the file was renamed. The name is NOT
        // in the catalog, but the content hash matches an item under a
        // different name -> Duplicate with IsCrossNameDuplicate = true.
        var match = CreateMatch(itemId: "item-9", itemName: "OldName", matchedLabel: "v2", currentLabel: "v3");
        SetupHashSearch("ABC123", "loadable", match);
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("newname", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        var result = await _sut.CheckAsync("newname", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.Duplicate, result.Status);
        Assert.True(result.IsCrossNameDuplicate);
        Assert.Equal("item-9", result.ExistingCatalogItemId);
        Assert.Equal("v3", result.ExistingVersionLabel);
        Assert.NotNull(result.HashMatch);
        Assert.Equal("v2", result.HashMatch!.MatchedVersionLabel);
        Assert.Equal("OldName", result.HashMatch.MatchedItemName);
    }

    [Fact]
    public async Task CheckAsync_NameContentConflict_ContentWins()
    {
        // Issue #126 conflict: content matches item A ("ItemA"), but the
        // row's name belongs to a DIFFERENT item B. Content wins — the
        // row is a cross-name Duplicate of A.
        var match = CreateMatch(itemId: "item-a", itemName: "ItemA", matchedLabel: "v1", currentLabel: "v1");
        SetupHashSearch("ABC123", "loadable", match);
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("itemb", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCatalogItem(id: "item-b", name: "ItemB"));

        var result = await _sut.CheckAsync("itemb", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.Duplicate, result.Status);
        Assert.True(result.IsCrossNameDuplicate);
        Assert.Equal("item-a", result.ExistingCatalogItemId);
    }

    [Fact]
    public async Task CheckAsync_CrossNameDuplicate_NameOwnedBySameItem_NoConflict()
    {
        // The matched item's name differs from the row's name, but the
        // name lookup resolves to the SAME item (e.g. the item was
        // renamed in the catalog earlier). Still a cross-name duplicate,
        // but no conflicting third item.
        var match = CreateMatch(itemId: "item-1", itemName: "ItemA", matchedLabel: "v1", currentLabel: "v1");
        SetupHashSearch("ABC123", "loadable", match);
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("itemb", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCatalogItem(id: "item-1", name: "ItemB"));

        var result = await _sut.CheckAsync("itemb", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.Duplicate, result.Status);
        Assert.True(result.IsCrossNameDuplicate);
        Assert.Equal("item-1", result.ExistingCatalogItemId);
    }

    [Fact]
    public async Task CheckAsync_SystemHashMatch_DifferentCategoryName_NotCrossNameDuplicate()
    {
        // Issue #192 regression (production log 2026-08-03): the same
        // OST_DuctInsulations content was staged from a mini-project whose
        // category display name («Изоляция воздуховодов», registry) differs
        // from the source document's («Материалы изоляции воздуховодов»).
        // The BuiltInCategory ordinal is part of the system hash, so a
        // hash match is always the same category — never a cross-name
        // duplicate.
        var match = CreateMatch(
            itemId: "item-sys",
            itemName: "Материалы изоляции воздуховодов",
            matchedLabel: "v1",
            currentLabel: "v1");
        SetupHashSearch("9349CA", "system", match);

        var hash = new FamilyContentHash("9349CA", FamilyContentHashFormat.CurrentVersion, "system");
        var result = await _sut.CheckAsync("изоляция воздуховодов", hash, "system", -2008123);

        Assert.Equal(FamilyBatchImportStatus.Duplicate, result.Status);
        Assert.False(result.IsCrossNameDuplicate);
        Assert.Equal("item-sys", result.ExistingCatalogItemId);
        Assert.Equal("v1", result.ExistingVersionLabel);
        _mockProvider.Verify(
            p => p.FindByNormalizedNameAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "System hash match must not trigger the cross-name conflict name lookup");
    }

    [Fact]
    public async Task CheckAsync_LoadableHashMatch_DifferentName_StillCrossNameDuplicate()
    {
        // Issue #192 must NOT relax the loadable rule: the .rfa file name
        // is the stable user-controlled identity.
        var match = CreateMatch(itemId: "item-9", itemName: "OldName", matchedLabel: "v1", currentLabel: "v1");
        SetupHashSearch("ABC123", "loadable", match);
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("newname", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        var result = await _sut.CheckAsync("newname", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.Duplicate, result.Status);
        Assert.True(result.IsCrossNameDuplicate);
    }

    [Fact]
    public async Task CheckAsync_SystemHashMiss_CategoryExists_ReturnsExisting()
    {
        // Issue #192 hidden duplicate bomb: content changed (hash miss)
        // and the category display name differs per document — the old
        // name lookup would miss the existing item and report New,
        // creating a second catalog row for the same system category.
        SetupHashSearch("FFF000", "system", null);
        _mockProvider
            .Setup(p => p.FindByRevitCategoryIdAsync(-2008123, "system", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCatalogItem(id: "item-sys", name: "Материалы изоляции воздуховодов", currentVersion: "v2", familySource: "system"));

        var hash = new FamilyContentHash("FFF000", FamilyContentHashFormat.CurrentVersion, "system");
        var result = await _sut.CheckAsync("изоляция воздуховодов", hash, "system", -2008123);

        Assert.Equal(FamilyBatchImportStatus.Existing, result.Status);
        Assert.Equal("item-sys", result.ExistingCatalogItemId);
        Assert.Equal("v2", result.ExistingVersionLabel);
        Assert.Null(result.HashMatch);
    }

    [Fact]
    public async Task CheckAsync_SystemHashMiss_CategoryNotInCatalog_ReturnsNew()
    {
        SetupHashSearch("FFF000", "system", null);
        _mockProvider
            .Setup(p => p.FindByRevitCategoryIdAsync(-2008123, "system", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        var hash = new FamilyContentHash("FFF000", FamilyContentHashFormat.CurrentVersion, "system");
        var result = await _sut.CheckAsync("изоляция воздуховодов", hash, "system", -2008123);

        Assert.Equal(FamilyBatchImportStatus.New, result.Status);
        Assert.Null(result.ExistingCatalogItemId);
    }

    [Fact]
    public async Task CheckAsync_SystemNoHash_CategoryExists_ReturnsExisting()
    {
        _mockProvider
            .Setup(p => p.FindByRevitCategoryIdAsync(-2008123, "system", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCatalogItem(id: "item-sys", name: "Трубы", currentVersion: "v3", familySource: "system"));

        var result = await _sut.CheckAsync("изоляция воздуховодов", null, "system", -2008123);

        Assert.Equal(FamilyBatchImportStatus.Existing, result.Status);
        Assert.Equal("item-sys", result.ExistingCatalogItemId);
        Assert.Equal("v3", result.ExistingVersionLabel);
    }

    [Fact]
    public async Task CheckAsync_SystemWithoutCategoryId_FallsBackToNameLookup()
    {
        // Defensive: a system row without the category id keeps the
        // legacy name-based behavior (callers are expected to always
        // pass the id; this guards future regressions).
        SetupHashSearch("FFF000", "system", null);
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("трубы", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCatalogItem(familySource: "system"));

        var hash = new FamilyContentHash("FFF000", FamilyContentHashFormat.CurrentVersion, "system");
        var result = await _sut.CheckAsync("трубы", hash, "system");

        Assert.Equal(FamilyBatchImportStatus.Existing, result.Status);
        _mockProvider.Verify(
            p => p.FindByRevitCategoryIdAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckAsync_SystemCategoryMiss_NameFound_ReturnsExisting()
    {
        // Legacy databases: pre-V22 system rows may have
        // revit_category_id = NULL (the backfill task is optional). A
        // category-lookup miss must fall through to the name lookup so
        // those rows keep matching instead of reporting New (validator
        // finding M1).
        SetupHashSearch("FFF000", "system", null);
        _mockProvider
            .Setup(p => p.FindByRevitCategoryIdAsync(-2008123, "system", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("изоляция воздуховодов", It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateCatalogItem(id: "item-legacy", name: "Изоляция воздуховодов", currentVersion: "v4", familySource: "system"));

        var hash = new FamilyContentHash("FFF000", FamilyContentHashFormat.CurrentVersion, "system");
        var result = await _sut.CheckAsync("изоляция воздуховодов", hash, "system", -2008123);

        Assert.Equal(FamilyBatchImportStatus.Existing, result.Status);
        Assert.Equal("item-legacy", result.ExistingCatalogItemId);
        Assert.Equal("v4", result.ExistingVersionLabel);
    }
}
