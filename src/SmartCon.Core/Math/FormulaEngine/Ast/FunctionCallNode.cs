namespace SmartCon.Core.Math.FormulaEngine.Ast;

internal sealed record FunctionCallNode(string Name, IReadOnlyList<AstNode> Args) : AstNode;
