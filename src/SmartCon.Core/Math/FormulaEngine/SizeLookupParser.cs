using SmartCon.Core.Math.FormulaEngine.Ast;

namespace SmartCon.Core.Math.FormulaEngine;

/// <summary>
/// Извлечение метаданных size_lookup из AST-дерева.
/// Рекурсивно ищет SizeLookupNode во всех ветках (включая внутри IF).
/// 
/// TODO [Фаза 6B — Multi-Column LookupTable]:
/// Текущий метод FindFirst находит ОДИН SizeLookupNode.
/// Для мультиколоночного подбора нужен FindAll — собрать ВСЕ SizeLookupNode
/// из ВСЕХ формул семейства, чтобы построить полный маппинг:
///   столбец CSV → параметр семейства → коннектор элемента.
/// Это позволит фильтровать строки CSV по комбинациям DN нескольких коннекторов.
/// См. эскиз: ILookupTableService.GetAllRows / FilterRows / GetNearestValidCombination.
/// </summary>
internal static class SizeLookupParser
{
    /// <summary>
    /// Найти первый SizeLookupNode в AST (включая внутри IF-веток).
    /// Возвращает null если size_lookup не найден.
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
    /// Найти ВСЕ SizeLookupNode в AST (для будущего мультиколоночного парсинга).
    /// 
    /// TODO [Фаза 6B]: Использовать в связке с анализом всех FamilyParameter.Formula
    /// для построения полной карты: {tableName → [{columnIndex, parameterName, connectorIndex}]}.
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
