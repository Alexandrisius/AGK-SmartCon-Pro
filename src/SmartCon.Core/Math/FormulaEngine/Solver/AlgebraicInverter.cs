using SmartCon.Core.Math.FormulaEngine.Ast;

namespace SmartCon.Core.Math.FormulaEngine.Solver;

/// <summary>
/// Algebraic inversion of linear formulas.
/// Checks linearity via probes f(0), f(1), f(2):
///   f(x) = a*x + b -> a = f(1)-f(0), b = f(0), check: |f(2) - (2a+b)| &lt; epsilon
/// If linear -> x = (target - b) / a
/// </summary>
internal static class AlgebraicInverter
{
    private const double Epsilon = Core.Tolerance.Default;

    /// <summary>
    /// Try to solve a formula algebraically (for linear formulas).
    /// Returns null if the formula is non-linear or a is approximately 0.
    /// </summary>
    internal static double? TrySolveLinear(
        AstNode ast,
        string variableName,
        double targetValue,
        IReadOnlyDictionary<string, double> otherValues)
    {
        // Compute f(0), f(1), f(2) by substituting variable values
        double f0 = EvalAt(ast, variableName, 0.0, otherValues);
        double f1 = EvalAt(ast, variableName, 1.0, otherValues);
        double f2 = EvalAt(ast, variableName, 2.0, otherValues);

        // Check for NaN/Infinity
        if (!IsFinite(f0) || !IsFinite(f1) || !IsFinite(f2))
            return null;

        double a = f1 - f0;
        double b = f0;

        // Linearity check: f(2) should be approximately 2a + b
        double expected = 2.0 * a + b;
        if (System.Math.Abs(f2 - expected) > Epsilon)
            return null; // non-linear

        // a approximately 0 -> formula does not depend on the variable (or is constant)
        if (System.Math.Abs(a) < Epsilon)
            return null;

        double result = (targetValue - b) / a;
        if (!IsFinite(result))
            return null;

        // Verification: f(result) should be approximately target.
        // Necessary for piecewise formulas (IF) where linearity on [0,1,2]
        // does not guarantee correctness over the entire range.
        double fResult = EvalAt(ast, variableName, result, otherValues);
        if (!IsFinite(fResult) || System.Math.Abs(fResult - targetValue) > Core.Lookup.SolverVerification)
            return null;

        return result;
    }

    private static double EvalAt(
        AstNode ast,
        string variableName,
        double value,
        IReadOnlyDictionary<string, double> otherValues)
    {
        var vars = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in otherValues) vars[kvp.Key] = kvp.Value;
        vars[variableName] = value;
        return Evaluator.Evaluate(ast, vars);
    }

    private static bool IsFinite(double d)
        => !double.IsNaN(d) && !double.IsInfinity(d);
}
