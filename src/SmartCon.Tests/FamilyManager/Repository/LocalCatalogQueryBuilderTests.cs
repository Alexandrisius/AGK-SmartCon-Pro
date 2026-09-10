using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.LocalCatalog;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Repository;

public sealed class LocalCatalogQueryBuilderTests
{
    private static FamilyCatalogQuery MakeQuery(
        string? searchText = null,
        string? categoryFilter = null,
        ContentStatus? statusFilter = null,
        IReadOnlyList<string>? tags = null,
        FamilyCatalogSort sort = FamilyCatalogSort.NameAsc,
        int offset = 0,
        int limit = 50,
        bool includeUncategorized = false,
        IReadOnlyList<string>? categoryIdsFilter = null,
        bool excludeUncategorized = false,
        IReadOnlyList<AttributeFilterCondition>? attributeFilters = null) =>
        new(searchText, categoryFilter, statusFilter, tags, sort, offset, limit,
            includeUncategorized, categoryIdsFilter, excludeUncategorized, attributeFilters);

    private static AttributeFilterCondition Condition(
        ValidationRuleOperator op, string value = "Vendor X") =>
        new(AssignmentConditionSourceKind.Attribute, "attr-1", "Manufacturer", null, op,
            op is ValidationRuleOperator.HasValue or ValidationRuleOperator.IsEmpty ? null : value);

    private static AttributeFilterCondition SystemCondition(
        AssignmentSystemField field, ValidationRuleOperator op, string? value = null) =>
        new(AssignmentConditionSourceKind.System, null, null, field, op, value);

