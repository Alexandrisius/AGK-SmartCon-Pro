using Microsoft.Data.Sqlite;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.Services.Validation;
using SmartCon.Tests.FamilyManager.Repository;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public sealed class FamilyImportValidationServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly LocalAttributeDefinitionRepository _attributeDefRepo;
    private readonly LocalCategoryAttributeBindingService _bindingService;
    private readonly LocalValidationRuleRepository _ruleRepository;
    private readonly FamilyImportValidationService _service;

    public FamilyImportValidationServiceTests()
    {
        _fixture = new TempCatalogFixture();
        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _attributeDefRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _bindingService = new LocalCategoryAttributeBindingService(_fixture.GetDatabase(), _categoryRepository, _fixture.GetMigrator());
        _ruleRepository = new LocalValidationRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
        _service = new FamilyImportValidationService(
            _bindingService,
            _ruleRepository,
            new FamilyValidationEngine(),
            _fixture.GetValueRepository(),
            _fixture.GetTypeRepository(),
            _fixture.GetRunRepository());
    }

    public void Dispose() => _fixture.Dispose();

    private async Task<(string CategoryId, string AttributeId, string BindingId)> SeedBindingAsync(
        string categoryName = "Pipes", string attributeName = "Pressure", string? parentId = null)
    {
        var cat = await _categoryRepository.AddAsync(categoryName, parentId, 0);
        var attr = await _attributeDefRepo.CreateAsync(attributeName, null);
        var binding = await _bindingService.CreateBindingAsync(cat.Id, attr.Id, 0);
        return (cat.Id, attr.Id, binding.Id);
    }

    private static ValidationRule NewRule(string bindingId, ValidationRuleOperator op = ValidationRuleOperator.HasValue) =>
        new(string.Empty, bindingId, op, null, null, null, null, null, 0, true);

    [Fact]
    public async Task GetEffectiveRulesAsync_NullCategory_ReturnsEmpty()
    {
        var rules = await _service.GetEffectiveRulesAsync(null);

        Assert.Empty(rules);
    }

    [Fact]
    public async Task GetEffectiveRulesAsync_CategoryWithoutRules_ReturnsEmpty()
    {
        var (catId, _, _) = await SeedBindingAsync();

        var rules = await _service.GetEffectiveRulesAsync(catId);

        Assert.Empty(rules);
    }

    [Fact]
    public async Task GetEffectiveRulesAsync_ReturnsRulesWithAttributeNames()
    {
        var (catId, _, bindingId) = await SeedBindingAsync();
        await _ruleRepository.CreateRuleAsync(NewRule(bindingId));
        await _ruleRepository.CreateRuleAsync(NewRule(bindingId, ValidationRuleOperator.GreaterThan) with { ValueNumber = 0.0 });

        var rules = await _service.GetEffectiveRulesAsync(catId);

        Assert.Equal(2, rules.Count);
        Assert.All(rules, r => Assert.Equal("Pressure", r.AttributeName));
        Assert.All(rules, r => Assert.False(r.IsInherited));
    }

    [Fact]
    public async Task GetEffectiveRulesAsync_DisabledRuleExcluded()
    {
        var (catId, _, bindingId) = await SeedBindingAsync();
        await _ruleRepository.CreateRuleAsync(NewRule(bindingId) with { IsEnabled = false });

        var rules = await _service.GetEffectiveRulesAsync(catId);

        Assert.Empty(rules);
    }

    [Fact]
    public async Task GetEffectiveRulesAsync_InheritedFromParentCategory()
    {
        var (parentCatId, _, parentBindingId) = await SeedBindingAsync("Pipe Accessories", "Pressure");
        var childCat = await _categoryRepository.AddAsync("Elbows", parentCatId, 0);
        await _ruleRepository.CreateRuleAsync(NewRule(parentBindingId));

        var rules = await _service.GetEffectiveRulesAsync(childCat.Id);

        var rule = Assert.Single(rules);
        Assert.Equal("Pressure", rule.AttributeName);
        Assert.True(rule.IsInherited);
    }

    [Fact]
    public void ValidateFromSnapshots_NoRules_Valid()
    {
        var snapshot = BuildSnapshot(("DN50", [("Pressure", true, "16", 16.0)]));

        var report = _service.ValidateFromSnapshots(snapshot, null, []);

        Assert.True(report.IsValid);
    }

    [Fact]
    public async Task ValidateFromSnapshots_ViolatingSnapshot_Invalid()
    {
        var (catId, _, bindingId) = await SeedBindingAsync();
        await _ruleRepository.CreateRuleAsync(NewRule(bindingId));
        var rules = await _service.GetEffectiveRulesAsync(catId);

        var snapshot = BuildSnapshot(("DN50", [("Pressure", false, null, null)]));

        var report = _service.ValidateFromSnapshots(snapshot, null, rules);

        Assert.False(report.IsValid);
        var violation = Assert.Single(report.Violations);
        Assert.Equal("DN50", violation.TypeName);
        Assert.Equal("Pressure", violation.AttributeName);
    }

    [Fact]
    public async Task ValidateCatalogItemAsync_NoExtractionRun_ReturnsNull()
    {
        var (catId, _, bindingId) = await SeedBindingAsync();
        await _ruleRepository.CreateRuleAsync(NewRule(bindingId));
        var rules = await _service.GetEffectiveRulesAsync(catId);

        var (itemId, _, _, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Elbow");

        var report = await _service.ValidateCatalogItemAsync(itemId, rules);

        Assert.Null(report);
    }

    [Fact]
    public async Task ValidateCatalogItemAsync_ExtractedValues_Evaluated()
    {
        var (catId, _, bindingId) = await SeedBindingAsync();
        await _ruleRepository.CreateRuleAsync(NewRule(bindingId));
        var rules = await _service.GetEffectiveRulesAsync(catId);

        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Elbow");
        var runId = await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);

        var types = await _fixture.GetTypeRepository().GetTypesForItemAsync(itemId);
        var typeId = Assert.Single(types).Id;

        await SeedTypedValueAsync(itemId, versionId, fileId, typeId, "Pressure",
            AttributeValueStatus.EmptyValue, null, null, runId);

        var report = await _service.ValidateCatalogItemAsync(itemId, rules);

        Assert.NotNull(report);
        Assert.False(report!.IsValid);
        var violation = Assert.Single(report.Violations);
        Assert.Equal("DN50", violation.TypeName);
        Assert.Equal("Pressure", violation.AttributeName);
    }

    [Fact]
    public async Task ValidateCatalogItemAsync_FilledValue_Passes()
    {
        var (catId, _, bindingId) = await SeedBindingAsync();
        await _ruleRepository.CreateRuleAsync(NewRule(bindingId));
        var rules = await _service.GetEffectiveRulesAsync(catId);

        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(_fixture, "Elbow");
        var runId = await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        var types = await _fixture.GetTypeRepository().GetTypesForItemAsync(itemId);
        var typeId = Assert.Single(types).Id;

        await SeedTypedValueAsync(itemId, versionId, fileId, typeId, "Pressure",
            AttributeValueStatus.Found, "16 бар", 16.0, runId);

        var report = await _service.ValidateCatalogItemAsync(itemId, rules);

        Assert.NotNull(report);
        Assert.True(report!.IsValid);
    }

    private static FamilySnapshot BuildSnapshot(params (string TypeName, (string Name, bool HasValue, string? Text, double? Number)[] Values)[] types)
    {
        return new FamilySnapshot(
            FamilyName: "Elbow",
            Category: "Pipe Fittings",
            Parameters: [],
            Types: types.Select(t => new FamilyTypeSnapshot(
                t.TypeName,
                t.Values.Select(v => new FamilyParameterValue(v.Name, "Double", v.HasValue, v.Text, v.Number, null)).ToList()))
                .ToList(),
            Geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: []);
    }

    private async Task SeedTypedValueAsync(
        string itemId, string versionId, string fileId, string typeId,
        string parameterName, AttributeValueStatus status, string? valueText, double? valueNumber, string runId)
    {
        using var conn = _fixture.GetDatabase().CreateConnection();
        await conn.OpenAsync();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO extracted_attribute_values
                (id, catalog_item_id, version_id, file_id, type_id, parameter_name, storage_type,
                 value_text, value_number, unit_type_id, status, extraction_run_id, extracted_at_utc)
            VALUES (@id, @itemId, @versionId, @fileId, @typeId, @paramName, 'Double',
                    @valueText, @valueNumber, NULL, @status, @runId, @t)
            """;
        cmd.Parameters.Add(new SqliteParameter("@id", Guid.NewGuid().ToString()));
        cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
        cmd.Parameters.Add(new SqliteParameter("@versionId", versionId));
        cmd.Parameters.Add(new SqliteParameter("@fileId", fileId));
        cmd.Parameters.Add(new SqliteParameter("@typeId", typeId));
        cmd.Parameters.Add(new SqliteParameter("@paramName", parameterName));
        cmd.Parameters.Add(new SqliteParameter("@valueText", (object?)valueText ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@valueNumber", (object?)valueNumber ?? DBNull.Value));
        cmd.Parameters.Add(new SqliteParameter("@status", status.ToString()));
        cmd.Parameters.Add(new SqliteParameter("@runId", runId));
        cmd.Parameters.Add(new SqliteParameter("@t", DateTimeOffset.UtcNow.ToString("o")));
        await cmd.ExecuteNonQueryAsync();
    }
}
