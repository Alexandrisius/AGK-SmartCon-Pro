using SmartCon.Core.Math.FormulaEngine.Ast;

namespace SmartCon.Core.Math.FormulaEngine.Solver;

/// <summary>
/// AST tree simplification by substituting known variables.
/// For IfNode: if condition is fully evaluable — selects the branch.
/// If condition contains an unknown variable — leaves IfNode as is.
/// </summary>
internal static class IfSimplifier
{
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Simplify AST by substituting known variables and collapsing evaluable IF branches.
    /// unknownVar — the variable being solved for (not substituted).
    /// </summary>
    internal static AstNode Simplify(AstNode node, IReadOnlyDictionary<string, double> knownVars, string unknownVar)
    {
        return SimplifyNode(node, knownVars, unknownVar);
    }

    private static AstNode SimplifyNode(AstNode node, IReadOnlyDictionary<string, double> known, string unknown)
    {
        switch (node)
        {
            case NumberNode:
                return node;

            case VariableNode v:
                if (string.Equals(v.Name, unknown, StringComparison.OrdinalIgnoreCase))
                    return node; // do not substitute the unknown
                if (TryResolve(v.Name, known, out double val))
                    return new NumberNode(val);
                return node;

            case UnaryOpNode u:
                var operand = SimplifyNode(u.Operand, known, unknown);
                if (operand is NumberNode n)
                {
                    return u.Op switch
                    {
                        UnaryOp.Negate => new NumberNode(-n.Value),
                        UnaryOp.Not => new NumberNode(System.Math.Abs(n.Value) < Epsilon ? 1.0 : 0.0),
                        _ => new UnaryOpNode(u.Op, operand)
                    };
                }
                return new UnaryOpNode(u.Op, operand);

            case BinaryOpNode b:
                var left = SimplifyNode(b.Left, known, unknown);
                var right = SimplifyNode(b.Right, known, unknown);
                if (left is NumberNode ln && right is NumberNode rn)
                    return new NumberNode(Evaluator.Evaluate(new BinaryOpNode(b.Op, ln, rn), known));
                return new BinaryOpNode(b.Op, left, right);

            case IfNode ifn:
                var cond = SimplifyNode(ifn.Condition, known, unknown);
                // If condition collapsed to a number -> select branch
                if (cond is NumberNode cn)
                {
                    var branch = System.Math.Abs(cn.Value) > Epsilon
                        ? ifn.TrueExpr
                        : ifn.FalseExpr;
                    return SimplifyNode(branch, known, unknown);
                }
                // Condition contains unknown -> leave IF, but simplify branches
                var trueSimp = SimplifyNode(ifn.TrueExpr, known, unknown);
                var falseSimp = SimplifyNode(ifn.FalseExpr, known, unknown);
                return new IfNode(cond, trueSimp, falseSimp);

            case FunctionCallNode f:
                var args = f.Args.Select(a => SimplifyNode(a, known, unknown)).ToList();
                if (args.All(a => a is NumberNode))
                    return new NumberNode(Evaluator.Evaluate(new FunctionCallNode(f.Name, args), known));
                return new FunctionCallNode(f.Name, args);

            case SizeLookupNode:
                return node; // size_lookup cannot be simplified

            default:
                return node;
        }
    }

    private static bool TryResolve(string name, IReadOnlyDictionary<string, double> vars, out double value)
    {
        if (vars.TryGetValue(name, out value))
            return true;
        foreach (var kv in vars)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = 0;
        return false;
    }
}
