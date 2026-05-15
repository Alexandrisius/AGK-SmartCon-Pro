using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalCategoryAttributeBindingServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalCategoryAttributeBindingService _service;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly LocalAttributeDefinitionRepository _attributeDefRepo;

    public LocalCategoryAttributeBindingServiceTests()
    {
        _fixture = new TempCatalogFixture();

        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _attributeDefRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _service = new LocalCategoryAttributeBindingService(_fixture.GetDatabase(), _categoryRepository, _fixture.GetMigrator());
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<string> SeedCategoryAsync(string name, string? parentId = null)
    {
        var node = await _categoryRepository.AddAsync(name, parentId, 0);
        return node.Id;
    }

    private async Task<string> SeedAttributeAsync(string name, string? group = null)
    {
        var def = await _attributeDefRepo.CreateAsync(name, group);
        return def.Id;
    }

    [Fact]
    public async Task GetBindingsForCategoryAsync_NoBindings_ReturnsEmpty()
    {
        var catId = await SeedCategoryAsync("Pipes");

        var result = await _service.GetBindingsForCategoryAsync(catId);

        Assert.Empty(result);
    }

    [Fact]
    public async Task CreateBindingAsync_SavesBinding_ReturnsInGetDirect()
    {
        var catId = await SeedCategoryAsync("Pipes");
        var attrId = await SeedAttributeAsync("Width");

        var created = await _service.CreateBindingAsync(catId, attrId, 0);

        Assert.Equal(catId, created.CategoryId);
        Assert.Equal(attrId, created.AttributeId);
        Assert.True(created.IsEnabled);

        var direct = await _service.GetDirectBindingsAsync(catId);
        Assert.Single(direct);
        Assert.Equal(created.Id, direct[0].Id);
    }

    [Fact]
    public async Task CreateBindingAsync_MultipleBindings_OrderedBySortOrder()
    {
        var catId = await SeedCategoryAsync("Pipes");
        var attr1 = await SeedAttributeAsync("Width");
        var attr2 = await SeedAttributeAsync("Height");
        var attr3 = await SeedAttributeAsync("Length");

        await _service.CreateBindingAsync(catId, attr3, 30);
        await _service.CreateBindingAsync(catId, attr1, 10);
        await _service.CreateBindingAsync(catId, attr2, 20);

        var result = await _service.GetDirectBindingsAsync(catId);
        Assert.Equal(3, result.Count);
        Assert.Equal(attr1, result[0].AttributeId);
        Assert.Equal(attr2, result[1].AttributeId);
        Assert.Equal(attr3, result[2].AttributeId);
    }

    [Fact]
    public async Task DeleteBindingAsync_ExistingId_ReturnsTrue()
    {
        var catId = await SeedCategoryAsync("Pipes");
        var attrId = await SeedAttributeAsync("Width");
        var created = await _service.CreateBindingAsync(catId, attrId, 0);

        var deleted = await _service.DeleteBindingAsync(created.Id);

        Assert.True(deleted);
        var remaining = await _service.GetDirectBindingsAsync(catId);
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task DeleteBindingAsync_NonExistentId_ReturnsFalse()
    {
        var deleted = await _service.DeleteBindingAsync("nonexistent");
        Assert.False(deleted);
    }

    [Fact]
    public async Task UpdateBindingAsync_ChangesSortOrder()
    {
        var catId = await SeedCategoryAsync("Pipes");
        var attrId = await SeedAttributeAsync("Width");
        var created = await _service.CreateBindingAsync(catId, attrId, 0);

        var updated = await _service.UpdateBindingAsync(created.Id, sortOrder: 42, isEnabled: null);

        Assert.Equal(42, updated.SortOrder);
    }

    [Fact]
    public async Task UpdateBindingAsync_DisablesBinding()
    {
        var catId = await SeedCategoryAsync("Pipes");
        var attrId = await SeedAttributeAsync("Width");
        var created = await _service.CreateBindingAsync(catId, attrId, 0);

        var updated = await _service.UpdateBindingAsync(created.Id, sortOrder: null, isEnabled: false);

        Assert.False(updated.IsEnabled);
    }

    [Fact]
    public async Task GetEffectiveAttributesAsync_NullCategoryId_ReturnsEmpty()
    {
        var attrId = await SeedAttributeAsync("Material");
        var catId = await SeedCategoryAsync("Pipes");
        await _service.CreateBindingAsync(catId, attrId, 1);

        var result = await _service.GetEffectiveAttributesAsync(null);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetEffectiveAttributesAsync_ChildInheritsParentBindings()
    {
        var attrId = await SeedAttributeAsync("Width");
        var rootId = await SeedCategoryAsync("Root");
        var childId = await SeedCategoryAsync("Child", rootId);
        await _service.CreateBindingAsync(rootId, attrId, 0);

        var result = await _service.GetEffectiveAttributesAsync(childId);

        Assert.Single(result);
        Assert.Equal(attrId, result[0].AttributeId);
        Assert.True(result[0].IsInherited);
        Assert.Equal(rootId, result[0].SourceCategoryId);
    }

    [Fact]
    public async Task GetEffectiveAttributesAsync_ChildOverridesParentBinding()
    {
        var attrId = await SeedAttributeAsync("Width");
        var rootId = await SeedCategoryAsync("Root");
        var childId = await SeedCategoryAsync("Child", rootId);
        await _service.CreateBindingAsync(rootId, attrId, 1);
        await _service.CreateBindingAsync(childId, attrId, 5);

        var result = await _service.GetEffectiveAttributesAsync(childId);

        Assert.Single(result);
        Assert.Equal(attrId, result[0].AttributeId);
        Assert.False(result[0].IsInherited);
        Assert.Equal(childId, result[0].SourceCategoryId);
        Assert.Equal(5, result[0].SortOrder);
    }

    [Fact]
    public async Task GetEffectiveAttributesAsync_GrandchildInheritsFromAllAncestors()
    {
        var attrA = await SeedAttributeAsync("A");
        var attrB = await SeedAttributeAsync("B");
        var attrC = await SeedAttributeAsync("C");
        var rootId = await SeedCategoryAsync("Root");
        var midId = await SeedCategoryAsync("Mid", rootId);
        var leafId = await SeedCategoryAsync("Leaf", midId);

        await _service.CreateBindingAsync(rootId, attrA, 1);
        await _service.CreateBindingAsync(midId, attrB, 2);
        await _service.CreateBindingAsync(leafId, attrC, 3);

        var result = await _service.GetEffectiveAttributesAsync(leafId);

        Assert.Equal(3, result.Count);

        var a = result.First(r => r.AttributeId == attrA);
        Assert.True(a.IsInherited);
        Assert.Equal(rootId, a.SourceCategoryId);

        var b = result.First(r => r.AttributeId == attrB);
        Assert.True(b.IsInherited);
        Assert.Equal(midId, b.SourceCategoryId);

        var c = result.First(r => r.AttributeId == attrC);
        Assert.False(c.IsInherited);
        Assert.Equal(leafId, c.SourceCategoryId);
    }

    [Fact]
    public async Task GetEffectiveAttributesAsync_DisabledBindingNotFilteredButReturned()
    {
        var attrId = await SeedAttributeAsync("Width");
        var catId = await SeedCategoryAsync("Pipes");
        var created = await _service.CreateBindingAsync(catId, attrId, 0);
        await _service.UpdateBindingAsync(created.Id, sortOrder: null, isEnabled: false);

        var result = await _service.GetEffectiveAttributesAsync(catId);

        Assert.Single(result);
        Assert.Equal(attrId, result[0].AttributeId);
        Assert.False(result[0].IsEnabled);
    }

    [Fact]
    public async Task GetEffectiveAttributesAsync_ThreeLevelChain_ChildOverridesMiddleSortOrder()
    {
        var attrId = await SeedAttributeAsync("A");
        var rootId = await SeedCategoryAsync("Root");
        var midId = await SeedCategoryAsync("Mid", rootId);
        var childId = await SeedCategoryAsync("Child", midId);

        await _service.CreateBindingAsync(rootId, attrId, 1);
        await _service.CreateBindingAsync(midId, attrId, 5);

        var result = await _service.GetEffectiveAttributesAsync(childId);

        Assert.Single(result);
        Assert.Equal(attrId, result[0].AttributeId);
        Assert.True(result[0].IsInherited);
        Assert.Equal(midId, result[0].SourceCategoryId);
        Assert.Equal(5, result[0].SortOrder);
    }

    [Fact]
    public async Task GetEffectiveAttributesAsync_EmptyCategory_ReturnsEmpty()
    {
        var catId = await SeedCategoryAsync("Empty");

        var result = await _service.GetEffectiveAttributesAsync(catId);

        Assert.Empty(result);
    }
}
