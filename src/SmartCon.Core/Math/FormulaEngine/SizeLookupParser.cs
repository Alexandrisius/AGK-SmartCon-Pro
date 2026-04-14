using SmartCon.Core.Math.FormulaEngine.Ast;

namespace SmartCon.Core.Math.FormulaEngine;

/// <summary>
/// Extracts size_lookup metadata from the AST tree.
/// Recursively finds SizeLookupNode in all branches (including inside IF).
/// 
/// TODO [Phase 6B — Multi-Column LookupTable]:
/// Current FindFirst method finds ONE SizeLookupNode.
/// For multi-column selection, FindAll is needed — collect ALL SizeLookupNode
/// from ALL family formulas to build a complete mapping:
///   CSV column -> family parameter -> element connector.
/// This enables filtering CSV rows by combinations of DN across multiple connectors.
/// See sketch: ILookupTableService.GetAllRows / FilterRows / GetNearestValidCombination.
/// </summary>
internal static class SizeLookupParser
{
    /// <summary>
    /// Find the first SizeLookupNode in AST (including inside IF branches).
    /// Returns null if no size_lookup found.
    /// </summary>
    internal static SizeLookupNode? FindFirst(AstNode node)
    {
        return node switch
        {
            SizeLookupNode sl => sl,
            IfNode ifn => FindFirst(ifn.TrueExpr) ?? FindFirst(ifn.FalseExpr) ?? FindFirst(ifn.Condition),
            BinaryOpNode b => FindFirst(b.Left) ?? FindFirst(b.Right),
            UnaryOpNode u => FindFirst(u.Operand),
            FunctionCallNode f => f.Args.Select(FindFirst).FirstOrDefault(n => n is not null),
            _ => null
        };
    }

    /// <summary>
    /// Find ALL SizeLookupNode in AST (for future multi-column parsing).
    /// 
    /// TODO [Phase 6B]: Use together with analysis of all FamilyParameter.Formula
    /// to build a complete map: {tableName -> [{columnIndex, parameterName, connectorIndex}]}.
    /// </summary>
    internal static List<SizeLookupNode> FindAll(AstNode node)
    {
        var result = new List<SizeLookupNode>();
        CollectAll(node, result);
        return result;
    }

    private static void CollectAll(AstNode node, List<SizeLookupNode> result)
    {
        switch (node)
        {
            case SizeLookupNode sl:
                result.Add(sl);
                break;
            case IfNode ifn:
                CollectAll(ifn.Condition, result);
                CollectAll(ifn.TrueExpr, result);
                CollectAll(ifn.FalseExpr, result);
                break;
            case BinaryOpNode b:
                CollectAll(b.Left, result);
                CollectAll(b.Right, result);
                break;
            case UnaryOpNode u:
                CollectAll(u.Operand, result);
                break;
            case FunctionCallNode f:
                foreach (var arg in f.Args) CollectAll(arg, result);
                break;
        }
    }
}
