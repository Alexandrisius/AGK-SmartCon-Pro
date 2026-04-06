using SmartCon.Core.Math.FormulaEngine;
using SmartCon.Core.Math.FormulaEngine.Ast;
using SmartCon.Core.Math.FormulaEngine.Solver;
using Xunit;

namespace SmartCon.Tests.Core.Math.FormulaEngine;

public class IfSimplifierTests
{
    // ── Базовое упрощение ────────────────────────────────────────────────

    [Fact]
    public void Simplify_NumberNode_Unchanged()
    {
        var node = new NumberNode(42);
        var result = IfSimplifier.Simplify(node, Empty, "x");
        Assert.IsType<NumberNode>(result);
        Assert.Equal(42, ((NumberNode)result).Value);
    }

    [Fact]
    public void Simplify_KnownVariable_ReplacedWithNumber()
    {
        var node = new VariableNode("y");
        var result = IfSimplifier.Simplify(node, Vars("y", 10), "x");
        Assert.IsType<NumberNode>(result);
        Assert.Equal(10, ((NumberNode)result).Value);
    }

    [Fact]
    public void Simplify_UnknownVariable_Preserved()
    {
        var node = new VariableNode("x");
        var result = IfSimplifier.Simplify(node, Empty, "x");
        Assert.IsType<VariableNode>(result);
    }

    [Fact]
    public void Simplify_UnaryNegate_KnownOperand_Folded()
    {
        // -y where y=5 → NumberNode(-5)
        var node = new UnaryOpNode(UnaryOp.Negate, new VariableNode("y"));
        var result = IfSimplifier.Simplify(node, Vars("y", 5), "x");
        Assert.IsType<NumberNode>(result);
        Assert.Equal(-5, ((NumberNode)result).Value);
    }

    [Fact]
    public void Simplify_BinaryOp_AllKnown_Folded()
    {
        // a + b where a=3, b=7 → NumberNode(10)
        var node = new BinaryOpNode(BinaryOp.Add, new VariableNode("a"), new VariableNode("b"));
        var result = IfSimplifier.Simplify(node, Vars("a", 3, "b", 7), "x");
        Assert.IsType<NumberNode>(result);
        Assert.Equal(10, ((NumberNode)result).Value);
    }

    [Fact]
    public void Simplify_BinaryOp_ContainsUnknown_NotFolded()
    {
        // x + 5 → BinaryOpNode (x not known)
        var node = new BinaryOpNode(BinaryOp.Add, new VariableNode("x"), new NumberNode(5));
        var result = IfSimplifier.Simplify(node, Empty, "x");
        Assert.IsType<BinaryOpNode>(result);
    }

    // ── IF: condition определён → выбор ветки ───────────────────────────

    [Fact]
    public void Simplify_If_CondTrue_SelectsTrueBranch()
    {
        // if(y > 5, 10, 20) where y=8 → condition=1 → 10
        var node = new IfNode(
            new BinaryOpNode(BinaryOp.Gt, new VariableNode("y"), new NumberNode(5)),
            new NumberNode(10),
            new NumberNode(20));
        var result = IfSimplifier.Simplify(node, Vars("y", 8), "x");
        Assert.IsType<NumberNode>(result);
        Assert.Equal(10, ((NumberNode)result).Value);
    }

    [Fact]
    public void Simplify_If_CondFalse_SelectsFalseBranch()
    {
        // if(y > 5, 10, 20) where y=3 → condition=0 → 20
        var node = new IfNode(
            new BinaryOpNode(BinaryOp.Gt, new VariableNode("y"), new NumberNode(5)),
            new NumberNode(10),
            new NumberNode(20));
        var result = IfSimplifier.Simplify(node, Vars("y", 3), "x");
        Assert.IsType<NumberNode>(result);
        Assert.Equal(20, ((NumberNode)result).Value);
    }

    [Fact]
    public void Simplify_If_CondUndetermined_PreservesIfNode()
    {
        // if(x > 5, 10, 20) where x is unknown → IfNode preserved
        var node = new IfNode(
            new BinaryOpNode(BinaryOp.Gt, new VariableNode("x"), new NumberNode(5)),
            new NumberNode(10),
            new NumberNode(20));
        var result = IfSimplifier.Simplify(node, Empty, "x");
        Assert.IsType<IfNode>(result);
    }

    // ── IF: вложенные ───────────────────────────────────────────────────

    [Fact]
    public void Simplify_NestedIf_OuterTrue_InnerSimplified()
    {
        // if(a < 10, if(b > 5, 100, 200), 300) where a=5, b=8
        // → a<10 true → if(b>5, 100, 200) → b>5 true → 100
        var node = new IfNode(
            new BinaryOpNode(BinaryOp.Lt, new VariableNode("a"), new NumberNode(10)),
            new IfNode(
                new BinaryOpNode(BinaryOp.Gt, new VariableNode("b"), new NumberNode(5)),
                new NumberNode(100),
                new NumberNode(200)),
            new NumberNode(300));
        var result = IfSimplifier.Simplify(node, Vars("a", 5, "b", 8), "x");
        Assert.IsType<NumberNode>(result);
        Assert.Equal(100, ((NumberNode)result).Value);
    }

