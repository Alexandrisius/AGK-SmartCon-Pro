namespace SmartCon.Core.Math.FormulaEngine;

internal readonly record struct Token(TokenType Type, string Text, double NumValue = 0.0);
