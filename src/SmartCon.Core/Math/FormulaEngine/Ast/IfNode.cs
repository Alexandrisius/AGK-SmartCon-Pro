namespace SmartCon.Core.Math.FormulaEngine.Ast;

internal sealed record IfNode(AstNode Condition, AstNode TrueExpr, AstNode FalseExpr) : AstNode;
