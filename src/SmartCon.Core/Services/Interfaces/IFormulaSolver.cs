namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Universal AST parser and solver for Revit formulas (ADR-005).
/// Supports: if(), and(), or(), arithmetic, comparisons, trigonometry.
/// Implementation: SmartCon.Core/Services/Implementation/FormulaSolver.cs
/// </summary>
public interface IFormulaSolver
{
    /// <summary>
    /// Direct evaluation of a formula with given parameters (Internal Units).
    /// </summary>
    double Evaluate(string formula, IReadOnlyDictionary<string, double> parameterValues);

    /// <summary>
    /// Inverse solving: find the value of variableName for which formula = targetValue.
    /// Linear formulas — algebraic inversion. Complex (if, trig) — bisection/Newton.
    /// </summary>
    double SolveFor(string formula, string variableName, double targetValue,
                    IReadOnlyDictionary<string, double> otherValues);

    /// <summary>
    /// Parse size_lookup(...) — extract table name and parameter order.
    /// </summary>
    (string TableName, IReadOnlyList<string> ParameterOrder) ParseSizeLookup(string formula);
}
