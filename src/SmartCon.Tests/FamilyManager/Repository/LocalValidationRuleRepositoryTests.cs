using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalValidationRuleRepositoryTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalValidationRuleRepository _repository;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly LocalAttributeDefinitionRepository _attributeDefRepo;
    private readonly LocalCategoryAttributeBindingService _bindingService;

    public LocalValidationRuleRepositoryTests()
    {
        _fixture = new TempCatalogFixture();

        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _attributeDefRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _bindingService = new LocalCategoryAttributeBindingService(_fixture.GetDatabase(), _categoryRepository, _fixture.GetMigrator());
        _repository = new LocalValidationRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<string> SeedBindingAsync(string categoryName = "Pipes", string attributeName = "Pressure")
    {
        var cat = await _categoryRepository.AddAsync(categoryName, null, 0);
        var attr = await _attributeDefRepo.CreateAsync(attributeName, null);
        var binding = await _bindingService.CreateBindingAsync(cat.Id, attr.Id, 0);
        return binding.Id;
    }

    private static ValidationRule NewRule(string bindingId, ValidationRuleOperator op = ValidationRuleOperator.HasValue) =>
        new(string.Empty, bindingId, op, null, null, null, null, null, 0, true);

    [Fact]
    public async Task GetRulesForBindingAsync_NoRules_ReturnsEmpty()
    {
        var bindingId = await SeedBindingAsync();

        var result = await _repository.GetRulesForBindingAsync(bindingId);

        Assert.Empty(result);
    }

    [Fact]
    public async Task CreateRuleAsync_AssignsIdAndPersists()
    {
        var bindingId = await SeedBindingAsync();

        var created = await _repository.CreateRuleAsync(NewRule(bindingId));

        Assert.False(string.IsNullOrEmpty(created.Id));

        var rules = await _repository.GetRulesForBindingAsync(bindingId);
        Assert.Single(rules);
        Assert.Equal(created.Id, rules[0].Id);
        Assert.Equal(ValidationRuleOperator.HasValue, rules[0].Operator);
        Assert.True(rules[0].IsEnabled);
    }

    [Fact]
    public async Task CreateRuleAsync_BetweenRule_PersistsMinMaxAndUnit()
    {
        var bindingId = await SeedBindingAsync();
        var rule = NewRule(bindingId, ValidationRuleOperator.Between) with
        {
            MinValue = 15.0,
            MaxValue = 100.0,
            UnitTypeId = "autodesk.unit.unit:millimeters-1.0.0",
            SortOrder = 3,
        };

        var created = await _repository.CreateRuleAsync(rule);

        var rules = await _repository.GetRulesForBindingAsync(bindingId);
        Assert.Single(rules);
        Assert.Equal(15.0, rules[0].MinValue);
        Assert.Equal(100.0, rules[0].MaxValue);
        Assert.Equal("autodesk.unit.unit:millimeters-1.0.0", rules[0].UnitTypeId);
        Assert.Equal(3, rules[0].SortOrder);
    }

    [Fact]
    public async Task CreateRuleAsync_TextRule_PersistsValueText()
    {
        var bindingId = await SeedBindingAsync();
        var rule = NewRule(bindingId, ValidationRuleOperator.Contains) with { ValueText = "AGK" };

        await _repository.CreateRuleAsync(rule);

        var rules = await _repository.GetRulesForBindingAsync(bindingId);
        Assert.Single(rules);
        Assert.Equal("AGK", rules[0].ValueText);
    }

    [Fact]
    public async Task GetRulesForBindingAsync_MultipleRules_OrderedBySortOrder()
    {
        var bindingId = await SeedBindingAsync();
        await _repository.CreateRuleAsync(NewRule(bindingId) with { SortOrder = 20 });
        await _repository.CreateRuleAsync(NewRule(bindingId, ValidationRuleOperator.IsPresent) with { SortOrder = 10 });

        var rules = await _repository.GetRulesForBindingAsync(bindingId);

        Assert.Equal(2, rules.Count);
        Assert.Equal(ValidationRuleOperator.IsPresent, rules[0].Operator);
        Assert.Equal(ValidationRuleOperator.HasValue, rules[1].Operator);
    }

    [Fact]
    public async Task GetRulesForBindingsAsync_EmptyInput_ReturnsEmpty()
    {
        var result = await _repository.GetRulesForBindingsAsync(Array.Empty<string>());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetRulesForBindingsAsync_MultipleBindings_ReturnsAllRules()
    {
        var binding1 = await SeedBindingAsync("Pipes", "Pressure");
        var binding2 = await SeedBindingAsync("Fittings", "DN");
        await _repository.CreateRuleAsync(NewRule(binding1));
        await _repository.CreateRuleAsync(NewRule(binding2, ValidationRuleOperator.Between) with { MinValue = 15.0, MaxValue = 100.0 });

        var result = await _repository.GetRulesForBindingsAsync(new[] { binding1, binding2 });

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetRuleCountsForAttributesAsync_ReturnsCountsPerAttribute()
    {
        var cat = await _categoryRepository.AddAsync("Pipes", null, 0);
        var attr1 = await _attributeDefRepo.CreateAsync("Pressure", null);
        var attr2 = await _attributeDefRepo.CreateAsync("DN", null);
        var binding1 = await _bindingService.CreateBindingAsync(cat.Id, attr1.Id, 0);
        var binding2 = await _bindingService.CreateBindingAsync(cat.Id, attr2.Id, 1);
        await _repository.CreateRuleAsync(NewRule(binding1.Id));
        await _repository.CreateRuleAsync(NewRule(binding1.Id, ValidationRuleOperator.GreaterThan) with { ValueNumber = 0.0 });
        await _repository.CreateRuleAsync(NewRule(binding2.Id));

        var counts = await _repository.GetRuleCountsForAttributesAsync(new[] { attr1.Id, attr2.Id });

        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts[attr1.Id]);
        Assert.Equal(1, counts[attr2.Id]);
    }

    [Fact]
    public async Task UpdateRuleAsync_ChangesOperatorAndValues()
    {
        var bindingId = await SeedBindingAsync();
        var created = await _repository.CreateRuleAsync(NewRule(bindingId));

        var updated = created with
        {
            Operator = ValidationRuleOperator.LessOrEqual,
            ValueNumber = 42.5,
            IsEnabled = false,
        };
        await _repository.UpdateRuleAsync(updated);

        var rules = await _repository.GetRulesForBindingAsync(bindingId);
        Assert.Single(rules);
        Assert.Equal(ValidationRuleOperator.LessOrEqual, rules[0].Operator);
        Assert.Equal(42.5, rules[0].ValueNumber);
        Assert.False(rules[0].IsEnabled);
    }

    [Fact]
    public async Task DeleteRuleAsync_RemovesRule()
    {
        var bindingId = await SeedBindingAsync();
        var created = await _repository.CreateRuleAsync(NewRule(bindingId));

        var deleted = await _repository.DeleteRuleAsync(created.Id);

        Assert.True(deleted);
        Assert.Empty(await _repository.GetRulesForBindingAsync(bindingId));
    }

    [Fact]
    public async Task DeleteRuleAsync_UnknownId_ReturnsFalse()
    {
        var deleted = await _repository.DeleteRuleAsync(Guid.NewGuid().ToString());

        Assert.False(deleted);
    }

    [Fact]
    public async Task DeleteRulesForBindingAsync_RemovesAllRulesOfBinding()
    {
        var bindingId = await SeedBindingAsync();
        await _repository.CreateRuleAsync(NewRule(bindingId));
        await _repository.CreateRuleAsync(NewRule(bindingId, ValidationRuleOperator.IsPresent));

        await _repository.DeleteRulesForBindingAsync(bindingId);

        Assert.Empty(await _repository.GetRulesForBindingAsync(bindingId));
    }

    [Fact]
    public async Task DeleteBinding_CascadeDeletesRules()
    {
        var cat = await _categoryRepository.AddAsync("Pipes", null, 0);
        var attr = await _attributeDefRepo.CreateAsync("Pressure", null);
        var binding = await _bindingService.CreateBindingAsync(cat.Id, attr.Id, 0);
        await _repository.CreateRuleAsync(NewRule(binding.Id));

        await _bindingService.DeleteBindingAsync(binding.Id);

        Assert.Empty(await _repository.GetRulesForBindingAsync(binding.Id));
    }

    [Fact]
    public async Task GetRulesForBindingAsync_UnknownOperator_RuleSkipped()
    {
        var bindingId = await SeedBindingAsync();
        var good = await _repository.CreateRuleAsync(NewRule(bindingId));

        using (var connection = _fixture.GetDatabase().CreateConnection())
        {
            await connection.OpenAsync();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "INSERT INTO category_validation_rules (id, binding_id, operator, sort_order, is_enabled) VALUES (@id, @bindingId, 'FutureOperatorV99', 99, 1)";
            cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@id", Guid.NewGuid().ToString()));
            cmd.Parameters.Add(new Microsoft.Data.Sqlite.SqliteParameter("@bindingId", bindingId));
            await cmd.ExecuteNonQueryAsync();
        }

        var rules = await _repository.GetRulesForBindingAsync(bindingId);

        Assert.Single(rules);
        Assert.Equal(good.Id, rules[0].Id);
    }
}