    [Fact]
    public void Simplify_NestedIf_OuterFalse_SkipsInner()
    {
        // if(a < 10, if(b > 5, 100, 200), 300) where a=20
        // → a<10 false → 300
        var node = new IfNode(
            new BinaryOpNode(BinaryOp.Lt, new VariableNode("a"), new NumberNode(10)),
            new IfNode(
                new BinaryOpNode(BinaryOp.Gt, new VariableNode("b"), new NumberNode(5)),
                new NumberNode(100),
                new NumberNode(200)),
            new NumberNode(300));
        var result = IfSimplifier.Simplify(node, Vars("a", 20), "x");
        Assert.IsType<NumberNode>(result);
        Assert.Equal(300, ((NumberNode)result).Value);
    }

    [Fact]
    public void Simplify_NestedIf_UnknownInCondition_PreservesStructure()
    {
        // if(x < 10, x * 2, x * 3) → x unknown → IF preserved, branches simplified
        var node = new IfNode(
            new BinaryOpNode(BinaryOp.Lt, new VariableNode("x"), new NumberNode(10)),
            new BinaryOpNode(BinaryOp.Mul, new VariableNode("x"), new NumberNode(2)),
            new BinaryOpNode(BinaryOp.Mul, new VariableNode("x"), new NumberNode(3)));
        var result = IfSimplifier.Simplify(node, Empty, "x");
        Assert.IsType<IfNode>(result);
        // Branches should still be BinaryOpNode (x is unknown)
        var ifResult = (IfNode)result;
        Assert.IsType<BinaryOpNode>(ifResult.TrueExpr);
        Assert.IsType<BinaryOpNode>(ifResult.FalseExpr);
    }

    // ── AND/OR в condition ───────────────────────────────────────────────

    [Fact]
    public void Simplify_If_WithAND_BothTrue_SelectsTrueBranch()
    {
        // if(AND(a > 5, b < 10), 1, 0) where a=8, b=3 → AND(1,1) → 1 → true branch
        var andNode = new FunctionCallNode("and", new AstNode[]
        {
            new BinaryOpNode(BinaryOp.Gt, new VariableNode("a"), new NumberNode(5)),
            new BinaryOpNode(BinaryOp.Lt, new VariableNode("b"), new NumberNode(10))
        });
        var node = new IfNode(andNode, new NumberNode(1), new NumberNode(0));
        var result = IfSimplifier.Simplify(node, Vars("a", 8, "b", 3), "x");
        Assert.IsType<NumberNode>(result);
        Assert.Equal(1, ((NumberNode)result).Value);
    }

    [Fact]
    public void Simplify_If_WithAND_OneFalse_SelectsFalseBranch()
    {
        // if(AND(a > 5, b < 10), 1, 0) where a=3, b=3 → AND(0,1) → 0 → false branch
        var andNode = new FunctionCallNode("and", new AstNode[]
        {
            new BinaryOpNode(BinaryOp.Gt, new VariableNode("a"), new NumberNode(5)),
            new BinaryOpNode(BinaryOp.Lt, new VariableNode("b"), new NumberNode(10))
        });
        var node = new IfNode(andNode, new NumberNode(1), new NumberNode(0));
        var result = IfSimplifier.Simplify(node, Vars("a", 3, "b", 3), "x");
        Assert.IsType<NumberNode>(result);
        Assert.Equal(0, ((NumberNode)result).Value);
    }

    // ── SizeLookup не упрощается ────────────────────────────────────────

    [Fact]
    public void Simplify_SizeLookupNode_Preserved()
    {
        var node = new SizeLookupNode("T", "R", "def", new[] { "DN" });
        var result = IfSimplifier.Simplify(node, Empty, "x");
        Assert.IsType<SizeLookupNode>(result);
    }

    // ── Case-insensitive ────────────────────────────────────────────────

    [Fact]
    public void Simplify_CaseInsensitive_VariableResolution()
    {
        // Variable "Diameter" matched by key "diameter"
        var node = new VariableNode("Diameter");
        var result = IfSimplifier.Simplify(node, Vars("diameter", 50), "x");
        Assert.IsType<NumberNode>(result);
        Assert.Equal(50, ((NumberNode)result).Value);
    }

    // ── Интеграция через FormulaSolver ───────────────────────────────────

    [Fact]
    public void SolveFor_IfLinear_TrueBranch()
    {
        // if(x < 100, x / 2, x / 3), solve for x, target=25 → x=50 (50<100 ✓)
        var result = FormulaSolver.SolveForStatic("if(x < 100, x / 2, x / 3)", "x", 25.0);
        Assert.NotNull(result);
        Assert.Equal(50.0, result!.Value, 1e-3);
    }

    [Fact]
    public void SolveFor_IfLinear_FalseBranch()
    {
        // if(x < 100, x / 2, x / 3), solve for x, target=50 → x=150 (150>=100 ✓)
        var result = FormulaSolver.SolveForStatic("if(x < 100, x / 2, x / 3)", "x", 50.0);
        Assert.NotNull(result);
        Assert.Equal(150.0, result!.Value, 1e-3);
    }

    // ── Вспомогательные ─────────────────────────────────────────────────

    private static readonly IReadOnlyDictionary<string, double> Empty =
        new Dictionary<string, double>();

    private static Dictionary<string, double> Vars(params object[] pairs)
    {
        var dict = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pairs.Length; i += 2)
            dict[(string)pairs[i]] = (double)(int)pairs[i + 1];
        return dict;
    }
}
