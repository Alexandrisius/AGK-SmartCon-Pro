using SmartCon.Core.Math.FormulaEngine.Ast;

namespace SmartCon.Core.Math.FormulaEngine;

/// <summary>
/// Recursive descent парсер формул Revit.
/// Грамматика (приоритеты снизу вверх):
///   expression     = comparison ((AND | OR) comparison)*
///   comparison     = additive (('&lt;' | '&gt;' | '&lt;=' | '&gt;=' | '=' | '&lt;&gt;') additive)?
///   additive       = multiplicative (('+' | '-') multiplicative)*
///   multiplicative = power (('*' | '/' | '%') power)*
///   power          = unary ('^' power)?          // правоассоциативный
///   unary          = '-' unary | primary
///   primary        = NUMBER | STRING | '(' expr ')' | IF(...) | size_lookup(...) | FUNC(...) | IDENT
/// </summary>
internal sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    private Parser(List<Token> tokens)
    {
        _tokens = tokens;
        _pos = 0;
    }

    internal static AstNode Parse(List<Token> tokens)
    {
        var parser = new Parser(tokens);
        var node = parser.ParseExpression();
        if (parser.Current.Type != TokenType.EOF)
            throw new FormulaParseException(
                $"Unexpected token '{parser.Current.Text}' at position {parser._pos}");
        return node;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private Token Current => _pos < _tokens.Count
        ? _tokens[_pos]
        : new Token(TokenType.EOF, "");

    private Token Advance()
    {
        var t = Current;
        _pos++;
        return t;
    }

    private Token Expect(TokenType type)
    {
        if (Current.Type != type)
            throw new FormulaParseException(
                $"Expected {type} but got {Current.Type} ('{Current.Text}') at position {_pos}");
        return Advance();
    }

    private bool MatchIdentifier(string name)
        => Current.Type == TokenType.Identifier
           && string.Equals(Current.Text, name, StringComparison.OrdinalIgnoreCase);

    // ── Grammar rules ───────────────────────────────────────────────────

    private AstNode ParseExpression()
    {
        var left = ParseComparison();

        // AND / OR как инфиксные логические операторы (помимо функций AND(...), OR(...))
        while (Current.Type == TokenType.Identifier)
        {
            var name = Current.Text.ToUpperInvariant();
            if (name == "AND" && PeekNextIsNot(TokenType.LParen))
            {
                Advance();
                var right = ParseComparison();
                left = new FunctionCallNode("and", [left, right]);
            }
            else if (name == "OR" && PeekNextIsNot(TokenType.LParen))
            {
                Advance();
                var right = ParseComparison();
                left = new FunctionCallNode("or", [left, right]);
            }
            else break;
        }

        return left;
    }

    private bool PeekNextIsNot(TokenType type)
    {
        int next = _pos + 1;
        return next >= _tokens.Count || _tokens[next].Type != type;
    }

    private AstNode ParseComparison()
    {
        var left = ParseAdditive();

        var t = Current.Type;
        BinaryOp? op = t switch
        {
            TokenType.Less => BinaryOp.Lt,
            TokenType.Greater => BinaryOp.Gt,
            TokenType.LessEqual => BinaryOp.Le,
            TokenType.GreaterEqual => BinaryOp.Ge,
            TokenType.Equal => BinaryOp.Eq,
            TokenType.NotEqual => BinaryOp.Ne,
            _ => null
        };

        if (op is not null)
        {
            Advance();
            var right = ParseAdditive();
            return new BinaryOpNode(op.Value, left, right);
        }

        return left;
    }

    private AstNode ParseAdditive()
    {
        var left = ParseMultiplicative();

        while (Current.Type is TokenType.Plus or TokenType.Minus)
        {
            var op = Advance().Type == TokenType.Plus ? BinaryOp.Add : BinaryOp.Sub;
            var right = ParseMultiplicative();
            left = new BinaryOpNode(op, left, right);
        }

        return left;
    }

    private AstNode ParseMultiplicative()
    {
        var left = ParsePower();

        while (Current.Type is TokenType.Star or TokenType.Slash or TokenType.Percent)
        {
            var op = Advance().Type switch
            {
                TokenType.Star => BinaryOp.Mul,
                TokenType.Slash => BinaryOp.Div,
                _ => BinaryOp.Mod
            };
            var right = ParsePower();
            left = new BinaryOpNode(op, left, right);
        }

        return left;
    }

    private AstNode ParsePower()
    {
        var left = ParseUnary();

        if (Current.Type == TokenType.Caret)
        {
            Advance();
            var right = ParsePower(); // правоассоциативный: рекурсия в себя
            return new BinaryOpNode(BinaryOp.Pow, left, right);
        }

        return left;
    }

    private AstNode ParseUnary()
    {
        // Унарный минус
        if (Current.Type == TokenType.Minus)
        {
            Advance();
            var operand = ParseUnary();
            return new UnaryOpNode(UnaryOp.Negate, operand);
        }

        return ParsePrimary();
    }

    private AstNode ParsePrimary()
    {
        var cur = Current;

        // Число
        if (cur.Type == TokenType.Number)
        {
            Advance();
            return new NumberNode(cur.NumValue);
        }

        // Строковый литерал
        if (cur.Type == TokenType.String)
        {
            Advance();
            return new NumberNode(0.0); // строковые результаты не поддерживаются — возвращаем 0
        }

        // Скобки
        if (cur.Type == TokenType.LParen)
        {
            Advance();
            var expr = ParseExpression();
            Expect(TokenType.RParen);
            return expr;
        }

        // Идентификатор: функция, if, size_lookup, константа, переменная
        if (cur.Type == TokenType.Identifier)
        {
            var name = cur.Text;
            var nameLower = name.ToLowerInvariant();

            // if(cond, trueExpr, falseExpr)
            if (nameLower == "if" && PeekIsLParen())
                return ParseIf();

            // size_lookup(table, target, default, params...)
            if (nameLower == "size_lookup" && PeekIsLParen())
                return ParseSizeLookup();

            // NOT(expr) → UnaryOpNode
            if (nameLower == "not" && PeekIsLParen())
            {
                Advance(); // skip NOT
                Expect(TokenType.LParen);
                var operand = ParseExpression();
                Expect(TokenType.RParen);
                return new UnaryOpNode(UnaryOp.Not, operand);
            }

            // Константы без скобок
            if (nameLower == "e" && !PeekIsLParen())
            {
                Advance();
                return new NumberNode(System.Math.E);
            }

            // pi или pi()
            if (nameLower == "pi")
            {
                Advance();
                if (Current.Type == TokenType.LParen)
                {
                    Advance(); // (
                    Expect(TokenType.RParen); // )
                }
                return new NumberNode(System.Math.PI);
            }

            // Функция: name(args...)
            if (PeekIsLParen())
                return ParseFunctionCall();

            // Переменная
            Advance();
            return new VariableNode(name);
        }

        throw new FormulaParseException(
            $"Unexpected token '{cur.Text}' ({cur.Type}) at position {_pos}");
    }

    // ── Специальные конструкции ──────────────────────────────────────────

    private bool PeekIsLParen()
    {
        int next = _pos + 1;
        return next < _tokens.Count && _tokens[next].Type == TokenType.LParen;
    }

    private IfNode ParseIf()
    {
        Advance(); // skip "if"
        Expect(TokenType.LParen);
        var condition = ParseExpression();
        Expect(TokenType.Comma);
        var trueExpr = ParseExpression();
        Expect(TokenType.Comma);
        var falseExpr = ParseExpression();
        Expect(TokenType.RParen);
        return new IfNode(condition, trueExpr, falseExpr);
    }

    private SizeLookupNode ParseSizeLookup()
    {
        Advance(); // skip "size_lookup"
        Expect(TokenType.LParen);

        // Аргумент 1: имя таблицы (идентификатор или строка)
        string tableName = ReadSizeLookupStringArg();
        Expect(TokenType.Comma);

        // Аргумент 2: целевой параметр
        string targetParam = ReadSizeLookupStringArg();
        Expect(TokenType.Comma);

        // Аргумент 3: значение по умолчанию (строка или идентификатор)
        string defaultValue = ReadSizeLookupStringArg();

        // Оставшиеся аргументы: query-параметры
        var queryParams = new List<string>();
        while (Current.Type == TokenType.Comma)
        {
            Advance(); // skip comma
            queryParams.Add(ReadSizeLookupStringArg());
        }

        Expect(TokenType.RParen);
        return new SizeLookupNode(tableName, targetParam, defaultValue, queryParams);
    }

    /// <summary>
    /// Читает аргумент size_lookup: строковый литерал, число или один/несколько
    /// последовательных идентификаторов (для имён с пробелами, например
    /// "ADSK_Диаметр условный" → токены [ADSK_Диаметр] [условный]).
    /// Числовой default value: size_lookup(T, "p", 22 мм, DN) → после UnitStripper
    /// "22" становится Number-токеном.
    /// </summary>
    private string ReadSizeLookupStringArg()
    {
        // Строковый литерал — возвращаем как есть
        if (Current.Type == TokenType.String)
            return Advance().Text;

        // Собираем токены до разделителя (Comma/RParen) на глубине 0.
        // Обрабатывает: простые идентификаторы (D1), числа (22),
        // выражения (D2 + 4, 1.2 * D3), имена с пробелами (ADSK_Диаметр условный).
        var parts = new List<string>();
        int parenDepth = 0;
        while (Current.Type != TokenType.EOF)
        {
            if (parenDepth == 0 && Current.Type is TokenType.Comma or TokenType.RParen)
                break;

            if (Current.Type == TokenType.LParen) parenDepth++;
            if (Current.Type == TokenType.RParen) parenDepth--;

            parts.Add(Advance().Text);
        }

        if (parts.Count == 0)
            throw new FormulaParseException(
                $"Expected identifier, string or number in size_lookup at position {_pos}, got {Current.Type}");

        return string.Join(" ", parts);
    }

    private FunctionCallNode ParseFunctionCall()
    {
        var name = Advance().Text; // function name
        Expect(TokenType.LParen);

        var args = new List<AstNode>();
        if (Current.Type != TokenType.RParen)
        {
            args.Add(ParseExpression());
            while (Current.Type == TokenType.Comma)
            {
                Advance();
                args.Add(ParseExpression());
            }
        }

        Expect(TokenType.RParen);
        return new FunctionCallNode(name.ToLowerInvariant(), args);
    }
}
