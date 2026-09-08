using Microsoft.Data.Sqlite;
using Moq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.Services.Validation;
using SmartCon.Tests.FamilyManager.Repository;
using SmartCon.Tests.TestDoubles;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// #259: catalog compliance check — catalog items vs the effective validation
/// rules of their category (pure SQLite + pure engine, session snapshot).
/// Runs against the real <see cref="FamilyImportValidationService"/> chain on
/// a temp catalog (same fixture as <c>FamilyImportValidationServiceTests</c>);
/// the per-category rule-cache contract is verified separately with Moq.
/// </summary>
public sealed class CatalogComplianceServiceTests : IDisposable
{
    private readonly TempCatalogFixture _fixture;
    private readonly LocalCategoryRepository _categoryRepository;
    private readonly LocalAttributeDefinitionRepository _attributeDefRepo;
    private readonly LocalCategoryAttributeBindingService _bindingService;
    private readonly LocalValidationRuleRepository _ruleRepository;
    private readonly FamilyImportValidationService _validationService;
    private readonly FamilyManagerMetadataMediator _mediator = new();
    private readonly FakeClock _clock = new();
    private readonly CatalogComplianceService _service;

    public CatalogComplianceServiceTests()
    {
        _fixture = new TempCatalogFixture();
        _categoryRepository = new LocalCategoryRepository(_fixture.GetDatabase());
        _attributeDefRepo = new LocalAttributeDefinitionRepository(_fixture.GetDatabase());
        _bindingService = new LocalCategoryAttributeBindingService(_fixture.GetDatabase(), _categoryRepository, _fixture.GetMigrator());
        _ruleRepository = new LocalValidationRuleRepository(_fixture.GetDatabase(), _fixture.GetMigrator());
        _validationService = new FamilyImportValidationService(
            _bindingService,
            _ruleRepository,
            new FamilyValidationEngine(),
            _fixture.GetValueRepository(),
            _fixture.GetTypeRepository(),
            _fixture.GetRunRepository());
        _service = new CatalogComplianceService(
            _fixture.GetProvider(), _validationService, _mediator, _clock);
    }

    public void Dispose() => _fixture.Dispose();

    // ── Seeding helpers ──────────────────────────────────────────────────

    private async Task<(string CategoryId, string BindingId)> SeedCategoryWithRuleAsync(
        string categoryName = "Pipes", string attributeName = "Pressure")
    {
        var cat = await _categoryRepository.AddAsync(categoryName, null, 0);
        var attr = await _attributeDefRepo.CreateAsync(attributeName, null);
        var binding = await _bindingService.CreateBindingAsync(cat.Id, attr.Id, 0);
        await _ruleRepository.CreateRuleAsync(new ValidationRule(
            string.Empty, binding.Id, ValidationRuleOperator.HasValue,
            null, null, null, null, null, 0, true));
        return (cat.Id, binding.Id);
    }

    private async Task<string> SeedCategoryWithoutRulesAsync(string categoryName = "Pipes")
    {
        var cat = await _categoryRepository.AddAsync(categoryName, null, 0);
        return cat.Id;
    }

