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
        string? manufacturerFilter = null,
        FamilyCatalogSort sort = FamilyCatalogSort.NameAsc,
        int offset = 0,
        int limit = 50) =>
        new(searchText, categoryFilter, statusFilter, tags, manufacturerFilter, sort, offset, limit);

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
    public void BuildWhereClause_ManufacturerFilter_AddsManufacturerCondition()
    {
        var query = MakeQuery(manufacturerFilter: "Acme Corp");

        var (sql, parameters) = LocalCatalogQueryBuilder.BuildWhereClause(query);

        Assert.Contains("ci.manufacturer =", sql);
        Assert.Single(parameters);
        Assert.Equal("Acme Corp", parameters[0].Value);
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

        var andCount = sql.Split("AND").Length - 1;
        Assert.Equal(1, andCount);
    }
}
