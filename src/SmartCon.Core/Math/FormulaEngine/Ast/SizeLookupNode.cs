namespace SmartCon.Core.Math.FormulaEngine.Ast;

/// <summary>
/// AST node for size_lookup(tableName, targetParam, defaultValue, queryParam1, queryParam2, ...).
/// 
/// TODO [Phase 6B — Multi-Column LookupTable]:
/// Currently SizeLookupNode stores only metadata for ParseSizeLookup.
/// In the future multi-column lookup table parsing module, this node will be
/// used to build a complete mapping:
///   - Each FamilyParameter with a size_lookup formula -> one SizeLookupNode
///   - Collect ALL SizeLookupNodes of a family -> column-to-parameter mapping
///   - Parameter-to-connector mapping -> combinatorial CSV row filtering
/// See plan: ILookupTableService.GetAllRows / FilterRows / GetNearestValidCombination
/// </summary>
internal sealed record SizeLookupNode(
    string TableName,
    string TargetParameter,
    string DefaultValue,
    IReadOnlyList<string> QueryParameters) : AstNode;
