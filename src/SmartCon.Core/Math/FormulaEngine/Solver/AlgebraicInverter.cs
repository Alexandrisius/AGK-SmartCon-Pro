using SmartCon.Core.Math.FormulaEngine.Ast;

namespace SmartCon.Core.Math.FormulaEngine.Solver;

/// <summary>
/// Алгебраическая инверсия линейных формул.
/// Проверяет линейность пробой f(0), f(1), f(2):
///   f(x) = a*x + b → a = f(1)-f(0), b = f(0), проверка: |f(2) - (2a+b)| &lt; ε
/// Если линейно → x = (target - b) / a
/// </summary>
internal static class AlgebraicInverter
{
    private const double Epsilon = 1e-9;

    /// <summary>
    /// Попытаться решить формулу алгебраически (для линейных формул).
    /// Возвращает null если формула нелинейна или a ≈ 0.
    /// </summary>
    internal static double? TrySolveLinear(
        AstNode ast,
        string variableName,
        double targetValue,
        IReadOnlyDictionary<string, double> otherValues)
    {
        // Вычислить f(0), f(1), f(2) подставляя значения переменной
        double f0 = EvalAt(ast, variableName, 0.0, otherValues);
        double f1 = EvalAt(ast, variableName, 1.0, otherValues);
        double f2 = EvalAt(ast, variableName, 2.0, otherValues);

        // Проверить на NaN/Infinity
        if (!IsFinite(f0) || !IsFinite(f1) || !IsFinite(f2))
            return null;

        double a = f1 - f0;
        double b = f0;

        // Проверка линейности: f(2) должно быть ≈ 2a + b
        double expected = 2.0 * a + b;
        if (System.Math.Abs(f2 - expected) > Epsilon)
            return null; // нелинейна

        // a ≈ 0 → формула не зависит от переменной (или константна)
        if (System.Math.Abs(a) < Epsilon)
            return null;

        double result = (targetValue - b) / a;
        if (!IsFinite(result))
            return null;

        // Верификация: f(result) должно быть ≈ target.
        // Необходимо для кусочных формул (IF), где линейность на [0,1,2]
        // не гарантирует корректность на всём диапазоне.
        double fResult = EvalAt(ast, variableName, result, otherValues);
        if (!IsFinite(fResult) || System.Math.Abs(fResult - targetValue) > 1e-6)
            return null;

        return result;
    }

    private static double EvalAt(
        AstNode ast,
        string variableName,
        double value,
        IReadOnlyDictionary<string, double> otherValues)
    {
        var vars = new Dictionary<string, double>(otherValues, StringComparer.OrdinalIgnoreCase)
        {
            [variableName] = value
        };
        return Evaluator.Evaluate(ast, vars);
    }

    private static bool IsFinite(double d)
        => !double.IsNaN(d) && !double.IsInfinity(d);
}