    private async Task<string> SeedItemAsync(
        string? categoryId, string name, bool withExtraction,
        AttributeValueStatus status = AttributeValueStatus.EmptyValue,
        string? valueText = null, double? valueNumber = null)
    {
        var (itemId, versionId, fileId, _) = await CatalogSeedHelper.SeedBareLoadableAsync(
            _fixture, name, categoryId: categoryId);
        if (!withExtraction) return itemId;

        var runId = await CatalogSeedHelper.SeedDataExtractedAsync(_fixture, itemId, versionId, fileId);
        var types = await _fixture.GetTypeRepository().GetTypesForItemAsync(itemId);
        var typeId = Assert.Single(types).Id;
        await SeedTypedValueAsync(itemId, versionId, fileId, typeId, "Pressure", status, valueText, valueNumber, runId);
        return itemId;
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

    private sealed class CollectingProgress<T> : IProgress<T>
    {
        public List<T> Reports { get; } = [];
        public void Report(T value) => Reports.Add(value);
    }

    // ── CheckCategoriesAsync ─────────────────────────────────────────────

    [Fact]
    public async Task CheckCategoriesAsync_NoRules_AllPass()
    {
        var catId = await SeedCategoryWithoutRulesAsync();
        await SeedItemAsync(catId, "Elbow", withExtraction: false);

        var results = await _service.CheckCategoriesAsync([catId]);

        var result = Assert.Single(results);
        Assert.Equal(ComplianceStatus.Pass, result.Status);
        Assert.Equal(catId, result.CategoryId);
        Assert.Equal(0, result.RuleCount);
        Assert.Empty(result.Violations);
        var snapshot = _service.GetCachedSnapshot();
        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Results);
    }

    [Fact]
    public async Task CheckCategoriesAsync_ViolatingItem_FailsWithViolations()
    {
        var (catId, _) = await SeedCategoryWithRuleAsync();
        var itemId = await SeedItemAsync(catId, "Elbow", withExtraction: true,
            status: AttributeValueStatus.EmptyValue);

        var results = await _service.CheckCategoriesAsync([catId]);

        var result = Assert.Single(results);
        Assert.Equal(ComplianceStatus.Fail, result.Status);
        Assert.Equal(itemId, result.CatalogItemId);
        Assert.Equal(catId, result.CategoryId);
        Assert.Equal(1, result.RuleCount);
        Assert.Equal(1, result.RulesEvaluated);
        var violation = Assert.Single(result.Violations);
        Assert.Equal("Pressure", violation.AttributeName);
        Assert.Equal("DN50", violation.TypeName);
        // The snapshot carries the verdict for later report opening.
        var snapshot = _service.GetCachedSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(ComplianceStatus.Fail, snapshot!.Results[itemId].Status);
        Assert.Single(snapshot.Results[itemId].Violations);
    }

