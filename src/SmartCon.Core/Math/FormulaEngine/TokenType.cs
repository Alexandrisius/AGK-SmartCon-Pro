namespace SmartCon.Core.Math.FormulaEngine;

internal enum TokenType
{
    Number,
    Identifier,
    String,

    // Arithmetic
    Plus,
    Minus,
    Star,
    Slash,
    Caret,
    Percent,

    // Brackets / comma
    LParen,
    RParen,
    Comma,

    // Comparisons
    Less,
    Greater,
    LessEqual,
    GreaterEqual,
    Equal,
    NotEqual,

    EOF
}
