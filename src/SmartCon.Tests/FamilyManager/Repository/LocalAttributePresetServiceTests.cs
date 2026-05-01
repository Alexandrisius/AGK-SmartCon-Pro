using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalAttributePresetServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalAttributePresetService _service;
    private readonly LocalCategoryRepository _categoryRepository;

    public LocalAttributePresetServiceTests()
    {
        _fixture = new TempCatalogFixture();
        _fixture.MigrateAsync().GetAwaiter().GetResult();

        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _service = new LocalAttributePresetService(
            _fixture.GetDatabase(),
            _categoryRepository,
            _fixture.GetMigrator());
    }

    private async Task<string> SeedCategoryAsync(string name)
    {
        var node = await _categoryRepository.AddAsync(name, null, 0);
        return node.Id;
    }

    [Fact]
    public async Task GetAllPresetsAsync_EmptyDb_ReturnsEmptyList()
    {
        var presets = await _service.GetAllPresetsAsync();

        Assert.Empty(presets);
    }

    [Fact]
    public async Task CreatePresetAsync_SavesPreset_ReturnsInGetAll()
    {
        var catId = await SeedCategoryAsync("TestCat1");
        var parameters = new List<AttributePresetParameter>
        {
            new("Width", "W", 0),
            new("Height", "H", 1),
            new("Length", null, 2)
        };

        var preset = await _service.CreatePresetAsync(catId, parameters);

        Assert.NotNull(preset.Id);
        Assert.Equal(catId, preset.CategoryId);
        Assert.Equal(3, preset.Parameters.Count);

        var all = await _service.GetAllPresetsAsync();
        Assert.Single(all);
        Assert.Equal(3, all[0].Parameters.Count);
    }

    [Fact]
    public async Task CreatePresetAsync_NullCategoryId_GlobalPreset()
    {
        var parameters = new List<AttributePresetParameter>
        {
            new("Diameter", "D", 0)
        };

        var preset = await _service.CreatePresetAsync(null, parameters);

        Assert.Null(preset.CategoryId);
        Assert.Single(preset.Parameters);
    }

    [Fact]
    public async Task DeletePresetAsync_RemovesPreset()
    {
        var catId = await SeedCategoryAsync("DeleteCat");
        var parameters = new List<AttributePresetParameter>
        {
            new("Size", "S", 0)
        };

        var preset = await _service.CreatePresetAsync(catId, parameters);
        await _service.DeletePresetAsync(preset.Id);

        var all = await _service.GetAllPresetsAsync();
        Assert.Empty(all);
    }

    [Fact]
    public async Task GetPresetForCategoryAsync_ReturnsMatchingPreset()
    {
        var catId1 = await SeedCategoryAsync("MatchCat1");
        var catId2 = await SeedCategoryAsync("MatchCat2");
        var params1 = new List<AttributePresetParameter>
        {
            new("ParamA", "A", 0)
        };
        var params2 = new List<AttributePresetParameter>
        {
            new("ParamB", "B", 0)
        };

        await _service.CreatePresetAsync(catId1, params1);
        await _service.CreatePresetAsync(catId2, params2);

        var found = await _service.GetPresetForCategoryAsync(catId1);

        Assert.NotNull(found);
        Assert.Equal(catId1, found.CategoryId);
        Assert.Single(found.Parameters);
        Assert.Equal("ParamA", found.Parameters[0].ParameterName);
    }

    [Fact]
    public async Task GetPresetForCategoryAsync_NoMatch_ReturnsNull()
    {
        var found = await _service.GetPresetForCategoryAsync("nonexistent");
        Assert.Null(found);
    }

    [Fact]
    public async Task CreatePresetAsync_ParametersCorrectlyStored()
    {
        var catId = await SeedCategoryAsync("ParamsCat");
        var parameters = new List<AttributePresetParameter>
        {
            new("Alpha", "A Label", 0),
            new("Beta", null, 1),
            new("Gamma", "G Label", 2)
        };

        await _service.CreatePresetAsync(catId, parameters);
        var found = await _service.GetPresetForCategoryAsync(catId);

        Assert.NotNull(found);
        Assert.Equal(3, found.Parameters.Count);

        Assert.Equal("Alpha", found.Parameters[0].ParameterName);
        Assert.Equal("A Label", found.Parameters[0].DisplayName);
        Assert.Equal(0, found.Parameters[0].SortOrder);

        Assert.Equal("Beta", found.Parameters[1].ParameterName);
        Assert.Null(found.Parameters[1].DisplayName);

        Assert.Equal("Gamma", found.Parameters[2].ParameterName);
        Assert.Equal("G Label", found.Parameters[2].DisplayText);
    }

    [Fact]
    public async Task UpdatePresetAsync_ReplacesParameters()
    {
        var catId = await SeedCategoryAsync("UpdateCat");
        var original = new List<AttributePresetParameter>
        {
            new("Old1", null, 0),
            new("Old2", null, 1)
        };

        var preset = await _service.CreatePresetAsync(catId, original);

        var updatedParams = new List<AttributePresetParameter>
        {
            new("New1", "N1", 0)
        };

        await _service.UpdatePresetAsync(preset.Id, updatedParams);

        var found = await _service.GetPresetForCategoryAsync(catId);
        Assert.NotNull(found);
        Assert.Single(found.Parameters);
        Assert.Equal("New1", found.Parameters[0].ParameterName);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