    [Fact]
    public void BuildWhereClause_NoFilters_ReturnsEmptyWhere()
    {
        var query = MakeQuery();

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Equal(string.Empty, sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildWhereClause_SearchText_AddsLikeConditions()
    {
        var query = MakeQuery(searchText: "pipe steel");

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("WHERE", sql);
        Assert.Contains("ci.normalized_name LIKE", sql);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildWhereClause_SingleSearchToken_AddsOneLikeCondition()
    {
        var query = MakeQuery(searchText: "pipe");

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("ci.normalized_name LIKE", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildWhereClause_SearchText_MatchesNameOrTag()
    {
        var query = MakeQuery(searchText: "pipe");

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("OR EXISTS", sql);
        Assert.Contains("catalog_tags", sql);
        Assert.Contains("ct.normalized_tag LIKE", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildWhereClause_SearchText_MultipleTokens_AndAcrossTokensOrWithinToken()
    {
        var query = MakeQuery(searchText: "pipe steel");

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Equal(2, parameters.Count);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(sql, "OR EXISTS"));
        Assert.Contains(") AND (", sql);
    }

    [Fact]
    public void BuildWhereClause_CategoryFilter_AddsRecursiveSubtreeCondition()
    {
        var query = MakeQuery(categoryFilter: "cat-1");

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("subtree", sql);
        Assert.Contains("categories", sql);
        Assert.Single(parameters);
        Assert.Equal("cat-1", parameters[0].Value);
    }

    [Fact]
    public void BuildWhereClause_StatusFilter_AddsContentStatusCondition()
    {
        var query = MakeQuery(statusFilter: ContentStatus.Deprecated);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("ci.content_status =", sql);
        Assert.Single(parameters);
        Assert.Equal("Deprecated", parameters[0].Value);
    }

    [Fact]
    public void BuildWhereClause_Tags_AddsExistsConditions()
    {
        var query = MakeQuery(tags: ["valve", "steel"]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("catalog_tags", sql);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildWhereClause_CombinedFilters_AddsAllConditions()
    {
        var query = MakeQuery(
            searchText: "pipe",
            categoryFilter: "cat-1",
            statusFilter: ContentStatus.Active);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("ci.normalized_name LIKE", sql);
        Assert.Contains("subtree", sql);
        Assert.Contains("ci.content_status =", sql);
        Assert.Contains("AND", sql);
        Assert.Equal(3, parameters.Count);
    }

    [Fact]
    public void BuildWhereClause_EmptySearchText_NoSearchCondition()
    {
        var query = MakeQuery(searchText: "   ");

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Equal(string.Empty, sql);
        Assert.Empty(parameters);
    }

    [Theory]
    [InlineData(FamilyCatalogSort.NameAsc, "ORDER BY ci.normalized_name ASC")]
    [InlineData(FamilyCatalogSort.NameDesc, "ORDER BY ci.normalized_name DESC")]
    [InlineData(FamilyCatalogSort.DateAsc, "ORDER BY ci.created_at_utc ASC")]
    [InlineData(FamilyCatalogSort.DateDesc, "ORDER BY ci.created_at_utc DESC")]
    public void BuildOrderBy_ReturnsCorrectClause(FamilyCatalogSort sort, string expected)
    {
        Assert.Equal(expected, LocalCatalogQueryBuilder.BuildOrderBy(sort));
    }

    [Fact]
    public void BuildLimitOffsetParameters_ValidValues_ReturnsParameters()
    {
        var query = MakeQuery(offset: 10, limit: 25);

        var parameters = LocalCatalogQueryBuilder.BuildLimitOffsetParameters(query);

        Assert.Equal(2, parameters.Count);
        Assert.Equal(25, parameters[0].Value);
        Assert.Equal(10, parameters[1].Value);
    }

    [Fact]
    public void BuildLimitOffsetParameters_ZeroLimit_DefaultsTo50()
    {
        var query = MakeQuery(limit: 0);

        var parameters = LocalCatalogQueryBuilder.BuildLimitOffsetParameters(query);

        Assert.Equal(50, parameters[0].Value);
    }

    [Fact]
    public void BuildLimitOffsetParameters_NegativeOffset_DefaultsTo0()
    {
        var query = MakeQuery(offset: -5);

        var parameters = LocalCatalogQueryBuilder.BuildLimitOffsetParameters(query);

        Assert.Equal(0, parameters[1].Value);
    }

    [Fact]
    public void BuildWhereClause_ConditionsJoinedWithAnd()
    {
        var query = MakeQuery(
            searchText: "pipe",
            statusFilter: ContentStatus.Active);

        var (sql, _) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("ci.normalized_name LIKE", sql);
        Assert.Contains("ci.content_status =", sql);
        Assert.Contains(" AND ci.content_status", sql);
    }

    [Fact]
    public void BuildWhereClause_IncludeUncategorizedOnly_AddsIsNullOrEmptyCondition()
    {
        var query = MakeQuery(includeUncategorized: true);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("(ci.category_id IS NULL OR ci.category_id = '')", sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildWhereClause_ExcludeUncategorized_AddsNotNullCondition()
    {
        var query = MakeQuery(excludeUncategorized: true);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("(ci.category_id IS NOT NULL AND ci.category_id != '')", sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildWhereClause_ExcludeUncategorized_WithSearch_BothConditionsPresent()
    {
        var query = MakeQuery(searchText: "elbow", excludeUncategorized: true);

        var (sql, _) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("normalized_name LIKE", sql);
        Assert.Contains("(ci.category_id IS NOT NULL AND ci.category_id != '')", sql);
    }

    [Fact]
    public void BuildWhereClause_CategoryIdsFilterOnly_AddsInClause()
    {
        var query = MakeQuery(categoryIdsFilter: ["cat-1", "cat-2", "cat-3"]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("ci.category_id IN (", sql);
        Assert.Contains("@category_0", sql);
        Assert.Contains("@category_1", sql);
        Assert.Contains("@category_2", sql);
        Assert.Equal(3, parameters.Count);
        Assert.Equal("cat-1", parameters[0].Value);
        Assert.Equal("cat-2", parameters[1].Value);
        Assert.Equal("cat-3", parameters[2].Value);
    }

    [Fact]
    public void BuildWhereClause_IncludeUncategorizedAndCategoryIds_BothConditionsPresent()
    {
        var query = MakeQuery(
            includeUncategorized: true,
            categoryIdsFilter: ["cat-1", "cat-2"]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("(ci.category_id IS NULL OR ci.category_id = '')", sql);
        Assert.Contains("ci.category_id IN (", sql);
        Assert.Contains(" AND ", sql);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildWhereClause_IncludeUncategorizedAndRecursiveCategoryFilter_BothPresent()
    {
        var query = MakeQuery(
            categoryFilter: "root-cat",
            includeUncategorized: true);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("subtree", sql);
        Assert.Contains("(ci.category_id IS NULL OR ci.category_id = '')", sql);
        Assert.Contains(" AND ", sql);
        Assert.Single(parameters);
    }

    [Fact]
    public void BuildWhereClause_CategoryFilterAndCategoryIdsFilter_BothPresent()
    {
        var query = MakeQuery(
            categoryFilter: "root-cat",
            categoryIdsFilter: ["leaf-1", "leaf-2"]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("subtree", sql);
        Assert.Contains("ci.category_id IN (", sql);
        Assert.Contains(" AND ", sql);
        Assert.Equal(3, parameters.Count);
    }

    [Fact]
    public void BuildWhereClause_AllCategoryFiltersTogether_AllThreePredicates()
    {
        var query = MakeQuery(
            categoryFilter: "root-cat",
            includeUncategorized: true,
            categoryIdsFilter: ["leaf-1"]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("subtree", sql);
        Assert.Contains("(ci.category_id IS NULL OR ci.category_id = '')", sql);
        Assert.Contains("ci.category_id IN (", sql);
        var andCount = sql.Split("AND").Length - 1;
        Assert.Equal(2, andCount);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildWhereClause_CategoryIdsFilterEmpty_Ignored()
    {
        var query = MakeQuery(categoryIdsFilter: Array.Empty<string>());

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.DoesNotContain("ci.category_id IN (", sql);
        Assert.Empty(parameters);
    }

    // ── #87 advanced attribute filters ──────────────────────────────────

    [Fact]
    public void BuildWhereClause_AttributeEquals_AddsExistsWithActiveVersionJoin()
    {
        var query = MakeQuery(attributeFilters: [Condition(ValidationRuleOperator.Equals)]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("EXISTS", sql);
        Assert.Contains("extracted_attribute_values", sql);
        Assert.Contains("current_version_label", sql);
        Assert.Contains("av.attribute_id = @attrId_0", sql);
        Assert.Contains("av.parameter_name = @attrName_1", sql);
        // Equals carries text + numeric fallback params.
        Assert.Equal(4, parameters.Count);
    }

    [Fact]
    public void BuildWhereClause_AttributeNotEquals_AddsNotExists()
    {
        var query = MakeQuery(attributeFilters: [Condition(ValidationRuleOperator.NotEquals)]);

        var (sql, _) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("NOT EXISTS", sql);
        Assert.Contains("av.value_text = @val_2 COLLATE NOCASE", sql);
    }

    [Fact]
    public void BuildWhereClause_AttributeContains_AddsLikeWithEscape()
    {
        var query = MakeQuery(attributeFilters: [Condition(ValidationRuleOperator.Contains)]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("av.value_text LIKE @val_2 ESCAPE '\\'", sql);
        Assert.Equal("%Vendor X%", parameters[2].Value);
    }

    [Fact]
    public void BuildWhereClause_AttributeContains_EscapesLikeWildcards()
    {
        var query = MakeQuery(attributeFilters: [Condition(ValidationRuleOperator.Contains, value: "50%_a\\b")]);

        var (_, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Equal(@"%50\%\_a\\b%", parameters[2].Value);
    }

    [Fact]
    public void BuildWhereClause_AttributeHasValue_AddsFoundStatusPredicate()
    {
        var query = MakeQuery(attributeFilters: [Condition(ValidationRuleOperator.HasValue)]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("EXISTS", sql);
        Assert.Contains("av.status = 'Found'", sql);
        Assert.DoesNotContain("NOT EXISTS", sql);
        // HasValue needs no value params — attribute id/name only.
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void BuildWhereClause_AttributeIsEmpty_AddsNotExistsFound()
    {
        var query = MakeQuery(attributeFilters: [Condition(ValidationRuleOperator.IsEmpty)]);

        var (sql, _) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("NOT EXISTS", sql);
        Assert.Contains("av.status = 'Found'", sql);
    }

    [Fact]
    public void BuildWhereClause_MultipleAttributeFilters_AndWithSearch()
    {
        var query = MakeQuery(
            searchText: "valve",
            attributeFilters:
            [
                Condition(ValidationRuleOperator.Equals),
                Condition(ValidationRuleOperator.HasValue),
            ]);

        var (sql, _) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("ci.normalized_name LIKE", sql);
        Assert.Equal(2, sql.Split("extracted_attribute_values").Length - 1);
        Assert.Contains(" AND ", sql);
    }

    [Fact]
    public void BuildWhereClause_EmptyAttributeFilterList_Ignored()
    {
        var query = MakeQuery(attributeFilters: Array.Empty<AttributeFilterCondition>());

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Equal(string.Empty, sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildWhereClause_AttributeFilterWithoutIdOrName_Skipped()
    {
        var query = MakeQuery(attributeFilters:
        [
            new AttributeFilterCondition(AssignmentConditionSourceKind.Attribute, "", "", null, ValidationRuleOperator.HasValue, null),
        ]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Equal(string.Empty, sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildWhereClause_SystemConditionWithoutField_Skipped()
    {
        var query = MakeQuery(attributeFilters:
        [
            new AttributeFilterCondition(AssignmentConditionSourceKind.System, "attr-1", "Manufacturer", null, ValidationRuleOperator.HasValue, null),
        ]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Equal(string.Empty, sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildWhereClause_UnsupportedOperator_Throws()
    {
        var query = MakeQuery(attributeFilters:
        [
            new AttributeFilterCondition(AssignmentConditionSourceKind.Attribute, "attr-1", "Manufacturer", null, ValidationRuleOperator.GreaterThan, "1"),
        ]);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => LocalCatalogQueryBuilder.BuildWhereClause(query));
    }

    // ── #87 system-field conditions ────────────────────────────────────

    [Fact]
    public void BuildWhereClause_FamilyNameEquals_CollateNocaseOnName()
    {
        var query = MakeQuery(attributeFilters: [SystemCondition(AssignmentSystemField.FamilyName, ValidationRuleOperator.Equals, "Valve")]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("ci.name = @name_0 COLLATE NOCASE", sql);
        Assert.Single(parameters);
        Assert.Equal("Valve", parameters[0].Value);
    }

    [Fact]
    public void BuildWhereClause_FamilyNameNotEquals_Negated()
    {
        var query = MakeQuery(attributeFilters: [SystemCondition(AssignmentSystemField.FamilyName, ValidationRuleOperator.NotEquals, "Valve")]);

        var (sql, _) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("NOT (ci.name = @name_0 COLLATE NOCASE)", sql);
    }

    [Fact]
    public void BuildWhereClause_FamilyNameContains_LikeWithEscape()
    {
        var query = MakeQuery(attributeFilters: [SystemCondition(AssignmentSystemField.FamilyName, ValidationRuleOperator.Contains, "клапан")]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("ci.name LIKE @name_0 ESCAPE '\\'", sql);
        Assert.Equal("%клапан%", parameters[0].Value);
    }

    [Fact]
    public void BuildWhereClause_RevitCategoryEquals_OrdinalParam()
    {
        var query = MakeQuery(attributeFilters: [SystemCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.Equals, "-2008049")]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("ci.revit_category_id = @ordinal_0", sql);
        Assert.Single(parameters);
        Assert.Equal((long)-2008049, parameters[0].Value);
    }

    [Fact]
    public void BuildWhereClause_RevitCategoryNotEquals_NullSafe()
    {
        var query = MakeQuery(attributeFilters: [SystemCondition(AssignmentSystemField.RevitCategory, ValidationRuleOperator.NotEquals, "5")]);

        var (sql, _) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("(ci.revit_category_id IS NULL OR ci.revit_category_id != @ordinal_0)", sql);
    }

    [Theory]
    [InlineData(ValidationRuleOperator.HasValue, "ci.revit_category_id IS NOT NULL")]
    [InlineData(ValidationRuleOperator.IsEmpty, "ci.revit_category_id IS NULL")]
    public void BuildWhereClause_RevitCategoryFilledOperators_NullChecks(ValidationRuleOperator op, string expected)
    {
        var query = MakeQuery(attributeFilters: [SystemCondition(AssignmentSystemField.RevitCategory, op)]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains(expected, sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void BuildWhereClause_PartTypeEquals_FactExists()
    {
        var query = MakeQuery(attributeFilters: [SystemCondition(AssignmentSystemField.PartType, ValidationRuleOperator.Equals, "3")]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("EXISTS (SELECT 1 FROM family_facts ff", sql);
        Assert.Contains("ff.fact_key = @factKey_0", sql);
        Assert.Contains("AND ff.value_key = @factValue_1", sql);
        Assert.Equal("part_type", parameters[0].Value);
        Assert.Equal("3", parameters[1].Value);
    }

    [Fact]
    public void BuildWhereClause_PartTypeIsEmpty_NotExistsFact()
    {
        var query = MakeQuery(attributeFilters: [SystemCondition(AssignmentSystemField.PartType, ValidationRuleOperator.IsEmpty)]);

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("NOT EXISTS (SELECT 1 FROM family_facts ff", sql);
        Assert.Single(parameters);
        Assert.Equal("part_type", parameters[0].Value);
    }
}
