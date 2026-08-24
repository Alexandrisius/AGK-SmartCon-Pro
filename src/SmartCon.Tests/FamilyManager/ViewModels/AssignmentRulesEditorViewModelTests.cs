using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class AssignmentRulesEditorViewModelTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalAssignmentRuleRepository _ruleRepository;
    private readonly LocalAttributeDefinitionRepository _attributeRepository;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly Mock<IRevitCategoryLabelService> _labelsMock;

    public AssignmentRulesEditorViewModelTests()
    {
        _fixture = new TempCatalogFixture();
        _ruleRepository = new LocalAssignmentRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
        _attributeRepository = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _labelsMock = new Mock<IRevitCategoryLabelService>();
        _labelsMock.Setup(l => l.GetModelCategories()).Returns(
        [
            new RevitCategoryLabel(-2008049, "Фитинги трубопроводов"),
            new RevitCategoryLabel(-2008010, "Фитинги воздуховодов"),
        ]);

        _attributeRepository.CreateAsync("ADSK_Материал", null).GetAwaiter().GetResult();
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<AssignmentRulesEditorViewModel> CreateVmAsync(string categoryName = "Фитинги")
    {
        var category = await _categoryRepository.AddAsync(categoryName, null, 0);
        var vm = new AssignmentRulesEditorViewModel(
            category.Id, categoryName, _ruleRepository, _attributeRepository, _labelsMock.Object);
        await vm.InitializeAsync();
        return vm;
    }

    [Fact]
    public async Task Initialize_NoGroups_EmptyGroups()
    {
        var vm = await CreateVmAsync();

        Assert.Empty(vm.Groups);
        Assert.NotEmpty(vm.AvailableAttributes);
        Assert.NotEmpty(vm.RevitCategories);
        Assert.NotEmpty(vm.PartTypes);
        Assert.Equal(3, vm.SystemFields.Count);
    }

    [Fact]
    public async Task Initialize_ExistingGroups_LoadsConditions()
    {
        var category = await _categoryRepository.AddAsync("Стальные", null, 0);
        var attribute = await _attributeRepository.GetByNameAsync("ADSK_Материал");
        var group = await _ruleRepository.CreateGroupAsync(category.Id);
        await _ruleRepository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.Attribute, attribute!.Id, null,
            ValidationRuleOperator.Contains, "сталь", null, null, null, true);

        var vm = new AssignmentRulesEditorViewModel(
            category.Id, "Стальные", _ruleRepository, _attributeRepository, _labelsMock.Object);
        await vm.InitializeAsync();

        var loadedGroup = Assert.Single(vm.Groups);
        Assert.True(loadedGroup.IsEnabled);
        var condition = Assert.Single(loadedGroup.Conditions);
        Assert.Equal(attribute.Id, condition.SelectedAttributeId);
        Assert.Equal(ValidationRuleOperator.Contains, condition.Operator);
        Assert.Equal("сталь", condition.ValueText);
    }

    [Fact]
    public async Task SaveAsync_IsPresentCondition_SavesWithoutValue()
    {
        // Audit #241: IsPresent/HasValue need no value — the engine
        // evaluates them on the parameter itself; the editor must not
        // block saving with "value required".
        var vm = await CreateVmAsync("Наличие");
        var attribute = vm.AvailableAttributes[0];
        vm.AddGroupCommand.Execute(null);
        var condition = vm.Groups[0].Conditions[0];
        condition.SelectedAttributeId = attribute.Id;
        condition.Operator = ValidationRuleOperator.IsPresent;
        Assert.False(condition.ShowValueField);

        var saved = false;
        vm.RequestClose += _ => saved = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(saved);
        Assert.Empty(vm.StatusMessage);
        var groups = await _ruleRepository.GetGroupsForCategoryAsync(
            (await _categoryRepository.GetAllAsync()).First(c => c.Name == "Наличие").Id);
        var stored = Assert.Single(Assert.Single(groups).Conditions);
        Assert.Equal(ValidationRuleOperator.IsPresent, stored.Operator);
        Assert.Null(stored.ValueText);
    }

    [Fact]
    public async Task SaveAsync_NewGroup_PersistsGroupAndCondition()
    {
        var vm = await CreateVmAsync("Новые");
        var attribute = vm.AvailableAttributes[0];
        vm.AddGroupCommand.Execute(null);
        var group = vm.Groups[0];
        var condition = group.Conditions[0];
        condition.SelectedAttributeId = attribute.Id;
        condition.Operator = ValidationRuleOperator.Contains;
        condition.ValueText = "сталь";

        var saved = false;
        vm.RequestClose += _ => saved = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(saved);
        Assert.Empty(vm.StatusMessage);
        var groups = await _ruleRepository.GetGroupsForCategoryAsync(
            (await _categoryRepository.GetAllAsync()).First(c => c.Name == "Новые").Id);
        var persisted = Assert.Single(groups);
        var persistedCondition = Assert.Single(persisted.Conditions);
        Assert.Equal(attribute.Id, persistedCondition.AttributeId);
        Assert.Equal("сталь", persistedCondition.ValueText);
    }

    [Fact]
    public async Task SaveAsync_SystemRevitCategoryCondition_PersistsOrdinal()
    {
        var vm = await CreateVmAsync("По категории");
        vm.AddGroupCommand.Execute(null);
        var condition = vm.Groups[0].Conditions[0];
        condition.SourceKindIndex = 1;
        condition.SystemField = AssignmentSystemField.RevitCategory;
        condition.Operator = ValidationRuleOperator.Equals;
        condition.ValueText = "-2008049";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(vm.StatusMessage);
        var categoryId = (await _categoryRepository.GetAllAsync()).First(c => c.Name == "По категории").Id;
        var persisted = Assert.Single(await _ruleRepository.GetGroupsForCategoryAsync(categoryId));
        var persistedCondition = Assert.Single(persisted.Conditions);
        Assert.Equal(AssignmentConditionSourceKind.System, persistedCondition.SourceKind);
        Assert.Equal(AssignmentSystemField.RevitCategory, persistedCondition.SystemField);
        Assert.Equal("-2008049", persistedCondition.ValueText);
    }

    [Fact]
    public async Task SaveAsync_NoGroups_SavesEmptyAndCloses()
    {
        // Deleting all groups and saving is the supported way to remove
        // all rules — the category returns to the no-rules state.
        var vm = await CreateVmAsync("Пустые");
        var closed = false;
        vm.RequestClose += _ => closed = true;

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(closed);
        Assert.Empty(vm.StatusMessage);
    }

    [Fact]
    public async Task SaveAsync_AllGroupsDeleted_RulesRemovedFromDatabase()
    {
        var category = await _categoryRepository.AddAsync("Удаление", null, 0);
        var attribute = await _attributeRepository.GetByNameAsync("ADSK_Материал");
        var group = await _ruleRepository.CreateGroupAsync(category.Id);
        await _ruleRepository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.Attribute, attribute!.Id, null,
            ValidationRuleOperator.Contains, "сталь", null, null, null, true);

        var vm = new AssignmentRulesEditorViewModel(
            category.Id, "Удаление", _ruleRepository, _attributeRepository, _labelsMock.Object);
        await vm.InitializeAsync();

        vm.DeleteGroupCommand.Execute(vm.Groups[0]);
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(await _ruleRepository.GetGroupsForCategoryAsync(category.Id));
    }

    [Fact]
    public async Task SaveAsync_EmptyGroup_IsDropped()
    {
        var vm = await CreateVmAsync("Пустая группа");
        vm.AddGroupCommand.Execute(null);
        vm.DeleteConditionCommand.Execute(vm.Groups[0].Conditions[0]);
        vm.AddGroupCommand.Execute(null);
        var condition = vm.Groups[1].Conditions[0];
        condition.SourceKindIndex = 1;
        condition.SystemField = AssignmentSystemField.PartType;
        condition.ValueText = "5";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Empty(vm.StatusMessage);
        var categoryId = (await _categoryRepository.GetAllAsync()).First(c => c.Name == "Пустая группа").Id;
        var persisted = Assert.Single(await _ruleRepository.GetGroupsForCategoryAsync(categoryId));
        Assert.Single(persisted.Conditions);
    }

    [Fact]
    public async Task SourceKindSwitch_ClearsStoredValue()
    {
        var vm = await CreateVmAsync("Сброс значения");
        vm.AddGroupCommand.Execute(null);
        var condition = vm.Groups[0].Conditions[0];

        condition.SourceKindIndex = 1;
        condition.SystemField = AssignmentSystemField.RevitCategory;
        condition.ValueText = "-2008049";

        condition.SystemField = AssignmentSystemField.FamilyName;

        Assert.Null(condition.ValueText);
    }

    [Fact]
    public async Task SaveAsync_AttributeConditionWithoutAttribute_ShowsError()
    {
        var vm = await CreateVmAsync("Без атрибута");
        vm.AddGroupCommand.Execute(null);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.NotEmpty(vm.StatusMessage);
    }

    [Fact]
    public async Task SaveAsync_DeletedGroup_IsRemovedFromDatabase()
    {
        var category = await _categoryRepository.AddAsync("С удалением", null, 0);
        await _ruleRepository.CreateGroupAsync(category.Id);
        var vm = new AssignmentRulesEditorViewModel(
            category.Id, "С удалением", _ruleRepository, _attributeRepository, _labelsMock.Object);
        await vm.InitializeAsync();

        vm.DeleteGroupCommand.Execute(vm.Groups[0]);
        vm.AddGroupCommand.Execute(null);
        var condition = vm.Groups[0].Conditions[0];
        condition.SourceKindIndex = 1;
        condition.SystemField = AssignmentSystemField.PartType;
        condition.ValueText = "5";
        await vm.SaveCommand.ExecuteAsync(null);

        var groups = await _ruleRepository.GetGroupsForCategoryAsync(category.Id);
        var group = Assert.Single(groups);
        var persisted = Assert.Single(group.Conditions);
        Assert.Equal(AssignmentSystemField.PartType, persisted.SystemField);
        Assert.Equal("5", persisted.ValueText);
    }

    [Fact]
    public async Task SourceKindSwitch_ResetsOperatorToAllowed()
    {
        var vm = await CreateVmAsync("Операторы");
        vm.AddGroupCommand.Execute(null);
        var condition = vm.Groups[0].Conditions[0];

        Assert.Equal(ValidationRuleOperator.Contains, condition.Operator);
        condition.Operator = ValidationRuleOperator.Between;
        condition.SourceKindIndex = 1;
        condition.SystemField = AssignmentSystemField.RevitCategory;

        // Contains is not allowed for the RevitCategory ordinal field —
        // the row must reset the operator to an allowed one.
        Assert.DoesNotContain(condition.AvailableOperators, o => o.Operator == ValidationRuleOperator.Contains);
        Assert.Contains(condition.AvailableOperators, o => o.Operator == ValidationRuleOperator.NotEquals);
    }

    [Fact]
    public async Task SaveAsync_DisabledGroupToggle_PersistsEnabledFlag()
    {
        var category = await _categoryRepository.AddAsync("Тогл", null, 0);
        var group = await _ruleRepository.CreateGroupAsync(category.Id);
        var attribute = await _attributeRepository.GetByNameAsync("ADSK_Материал");
        await _ruleRepository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.Attribute, attribute!.Id, null,
            ValidationRuleOperator.Contains, "сталь", null, null, null, true);

        var vm = new AssignmentRulesEditorViewModel(
            category.Id, "Тогл", _ruleRepository, _attributeRepository, _labelsMock.Object);
        await vm.InitializeAsync();

        vm.Groups[0].IsEnabled = false;
        await vm.SaveCommand.ExecuteAsync(null);

        var loaded = (await _ruleRepository.GetGroupsForCategoryAsync(category.Id)).Single();
        Assert.False(loaded.IsEnabled);
        Assert.Equal(0, loaded.SortOrder);
    }
}
