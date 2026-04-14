using SmartCon.Core.Logging;
using SmartCon.Core.Math.FormulaEngine.Ast;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Math.FormulaEngine.Solver;

/// <summary>
/// Production-ready AST parser and solver for Revit formulas.
/// Implements IFormulaSolver (ADR-005).
/// Pipeline: UnitStripper -> Tokenizer -> Parser -> Evaluator/Solver.
/// 
/// Also provides static methods (ExtractVariables, ParseSizeLookupRaw)
/// for use from the Revit layer without DI (compatibility with MiniFormulaSolver API).
/// </summary>
public sealed class FormulaSolver : IFormulaSolver
{
    // ── IFormulaSolver ───────────────────────────────────────────────────

    public double Evaluate(string formula, IReadOnlyDictionary<string, double> parameterValues)
    {
        if (string.IsNullOrWhiteSpace(formula))
            throw new FormulaParseException("Formula is empty");

        var (normFormula, normVars) = NormalizeForEvaluate(formula, parameterValues);
        var ast = ParseToAst(normFormula);
        return Evaluator.Evaluate(ast, normVars);
    }

    public double SolveFor(string formula, string variableName, double targetValue,
        IReadOnlyDictionary<string, double> otherValues)
    {
        if (string.IsNullOrWhiteSpace(formula))
            throw new FormulaParseException("Formula is empty");

        var (normFormula, normVar, normOthers) = NormalizeForSolveFor(formula, variableName, otherValues);
        var ast = ParseToAst(normFormula);

        // Check: is the variable present in the formula?
        var vars = VariableExtractor.Extract(ast);
        if (!vars.Contains(normVar))
            throw new FormulaParseException(
                $"Variable '{variableName}' not found in formula '{formula}'");

        // 1. IF-Simplifier: substitute known -> collapse IF branches
        var simplified = IfSimplifier.Simplify(ast, normOthers, normVar);

        // If after simplification contains SizeLookupNode — cannot solve
        if (SizeLookupParser.FindFirst(simplified) is not null)
            throw new FormulaParseException("Cannot solve formula containing size_lookup");

        // 2. Algebraic inversion (linear formulas)
        var algebraic = AlgebraicInverter.TrySolveLinear(simplified, normVar, targetValue, normOthers);
        if (algebraic.HasValue)
            return algebraic.Value;

        // 3. Bisection (fallback for non-linear)
        var bisection = BisectionSolver.Solve(
            x =>
            {
                var evalVars = new Dictionary<string, double>(normOthers, StringComparer.OrdinalIgnoreCase)
                {
                    [normVar] = x
                };
                return Evaluator.Evaluate(simplified, evalVars);
            },
            targetValue);

        if (bisection.HasValue)
            return bisection.Value;

        throw new FormulaParseException(
            $"Cannot solve formula '{formula}' for '{variableName}' = {targetValue}");
    }

    public (string TableName, IReadOnlyList<string> ParameterOrder) ParseSizeLookup(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
            throw new FormulaParseException("Formula is empty");

        var ast = ParseToAst(formula);
        var slNode = SizeLookupParser.FindFirst(ast);

        if (slNode is null)
            throw new FormulaParseException($"No size_lookup found in formula '{formula}'");

        return (slNode.TableName, slNode.QueryParameters);
    }

    // ── Static methods (compatibility with MiniFormulaSolver API) ────────

    /// <summary>
    /// Direct evaluation (static method for Revit layer).
    /// </summary>
    internal static double EvaluateStatic(string formula, IReadOnlyDictionary<string, double> variables)
    {
        try
        {
            var (normFormula, normVars) = NormalizeForEvaluate(formula, variables);
            var ast = ParseToAst(normFormula);
            var result = Evaluator.Evaluate(ast, normVars);
            SmartConLogger.FormulaOk("Evaluate", formula,
                $"{result:G6}  vars=[{FormatVars(variables)}]");
            return result;
        }
        catch (Exception ex)
        {
            SmartConLogger.FormulaFail("Evaluate", formula,
                $"{ex.GetType().Name}: {ex.Message}  vars=[{FormatVars(variables)}]");
            throw;
        }
    }

    /// <summary>
    /// Inverse solving (static method, returns null instead of exception).
    /// Compatible with MiniFormulaSolver.SolveFor API.
    /// </summary>
    internal static double? SolveForStatic(string formula, string variableName, double targetValue,
        IReadOnlyDictionary<string, double>? otherValues = null)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return null;

