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

    private static FamilyContentHash CreateHash(string hex = "ABC123")
    {
        return new FamilyContentHash(hex, FamilyContentHashFormat.CurrentVersion, "loadable");
    }

    [Fact]
    public async Task CheckAsync_NameNotFound_ReturnsNew()
    {
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("testfamily", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        var result = await _sut.CheckAsync("testfamily", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.New, result.Status);
        Assert.Null(result.ExistingCatalogItemId);
        Assert.Null(result.HashMatch);
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
    public async Task CheckAsync_NameFound_HashMatchesCurrentVersion_ReturnsDuplicate()
    {
        var item = CreateCatalogItem(currentVersion: "v2");
        var match = new ContentHashMatch("item-1", "v2", true);

        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("testfamily", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        _mockProvider
            .Setup(p => p.FindByContentHashAcrossVersionsAsync(
                "ABC123", FamilyContentHashFormat.CurrentVersion, "loadable",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(match);

        var result = await _sut.CheckAsync("testfamily", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.Duplicate, result.Status);
        Assert.Equal("item-1", result.ExistingCatalogItemId);
        Assert.NotNull(result.HashMatch);
        Assert.Equal("v2", result.HashMatch!.MatchedVersionLabel);
        Assert.True(result.HashMatch.IsCurrentVersion);
    }

    [Fact]
    public async Task CheckAsync_NameFound_HashMatchesArchivedVersion_ReturnsDuplicate()
    {
        var item = CreateCatalogItem(currentVersion: "v3");
        var match = new ContentHashMatch("item-1", "v2", false);

        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("testfamily", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        _mockProvider
            .Setup(p => p.FindByContentHashAcrossVersionsAsync(
                "ABC123", FamilyContentHashFormat.CurrentVersion, "loadable",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(match);

        var result = await _sut.CheckAsync("testfamily", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.Duplicate, result.Status);
        Assert.Equal("item-1", result.ExistingCatalogItemId);
        Assert.NotNull(result.HashMatch);
        Assert.Equal("v2", result.HashMatch!.MatchedVersionLabel);
        Assert.False(result.HashMatch.IsCurrentVersion);
    }

    [Fact]
    public async Task CheckAsync_NameFound_HashNotMatched_ReturnsExisting()
    {
        var item = CreateCatalogItem(currentVersion: "v1");

        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("testfamily", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        _mockProvider
            .Setup(p => p.FindByContentHashAcrossVersionsAsync(
                "ABC123", FamilyContentHashFormat.CurrentVersion, "loadable",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync((ContentHashMatch?)null);

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
    public async Task CheckAsync_NameNotFound_DoesNotSearchByHash()
    {
        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("unique", It.IsAny<CancellationToken>()))
            .ReturnsAsync((FamilyCatalogItem?)null);

        var result = await _sut.CheckAsync("unique", CreateHash(), "loadable");

        Assert.Equal(FamilyBatchImportStatus.New, result.Status);
        _mockProvider.Verify(
            p => p.FindByContentHashAcrossVersionsAsync(
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "Hash search must NOT be called when name is not found — dedup only applies on name match");
    }

    [Fact]
    public async Task CheckAsync_NameFound_NoHash_DoesNotSearchByHash()
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
        var item = CreateCatalogItem(familySource: "system");
        var match = new ContentHashMatch("item-1", "v1", true);

        _mockProvider
            .Setup(p => p.FindByNormalizedNameAsync("трубы", It.IsAny<CancellationToken>()))
            .ReturnsAsync(item);
        _mockProvider
            .Setup(p => p.FindByContentHashAcrossVersionsAsync(
                "DEF456", FamilyContentHashFormat.CurrentVersion, "system",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(match);

        var hash = new FamilyContentHash("DEF456", FamilyContentHashFormat.CurrentVersion, "system");
        var result = await _sut.CheckAsync("трубы", hash, "system");

        Assert.Equal(FamilyBatchImportStatus.Duplicate, result.Status);
        _mockProvider.Verify(
            p => p.FindByContentHashAcrossVersionsAsync(
                "DEF456", FamilyContentHashFormat.CurrentVersion, "system",
                It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
