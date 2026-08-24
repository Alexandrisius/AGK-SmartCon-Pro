using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.Services.Validation;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public sealed class CategoryAutoAssignServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalAssignmentRuleRepository _ruleRepository;
    private readonly LocalAttributeDefinitionRepository _attributeDefRepo;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly CategoryAutoAssignService _service;

    public CategoryAutoAssignServiceTests()
    {
        _fixture = new TempCatalogFixture();
        _ruleRepository = new LocalAssignmentRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
        _attributeDefRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _service = new CategoryAutoAssignService(
            _ruleRepository,
            _attributeDefRepo,
            new CategoryAutoAssignEngine(new FamilyValidationEngine()));
    }

    public void Dispose() => _fixture.Dispose();

    private static FamilySnapshot Snapshot(
        string familyName = "Отвод 90",
        int? categoryId = -2008049,
        IReadOnlyList<FamilyFact>? facts = null,
        params (string Name, string? Value)[] parameters) =>
        new(
            FamilyName: familyName,
            Category: "Фитинги трубопроводов",
            Parameters: [],
            Types: [new FamilyTypeSnapshot(
                "DN50",
                parameters.Select(p => new FamilyParameterValue(
                    p.Name, "Text", p.Value is not null, p.Value, null, null)).ToList())],
            Geometry: new GeometryMetrics(0, []),
            SharedNestedFamilyNames: [],
            CategoryId: categoryId,
            Facts: facts);

    private async Task<string> SeedRuleAsync(
        string categoryName,
        ValidationRuleOperator op = ValidationRuleOperator.Contains,
        string? valueText = "сталь",
        int? categoryIdOrdinal = null,
        AssignmentSystemField? systemField = null)
    {
        var category = await _categoryRepository.AddAsync(categoryName, null, 0);
        var group = await _ruleRepository.CreateGroupAsync(category.Id);

        if (systemField is { } field)
        {
            await _ruleRepository.CreateConditionAsync(
                group.Id, AssignmentConditionSourceKind.System, null, field,
                ValidationRuleOperator.Equals, categoryIdOrdinal?.ToString(System.Globalization.CultureInfo.InvariantCulture),
                null, null, null, true);
        }
        else
        {
            var attribute = await _attributeDefRepo.CreateAsync("ADSK_Материал", null);
            await _ruleRepository.CreateConditionAsync(
                group.Id, AssignmentConditionSourceKind.Attribute, attribute.Id, null,
                op, valueText, null, null, null, true);
        }

        return category.Id;
    }

    [Fact]
    public async Task PreloadAsync_NoRules_ReturnsEmpty()
    {
        var preloaded = await _service.PreloadAsync();

        Assert.False(preloaded.HasRules);
        Assert.Empty(preloaded.EnabledGroups);

        var result = _service.Evaluate(preloaded, Snapshot(), null);
        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public async Task Evaluate_AttributeConditionMatches_ReturnsMatched()
    {
        var categoryId = await SeedRuleAsync("Стальные");
        var preloaded = await _service.PreloadAsync();

        var result = _service.Evaluate(
            preloaded,
            Snapshot(parameters: [("ADSK_Материал", "нержавеющая сталь")]),
            null);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
        Assert.Equal(categoryId, result.CategoryId);
    }

    [Fact]
    public async Task Evaluate_AttributeConditionNoMatch_ReturnsNoMatch()
    {
        await SeedRuleAsync("Стальные", valueText: "медь");
        var preloaded = await _service.PreloadAsync();

        var result = _service.Evaluate(
            preloaded,
            Snapshot(parameters: [("ADSK_Материал", "нержавеющая сталь")]),
            null);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public async Task Evaluate_SystemFieldMatches_ReturnsMatched()
    {
        var categoryId = await SeedRuleAsync(
            "Фитинги", categoryIdOrdinal: -2008049, systemField: AssignmentSystemField.RevitCategory);
        var preloaded = await _service.PreloadAsync();

        var result = _service.Evaluate(preloaded, Snapshot(categoryId: -2008049), null);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
        Assert.Equal(categoryId, result.CategoryId);
    }

    [Fact]
    public async Task Evaluate_FamilyNameOverride_IsUsed()
    {
        var categoryId = await SeedRuleAsync("Отводы");
        var group = (await _ruleRepository.GetGroupsForCategoryAsync(categoryId)).Single();
        var attribute = await _attributeDefRepo.GetByNameAsync("ADSK_Материал");

        await _ruleRepository.CreateConditionAsync(
            group.Id, AssignmentConditionSourceKind.System, null, AssignmentSystemField.FamilyName,
            ValidationRuleOperator.Equals, "Мой отвод", null, null, null, true);

        var preloaded = await _service.PreloadAsync();
        var result = _service.Evaluate(
            preloaded,
            Snapshot(familyName: "Отвод 90", parameters: [("ADSK_Материал", "сталь")]),
            null,
            familyNameOverride: "Мой отвод");

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
        Assert.Equal(categoryId, result.CategoryId);
    }

    [Fact]
    public async Task Evaluate_TwoCategoriesMatch_ReturnsAmbiguous()
    {
        await SeedRuleAsync("Стальные");
        await SeedRuleAsync("Фитинги", categoryIdOrdinal: -2008049, systemField: AssignmentSystemField.RevitCategory);
        var preloaded = await _service.PreloadAsync();

        var result = _service.Evaluate(
            preloaded,
            Snapshot(parameters: [("ADSK_Материал", "сталь")]),
            null);

        Assert.Equal(CategoryAutoAssignOutcome.Ambiguous, result.Outcome);
        Assert.Equal(2, result.CandidateCategoryIds.Count);
    }

    [Fact]
    public async Task Evaluate_DisabledGroup_IsIgnored()
    {
        var categoryId = await SeedRuleAsync("Стальные");
        var group = (await _ruleRepository.GetGroupsForCategoryAsync(categoryId)).Single();
        await _ruleRepository.UpdateGroupAsync(group.Id, null, false);
        var preloaded = await _service.PreloadAsync();

        var result = _service.Evaluate(
            preloaded,
            Snapshot(parameters: [("ADSK_Материал", "сталь")]),
            null);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public async Task Evaluate_SystemSnapshot_IsSupported()
    {
        var categoryId = await SeedRuleAsync(
            "Трубы", categoryIdOrdinal: -2008013, systemField: AssignmentSystemField.RevitCategory);
        var preloaded = await _service.PreloadAsync();

        var systemSnapshot = new SystemFamilySnapshot(
            "Трубы",
            -2008013,
            [new SystemTypeSnapshot("Стандарт", [])]);

        var result = _service.Evaluate(preloaded, null, systemSnapshot);

        Assert.Equal(CategoryAutoAssignOutcome.Matched, result.Outcome);
        Assert.Equal(categoryId, result.CategoryId);
    }

    [Fact]
    public async Task Evaluate_BothSnapshotsNull_ReturnsNoMatch()
    {
        await SeedRuleAsync("Стальные");
        var preloaded = await _service.PreloadAsync();

        var result = _service.Evaluate(preloaded, null, null);

        Assert.Equal(CategoryAutoAssignOutcome.NoMatch, result.Outcome);
    }
}
