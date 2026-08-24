using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalAssignmentRuleRepositoryTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalAssignmentRuleRepository _repository;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly LocalAttributeDefinitionRepository _attributeDefRepo;

    public LocalAssignmentRuleRepositoryTests()
    {
        _fixture = new TempCatalogFixture();
        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _attributeDefRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _repository = new LocalAssignmentRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<string> SeedCategoryAsync(string name = "Фитинги")
    {
        var cat = await _categoryRepository.AddAsync(name, null, 0);
        return cat.Id;
    }

    private async Task<string> SeedAttributeAsync(string name = "ADSK_Материал")
    {
        var attr = await _attributeDefRepo.CreateAsync(name, null);
        return attr.Id;
    }

    [Fact]
    public async Task CreateGroupAsync_AssignsIdAndIncrementingSortOrder()
    {
        var categoryId = await SeedCategoryAsync();

        var first = await _repository.CreateGroupAsync(categoryId);
        var second = await _repository.CreateGroupAsync(categoryId);

        Assert.False(string.IsNullOrEmpty(first.Id));
        Assert.Equal(0, first.SortOrder);
        Assert.True(first.IsEnabled);
        Assert.Empty(first.Conditions);
        Assert.Equal(1, second.SortOrder);
    }

    [Fact]
    public async Task CreateConditionAsync_AttributeKind_PersistsAllFields()
    {
        var categoryId = await SeedCategoryAsync();
        var attributeId = await SeedAttributeAsync();
        var group = await _repository.CreateGroupAsync(categoryId);

        var condition = await _repository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.Attribute, attributeId, null,
            ValidationRuleOperator.Contains, "сталь", null, null, null, true);

        Assert.False(string.IsNullOrEmpty(condition.Id));
        Assert.Equal(group.Id, condition.GroupId);
        Assert.Equal(0, condition.SortOrder);

        var loaded = await _repository.GetGroupsForCategoryAsync(categoryId);
        var loadedCondition = Assert.Single(loaded.Single().Conditions);
        Assert.Equal(AssignmentConditionSourceKind.Attribute, loadedCondition.SourceKind);
        Assert.Equal(attributeId, loadedCondition.AttributeId);
        Assert.Null(loadedCondition.SystemField);
        Assert.Equal(ValidationRuleOperator.Contains, loadedCondition.Operator);
        Assert.Equal("сталь", loadedCondition.ValueText);
        Assert.True(loadedCondition.IsEnabled);
    }

    [Fact]
    public async Task CreateConditionAsync_SystemKind_PersistsSystemKey()
    {
        var categoryId = await SeedCategoryAsync();
        var group = await _repository.CreateGroupAsync(categoryId);

        await _repository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.System, null, AssignmentSystemField.RevitCategory,
            ValidationRuleOperator.Equals, "-2008049", null, null, null, true);

        var loaded = await _repository.GetGroupsForCategoryAsync(categoryId);
        var condition = Assert.Single(loaded.Single().Conditions);
        Assert.Equal(AssignmentConditionSourceKind.System, condition.SourceKind);
        Assert.Null(condition.AttributeId);
        Assert.Equal(AssignmentSystemField.RevitCategory, condition.SystemField);
        Assert.Equal("-2008049", condition.ValueText);
    }

    [Fact]
    public async Task GetGroupsWithConditionsAsync_ReturnsAllCategories()
    {
        var catA = await SeedCategoryAsync("A");
        var catB = await SeedCategoryAsync("B");
        await _repository.CreateGroupAsync(catA);
        await _repository.CreateGroupAsync(catB);

        var groups = await _repository.GetGroupsWithConditionsAsync();

        Assert.Equal(2, groups.Count);
        // Cross-category read order follows category_id (storage order);
        // evaluation priority is driven by group SortOrder, not this list.
        Assert.Contains(groups, g => g.CategoryId == catA);
        Assert.Contains(groups, g => g.CategoryId == catB);
    }

    [Fact]
    public async Task GetEnabledGroupCountsAsync_CountsOnlyEnabled()
    {
        var categoryId = await SeedCategoryAsync();
        var enabled = await _repository.CreateGroupAsync(categoryId);
        var disabled = await _repository.CreateGroupAsync(categoryId);
        await _repository.UpdateGroupAsync(disabled.Id, null, false);

        var counts = await _repository.GetEnabledGroupCountsAsync();

        Assert.Equal(1, counts[categoryId]);
        Assert.True(disabled.Id != enabled.Id);
    }

    [Fact]
    public async Task UpdateConditionAsync_ChangesFields()
    {
        var categoryId = await SeedCategoryAsync();
        var attributeId = await SeedAttributeAsync();
        var group = await _repository.CreateGroupAsync(categoryId);
        var condition = await _repository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.Attribute, attributeId, null,
            ValidationRuleOperator.Contains, "сталь", null, null, null, true);

        await _repository.UpdateConditionAsync(condition with
        {
            Operator = ValidationRuleOperator.Between,
            ValueText = null,
            ValueNumber = 5,
            MinValue = 1,
            MaxValue = 10,
            IsEnabled = false,
        });

        var loaded = (await _repository.GetGroupsForCategoryAsync(categoryId)).Single().Conditions.Single();
        Assert.Equal(ValidationRuleOperator.Between, loaded.Operator);
        Assert.Equal(5, loaded.ValueNumber);
        Assert.Equal(1, loaded.MinValue);
        Assert.Equal(10, loaded.MaxValue);
        Assert.False(loaded.IsEnabled);
    }

    [Fact]
    public async Task UpdateGroupAsync_NullFields_KeepStoredValues()
    {
        var categoryId = await SeedCategoryAsync();
        var group = await _repository.CreateGroupAsync(categoryId);
        var second = await _repository.CreateGroupAsync(categoryId);

        // Partial update: toggling IsEnabled must not touch SortOrder.
        await _repository.UpdateGroupAsync(second.Id, null, false);

        var loaded = await _repository.GetGroupsForCategoryAsync(categoryId);
        var loadedSecond = loaded.Single(g => g.Id == second.Id);
        Assert.False(loadedSecond.IsEnabled);
        Assert.Equal(1, loadedSecond.SortOrder);

        // Partial update: null for both fields is a no-op existence check.
        var exists = await _repository.UpdateGroupAsync(group.Id, null, null);
        Assert.True(exists);
        var missing = await _repository.UpdateGroupAsync("no-such-group", null, null);
        Assert.False(missing);
    }

    [Fact]
    public async Task DeleteGroupAsync_CascadesConditions()
    {
        var categoryId = await SeedCategoryAsync();
        var attributeId = await SeedAttributeAsync();
        var group = await _repository.CreateGroupAsync(categoryId);
        await _repository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.Attribute, attributeId, null,
            ValidationRuleOperator.HasValue, null, null, null, null, true);

        var deleted = await _repository.DeleteGroupAsync(group.Id);

        Assert.True(deleted);
        Assert.Empty(await _repository.GetGroupsForCategoryAsync(categoryId));
        Assert.Empty(await GetAllConditionIdsAsync());
    }

    [Fact]
    public async Task DeleteCategoryAsync_CascadesGroups()
    {
        var categoryId = await SeedCategoryAsync();
        await _repository.CreateGroupAsync(categoryId);

        await _categoryRepository.DeleteAsync(categoryId);

        Assert.Empty(await _repository.GetGroupsWithConditionsAsync());
    }

    [Fact]
    public async Task DeleteAttributeAsync_CascadesConditions()
    {
        var categoryId = await SeedCategoryAsync();
        var attributeId = await SeedAttributeAsync();
        var group = await _repository.CreateGroupAsync(categoryId);
        await _repository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.Attribute, attributeId, null,
            ValidationRuleOperator.HasValue, null, null, null, null, true);

        await _attributeDefRepo.DeleteAsync(attributeId);

        var loaded = await _repository.GetGroupsForCategoryAsync(categoryId);
        Assert.Single(loaded);
        Assert.Empty(loaded.Single().Conditions);
    }

    [Fact]
    public async Task CreateConditionAsync_AttributeKindWithoutAttributeId_ViolatesCheckConstraint()
    {
        var categoryId = await SeedCategoryAsync();
        var group = await _repository.CreateGroupAsync(categoryId);

        await Assert.ThrowsAsync<SqliteException>(() => _repository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.Attribute, null, null,
            ValidationRuleOperator.Equals, "x", null, null, null, true));
    }

    [Fact]
    public async Task CreateConditionAsync_SystemKindWithAttributeId_ViolatesCheckConstraint()
    {
        var categoryId = await SeedCategoryAsync();
        var attributeId = await SeedAttributeAsync();
        var group = await _repository.CreateGroupAsync(categoryId);

        await Assert.ThrowsAsync<SqliteException>(() => _repository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.System, attributeId, AssignmentSystemField.PartType,
            ValidationRuleOperator.Equals, "5", null, null, null, true));
    }

    [Fact]
    public async Task GetGroupsWithConditionsAsync_UnknownOperator_SkipsCondition()
    {
        var categoryId = await SeedCategoryAsync();
        var attributeId = await SeedAttributeAsync();
        var group = await _repository.CreateGroupAsync(categoryId);
        await _repository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.Attribute, attributeId, null,
            ValidationRuleOperator.Equals, "x", null, null, null, true);

        using (var connection = _fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE category_assignment_conditions SET operator = 'NotAnOperator'";
            await cmd.ExecuteNonQueryAsync();
        }

        var loaded = await _repository.GetGroupsForCategoryAsync(categoryId);

        Assert.Single(loaded);
        Assert.Empty(loaded.Single().Conditions);
    }

    [Fact]
    public async Task GetGroupsWithConditionsAsync_DisallowedOperator_SkipsCondition()
    {
        var categoryId = await SeedCategoryAsync();
        var attributeId = await SeedAttributeAsync();
        var group = await _repository.CreateGroupAsync(categoryId);
        await _repository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.Attribute, attributeId, null,
            ValidationRuleOperator.Equals, "x", null, null, null, true);

        using (var connection = _fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE category_assignment_conditions SET operator = 'NotEquals'";
            await cmd.ExecuteNonQueryAsync();
        }

        var loaded = await _repository.GetGroupsForCategoryAsync(categoryId);

        Assert.Empty(loaded.Single().Conditions);
    }

    private async Task<List<string>> GetAllConditionIdsAsync()
    {
        var ids = new List<string>();
        using var connection = _fixture.GetDatabase().CreateConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT id FROM category_assignment_conditions";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }

        return ids;
    }
}
