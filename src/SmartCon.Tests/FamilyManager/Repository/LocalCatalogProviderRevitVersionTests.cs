using SmartCon.Core.Models.FamilyManager;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalCatalogProviderRevitVersionTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;

    public LocalCatalogProviderRevitVersionTests()
    {
        _fixture = new TempCatalogFixture();
    }

    [Fact]
    public async Task SearchAsync_ReturnsActiveAndMinRevitMajorVersion()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, name: "FamVerA", versionLabel: "v1", revitVersion: 2021);

        var results = await _fixture.GetProvider().SearchAsync(new FamilyCatalogQuery(
            SearchText: null, CategoryFilter: null, StatusFilter: null, Tags: null,
            Sort: FamilyCatalogSort.NameAsc, Offset: 0, Limit: 100));

        var item = Assert.Single(results, r => r.Id == itemId);
        Assert.Equal(2021, item.ActiveRevitMajorVersion);
        Assert.Equal(2021, item.MinRevitMajorVersion);
    }

    [Fact]
    public async Task SearchAsync_LegacyMultiVariant_MinReflectsOldestAcrossVersions()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, name: "FamVerB", versionLabel: "v1", revitVersion: 2025);
        await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamVerB2021", "v1", 2021);

        var results = await _fixture.GetProvider().SearchAsync(new FamilyCatalogQuery(
            SearchText: null, CategoryFilter: null, StatusFilter: null, Tags: null,
            Sort: FamilyCatalogSort.NameAsc, Offset: 0, Limit: 100));

        var item = Assert.Single(results, r => r.Id == itemId);
        Assert.Equal(2021, item.ActiveRevitMajorVersion);
        Assert.Equal(2021, item.MinRevitMajorVersion);
    }

    [Fact]
    public async Task SearchAsync_MinSpansArchivedVersions()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, name: "FamVerC", versionLabel: "v1", revitVersion: 2021, currentLabel: "v2");
        await CatalogSeedHelper.SeedAdditionalVariantAsync(_fixture, itemId, "FamVerC_v2", "v2", 2025);

        var results = await _fixture.GetProvider().SearchAsync(new FamilyCatalogQuery(
            SearchText: null, CategoryFilter: null, StatusFilter: null, Tags: null,
            Sort: FamilyCatalogSort.NameAsc, Offset: 0, Limit: 100));

        var item = Assert.Single(results, r => r.Id == itemId);
        Assert.Equal(2025, item.ActiveRevitMajorVersion);
        Assert.Equal(2021, item.MinRevitMajorVersion);
    }

    [Fact]
    public async Task SearchAsync_ItemWithoutVersions_ReturnsNullVersions()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, name: "FamVerD", versionLabel: "v1", revitVersion: 2025, currentLabel: "v-missing");

        var results = await _fixture.GetProvider().SearchAsync(new FamilyCatalogQuery(
            SearchText: null, CategoryFilter: null, StatusFilter: null, Tags: null,
            Sort: FamilyCatalogSort.NameAsc, Offset: 0, Limit: 100));

        var item = Assert.Single(results, r => r.Id == itemId);
        Assert.Null(item.ActiveRevitMajorVersion);
        Assert.Equal(2025, item.MinRevitMajorVersion);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
