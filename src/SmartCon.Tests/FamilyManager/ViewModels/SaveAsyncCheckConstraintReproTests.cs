using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.Tests.FamilyManager.Repository;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

/// <summary>
/// Reproduction of the beta.9 release incident: saving a System +
/// RevitCategory + Equals rule through the exact UI flow failed with
/// SQLite CHECK constraint (source_kind/attribute_id/system_key).
/// </summary>
public sealed class SaveAsyncCheckConstraintReproTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalAssignmentRuleRepository _ruleRepository;
    private readonly LocalAttributeDefinitionRepository _attributeRepository;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly Mock<IRevitCategoryLabelService> _labelsMock;
    private readonly FakeFamilyManagerDialogService _dialogFake = new();

    public SaveAsyncCheckConstraintReproTests()
    {
        _fixture = new TempCatalogFixture();
        _ruleRepository = new LocalAssignmentRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
        _attributeRepository = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _labelsMock = new Mock<IRevitCategoryLabelService>();
        _labelsMock.Setup(l => l.GetModelCategories()).Returns(
        [
            new RevitCategoryLabel(-2001160, "Оборудование"),
        ]);

        _attributeRepository.CreateAsync("ADSK_Материал", null).GetAwaiter().GetResult();
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<AssignmentRulesEditorViewModel> CreateVmAsync(string categoryName = "Оборудование")
    {
        var category = await _categoryRepository.AddAsync(categoryName, null, 0);
        var vm = new AssignmentRulesEditorViewModel(
            category.Id, categoryName, _ruleRepository, _attributeRepository, _labelsMock.Object, _dialogFake);
        await vm.InitializeAsync();
        return vm;
    }

    [Fact]
    public async Task Repro_SystemRevitCategory_Equals_SaveSucceeds()
    {
        // Exact user flow from the incident screenshot: fresh dialog →
        // AddGroup → switch Source to "Системное поле" → pick "Категория
        // Revit" → "Равно" → pick "Оборудование" → Сохранить.
        var vm = await CreateVmAsync();
        vm.AddGroupCommand.Execute(null);
        var condition = vm.Groups[0].Conditions[0];

        condition.SourceKindIndex = 1;
        condition.SystemField = AssignmentSystemField.RevitCategory;
        condition.Operator = ValidationRuleOperator.Equals;
        condition.ValueText = "-2001160";

        var saved = false;
        vm.RequestClose += _ => saved = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(saved);
        Assert.Equal(0, _dialogFake.ErrorCalls);
        var groups = await _ruleRepository.GetGroupsForCategoryAsync(
            (await _categoryRepository.GetAllAsync()).First(c => c.Name == "Оборудование").Id);
        var stored = Assert.Single(Assert.Single(groups).Conditions);
        Assert.Equal(AssignmentConditionSourceKind.System, stored.SourceKind);
        Assert.Equal(AssignmentSystemField.RevitCategory, stored.SystemField);
        Assert.Null(stored.AttributeId);
    }

    [Fact]
    public async Task Repro_SystemThenBackToAttribute_WithAttribute_SavesCleanCondition()
    {
        // THE INCIDENT (#248): System + RevitCategory picked, then back to
        // Attribute and an attribute chosen. Before the fix SystemField
        // survived the switch and the stored triple
        // (attribute, attribute_id, system_key) violated the DB CHECK.
        var vm = await CreateVmAsync("Смешанные");
        var attribute = vm.AvailableAttributes[0];
        vm.AddGroupCommand.Execute(null);
        var condition = vm.Groups[0].Conditions[0];

        condition.SourceKindIndex = 1;
        condition.SystemField = AssignmentSystemField.RevitCategory;
        condition.SourceKindIndex = 0;
        Assert.True(condition.IsAttributeSource);
        Assert.Null(condition.SystemField);
        condition.SelectedAttributeId = attribute.Id;
        condition.Operator = ValidationRuleOperator.Contains;
        condition.ValueText = "сталь";

        var saved = false;
        vm.RequestClose += _ => saved = true;
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(saved);
        Assert.Equal(0, _dialogFake.ErrorCalls);
        var groups = await _ruleRepository.GetGroupsForCategoryAsync(
            (await _categoryRepository.GetAllAsync()).First(c => c.Name == "Смешанные").Id);
        var stored = Assert.Single(Assert.Single(groups).Conditions);
        Assert.Equal(AssignmentConditionSourceKind.Attribute, stored.SourceKind);
        Assert.Equal(attribute.Id, stored.AttributeId);
        Assert.Null(stored.SystemField);
    }

    [Fact]
    public void SwitchToSystem_PreselectsFirstField()
    {
        // UX + consistency: the field ComboBox is never left empty in the
        // System source — the default is RevitCategory.
        var vm = CreateVmSync("Дефолт");
        vm.AddGroupCommand.Execute(null);
        var condition = vm.Groups[0].Conditions[0];

        condition.SourceKindIndex = 1;

        Assert.Equal(AssignmentSystemField.RevitCategory, condition.SystemField);
        Assert.False(condition.ShowValueField);
        Assert.True(condition.ShowCategoryPicker);
    }

    [Fact]
    public async Task SaveAsync_WritesBackCreatedIds_NoOrphanDuplicationOnRetry()
    {
        // Incident #248 follow-up: a failed save must not orphan the
        // created group — the retry path needs the persisted ids.
        var vm = await CreateVmAsync("Идемпотентность");
        vm.AddGroupCommand.Execute(null);
        var group = vm.Groups[0];
        var condition = group.Conditions[0];
        condition.SourceKindIndex = 1;
        condition.Operator = ValidationRuleOperator.Equals;
        condition.ValueText = "-2001160";

        var saved = false;
        vm.RequestClose += _ => saved = true;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(saved);

        Assert.False(string.IsNullOrEmpty(group.Id));
        Assert.False(string.IsNullOrEmpty(condition.Id));

        // A second save (e.g. user edits again) must UPDATE, not duplicate.
        await vm.SaveCommand.ExecuteAsync(null);
        var category = (await _categoryRepository.GetAllAsync()).First(c => c.Name == "Идемпотентность");
        var groups = await _ruleRepository.GetGroupsForCategoryAsync(category.Id);
        var storedGroup = Assert.Single(groups);
        Assert.Equal(group.Id, storedGroup.Id);
        Assert.Single(storedGroup.Conditions);
    }

    private AssignmentRulesEditorViewModel CreateVmSync(string categoryName)
    {
        var category = _categoryRepository.AddAsync(categoryName, null, 0).GetAwaiter().GetResult();
        var vm = new AssignmentRulesEditorViewModel(
            category.Id, categoryName, _ruleRepository, _attributeRepository, _labelsMock.Object, _dialogFake);
        vm.InitializeAsync().GetAwaiter().GetResult();
        return vm;
    }
}
