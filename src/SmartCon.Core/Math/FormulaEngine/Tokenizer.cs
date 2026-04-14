using System.Globalization;

namespace SmartCon.Core.Math.FormulaEngine;

/// <summary>
/// Lexer for Revit formulas.
/// Supports: numbers (integer, fractional, scientific notation), identifiers (ASCII, Cyrillic, _),
/// identifiers in [square brackets], string literals, operators, comparisons.
/// </summary>
internal static class Tokenizer
{
    internal static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        int i = 0;
        int len = input.Length;

        while (i < len)
        {
            char c = input[i];

            // Skip whitespace
            if (char.IsWhiteSpace(c)) { i++; continue; }

            // Numbers: 42, 3.14, 1.5e-3
            if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(input[i + 1])))
            {
                tokens.Add(ReadNumber(input, ref i));
                continue;
            }

            // String literals: "..."
            if (c == '"')
            {
                tokens.Add(ReadString(input, ref i));
                continue;
            }

            // Identifiers in [square brackets]: [Length-A], [Parameter 1]
            if (c == '[')
            {
                tokens.Add(ReadBracketedIdentifier(input, ref i));
                continue;
            }

            // Identifiers: letters, _, Cyrillic
            if (IsIdentStart(c))
            {
                tokens.Add(ReadIdentifier(input, ref i));
                continue;
            }

            // Two-character operators
            if (i + 1 < len)
            {
                string two = input.Substring(i, 2);
                switch (two)
                {
                    case "<=": tokens.Add(new Token(TokenType.LessEqual, "<=")); i += 2; continue;
                    case ">=": tokens.Add(new Token(TokenType.GreaterEqual, ">=")); i += 2; continue;
                    case "<>": tokens.Add(new Token(TokenType.NotEqual, "<>")); i += 2; continue;
                }
            }

            // Single-character operators
            switch (c)
            {
                case '+': tokens.Add(new Token(TokenType.Plus, "+")); i++; continue;
                case '-': tokens.Add(new Token(TokenType.Minus, "-")); i++; continue;
                case '*': tokens.Add(new Token(TokenType.Star, "*")); i++; continue;
                case '/': tokens.Add(new Token(TokenType.Slash, "/")); i++; continue;
                case '^': tokens.Add(new Token(TokenType.Caret, "^")); i++; continue;
                case '%': tokens.Add(new Token(TokenType.Percent, "%")); i++; continue;
                case '(': tokens.Add(new Token(TokenType.LParen, "(")); i++; continue;
                case ')': tokens.Add(new Token(TokenType.RParen, ")")); i++; continue;
                case ',': tokens.Add(new Token(TokenType.Comma, ",")); i++; continue;
                case '<': tokens.Add(new Token(TokenType.Less, "<")); i++; continue;
                case '>': tokens.Add(new Token(TokenType.Greater, ">")); i++; continue;
                case '=': tokens.Add(new Token(TokenType.Equal, "=")); i++; continue;
            }

            throw new FormulaParseException($"Unexpected character '{c}' at position {i}");
        }

        tokens.Add(new Token(TokenType.EOF, ""));
        return tokens;
    }

    private static Token ReadNumber(string input, ref int i)
    {
        int start = i;
        while (i < input.Length && char.IsDigit(input[i])) i++;

        if (i < input.Length && input[i] == '.')
        {
            i++;
            while (i < input.Length && char.IsDigit(input[i])) i++;
        }

        // Scientific notation: e+3, e-3, E3
        if (i < input.Length && (input[i] == 'e' || input[i] == 'E'))
        {
            i++;
            if (i < input.Length && (input[i] == '+' || input[i] == '-')) i++;
            while (i < input.Length && char.IsDigit(input[i])) i++;
        }

        string text = input[start..i];
        double val = double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
        return new Token(TokenType.Number, text, val);
    }

    private static Token ReadString(string input, ref int i)
    {
        i++; // skip opening "
        int start = i;
        while (i < input.Length && input[i] != '"') i++;
        string text = input[start..i];
        if (i < input.Length) i++; // skip closing "
        return new Token(TokenType.String, text);
    }

    private static Token ReadBracketedIdentifier(string input, ref int i)
    {
        i++; // skip [
        int start = i;
        while (i < input.Length && input[i] != ']') i++;
        string name = input[start..i];
        if (i < input.Length) i++; // skip ]
        return new Token(TokenType.Identifier, name);
    }

    private static Token ReadIdentifier(string input, ref int i)
    {
        int start = i;
        while (i < input.Length && IsIdentPart(input[i])) i++;
        string text = input[start..i];
        return new Token(TokenType.Identifier, text);
    }

    private static bool IsIdentStart(char c)
        => char.IsLetter(c) || c == '_';

    private static bool IsIdentPart(char c)
        => char.IsLetterOrDigit(c) || c == '_';
}
