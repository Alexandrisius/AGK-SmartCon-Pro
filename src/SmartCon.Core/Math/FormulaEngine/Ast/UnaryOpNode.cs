namespace SmartCon.Core.Math.FormulaEngine.Ast;

internal sealed record UnaryOpNode(UnaryOp Op, AstNode Operand) : AstNode;
