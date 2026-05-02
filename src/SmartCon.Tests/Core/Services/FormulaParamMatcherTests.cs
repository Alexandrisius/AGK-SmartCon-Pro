using SmartCon.Core.Services;
using Xunit;

namespace SmartCon.Tests.Core.Services;

public sealed class FormulaParamMatcherTests
{
    [Fact]
    public void ContainsParamReference_ExactMatch_ReturnsTrue()
    {
        Assert.True(FormulaParamMatcher.ContainsParamReference("DN * 2", "DN"));
    }

    [Fact]
    public void ContainsParamReference_MatchAtStart_ReturnsTrue()
    {
        Assert.True(FormulaParamMatcher.ContainsParamReference("Radius + 1", "Radius"));
    }

    [Fact]
    public void ContainsParamReference_MatchAtEnd_ReturnsTrue()
    {
        Assert.True(FormulaParamMatcher.ContainsParamReference("2 * Size", "Size"));
    }

    [Fact]
    public void ContainsParamReference_SubstringInLargerIdent_ReturnsFalse()
    {
        Assert.False(FormulaParamMatcher.ContainsParamReference("ADSK_Radius * 2", "Radius"));
    }

    [Fact]
    public void ContainsParamReference_SubstringAtEndOfLargerIdent_ReturnsFalse()
    {
        Assert.False(FormulaParamMatcher.ContainsParamReference("MyDN * 2", "DN"));
    }

    [Fact]
    public void ContainsParamReference_SubstringAtStartOfLargerIdent_ReturnsFalse()
    {
        Assert.False(FormulaParamMatcher.ContainsParamReference("DN_Size * 2", "DN"));
    }

    [Fact]
    public void ContainsParamReference_WithUnderscore_ReturnsFalse()
    {
        Assert.False(FormulaParamMatcher.ContainsParamReference("param_DN * 2", "DN"));
    }

    [Theory]
    [InlineData("DN_100")]
    [InlineData("100")]
    [InlineData("_DN")]
    public void ContainsParamReference_NotAWordBoundary_ReturnsFalse(string formula)
    {
        Assert.False(FormulaParamMatcher.ContainsParamReference(formula, "DN"));
    }

    [Fact]
    public void ContainsParamReference_CaseInsensitive_ReturnsTrue()
    {
        Assert.True(FormulaParamMatcher.ContainsParamReference("dn * 2", "DN"));
        Assert.True(FormulaParamMatcher.ContainsParamReference("Dn * 2", "DN"));
    }

    [Fact]
    public void ContainsParamReference_NotFound_ReturnsFalse()
    {
        Assert.False(FormulaParamMatcher.ContainsParamReference("radius * 2", "DN"));
    }

    [Fact]
    public void ContainsParamReference_MultipleOccurrences_FindsValidOne()
    {
        Assert.True(FormulaParamMatcher.ContainsParamReference("DN + MyDN + DN", "DN"));
    }

    [Fact]
    public void IsIdentChar_Letter_ReturnsTrue()
    {
        Assert.True(FormulaParamMatcher.IsIdentChar('a'));
        Assert.True(FormulaParamMatcher.IsIdentChar('Z'));
        Assert.True(FormulaParamMatcher.IsIdentChar('а'));
    }

    [Fact]
    public void IsIdentChar_Digit_ReturnsTrue()
    {
        Assert.True(FormulaParamMatcher.IsIdentChar('5'));
    }

    [Fact]
    public void IsIdentChar_Underscore_ReturnsTrue()
    {
        Assert.True(FormulaParamMatcher.IsIdentChar('_'));
    }

    [Fact]
    public void IsIdentChar_OtherChars_ReturnsFalse()
    {
        Assert.False(FormulaParamMatcher.IsIdentChar('+'));
        Assert.False(FormulaParamMatcher.IsIdentChar(' '));
        Assert.False(FormulaParamMatcher.IsIdentChar('('));
        Assert.False(FormulaParamMatcher.IsIdentChar('*'));
    }
}
