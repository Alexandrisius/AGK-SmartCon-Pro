using SmartCon.Core.Math.FormulaEngine;
using SmartCon.Core.Math.FormulaEngine.Ast;
using Xunit;

namespace SmartCon.Tests.Core.Math.FormulaEngine;

public sealed class ParserTests
{
    private static AstNode Parse(string formula)
    {
        var tokens = Tokenizer.Tokenize(formula);
        return Parser.Parse(tokens);
    }

    [Fact]
    public void Parse_Number()
    {
        var node = Parse("42");
        var n = Assert.IsType<NumberNode>(node);
        Assert.Equal(42.0, n.Value);
    }

    [Fact]
    public void Parse_Variable()
    {
        var node = Parse("DN");
        var v = Assert.IsType<VariableNode>(node);
        Assert.Equal("DN", v.Name);
    }

    [Fact]
    public void Parse_Addition()
    {
        var node = Parse("2 + 3");
        var b = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Add, b.Op);
    }

    [Fact]
    public void Parse_OperatorPrecedence()
    {
        // 2 + 3 * 4 → Add(2, Mul(3, 4))
        var node = Parse("2 + 3 * 4");
        var add = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Add, add.Op);
        Assert.IsType<NumberNode>(add.Left);
        var mul = Assert.IsType<BinaryOpNode>(add.Right);
        Assert.Equal(BinaryOp.Mul, mul.Op);
    }

    [Fact]
    public void Parse_PowerRightAssociative()
    {
        // 2 ^ 3 ^ 2 → Pow(2, Pow(3, 2))
        var node = Parse("2 ^ 3 ^ 2");
        var outer = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Pow, outer.Op);
        Assert.IsType<NumberNode>(outer.Left);
        var inner = Assert.IsType<BinaryOpNode>(outer.Right);
        Assert.Equal(BinaryOp.Pow, inner.Op);
    }

    [Fact]
    public void Parse_UnaryMinus()
    {
        var node = Parse("-5");
        var u = Assert.IsType<UnaryOpNode>(node);
        Assert.Equal(UnaryOp.Negate, u.Op);
    }

    [Fact]
    public void Parse_Parentheses()
    {
        var node = Parse("(2 + 3) * 4");
        var mul = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Mul, mul.Op);
        var add = Assert.IsType<BinaryOpNode>(mul.Left);
        Assert.Equal(BinaryOp.Add, add.Op);
    }

    [Fact]
    public void Parse_Comparison()
    {
        var node = Parse("a < 5");
        var b = Assert.IsType<BinaryOpNode>(node);
        Assert.Equal(BinaryOp.Lt, b.Op);
    }

    [Fact]
    public void Parse_If()
    {
        var node = Parse("if(a < 5, 1, 2)");
        var ifn = Assert.IsType<IfNode>(node);
        Assert.IsType<BinaryOpNode>(ifn.Condition);
        Assert.IsType<NumberNode>(ifn.TrueExpr);
        Assert.IsType<NumberNode>(ifn.FalseExpr);
    }

    [Fact]
    public void Parse_NestedIf()
    {
        var node = Parse("if(a < 5, if(b > 3, 10, 20), 30)");
        var outer = Assert.IsType<IfNode>(node);
        var inner = Assert.IsType<IfNode>(outer.TrueExpr);
        Assert.IsType<NumberNode>(inner.TrueExpr);
    }

    [Fact]
    public void Parse_AndFunction()
    {
        var node = Parse("AND(a > 5, b < 10)");
        var f = Assert.IsType<FunctionCallNode>(node);
        Assert.Equal("and", f.Name);
        Assert.Equal(2, f.Args.Count);
    }

    [Fact]
    public void Parse_NotUnary()
    {
        var node = Parse("NOT(a > 5)");
        var u = Assert.IsType<UnaryOpNode>(node);
        Assert.Equal(UnaryOp.Not, u.Op);
    }

    [Fact]
    public void Parse_SizeLookup()
    {
        var node = Parse("size_lookup(\"Table1\", param, \"def\", DN, PN)");
        var sl = Assert.IsType<SizeLookupNode>(node);
        Assert.Equal("Table1", sl.TableName);
        Assert.Equal("param", sl.TargetParameter);
        Assert.Equal("def", sl.DefaultValue);
        Assert.Equal(2, sl.QueryParameters.Count);
        Assert.Equal("DN", sl.QueryParameters[0]);
        Assert.Equal("PN", sl.QueryParameters[1]);
    }

    [Fact]
    public void Parse_BracketedIdentifiers()
    {
        var node = Parse("[Длина-A] + [Длина-B]");
        var b = Assert.IsType<BinaryOpNode>(node);
        var left = Assert.IsType<VariableNode>(b.Left);
        Assert.Equal("Длина-A", left.Name);
    }

    [Fact]
    public void Parse_PiConstant()
    {
        var node = Parse("pi()");
        var n = Assert.IsType<NumberNode>(node);
        Assert.Equal(System.Math.PI, n.Value, 10);
    }

    [Fact]
    public void Parse_EConstant()
    {
        var node = Parse("e");
        var n = Assert.IsType<NumberNode>(node);
        Assert.Equal(System.Math.E, n.Value, 10);
    }

    [Fact]
    public void Parse_FunctionCall_Sin()
    {
        var node = Parse("sin(0)");
        var f = Assert.IsType<FunctionCallNode>(node);
        Assert.Equal("sin", f.Name);
        Assert.Single(f.Args);
    }

    [Fact]
    public void Parse_UnbalancedParens_Throws()
    {
        Assert.Throws<FormulaParseException>(() => Parse("(2 + 3"));
    }

    [Fact]
    public void Parse_UnexpectedEOF_Throws()
    {
        Assert.Throws<FormulaParseException>(() => Parse("2 +"));
    }
}