    [Fact]
    public async Task CheckCategoriesAsync_PassingItem_PassWithRuleCount()
    {
        var (catId, _) = await SeedCategoryWithRuleAsync();
        await SeedItemAsync(catId, "Elbow", withExtraction: true,
            status: AttributeValueStatus.Found, valueText: "16 бар", valueNumber: 16.0);

        var results = await _service.CheckCategoriesAsync([catId]);

        var result = Assert.Single(results);
        Assert.Equal(ComplianceStatus.Pass, result.Status);
        Assert.Equal(1, result.RuleCount);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task CheckCategoriesAsync_NoExtractionRun_CannotVerify()
    {
        var (catId, _) = await SeedCategoryWithRuleAsync();
        var itemId = await SeedItemAsync(catId, "Elbow", withExtraction: false);

        var results = await _service.CheckCategoriesAsync([catId]);

        var result = Assert.Single(results);
        Assert.Equal(ComplianceStatus.CannotVerify, result.Status);
        Assert.Equal(itemId, result.CatalogItemId);
        Assert.Equal(catId, result.CategoryId);
    }

    [Fact]
    public async Task CheckCategoriesAsync_RulesResolvedOncePerCategory()
    {
        var catA = await SeedCategoryWithoutRulesAsync("Pipes");
        var catB = await SeedCategoryWithoutRulesAsync("Fittings");
        await SeedItemAsync(catA, "Elbow", withExtraction: false);
        await SeedItemAsync(catA, "Tee", withExtraction: false);
        await SeedItemAsync(catB, "Flange", withExtraction: false);

        var validationMock = new Mock<IFamilyImportValidationService>();
        validationMock
            .Setup(m => m.GetEffectiveRulesAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<EffectiveValidationRule>());
        var service = new CatalogComplianceService(
            _fixture.GetProvider(), validationMock.Object, _mediator, _clock);

        var results = await service.CheckCategoriesAsync([catA, catB]);

        Assert.Equal(3, results.Count);
        // Per-category rule cache: one resolve per category, not per item.
        validationMock.Verify(
            m => m.GetEffectiveRulesAsync(catA, It.IsAny<CancellationToken>()), Times.Once);
        validationMock.Verify(
            m => m.GetEffectiveRulesAsync(catB, It.IsAny<CancellationToken>()), Times.Once);
        // Free pass short-circuits before any extraction read.
        validationMock.Verify(
            m => m.ValidateCatalogItemAsync(
                It.IsAny<string>(), It.IsAny<IReadOnlyList<EffectiveValidationRule>>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CheckCategoriesAsync_ProgressReportedPerItem()
    {
        var catId = await SeedCategoryWithoutRulesAsync();
        await SeedItemAsync(catId, "A", withExtraction: false);
        await SeedItemAsync(catId, "B", withExtraction: false);
        await SeedItemAsync(catId, "C", withExtraction: false);
        var progress = new CollectingProgress<ComplianceCheckProgress>();

        await _service.CheckCategoriesAsync([catId], progress);

        Assert.Equal(3, progress.Reports.Count);
        Assert.Equal([1, 2, 3], progress.Reports.Select(p => p.Completed));
        Assert.All(progress.Reports, p => Assert.Equal(3, p.Total));
        Assert.All(progress.Reports, p => Assert.False(string.IsNullOrEmpty(p.CurrentItemName)));
    }

    [Fact]
    public async Task CheckCategoriesAsync_SecondRunMergesIntoSnapshot()
    {
        var catA = await SeedCategoryWithoutRulesAsync("Pipes");
        var catB = await SeedCategoryWithoutRulesAsync("Fittings");
        var itemA = await SeedItemAsync(catA, "Elbow", withExtraction: false);
        var itemB = await SeedItemAsync(catB, "Flange", withExtraction: false);

        await _service.CheckCategoriesAsync([catA]);
        await _service.CheckCategoriesAsync([catB]);

        var snapshot = _service.GetCachedSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(2, snapshot!.Results.Count);
        Assert.True(snapshot.Results.ContainsKey(itemA));
        Assert.True(snapshot.Results.ContainsKey(itemB));
    }

    [Fact]
    public async Task CheckCategoriesAsync_NoCategoryNodeId_MatchesUncategorizedItems()
    {
        var catId = await SeedCategoryWithoutRulesAsync();
        var uncategorizedId = await SeedItemAsync(null, "Loose", withExtraction: false);
        await SeedItemAsync(catId, "Elbow", withExtraction: false);

        var results = await _service.CheckCategoriesAsync(["__no_category__"]);

        var result = Assert.Single(results);
        Assert.Equal(uncategorizedId, result.CatalogItemId);
        Assert.Null(result.CategoryId);
        Assert.Equal(ComplianceStatus.Pass, result.Status);
    }

    [Fact]
    public async Task CheckCategoriesAsync_EmptyCategory_ReturnsEmpty()
    {
        var catId = await SeedCategoryWithoutRulesAsync();

        var results = await _service.CheckCategoriesAsync([catId]);

        Assert.Empty(results);
        Assert.Null(_service.GetCachedSnapshot());
    }

    // ── CheckItemAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task CheckItemAsync_ViolatingItem_FailsAndUpsertsSnapshot()
    {
        var (catId, _) = await SeedCategoryWithRuleAsync();
        var itemId = await SeedItemAsync(catId, "Elbow", withExtraction: true,
            status: AttributeValueStatus.EmptyValue);

        var result = await _service.CheckItemAsync(itemId);

        Assert.Equal(ComplianceStatus.Fail, result.Status);
        Assert.Equal(catId, result.CategoryId);
        var violation = Assert.Single(result.Violations);
        Assert.Equal("Pressure", violation.AttributeName);
        var snapshot = _service.GetCachedSnapshot();
        Assert.NotNull(snapshot);
        Assert.Equal(ComplianceStatus.Fail, snapshot!.Results[itemId].Status);
    }

    [Fact]
    public async Task CheckItemAsync_MissingItem_CannotVerify()
    {
        var result = await _service.CheckItemAsync("no-such-item");

        Assert.Equal(ComplianceStatus.CannotVerify, result.Status);
        Assert.Equal("no-such-item", result.CatalogItemId);
        Assert.Null(result.CategoryId);
    }

    // ── Snapshot lifecycle ───────────────────────────────────────────────

    [Fact]
    public void GetMergedSnapshot_ColdCache_StartsFromEmptyWithoutMutatingCache()
    {
        Assert.Null(_service.GetCachedSnapshot());

        var merged = _service.GetMergedSnapshot(
            [ComplianceCheckResult.Pass("item-1", "cat-1", 0)]);

        Assert.Single(merged.Results);
        // GetMergedSnapshot must not populate the cache by itself.
        Assert.Null(_service.GetCachedSnapshot());
    }

    [Fact]
    public async Task InvalidateCache_DropsSnapshot()
    {
        var catId = await SeedCategoryWithoutRulesAsync();
        await SeedItemAsync(catId, "Elbow", withExtraction: false);
        await _service.CheckCategoriesAsync([catId]);
        Assert.NotNull(_service.GetCachedSnapshot());

        _service.InvalidateCache();

        Assert.Null(_service.GetCachedSnapshot());
    }

    [Fact]
    public async Task InvalidateItems_RemovesOnlySpecifiedVerdicts()
    {
        var catId = await SeedCategoryWithoutRulesAsync();
        var item1 = await SeedItemAsync(catId, "Elbow", withExtraction: false);
        var item2 = await SeedItemAsync(catId, "Tee", withExtraction: false);
        await _service.CheckCategoriesAsync([catId]);

        _service.InvalidateItems([item1]);

        var snapshot = _service.GetCachedSnapshot();
        Assert.NotNull(snapshot);
        Assert.False(snapshot!.Results.ContainsKey(item1));
        Assert.True(snapshot.Results.ContainsKey(item2));
    }

    [Fact]
    public async Task MetadataChanged_InvalidatesSnapshot()
    {
        var catId = await SeedCategoryWithoutRulesAsync();
        await SeedItemAsync(catId, "Elbow", withExtraction: false);
        await _service.CheckCategoriesAsync([catId]);
        Assert.NotNull(_service.GetCachedSnapshot());

        // Rule-editor save raises MetadataChanged → verdicts computed under
        // the old rule set must not survive.
        _mediator.RaiseMetadataChanged();

        Assert.Null(_service.GetCachedSnapshot());
    }

    [Fact]
    public async Task Recheck_OverwritesPreviousVerdict()
    {
        var (catId, _) = await SeedCategoryWithRuleAsync();
        var itemId = await SeedItemAsync(catId, "Elbow", withExtraction: true,
            status: AttributeValueStatus.EmptyValue);
        var first = await _service.CheckItemAsync(itemId);
        Assert.Equal(ComplianceStatus.Fail, first.Status);

        // The family was "fixed": the attribute value appeared (same row —
        // a second INSERT for the same (item, type, parameter) would be
        // ambiguous for the mapper).
        using (var conn = _fixture.GetDatabase().CreateConnection())
        {
            await conn.OpenAsync();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE extracted_attribute_values
                SET status = 'Found', value_text = '16 бар', value_number = 16.0
                WHERE catalog_item_id = @itemId AND parameter_name = 'Pressure'
                """;
            cmd.Parameters.Add(new SqliteParameter("@itemId", itemId));
            await cmd.ExecuteNonQueryAsync();
        }

        var second = await _service.CheckItemAsync(itemId);

        Assert.Equal(ComplianceStatus.Pass, second.Status);
        var snapshot = _service.GetCachedSnapshot();
        Assert.Equal(ComplianceStatus.Pass, snapshot!.Results[itemId].Status);
    }
}
