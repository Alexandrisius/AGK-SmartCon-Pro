using SmartCon.Core.Math.FormulaEngine.Ast;

namespace SmartCon.Core.Math.FormulaEngine.Solver;

/// <summary>
/// Extracts all variables from a formula AST tree.
/// Distinguishes variables from functions (functions = FunctionCallNode/IfNode/SizeLookupNode).
/// </summary>
internal static class VariableExtractor
{
    internal static HashSet<string> Extract(AstNode node)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(node, result);
        return result;
    }

    private static void Collect(AstNode node, HashSet<string> vars)
    {
        switch (node)
        {
            case VariableNode v:
                vars.Add(v.Name);
                break;

            case BinaryOpNode b:
                Collect(b.Left, vars);
                Collect(b.Right, vars);
                break;

            case UnaryOpNode u:
                Collect(u.Operand, vars);
                break;

            case IfNode ifn:
                Collect(ifn.Condition, vars);
                Collect(ifn.TrueExpr, vars);
                Collect(ifn.FalseExpr, vars);
                break;

            case FunctionCallNode f:
                foreach (var arg in f.Args)
                    Collect(arg, vars);
                break;

            // SizeLookupNode query parameters are variables too
            case SizeLookupNode sl:
                foreach (var qp in sl.QueryParameters)
                    vars.Add(qp);
                break;

                // NumberNode — nothing
        }
    }
}
