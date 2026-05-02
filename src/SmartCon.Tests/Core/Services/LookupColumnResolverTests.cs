using SmartCon.Core.Services;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class LookupColumnResolverTests
{
    [Fact]
    public void ResolveTableAlias_QuotedString_ReturnsUnquoted()
    {
        var dict = new Dictionary<string, string>();
        var result = LookupColumnResolver.ResolveTableAlias(dict, "\"MyTable\"");
        Assert.Equal("MyTable", result);
    }

    [Fact]
    public void ResolveTableAlias_VariableInDict_ReturnsFormula()
    {
        var dict = new Dictionary<string, string> { ["TableName"] = "\"ActualTable\"" };
        var result = LookupColumnResolver.ResolveTableAlias(dict, "TableName");
        Assert.Equal("ActualTable", result);
    }

    [Fact]
    public void ResolveTableAlias_NotFound_ReturnsToken()
    {
        var dict = new Dictionary<string, string>();
        var result = LookupColumnResolver.ResolveTableAlias(dict, "UnknownToken");
        Assert.Equal("UnknownToken", result);
    }

    [Fact]
    public void DependsOn_SameName_ReturnsTrue()
    {
        var dict = new Dictionary<string, string>();
        Assert.True(LookupColumnResolver.DependsOn(dict, "DN", "DN"));
    }

    [Fact]
    public void DependsOn_FormulaContainsTarget_ReturnsTrue()
    {
        var dict = new Dictionary<string, string> { ["Size"] = "DN * 2" };
        Assert.True(LookupColumnResolver.DependsOn(dict, "Size", "DN"));
    }

    [Fact]
    public void DependsOn_FormulaDoesNotContainTarget_ReturnsFalse()
    {
        var dict = new Dictionary<string, string> { ["Size"] = "radius * 2" };
        Assert.False(LookupColumnResolver.DependsOn(dict, "Size", "DN"));
    }

    [Fact]
    public void DependsOn_QuotedFormula_ReturnsFalse()
    {
        var dict = new Dictionary<string, string> { ["TableRef"] = "\"SomeTable\"" };
        Assert.False(LookupColumnResolver.DependsOn(dict, "TableRef", "SomeTable"));
    }

    [Fact]
    public void DependsOn_NotInDict_ReturnsFalse()
    {
        var dict = new Dictionary<string, string>();
        Assert.False(LookupColumnResolver.DependsOn(dict, "Size", "DN"));
    }

    [Theory]
    [InlineData("Param1", "1")]
    [InlineData("Radius25", "25")]
    [InlineData("DN_100", "100")]
    [InlineData("Test", null)]
    [InlineData("", null)]
    public void ExtractTrailingDigits_ReturnsExpected(string input, string? expected)
    {
        Assert.Equal(expected, LookupColumnResolver.ExtractTrailingDigits(input));
    }

    [Fact]
    public void FindQueryParamsForTable_MatchingTable_ReturnsWidestParams()
    {
        var snapshot = new List<(string? Name, string? Formula)>
        {
            ("Radius", "size_lookup(\"DN_Table\", radius, \"0\", DN)"),
            ("Radius2", "size_lookup(\"DN_Table\", radius, \"0\", DN, Schedule)"),
        };

        var dict = new Dictionary<string, string>();
        var result = LookupColumnResolver.FindQueryParamsForTable("DN_Table", snapshot, dict);

        Assert.Equal(2, result.Count);
        Assert.Equal("DN", result[0]);
        Assert.Equal("Schedule", result[1]);
    }

    [Fact]
    public void FindQueryParamsForTable_NoMatchingTable_ReturnsEmpty()
    {
        var snapshot = new List<(string? Name, string? Formula)>
        {
            ("Radius", "size_lookup(\"OtherTable\", radius, \"0\", DN)"),
        };

        var dict = new Dictionary<string, string>();
        var result = LookupColumnResolver.FindQueryParamsForTable("DN_Table", snapshot, dict);

        Assert.Empty(result);
    }

    [Fact]
    public void FindQueryParamsForTable_WithAlias_ResolvesTableName()
    {
        var snapshot = new List<(string? Name, string? Formula)>
        {
            ("Radius", "size_lookup(TableNameRef, radius, \"0\", DN)"),
        };

        var dict = new Dictionary<string, string> { ["TableNameRef"] = "\"DN_Table\"" };
        var result = LookupColumnResolver.FindQueryParamsForTable("DN_Table", snapshot, dict);

        Assert.Single(result);
    }

    [Fact]
    public void FindColumnIndex_DirectMatch_ReturnsCorrectIndex()
    {
        var snapshot = new List<(string? Name, string? Formula)>
        {
            ("Radius", "size_lookup(\"DN_Table\", radius, \"0\", DN, Schedule)"),
        };

        var dict = new Dictionary<string, string>();
        var (colIdx, allCols, viaDepends) = LookupColumnResolver.FindColumnIndex(
            "DN_Table", "DN", snapshot, dict);

        Assert.Equal(1, colIdx);
        Assert.Equal(2, allCols.Count);
        Assert.False(viaDepends);
    }

    [Fact]
    public void FindColumnIndex_NotFound_ReturnsMinusOne()
    {
        var snapshot = new List<(string? Name, string? Formula)>
        {
            ("Radius", "size_lookup(\"DN_Table\", radius, \"0\", DN)"),
        };

        var dict = new Dictionary<string, string>();
        var (colIdx, _, _) = LookupColumnResolver.FindColumnIndex(
            "DN_Table", "NonExistent", snapshot, dict);

        Assert.Equal(-1, colIdx);
    }

    [Fact]
    public void FindColumnIndex_ViaDependsOn_ReturnsFlag()
    {
        var snapshot = new List<(string? Name, string? Formula)>
        {
            ("Radius", "size_lookup(\"DN_Table\", radius, \"0\", Size)"),
        };

        var dict = new Dictionary<string, string> { ["Size"] = "DN * 2" };
        var (colIdx, _, viaDepends) = LookupColumnResolver.FindColumnIndex(
            "DN_Table", "DN", snapshot, dict);

        Assert.True(colIdx >= 0);
        Assert.True(viaDepends);
    }
}