        try
        {
            var others = otherValues ?? new Dictionary<string, double>();
            var (normFormula, normVar, normOthers) = NormalizeForSolveFor(formula, variableName, others);
            var ast = ParseToAst(normFormula);
            var vars = VariableExtractor.Extract(ast);
            if (!vars.Contains(normVar))
            {
                SmartConLogger.FormulaFail("SolveFor", formula,
                    $"variable '{variableName}' not found in AST  others=[{FormatVars(others)}]");
                return null;
            }

            var simplified = IfSimplifier.Simplify(ast, normOthers, normVar);

            if (SizeLookupParser.FindFirst(simplified) is not null)
            {
                SmartConLogger.Formula(
                    $"[SolveFor] '{formula}' → contains size_lookup, skipped (needs LookupTable)");
                return null;
            }

            var algebraic = AlgebraicInverter.TrySolveLinear(simplified, normVar, targetValue, normOthers);
            if (algebraic.HasValue)
            {
                SmartConLogger.FormulaOk("SolveFor", formula,
                    $"algebraic: {variableName}={algebraic.Value:G6}  target={targetValue:G6}  others=[{FormatVars(others)}]");
                return algebraic.Value;
            }

            var bisection = BisectionSolver.Solve(
                x =>
                {
                    var evalVars = new Dictionary<string, double>(normOthers, StringComparer.OrdinalIgnoreCase)
                    {
                        [normVar] = x
                    };
                    return Evaluator.Evaluate(simplified, evalVars);
                },
                targetValue);

            if (bisection.HasValue)
            {
                SmartConLogger.FormulaOk("SolveFor", formula,
                    $"bisection: {variableName}={bisection.Value:G6}  target={targetValue:G6}  others=[{FormatVars(others)}]");
                return bisection.Value;
            }

            SmartConLogger.FormulaFail("SolveFor", formula,
                $"no solution found  var='{variableName}' target={targetValue:G6}  others=[{FormatVars(others)}]");
            return null;
        }
        catch (Exception ex)
        {
            SmartConLogger.FormulaFail("SolveFor", formula,
                $"{ex.GetType().Name}: {ex.Message}  var='{variableName}' target={targetValue:G6}");
            return null;
        }
    }

    /// <summary>
    /// Parse size_lookup (static, returns null instead of exception).
    /// Compatible with MiniFormulaSolver.ParseSizeLookup API.
    /// </summary>
    internal static (string TableName, string TargetParameter, IReadOnlyList<string> QueryParameters)?
        ParseSizeLookupStatic(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return null;

        try
        {
            var ast = ParseToAst(formula);
            var slNode = SizeLookupParser.FindFirst(ast);
            if (slNode is null)
            {
                SmartConLogger.Formula($"[ParseSizeLookup] '{formula}' → not a size_lookup");
                return null;
            }

            SmartConLogger.FormulaOk("ParseSizeLookup", formula,
                $"table='{slNode.TableName}' target='{slNode.TargetParameter}' query=[{string.Join(", ", slNode.QueryParameters)}]");
            return (slNode.TableName, slNode.TargetParameter, slNode.QueryParameters);
        }
        catch (Exception ex)
        {
            SmartConLogger.FormulaFail("ParseSizeLookup", formula,
                $"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Extract variables (static method).
    /// Compatible with MiniFormulaSolver.ExtractVariables API.
    /// </summary>
    internal static IReadOnlyList<string> ExtractVariablesStatic(string formula)
    {
        if (string.IsNullOrWhiteSpace(formula))
            return [];

        try
        {
            var ast = ParseToAst(formula);
            var result = VariableExtractor.Extract(ast).ToList();
            SmartConLogger.FormulaOk("ExtractVars", formula,
                $"[{string.Join(", ", result)}]");
            return result;
        }
        catch (Exception ex)
        {
            SmartConLogger.FormulaFail("ExtractVars", formula,
                $"{ex.GetType().Name}: {ex.Message}");
            return [];
        }
    }

    // ── Space-containing name normalization ──────────────────────────────────

    /// <summary>
    /// Replaces variable names with spaces with aliases __p0__, __p1__
    /// so the tokenizer does not split them into separate tokens.
    /// Longer names are replaced first to avoid partial matches.
    /// </summary>
    private static (string normFormula, Dictionary<string, double> normVars)
        NormalizeForEvaluate(string formula, IReadOnlyDictionary<string, double> variables)
    {
        var spaced = variables.Keys
            .Where(k => k.Contains(' '))
            .OrderByDescending(k => k.Length)
            .ToList();

        if (spaced.Count == 0)
            return (formula, new Dictionary<string, double>(variables, StringComparer.OrdinalIgnoreCase));

        var normFormula = formula;
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int idx = 0;
        foreach (var name in spaced)
        {
            var alias = $"__p{idx++}__";
            normFormula = normFormula.Replace(name, alias);
            aliases[name] = alias;
        }

        var normVars = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in variables)
        {
            var key = aliases.TryGetValue(kv.Key, out var a) ? a : kv.Key;
            normVars[key] = kv.Value;
        }
        return (normFormula, normVars);
    }

    private static (string normFormula, string normVarName, Dictionary<string, double> normOthers)
        NormalizeForSolveFor(string formula, string variableName,
            IReadOnlyDictionary<string, double> others)
    {
        var spaced = new List<string>();
        if (variableName.Contains(' ')) spaced.Add(variableName);
        foreach (var k in others.Keys)
            if (k.Contains(' ') && !spaced.Any(s =>
                    string.Equals(s, k, StringComparison.OrdinalIgnoreCase)))
                spaced.Add(k);
        spaced = spaced.OrderByDescending(s => s.Length).ToList();

        if (spaced.Count == 0)
            return (formula, variableName,
                new Dictionary<string, double>(others, StringComparer.OrdinalIgnoreCase));

        var normFormula = formula;
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int idx = 0;
        foreach (var name in spaced)
        {
            var alias = $"__p{idx++}__";
            normFormula = normFormula.Replace(name, alias);
            aliases[name] = alias;
        }

        string normVarName = aliases.TryGetValue(variableName, out var av) ? av : variableName;
        var normOthers = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in others)
        {
            var key = aliases.TryGetValue(kv.Key, out var a) ? a : kv.Key;
            normOthers[key] = kv.Value;
        }
        return (normFormula, normVarName, normOthers);
    }

    // ── Pipeline ─────────────────────────────────────────────────────────

    private static AstNode ParseToAst(string formula)
    {
        var stripped = UnitStripper.Strip(formula);
        var tokens = Tokenizer.Tokenize(stripped);
        return Parser.Parse(tokens);
    }

    private static string FormatVars(IReadOnlyDictionary<string, double> vars)
    {
        if (vars.Count == 0) return "";
        return string.Join(", ", vars.Select(kv => $"{kv.Key}={kv.Value:G6}"));
    }
}
