using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class ValidationRulesEditorViewModelTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalValidationRuleRepository _ruleRepository;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly LocalAttributeDefinitionRepository _attributeDefRepo;
    private readonly LocalCategoryAttributeBindingService _bindingService;

    public ValidationRulesEditorViewModelTests()
    {
        _fixture = new TempCatalogFixture();
        _ruleRepository = new LocalValidationRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _attributeDefRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _bindingService = new LocalCategoryAttributeBindingService(_fixture.GetDatabase(), _categoryRepository, _fixture.GetMigrator());
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<string> SeedBindingAsync()
    {
        var cat = await _categoryRepository.AddAsync("Pipes", null, 0);
        var attr = await _attributeDefRepo.CreateAsync("Pressure", null);
        var binding = await _bindingService.CreateBindingAsync(cat.Id, attr.Id, 0);
        return binding.Id;
    }

    private ValidationRulesEditorViewModel CreateVm(string bindingId) =>
        new(bindingId, "Pressure", "Pipes", _ruleRepository);

    [Fact]
    public async Task Initialize_LoadsExistingRules()
    {
        var bindingId = await SeedBindingAsync();
        await _ruleRepository.CreateRuleAsync(
            new ValidationRule(string.Empty, bindingId, ValidationRuleOperator.Between, null, null, 15.0, 100.0, null, 0, true));

        var vm = CreateVm(bindingId);
        await vm.InitializeAsync();

        var row = Assert.Single(vm.Rules);
        Assert.Equal(ValidationRuleOperator.Between, row.Operator);
        Assert.Equal("15", row.MinValue);
        Assert.Equal("100", row.MaxValue);
        Assert.True(vm.HasRules);
    }

    [Fact]
    public async Task Save_NewRule_Persisted()
    {
        var bindingId = await SeedBindingAsync();
        var vm = CreateVm(bindingId);
        await vm.InitializeAsync();

        vm.AddRuleCommand.Execute(null);
        vm.Rules[0].Operator = ValidationRuleOperator.HasValue;

        await vm.SaveCommand.ExecuteAsync(null);

        var rules = await _ruleRepository.GetRulesForBindingAsync(bindingId);
        Assert.Single(rules);
        Assert.Equal(ValidationRuleOperator.HasValue, rules[0].Operator);
    }

    [Fact]
    public async Task Save_NumericOperator_ParsesNumber()
    {
        var bindingId = await SeedBindingAsync();
        var vm = CreateVm(bindingId);
        await vm.InitializeAsync();

        vm.AddRuleCommand.Execute(null);
        vm.Rules[0].Operator = ValidationRuleOperator.GreaterOrEqual;
        vm.Rules[0].Value = "10.5";

        await vm.SaveCommand.ExecuteAsync(null);

        var rules = await _ruleRepository.GetRulesForBindingAsync(bindingId);
        Assert.Single(rules);
        Assert.Equal(10.5, rules[0].ValueNumber);
        Assert.Null(rules[0].ValueText);
    }

    [Fact]
    public async Task Save_EqualsWithNumber_StoredAsNumber()
    {
        var bindingId = await SeedBindingAsync();
        var vm = CreateVm(bindingId);
        await vm.InitializeAsync();

        vm.AddRuleCommand.Execute(null);
        vm.Rules[0].Operator = ValidationRuleOperator.Equals;
        vm.Rules[0].Value = "50";

        await vm.SaveCommand.ExecuteAsync(null);

        var rules = await _ruleRepository.GetRulesForBindingAsync(bindingId);
        Assert.Equal(50.0, rules[0].ValueNumber);
        Assert.Null(rules[0].ValueText);
    }

    [Fact]
    public async Task Save_EqualsWithText_StoredAsText()
    {
        var bindingId = await SeedBindingAsync();
        var vm = CreateVm(bindingId);
        await vm.InitializeAsync();

        vm.AddRuleCommand.Execute(null);
        vm.Rules[0].Operator = ValidationRuleOperator.Equals;
        vm.Rules[0].Value = "AGK";

        await vm.SaveCommand.ExecuteAsync(null);

        var rules = await _ruleRepository.GetRulesForBindingAsync(bindingId);
        Assert.Equal("AGK", rules[0].ValueText);
        Assert.Null(rules[0].ValueNumber);
    }

    [Fact]
    public async Task Save_NumberRequired_ShowsError_NotSaved()
    {
        var bindingId = await SeedBindingAsync();
        var vm = CreateVm(bindingId);
        await vm.InitializeAsync();

        vm.AddRuleCommand.Execute(null);
        vm.Rules[0].Operator = ValidationRuleOperator.LessThan;
        vm.Rules[0].Value = "abc";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.StatusMessage);
        Assert.Empty(await _ruleRepository.GetRulesForBindingAsync(bindingId));
    }

    [Fact]
    public async Task Save_RangeInverted_ShowsError_NotSaved()
    {
        var bindingId = await SeedBindingAsync();
        var vm = CreateVm(bindingId);
        await vm.InitializeAsync();

        vm.AddRuleCommand.Execute(null);
        vm.Rules[0].Operator = ValidationRuleOperator.Between;
        vm.Rules[0].MinValue = "100";
        vm.Rules[0].MaxValue = "15";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.StatusMessage);
        Assert.Empty(await _ruleRepository.GetRulesForBindingAsync(bindingId));
    }

    [Fact]
    public async Task Save_DeletedRow_RemovedFromDb()
    {
        var bindingId = await SeedBindingAsync();
        var created = await _ruleRepository.CreateRuleAsync(
            new ValidationRule(string.Empty, bindingId, ValidationRuleOperator.HasValue, null, null, null, null, null, 0, true));

        var vm = CreateVm(bindingId);
        await vm.InitializeAsync();

        vm.DeleteRuleCommand.Execute(vm.Rules[0]);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(await _ruleRepository.GetRulesForBindingAsync(bindingId));
    }

    [Fact]
    public async Task Save_ExistingRule_Updated()
    {
        var bindingId = await SeedBindingAsync();
        var created = await _ruleRepository.CreateRuleAsync(
            new ValidationRule(string.Empty, bindingId, ValidationRuleOperator.HasValue, null, null, null, null, null, 0, true));

        var vm = CreateVm(bindingId);
        await vm.InitializeAsync();
        vm.Rules[0].Operator = ValidationRuleOperator.IsEmpty;

        await vm.SaveCommand.ExecuteAsync(null);

        var rules = await _ruleRepository.GetRulesForBindingAsync(bindingId);
        Assert.Single(rules);
        Assert.Equal(created.Id, rules[0].Id);
        Assert.Equal(ValidationRuleOperator.IsEmpty, rules[0].Operator);
    }
}
