using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalFamilyTypeRepositoryTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalFamilyTypeRepository _repository;
    private readonly LocalFamilyImportService _importService;

    public LocalFamilyTypeRepositoryTests()
    {
        _fixture = new TempCatalogFixture();
        _fixture.MigrateAsync().GetAwaiter().GetResult();

        _repository = new LocalFamilyTypeRepository(_fixture.GetDatabase());

        var hasher = new Sha256FileHasher();
        var metadataService = new FileNameOnlyMetadataExtractionService(hasher);
        _importService = new LocalFamilyImportService(
            _fixture.GetDatabase(),
            _fixture.GetMigrator(),
            _fixture.GetProvider(),
            _fixture.GetPathResolver(),
            metadataService);
    }

    private async Task<string> SeedItemAsync(string fileName)
    {
        var path = _fixture.CreateFakeRfaFile(fileName);
        var result = await _importService.ImportFileAsync(new FamilyImportRequest(path, 2025, null, null, null));
        Assert.True(result.Success);
        return result.CatalogItemId!;
    }

    [Fact]
    public async Task GetTypesForItemAsync_NoTypes_ReturnsEmptyList()
    {
        var itemId = await SeedItemAsync("NoTypes.rfa");

        var types = await _repository.GetTypesForItemAsync(itemId);

        Assert.Empty(types);
    }

    [Fact]
    public async Task SaveTypesAsync_TwoTypes_BothAppearInGet()
    {
        var itemId = await SeedItemAsync("TwoTypes.rfa");
        var types = new List<FamilyTypeDescriptor>
        {
            new("t1", itemId, "Type A", 0),
            new("t2", itemId, "Type B", 1)
        };

        await _repository.SaveTypesAsync(itemId, types);
        var result = await _repository.GetTypesForItemAsync(itemId);

        Assert.Equal(2, result.Count);
        Assert.Equal("Type A", result[0].Name);
        Assert.Equal("Type B", result[1].Name);
        Assert.Equal(itemId, result[0].CatalogItemId);
        Assert.Equal(itemId, result[1].CatalogItemId);
    }

    [Fact]
    public async Task SaveTypesAsync_CalledTwice_ReplacesExistingTypes()
    {
        var itemId = await SeedItemAsync("ReplaceTypes.rfa");

        await _repository.SaveTypesAsync(itemId, new List<FamilyTypeDescriptor>
        {
            new("old1", itemId, "Old Type 1", 0),
            new("old2", itemId, "Old Type 2", 1)
        });

        await _repository.SaveTypesAsync(itemId, new List<FamilyTypeDescriptor>
        {
            new("new1", itemId, "New Type", 0)
        });

        var result = await _repository.GetTypesForItemAsync(itemId);
        Assert.Single(result);
        Assert.Equal("New Type", result[0].Name);
    }

    [Fact]
    public async Task GetAllTypesBatchAsync_MultipleItems_AllReturned()
    {
        var itemId1 = await SeedItemAsync("BatchA.rfa");
        var itemId2 = await SeedItemAsync("BatchB.rfa");

        await _repository.SaveTypesAsync(itemId1, new List<FamilyTypeDescriptor>
        {
            new("b1", itemId1, "Batch Type A", 0)
        });
        await _repository.SaveTypesAsync(itemId2, new List<FamilyTypeDescriptor>
        {
            new("b2", itemId2, "Batch Type B1", 0),
            new("b3", itemId2, "Batch Type B2", 1)
        });

        var batch = await _repository.GetAllTypesBatchAsync(new[] { itemId1, itemId2 });

        Assert.Equal(2, batch.Count);
        Assert.Single(batch[itemId1]);
        Assert.Equal(2, batch[itemId2].Count);
    }

    [Fact]
    public async Task HasTypesAsync_NoTypes_ReturnsFalse()
    {
        var itemId = await SeedItemAsync("HasNone.rfa");

        var has = await _repository.HasTypesAsync(itemId);

        Assert.False(has);
    }

    [Fact]
    public async Task HasTypesAsync_WithTypes_ReturnsTrue()
    {
        var itemId = await SeedItemAsync("HasSome.rfa");
        await _repository.SaveTypesAsync(itemId, new List<FamilyTypeDescriptor>
        {
            new("ht1", itemId, "Has Type", 0)
        });

        var has = await _repository.HasTypesAsync(itemId);

        Assert.True(has);
    }

    [Fact]
    public async Task SaveTypesAsync_PropertiesCorrectlyStored()
    {
        var itemId = await SeedItemAsync("Props.rfa");

        await _repository.SaveTypesAsync(itemId, new List<FamilyTypeDescriptor>
        {
            new("prop-id-1", itemId, "First", 0),
            new("prop-id-2", itemId, "Second", 1)
        });

        var result = await _repository.GetTypesForItemAsync(itemId);

        Assert.Equal("prop-id-1", result[0].Id);
        Assert.Equal("prop-id-2", result[1].Id);
        Assert.Equal(0, result[0].SortOrder);
        Assert.Equal(1, result[1].SortOrder);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }
}
