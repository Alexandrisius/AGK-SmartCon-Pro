using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.ViewModels;
using Xunit;

namespace SmartCon.Tests.FamilyManager.ViewModels;

public sealed class AdvancedSearchViewModelTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly Mock<ICategoryRepository> _categoryRepo = new();
    private readonly Mock<ICategoryAttributeBindingService> _bindingService = new();
    private readonly Mock<IAttributeDefinitionRepository> _attrDefRepo = new();
    private readonly Mock<IAttributeValueRepository> _valueRepo = new();
    private readonly Mock<IRevitCategoryLabelService> _revitCategoryLabels = new();
    private readonly Mock<IFamilyManagerDialogService> _dialogService = new();

    public AdvancedSearchViewModelTests()
    {
        _categoryRepo
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<CategoryNode>
            {
                new("cat-1", "Фитинги", null, 0, "Фитинги", Now),
                new("cat-2", "Оборудование", null, 1, "Оборудование", Now),
            });
        _attrDefRepo
            .Setup(r => r.GetAllAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AttributeDefinition>
            {
                new("attr-1", "Производитель", null, true, Now),
                new("attr-2", "Диаметр", null, true, Now),
                new("attr-inactive", "Старый", null, false, Now),
            });
        _bindingService
            .Setup(s => s.GetEffectiveAttributesAsync("cat-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EffectiveCategoryAttribute>
            {
                new("attr-1", "Производитель", null, 0, true, false, null, null),
                new("attr-2", "Диаметр", null, 1, false, false, null, null), // disabled binding
                new("attr-inactive", "Старый", null, 2, true, false, null, null),
            });
        _bindingService
            .Setup(s => s.GetEffectiveAttributesAsync("cat-2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<EffectiveCategoryAttribute>
            {
                new("attr-2", "Диаметр", null, 0, true, true, "cat-1", null),
            });
        _revitCategoryLabels
            .Setup(r => r.GetModelCategories())
            .Returns(new List<RevitCategoryLabel> { new(-2008049, "Трубопроводные фитинги") });
        // Default: no stored suggestions (tests that need them set it up
        // explicitly); Moq's loose default would return a null list.
        _valueRepo
            .Setup(r => r.GetDistinctValueTextsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string>());
    }

    private AdvancedSearchViewModel CreateSut() =>
        new(_categoryRepo.Object, _bindingService.Object, _attrDefRepo.Object, _valueRepo.Object,
            _revitCategoryLabels.Object, _dialogService.Object);

    private AdvancedSearchConditionViewModel AddValidAttributeCondition(AdvancedSearchViewModel sut)
    {
        sut.AddConditionCommand.Execute(null);
        var row = sut.Conditions[0];
        row.SelectedAttributeId = "attr-1";
        row.Operator = ValidationRuleOperator.HasValue;
        return row;
    }

    [Fact]
    public async Task InitializeAsync_Empty_AllCategoriesSelectedByDefault()
    {
        var sut = CreateSut();

        await sut.InitializeAsync(null);

        Assert.Null(sut.SelectedCategoryId);
        Assert.Equal("Все категории", sut.SelectedCategoryPath);
        Assert.True(sut.HasNoConditions);
        Assert.True(sut.CanApply);
        Assert.Equal("Трубопроводные фитинги", Assert.Single(sut.RevitCategories).Label);
        Assert.True(sut.PartTypes.Count > 0);
    }

    [Fact]
    public async Task InitializeAsync_NoCategory_ShowsAllActiveDefinitions()
    {
        var sut = CreateSut();

        await sut.InitializeAsync(null);

        // Inactive definitions never appear, regardless of bindings. The
        // display-name sort order is culture-dependent — assert the set.
        Assert.Equal(
            new[] { "attr-1", "attr-2" },
            sut.AvailableAttributes.Select(a => a.AttributeId).OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public async Task InitializeAsync_CategorySelected_EffectiveAttributesOnly()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);

        sut.SelectedCategoryId = "cat-1";
        await sut.LoadAttributesForCurrentCategoryAsync();

        // Disabled binding + inactive definition are filtered out.
        Assert.Equal(["attr-1"], sut.AvailableAttributes.Select(a => a.AttributeId).ToList());
        Assert.Equal("Фитинги", sut.SelectedCategoryPath);
    }

    [Fact]
    public async Task PickCategory_AppliesPickerResult()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        _dialogService
            .Setup(d => d.ShowCategoryPicker(It.IsAny<object>()))
            .Returns("cat-2");

        await sut.PickCategoryCommand.ExecuteAsync(null);
        await sut.LoadAttributesForCurrentCategoryAsync();

        Assert.Equal("cat-2", sut.SelectedCategoryId);
        Assert.Equal("Оборудование", sut.SelectedCategoryPath);
        _dialogService.Verify(d => d.ShowCategoryPicker(It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public async Task PickCategory_EmptyResult_ClearsToAllCategories()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        sut.SelectedCategoryId = "cat-1";
        _dialogService
            .Setup(d => d.ShowCategoryPicker(It.IsAny<object>()))
            .Returns("");

        await sut.PickCategoryCommand.ExecuteAsync(null);

        Assert.Null(sut.SelectedCategoryId);
    }

    [Fact]
    public async Task PickCategory_Cancelled_KeepsCurrentCategory()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        sut.SelectedCategoryId = "cat-1";
        _dialogService
            .Setup(d => d.ShowCategoryPicker(It.IsAny<object>()))
            .Returns((string?)null);

        await sut.PickCategoryCommand.ExecuteAsync(null);

        Assert.Equal("cat-1", sut.SelectedCategoryId);
    }

    [Fact]
    public async Task InitializeAsync_RestoresCurrentFilter()
    {
        var sut = CreateSut();
        var current = new AdvancedSearchFilter("cat-2",
        [
            new AttributeFilterCondition(
                AssignmentConditionSourceKind.Attribute, "attr-2", "Диаметр", null,
                ValidationRuleOperator.Equals, "50"),
        ]);

        await sut.InitializeAsync(current);

        Assert.Equal("cat-2", sut.SelectedCategoryId);
        var row = Assert.Single(sut.Conditions);
        Assert.True(row.IsAttributeSource);
        Assert.Equal("attr-2", row.SelectedAttributeId);
        Assert.Equal(ValidationRuleOperator.Equals, row.Operator);
        Assert.Equal("50", row.ValueText);
        Assert.True(sut.CanApply);
    }

    [Fact]
    public async Task BuildFilter_MapsAttributeAndSystemConditions()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        sut.AddConditionCommand.Execute(null);
        sut.AddConditionCommand.Execute(null);

        sut.Conditions[0].SelectedAttributeId = "attr-1";
        sut.Conditions[0].Operator = ValidationRuleOperator.Contains;
        sut.Conditions[0].ValueText = "vendor";

        sut.Conditions[1].SourceKindIndex = 1; // system field
        sut.Conditions[1].SystemField = AssignmentSystemField.RevitCategory;
        sut.Conditions[1].Operator = ValidationRuleOperator.Equals;
        sut.Conditions[1].ValueText = "-2008049";

        var filter = sut.BuildFilter();

        Assert.Equal(2, filter.Conditions.Count);
        var attributeCondition = filter.Conditions[0];
        Assert.Equal(AssignmentConditionSourceKind.Attribute, attributeCondition.SourceKind);
        Assert.Equal("attr-1", attributeCondition.AttributeId);
        Assert.Equal("Производитель", attributeCondition.AttributeName);
        Assert.Null(attributeCondition.SystemField);
        var systemCondition = filter.Conditions[1];
        Assert.Equal(AssignmentConditionSourceKind.System, systemCondition.SourceKind);
        Assert.Null(systemCondition.AttributeId);
        Assert.Equal(AssignmentSystemField.RevitCategory, systemCondition.SystemField);
        Assert.Equal("-2008049", systemCondition.Value);
    }

    [Fact]
    public async Task BuildFilter_Empty_IsEmptyFilter()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);

        var filter = sut.BuildFilter();

        Assert.True(filter.IsEmpty);
        Assert.Null(filter.CategoryId);
        Assert.Empty(filter.Conditions);
    }

    [Fact]
    public async Task CanApply_False_WhenValueOperatorHasNoValue()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        sut.AddConditionCommand.Execute(null);
        sut.Conditions[0].SelectedAttributeId = "attr-1";
        sut.Conditions[0].Operator = ValidationRuleOperator.Equals;
        sut.Conditions[0].ValueText = "";

        Assert.False(sut.CanApply);
        Assert.False(sut.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task CanApply_True_ForFilledOperatorsWithoutValue()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        var row = AddValidAttributeCondition(sut); // HasValue — no value needed

        Assert.False(row.ShowSuggestBox);
        Assert.True(row.ShowNoValuePlaceholder);
        Assert.True(sut.CanApply);
        Assert.True(sut.ApplyCommand.CanExecute(null));
    }

    [Fact]
    public async Task SwitchToSystemField_ResetsValueAndOperators()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        sut.AddConditionCommand.Execute(null);
        var row = sut.Conditions[0];
        row.SelectedAttributeId = "attr-1";
        row.Operator = ValidationRuleOperator.Contains;
        row.ValueText = "vendor";

        row.SourceKindIndex = 1; // → system field (preselects RevitCategory)

        Assert.False(row.IsAttributeSource);
        Assert.Equal(AssignmentSystemField.RevitCategory, row.SystemField);
        Assert.Null(row.ValueText);
        // Ordinal operators only; Contains was reset to the first allowed one.
        Assert.Equal(
            [
                ValidationRuleOperator.Equals,
                ValidationRuleOperator.NotEquals,
                ValidationRuleOperator.HasValue,
                ValidationRuleOperator.IsEmpty,
            ],
            row.AvailableOperators.Select(o => o.Operator).ToList());
        Assert.Equal(ValidationRuleOperator.Equals, row.Operator);
        Assert.True(row.ShowCategoryPicker);
        Assert.False(row.ShowSuggestBox);
    }

    [Fact]
    public async Task SwitchSystemFieldToFamilyName_TextOperatorsAndTextBox()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        sut.AddConditionCommand.Execute(null);
        var row = sut.Conditions[0];
        row.SourceKindIndex = 1;
        row.SystemField = AssignmentSystemField.FamilyName;

        Assert.Equal(
            [
                ValidationRuleOperator.Equals,
                ValidationRuleOperator.NotEquals,
                ValidationRuleOperator.Contains,
                ValidationRuleOperator.NotContains,
            ],
            row.AvailableOperators.Select(o => o.Operator).ToList());
        Assert.True(row.ShowTextBox);
        Assert.False(row.ShowCategoryPicker);
        Assert.False(row.ShowPartTypePicker);
    }

    [Fact]
    public async Task SystemField_PartType_ShowsPartTypePicker()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        sut.AddConditionCommand.Execute(null);
        var row = sut.Conditions[0];
        row.SourceKindIndex = 1;
        row.SystemField = AssignmentSystemField.PartType;

        Assert.True(row.ShowPartTypePicker);
        Assert.False(row.ShowCategoryPicker);
        Assert.False(row.ShowSuggestBox);
    }

    [Fact]
    public async Task CategoryChange_StaleAttributeBlocksApply()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        var row = AddValidAttributeCondition(sut);
        Assert.True(sut.CanApply);

        // cat-2's effective list has only attr-2 — attr-1 became "foreign".
        sut.SelectedCategoryId = "cat-2";
        await sut.LoadAttributesForCurrentCategoryAsync();

        Assert.False(sut.CanApply);
    }

    [Fact]
    public async Task SelectedSuggestion_FillsValueAndClosesPopup()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        sut.AddConditionCommand.Execute(null);
        var row = sut.Conditions[0];
        row.SelectedAttributeId = "attr-1";
        row.Operator = ValidationRuleOperator.Contains;
        row.IsSuggestionsOpen = true;

        row.SelectedSuggestion = "Vendor A";

        Assert.Equal("Vendor A", row.ValueText);
        Assert.False(row.IsSuggestionsOpen);
        Assert.Null(row.SelectedSuggestion); // reset for repeat picks
    }

    [Fact]
    public async Task DeleteCondition_RemovesRow()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        sut.AddConditionCommand.Execute(null);
        var row = sut.Conditions[0];

        sut.DeleteConditionCommand.Execute(row);

        Assert.Empty(sut.Conditions);
        Assert.True(sut.HasNoConditions);
    }

    [Fact]
    public async Task Reset_ClearsConditionsAndCategory()
    {
        var sut = CreateSut();
        var current = new AdvancedSearchFilter("cat-2",
        [
            new AttributeFilterCondition(
                AssignmentConditionSourceKind.Attribute, "attr-2", "Диаметр", null,
                ValidationRuleOperator.Equals, "50"),
        ]);
        await sut.InitializeAsync(current);
        bool? closeResult = null;
        sut.RequestClose += r => closeResult = r;

        sut.ResetCommand.Execute(null);

        Assert.Null(sut.SelectedCategoryId);
        Assert.Empty(sut.Conditions);
        Assert.True(sut.BuildFilter().IsEmpty);
        Assert.True(closeResult);
    }

    [Fact]
    public async Task Apply_RequestCloseTrue()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        bool? closeResult = null;
        sut.RequestClose += r => closeResult = r;

        sut.ApplyCommand.Execute(null);

        Assert.True(closeResult);
    }

    [Fact]
    public async Task Cancel_RequestCloseFalse()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        bool? closeResult = null;
        sut.RequestClose += r => closeResult = r;

        sut.CancelCommand.Execute(null);

        Assert.False(closeResult);
    }

    [Fact]
    public async Task GetSuggestionsAsync_DelegatesToRepository()
    {
        _valueRepo
            .Setup(r => r.GetDistinctValueTextsAsync("attr-1", "Производитель", 100, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<string> { "Vendor A" });
        var sut = CreateSut();
        await sut.InitializeAsync(null);

        var suggestions = await sut.GetSuggestionsAsync("attr-1", "Производитель");

        Assert.Equal(["Vendor A"], suggestions);
    }

    [Fact]
    public async Task Condition_Lifecycle_HasConditionsTracksCollection()
    {
        var sut = CreateSut();
        await sut.InitializeAsync(null);
        Assert.True(sut.HasNoConditions);

        sut.AddConditionCommand.Execute(null);
        Assert.False(sut.HasNoConditions);

        sut.DeleteConditionCommand.Execute(sut.Conditions[0]);
        Assert.True(sut.HasNoConditions);
    }
}
