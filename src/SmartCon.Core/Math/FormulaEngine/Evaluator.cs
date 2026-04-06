using SmartCon.Core.Math.FormulaEngine.Ast;

namespace SmartCon.Core.Math.FormulaEngine;

/// <summary>
/// Рекурсивный вычислитель AST-дерева формулы Revit.
/// Все значения — double (Internal Units). Неизвестные переменные → 0.0.
/// Сравнения → 1.0 (true) / 0.0 (false). IF — ленивое вычисление.
/// </summary>
internal static class Evaluator
{
    private const double Epsilon = 1e-9;

    internal static double Evaluate(AstNode node, IReadOnlyDictionary<string, double> variables)
    {
        return node switch
        {
            NumberNode n => n.Value,

            VariableNode v => ResolveVariable(v.Name, variables),

            UnaryOpNode u => u.Op switch
            {
                UnaryOp.Negate => -Evaluate(u.Operand, variables),
                UnaryOp.Not => System.Math.Abs(Evaluate(u.Operand, variables)) < Epsilon ? 1.0 : 0.0,
                _ => throw new FormulaParseException($"Unknown unary op: {u.Op}")
            },

            BinaryOpNode b => EvaluateBinary(b, variables),

            IfNode ifn => System.Math.Abs(Evaluate(ifn.Condition, variables)) > Epsilon
                ? Evaluate(ifn.TrueExpr, variables)
                : Evaluate(ifn.FalseExpr, variables),

            FunctionCallNode f => EvaluateFunction(f, variables),

            // size_lookup → 0.0 в чистом парсере (lookup выполняется на уровне Revit)
            SizeLookupNode => 0.0,

            _ => throw new FormulaParseException($"Unknown AST node type: {node.GetType().Name}")
        };
    }

    // ── Binary ──────────────────────────────────────────────────────────

    private static double EvaluateBinary(BinaryOpNode b, IReadOnlyDictionary<string, double> vars)
    {
        double left = Evaluate(b.Left, vars);
        double right = Evaluate(b.Right, vars);

        return b.Op switch
        {
            BinaryOp.Add => left + right,
            BinaryOp.Sub => left - right,
            BinaryOp.Mul => left * right,
            BinaryOp.Div => right == 0.0 ? double.PositiveInfinity : left / right,
            BinaryOp.Pow => System.Math.Pow(left, right),
            BinaryOp.Mod => right == 0.0 ? 0.0 : left % right,

            BinaryOp.Lt => left < right ? 1.0 : 0.0,
            BinaryOp.Gt => left > right ? 1.0 : 0.0,
            BinaryOp.Le => left <= right + Epsilon ? 1.0 : 0.0,
            BinaryOp.Ge => left >= right - Epsilon ? 1.0 : 0.0,
            BinaryOp.Eq => System.Math.Abs(left - right) < Epsilon ? 1.0 : 0.0,
            BinaryOp.Ne => System.Math.Abs(left - right) >= Epsilon ? 1.0 : 0.0,

            _ => throw new FormulaParseException($"Unknown binary op: {b.Op}")
        };
    }

    // ── Functions ────────────────────────────────────────────────────────

    private static double EvaluateFunction(FunctionCallNode f, IReadOnlyDictionary<string, double> vars)
    {
        var name = f.Name; // уже lowercase из Parser

        // Логические
        if (name == "and")
        {
            foreach (var arg in f.Args)
                if (System.Math.Abs(Evaluate(arg, vars)) < Epsilon) return 0.0;
            return 1.0;
        }
        if (name == "or")
        {
            foreach (var arg in f.Args)
                if (System.Math.Abs(Evaluate(arg, vars)) > Epsilon) return 1.0;
            return 0.0;
        }
        if (name == "not")
        {
            if (f.Args.Count == 0) return 1.0;
            return System.Math.Abs(Evaluate(f.Args[0], vars)) < Epsilon ? 1.0 : 0.0;
        }

        // Одноаргументные
        if (f.Args.Count >= 1)
        {
            double a = Evaluate(f.Args[0], vars);

            switch (name)
            {
                case "sin":       return System.Math.Sin(a);
                case "cos":       return System.Math.Cos(a);
                case "tan":       return System.Math.Tan(a);
                case "asin":      return System.Math.Asin(a);
                case "acos":      return System.Math.Acos(a);
                case "atan":      return System.Math.Atan(a);
                case "abs":       return System.Math.Abs(a);
                case "sqrt":      return System.Math.Sqrt(a);
                case "round":     return System.Math.Round(a, MidpointRounding.AwayFromZero);
                case "roundup":   return System.Math.Ceiling(a);
                case "rounddown": return System.Math.Floor(a);
                case "log":       return System.Math.Log10(a);
                case "ln":        return System.Math.Log(a);
                case "exp":       return System.Math.Exp(a);
            }

            // Двуаргументные
            if (f.Args.Count >= 2)
            {
                double b = Evaluate(f.Args[1], vars);
                switch (name)
                {
                    case "min": return System.Math.Min(a, b);
                    case "max": return System.Math.Max(a, b);
                }
            }
        }

        // Неизвестная функция → 0
        return 0.0;
    }

    // ── Variable resolution (case-insensitive) ──────────────────────────

    private static double ResolveVariable(string name, IReadOnlyDictionary<string, double> variables)
    {
        // Точное совпадение
        if (variables.TryGetValue(name, out double val))
            return val;

        // Case-insensitive поиск
        foreach (var kv in variables)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
                return kv.Value;

        // Неизвестная переменная → 0.0 (совместимость с MiniFormulaSolver)
        return 0.0;
    }
}
