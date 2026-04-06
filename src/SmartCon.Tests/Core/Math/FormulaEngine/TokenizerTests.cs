using SmartCon.Core.Math.FormulaEngine;
using Xunit;

namespace SmartCon.Tests.Core.Math.FormulaEngine;

public class TokenizerTests
{
    [Fact]
    public void Tokenize_Integer()
    {
        var tokens = Tokenizer.Tokenize("42");
        Assert.Equal(TokenType.Number, tokens[0].Type);
        Assert.Equal(42.0, tokens[0].NumValue);
    }

    [Fact]
    public void Tokenize_Decimal()
    {
        var tokens = Tokenizer.Tokenize("3.14");
        Assert.Equal(TokenType.Number, tokens[0].Type);
        Assert.Equal(3.14, tokens[0].NumValue, 10);
    }

    [Fact]
    public void Tokenize_ScientificNotation()
    {
        var tokens = Tokenizer.Tokenize("1.5e-3");
        Assert.Equal(TokenType.Number, tokens[0].Type);
        Assert.Equal(0.0015, tokens[0].NumValue, 10);
    }

    [Fact]
    public void Tokenize_Identifier()
    {
        var tokens = Tokenizer.Tokenize("DN");
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("DN", tokens[0].Text);
    }

    [Fact]
    public void Tokenize_CyrillicIdentifier()
    {
        var tokens = Tokenizer.Tokenize("ADSK_Диаметр");
        Assert.Equal(TokenType.Identifier, tokens[0].Type);
        Assert.Equal("ADSK_Диаметр", tokens[0].Text);
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

    [Fact] public void Tokenize_LessEqual() => Assert.Equal(TokenType.LessEqual, Tokenizer.Tokenize("<=")[0].Type);
    [Fact] public void Tokenize_GreaterEqual() => Assert.Equal(TokenType.GreaterEqual, Tokenizer.Tokenize(">=")[0].Type);
    [Fact] public void Tokenize_NotEqual() => Assert.Equal(TokenType.NotEqual, Tokenizer.Tokenize("<>")[0].Type);
    [Fact] public void Tokenize_Equal() => Assert.Equal(TokenType.Equal, Tokenizer.Tokenize("=")[0].Type);
    [Fact] public void Tokenize_Less() => Assert.Equal(TokenType.Less, Tokenizer.Tokenize("<")[0].Type);
    [Fact] public void Tokenize_Greater() => Assert.Equal(TokenType.Greater, Tokenizer.Tokenize(">")[0].Type);
    [Fact] public void Tokenize_Caret() => Assert.Equal(TokenType.Caret, Tokenizer.Tokenize("^")[0].Type);
    [Fact] public void Tokenize_Percent() => Assert.Equal(TokenType.Percent, Tokenizer.Tokenize("%")[0].Type);

    [Fact]
    public void Tokenize_FullFormula()
    {
        var tokens = Tokenizer.Tokenize("if(DN < 50, DN / 2 - 1, DN / 2 - 2)");
        var types = tokens.Select(t => t.Type).ToList();
        Assert.Contains(TokenType.Identifier, types); // if, DN
        Assert.Contains(TokenType.Less, types);
        Assert.Contains(TokenType.Number, types);
        Assert.Contains(TokenType.Comma, types);
        Assert.Equal(TokenType.EOF, types.Last());
    }

    [Fact]
    public void Tokenize_Empty_ReturnsEOF()
    {
        var tokens = Tokenizer.Tokenize("");
        Assert.Single(tokens);
        Assert.Equal(TokenType.EOF, tokens[0].Type);
    }

    [Fact]
    public void Tokenize_OnlyWhitespace_ReturnsEOF()
    {
        var tokens = Tokenizer.Tokenize("   ");
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
