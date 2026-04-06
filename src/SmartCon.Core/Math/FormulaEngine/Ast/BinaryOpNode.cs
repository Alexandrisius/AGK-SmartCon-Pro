namespace SmartCon.Core.Math.FormulaEngine.Ast;

internal sealed record BinaryOpNode(BinaryOp Op, AstNode Left, AstNode Right) : AstNode;
