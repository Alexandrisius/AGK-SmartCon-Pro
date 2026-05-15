using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalAttributeDefinitionRepositoryTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalAttributeDefinitionRepository _repo;

    public LocalAttributeDefinitionRepositoryTests()
    {
        _fixture = new TempCatalogFixture();

        _repo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task GetAllAsync_EmptyDb_ReturnsEmpty()
    {
        var result = await _repo.GetAllAsync();
        Assert.Empty(result);
    }

    [Fact]
    public async Task CreateAsync_SavesDefinition_ReturnsInGetAll()
    {
        await _repo.CreateAsync("Width", null);

        var all = await _repo.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("Width", all[0].Name);
    }

    [Fact]
    public async Task CreateAsync_WithGroup_SavesGroup()
    {
        await _repo.CreateAsync("Width", "Dimensions");

        var all = await _repo.GetAllAsync();
        Assert.Single(all);
        Assert.Equal("Dimensions", all[0].Group);
    }

    [Fact]
    public async Task CreateAsync_WithoutGroup_GroupIsNull()
    {
        await _repo.CreateAsync("Width", null);

        var all = await _repo.GetAllAsync();
        Assert.Single(all);
        Assert.Null(all[0].Group);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsDefinition()
    {
        var created = await _repo.CreateAsync("Width", null);

        var result = await _repo.GetByIdAsync(created.Id);
        Assert.NotNull(result);
        Assert.Equal(created.Id, result.Id);
        Assert.Equal("Width", result.Name);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistentId_ReturnsNull()
    {
        var result = await _repo.GetByIdAsync("nonexistent");
        Assert.Null(result);
    }

    [Fact]
    public async Task GetByNameAsync_ExistingName_ReturnsDefinition()
    {
        await _repo.CreateAsync("Width", null);

        var result = await _repo.GetByNameAsync("Width");
        Assert.NotNull(result);
        Assert.Equal("Width", result.Name);
    }

    [Fact]
    public async Task GetByNameAsync_DifferentCase_ReturnsDefinition()
    {
        await _repo.CreateAsync("Width", null);

        var result = await _repo.GetByNameAsync("WIDTH");
        Assert.NotNull(result);
        Assert.Equal("Width", result.Name);
    }

    [Fact]
    public async Task GetByNameAsync_NonExistent_ReturnsNull()
    {
        var result = await _repo.GetByNameAsync("NotExist");
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateAsync_ChangesName()
    {
        var created = await _repo.CreateAsync("Width", null);

        var updated = await _repo.UpdateAsync(created.Id, "Height", null, null);

        Assert.Equal("Height", updated.Name);
    }

    [Fact]
    public async Task UpdateAsync_DeactivatesDefinition()
    {
        var created = await _repo.CreateAsync("Width", null);

        var updated = await _repo.UpdateAsync(created.Id, null, null, false);

        Assert.False(updated.IsActive);
    }

    [Fact]
    public async Task UpdateAsync_PartialUpdate_OnlyChangesProvidedFields()
    {
        var created = await _repo.CreateAsync("Width", "Dimensions");

        var updated = await _repo.UpdateAsync(created.Id, "Height", null, null);

        Assert.Equal("Height", updated.Name);
        Assert.Equal("Dimensions", updated.Group);
    }

    [Fact]
    public async Task DeleteAsync_ExistingId_ReturnsTrue()
    {
        var created = await _repo.CreateAsync("Width", null);

        var deleted = await _repo.DeleteAsync(created.Id);

        Assert.True(deleted);
        Assert.Empty(await _repo.GetAllAsync());
    }

    [Fact]
    public async Task DeleteAsync_NonExistentId_ReturnsFalse()
    {
        var deleted = await _repo.DeleteAsync("nonexistent");
        Assert.False(deleted);
    }

    [Fact]
    public async Task NameExistsAsync_ExistingName_ReturnsTrue()
    {
        await _repo.CreateAsync("Width", null);

        var exists = await _repo.NameExistsAsync("Width", null);
        Assert.True(exists);
    }

    [Fact]
    public async Task NameExistsAsync_DifferentCase_ReturnsTrue()
    {
        await _repo.CreateAsync("Width", null);

        var exists = await _repo.NameExistsAsync("WIDTH", null);
        Assert.True(exists);
    }

    [Fact]
    public async Task NameExistsAsync_WithExcludeId_ExcludesSelf()
    {
        var created = await _repo.CreateAsync("Width", null);

        var exists = await _repo.NameExistsAsync("Width", created.Id);
        Assert.False(exists);
    }

    [Fact]
    public async Task NameExistsAsync_NonExistent_ReturnsFalse()
    {
        var exists = await _repo.NameExistsAsync("NotExist", null);
        Assert.False(exists);
    }

    [Fact]
    public async Task GetAllAsync_OrderedByName()
    {
        await _repo.CreateAsync("Zeta", null);
        await _repo.CreateAsync("Alpha", null);
        await _repo.CreateAsync("Mid", null);

        var all = await _repo.GetAllAsync();
        Assert.Equal(3, all.Count);
        Assert.Equal("Alpha", all[0].Name);
        Assert.Equal("Mid", all[1].Name);
        Assert.Equal("Zeta", all[2].Name);
    }
}
