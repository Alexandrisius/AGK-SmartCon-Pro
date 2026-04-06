namespace SmartCon.Core.Math.FormulaEngine;

internal enum TokenType
{
    Number,
    Identifier,
    String,

    // Арифметика
    Plus,
    Minus,
    Star,
    Slash,
    Caret,
    Percent,

    // Скобки / запятая
    LParen,
    RParen,
    Comma,

    // Сравнения
    Less,
    Greater,
    LessEqual,
    GreaterEqual,
    Equal,
    NotEqual,

    EOF
}
