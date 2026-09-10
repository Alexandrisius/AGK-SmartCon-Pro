using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Tests for <see cref="LocalFamilyFactRepository"/> (ADR-055): read path
/// of the family-facts subsystem for the properties window.
/// </summary>
public sealed class LocalFamilyFactRepositoryTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyFactRepository _sut;

    public LocalFamilyFactRepositoryTests()
    {
        _fixture = new TempCatalogFixture();
        _sut = new LocalFamilyFactRepository(_fixture.GetDatabase());
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GetForItem_ItemWithCategoryAndFacts_ReturnsAll()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, "FamA", revitCategoryId: -2008049);
        await CatalogSeedHelper.SeedFactAsync(_fixture, itemId, "part_type", "5", "Elbow");

        var data = await _sut.GetForItemAsync(itemId);

        Assert.Equal(-2008049, data.RevitCategoryId);
        var fact = Assert.Single(data.Facts);
        Assert.Equal("part_type", fact.FactKey);
        Assert.Equal("5", fact.ValueKey);
        Assert.Equal("Elbow", fact.ValueDisplay);
    }

    [Fact]
    public async Task GetForItem_ItemWithoutCategoryId_ReturnsNullIdEmptyFacts()
    {
        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "FamA");

        var data = await _sut.GetForItemAsync(itemId);

        Assert.Null(data.RevitCategoryId);
        Assert.Empty(data.Facts);
    }

    [Fact]
    public async Task GetForItem_MissingItem_ReturnsNullIdEmptyFacts()
    {
        var data = await _sut.GetForItemAsync("no-such-item");

        Assert.Null(data.RevitCategoryId);
        Assert.Empty(data.Facts);
    }
}
