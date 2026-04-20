using SmartCon.Core.Math.FormulaEngine;
using Xunit;

namespace SmartCon.Tests.Core.Math.FormulaEngine;

public sealed class TokenizerTests
{
    [Theory]
    [InlineData("42", 42.0)]
    [InlineData("3.14", 3.14)]
    [InlineData("1.5e-3", 0.0015)]
    public void Tokenize_NumberValues(string input, double expectedValue)
    {
        var tokens = Tokenizer.Tokenize(input);
        Assert.Equal(TokenType.Number, tokens[0].Type);
        Assert.Equal(expectedValue, tokens[0].NumValue, 5);
    }

    [Theory]
    [InlineData("DN", "DN")]
    [InlineData("ADSK_Диаметр", "ADSK_Диаметр")]
    public void Tokenize_Identifiers(string input, string expectedText)
    {
        var tokens = Tokenizer.Tokenize(input);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal(expectedText, tokens[0].Text);
    }

    [Fact]
    public void Tokenize_Operators()
    {
        Assert.Equal(TokenType.LessEqual, Tokenizer.Tokenize("<=")[0].Type);
        Assert.Equal(TokenType.GreaterEqual, Tokenizer.Tokenize(">=")[0].Type);
        Assert.Equal(TokenType.NotEqual, Tokenizer.Tokenize("<>")[0].Type);
        Assert.Equal(TokenType.Equal, Tokenizer.Tokenize("=")[0].Type);
        Assert.Equal(TokenType.Less, Tokenizer.Tokenize("<")[0].Type);
        Assert.Equal(TokenType.Greater, Tokenizer.Tokenize(">")[0].Type);
        Assert.Equal(TokenType.Caret, Tokenizer.Tokenize("^")[0].Type);
        Assert.Equal(TokenType.Percent, Tokenizer.Tokenize("%")[0].Type);
    }

    [Fact]
    public void Tokenize_BracketedIdentifier()
    {
        var tokens = Tokenizer.Tokenize("[Length-A]");
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("Length-A", tokens[0].Text);
    }

    [Fact]
    public void Tokenize_StringLiteral()
    {
        var tokens = Tokenizer.Tokenize("\"default\"");
        Assert.Equal(TokenType.String, tokens[0].Type);
        Assert.Equal("default", tokens[0].Text);
    }

    [Fact]
    public void Tokenize_FullFormula()
    {
        var tokens = Tokenizer.Tokenize("if(DN < 50, DN / 2 - 1, DN / 2 - 2)");
        var types = tokens.Select(t => t.Type).ToList();
        Assert.Contains(TokenType.Identifier, types);
        Assert.Contains(TokenType.Less, types);
        Assert.Contains(TokenType.Number, types);
        Assert.Contains(TokenType.Comma, types);
        Assert.Equal(TokenType.EOF, types.Last());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Tokenize_EmptyOrWhitespace_ReturnsEOF(string input)
    {
        var tokens = Tokenizer.Tokenize(input);
        Assert.Single(tokens);
        Assert.Equal(TokenType.EOF, tokens[0].Type);
    }

    [Fact]
    public void Tokenize_UnexpectedChar_Throws()
    {
        Assert.Throws<FormulaParseException>(() => Tokenizer.Tokenize("@"));
    }

    [Fact]
    public void Tokenize_ComplexNestedIf()
    {
        var tokens = Tokenizer.Tokenize("if(AND(a > 5, b < 10), 1, 0)");
        Assert.True(tokens.Count > 10);
        Assert.Equal(TokenType.EOF, tokens.Last().Type);
    }

    [Fact]
    public void Tokenize_SizeLookup()
    {
        var tokens = Tokenizer.Tokenize("size_lookup(\"Table1\", param, \"def\", DN, PN)");
        Assert.Equal("size_lookup", tokens[0].Text);
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
    }
}
