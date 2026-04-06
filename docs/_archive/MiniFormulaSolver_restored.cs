using System.Collections.Generic;
using System.Linq;

namespace SmartCon.Core.Math;

/// <summary>
/// ╨Ь╨╕╨╜╨╕╨╝╨░╨╗╤М╨╜╤Л╨╣ ╤А╨╡╤И╨░╤В╨╡╨╗╤М ╤Д╨╛╤А╨╝╤Г╨╗ ╨┤╨╗╤П Phase 4 (S4 тАФ ResolvingParameters).
/// ╨Я╨╛╨┤╨┤╨╡╤А╨╢╨╕╨▓╨░╨╡╤В: ╤З╨╕╤Б╨╗╨░, ╨┐╨╡╤А╨╡╨╝╨╡╨╜╨╜╤Л╨╡, +, -, *, /, ╤Г╨╜╨░╤А╨╜╤Л╨╣ ╨╝╨╕╨╜╤Г╤Б, ╤Б╨║╨╛╨▒╨║╨╕.
/// ╨Ф╨╗╤П ╤Б╨╗╨╛╨╢╨╜╤Л╤Е ╤Д╨╛╤А╨╝╤Г╨╗ (if, trig, ╨╜╨╡╨╗╨╕╨╜╨╡╨╣╨╜╤Л╨╡) тАФ SolveFor ╨▓╨╛╨╖╨▓╤А╨░╤Й╨░╨╡╤В null.
/// Phase 6 (FormulaSolver) ╨┤╨╛╨▒╨░╨▓╨╕╤В ╨┐╨╛╨╗╨╜╤Г╤О ╨┐╨╛╨┤╨┤╨╡╤А╨╢╨║╤Г ╤З╨╡╤А╨╡╨╖ AST.
/// </summary>
internal static class MiniFormulaSolver
{
    // тФАтФА ╨Я╤Г╨▒╨╗╨╕╤З╨╜╨╛╨╡ API тФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФА

    /// <summary>
    /// ╨Т╤Л╤З╨╕╤Б╨╗╨╕╤В╤М ╨┐╤А╨╛╤Б╤В╨╛╨╡ ╨▓╤Л╤А╨░╨╢╨╡╨╜╨╕╨╡: "diameter / 2", "DN * 2 + 1".
    /// ╨Э╨╡╨╕╨╖╨▓╨╡╤Б╤В╨╜╤Л╨╡ ╨┐╨╡╤А╨╡╨╝╨╡╨╜╨╜╤Л╨╡ ╤В╤А╨░╨║╤В╤Г╤О╤В╤Б╤П ╨║╨░╨║ 0.
    /// ╨д╤Г╨╜╨║╤Ж╨╕╨╛╨╜╨░╨╗╤М╨╜╤Л╨╡ ╨▓╤Л╨╖╨╛╨▓╤Л (size_lookup, if, ...) ╤В╤А╨░╨║╤В╤Г╤О╤В╤Б╤П ╨║╨░╨║ 0.
    /// </summary>
    internal static double Evaluate(string formula,
        IReadOnlyDictionary<string, double> variables)
    {
        // ╨Э╨╛╤А╨╝╨░╨╗╨╕╨╖╤Г╨╡╨╝ ╨╕╨╝╨╡╨╜╨░ ╤Б ╨┐╤А╨╛╨▒╨╡╨╗╨░╨╝╨╕ ╨┤╨╛ ╤В╨╛╨║╨╡╨╜╨╕╨╖╨░╤Ж╨╕╨╕
        var (normFormula, normVars) = NormalizeForEvaluate(formula, variables);
        var tokens = Tokenize(normFormula);
        var ci = new Dictionary<string, double>(normVars, System.StringComparer.OrdinalIgnoreCase);
        var parser  = new Parser(tokens, ci);
        return parser.ParseExpr();
    }

    /// <summary>
    /// ╨Ю╨▒╤А╨░╤В╨╜╨╛╨╡ ╤А╨╡╤И╨╡╨╜╨╕╨╡ ╨╗╨╕╨╜╨╡╨╣╨╜╨╛╨╣ ╤Д╨╛╤А╨╝╤Г╨╗╤Л.
    /// ╨Я╤А╨╕╨╝╨╡╤А: SolveFor("diameter / 2", "diameter", 25) тЖТ 50.
    /// ╨Т╨╛╨╖╨▓╤А╨░╤Й╨░╨╡╤В null ╨╡╤Б╨╗╨╕:
    ///   тАФ ╨┐╨╡╤А╨╡╨╝╨╡╨╜╨╜╨░╤П ╨╜╨╡ ╤Д╨╕╨│╤Г╤А╨╕╤А╤Г╨╡╤В ╨▓ ╤Д╨╛╤А╨╝╤Г╨╗╨╡,
    ///   тАФ ╤Д╨╛╤А╨╝╤Г╨╗╨░ ╨╜╨╡╨╗╨╕╨╜╨╡╨╣╨╜╨░ (x*x, if, trig, size_lookup),
    ///   тАФ ╨┤╨╡╨╗╨╡╨╜╨╕╨╡ ╨╜╨░ ╨╜╨╛╨╗╤М.
    /// </summary>
    internal static double? SolveFor(string formula, string variableName,
        double targetValue,
        IReadOnlyDictionary<string, double>? otherValues = null)
    {
        var others = otherValues ?? new Dictionary<string, double>();

        // ╨Э╨╛╤А╨╝╨░╨╗╨╕╨╖╤Г╨╡╨╝ ╨╕╨╝╨╡╨╜╨░ ╤Б ╨┐╤А╨╛╨▒╨╡╨╗╨░╨╝╨╕ ╨┤╨╛ ╤В╨╛╨║╨╡╨╜╨╕╨╖╨░╤Ж╨╕╨╕
        var (normFormula, normVarName, normOthers) = NormalizeForSolveFor(formula, variableName, others);

        // ╨С╤Л╤Б╤В╤А╨░╤П ╨┐╤А╨╛╨▓╨╡╤А╨║╨░: ╨┐╨╡╤А╨╡╨╝╨╡╨╜╨╜╨░╤П ╨┐╤А╨╕╤Б╤Г╤В╤Б╤В╨▓╤Г╨╡╤В ╨▓ ╤В╨╛╨║╨╡╨╜╨░╤Е
        var vars = ExtractVariables(normFormula);
        if (!vars.Any(v => string.Equals(v, normVarName, System.StringComparison.OrdinalIgnoreCase)))
            return null;

        try
        {
            var dict0 = BuildVars(normOthers, normVarName, 0.0);
            var dict1 = BuildVars(normOthers, normVarName, 1.0);
            var dict2 = BuildVars(normOthers, normVarName, 2.0);

            double f0 = Evaluate(normFormula, dict0);
            double f1 = Evaluate(normFormula, dict1);
            double f2 = Evaluate(normFormula, dict2);

            double a = f1 - f0;
            double b = f0;

            // ╨Я╤А╨╛╨▓╨╡╤А╨║╨░ ╨╗╨╕╨╜╨╡╨╣╨╜╨╛╤Б╤В╨╕: f(2) тЙИ 2*a + b
            if (System.Math.Abs(f2 - (2.0 * a + b)) > 1e-6)
                return null;

            // ╨Я╨╡╤А╨╡╨╝╨╡╨╜╨╜╨░╤П ╨╜╨╡ ╨▓╨╗╨╕╤П╨╡╤В (╨║╨╛╤Н╤Д╤Д╨╕╤Ж╨╕╨╡╨╜╤В тЙИ 0)
            if (System.Math.Abs(a) < 1e-12)
                return null;

            return (targetValue - b) / a;
        }
        catch
        {
            return null;
        }
    }

    // тФАтФА ╨Э╨╛╤А╨╝╨░╨╗╨╕╨╖╨░╤Ж╨╕╤П ╨╕╨╝╤С╨╜ ╤Б ╨┐╤А╨╛╨▒╨╡╨╗╨░╨╝╨╕ тФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФА

    /// <summary>
    /// ╨Ч╨░╨╝╨╡╨╜╤П╨╡╤В ╨╕╨╝╨╡╨╜╨░ ╨┐╨╡╤А╨╡╨╝╨╡╨╜╨╜╤Л╤Е ╤Б ╨┐╤А╨╛╨▒╨╡╨╗╨░╨╝╨╕ ╨╜╨░ ╨┐╤Б╨╡╨▓╨┤╨╛╨╜╨╕╨╝╤Л __p0__, __p1__
    /// ╤З╤В╨╛╨▒╤Л ╤В╨╛╨║╨╡╨╜╨╕╨╖╨░╤В╨╛╤А ╨╜╨╡ ╤А╨░╨╖╨▒╨╕╨▓╨░╨╗ ╨╕╤Е ╨╜╨░ ╨╛╤В╨┤╨╡╨╗╤М╨╜╤Л╨╡ ╤В╨╛╨║╨╡╨╜╤Л.
    /// ╨Ф╨╗╨╕╨╜╨╜╤Л╨╡ ╨╕╨╝╨╡╨╜╨░ ╨╖╨░╨╝╨╡╨╜╤П╤О╤В╤Б╤П ╨┐╨╡╤А╨▓╤Л╨╝╨╕, ╤З╤В╨╛╨▒╤Л ╨╕╨╖╨▒╨╡╨╢╨░╤В╤М ╤З╨░╤Б╤В╨╕╤З╨╜╤Л╤Е ╤Б╨╛╨▓╨┐╨░╨┤╨╡╨╜╨╕╨╣.
    /// </summary>
    private static (string normFormula, Dictionary<string, double> normVars)
        NormalizeForEvaluate(string formula, IReadOnlyDictionary<string, double> variables)
    {
        var spaced = variables.Keys
            .Where(k => k.Contains(' '))
            .OrderByDescending(k => k.Length)
            .ToList();

        if (spaced.Count == 0)
            return (formula, new Dictionary<string, double>(variables, System.StringComparer.OrdinalIgnoreCase));

        var normFormula = formula;
        var aliases     = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        int idx         = 0;
        foreach (var name in spaced)
        {
            var alias = $"__p{idx++}__";
            normFormula    = normFormula.Replace(name, alias);
            aliases[name]  = alias;
        }

        var normVars = new Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var kv in variables)
        {
            var key = aliases.TryGetValue(kv.Key, out var a) ? a : kv.Key;
            normVars[key] = kv.Value;
        }
        return (normFormula, normVars);
    }

    /// <summary>
    /// ╨Э╨╛╤А╨╝╨░╨╗╨╕╨╖╤Г╨╡╤В formula, variableName ╨╕ otherValues: ╨╕╨╝╨╡╨╜╨░ ╤Б ╨┐╤А╨╛╨▒╨╡╨╗╨░╨╝╨╕ тЖТ ╨┐╤Б╨╡╨▓╨┤╨╛╨╜╨╕╨╝╤Л.
    /// </summary>
    private static (string normFormula, string normVarName, IReadOnlyDictionary<string, double> normOthers)
        NormalizeForSolveFor(string formula, string variableName, IReadOnlyDictionary<string, double> others)
    {
        var spaced = new List<string>();
        if (variableName.Contains(' ')) spaced.Add(variableName);
        foreach (var k in others.Keys)
            if (k.Contains(' ') && !spaced.Any(s => string.Equals(s, k, System.StringComparison.OrdinalIgnoreCase)))
                spaced.Add(k);
        spaced = spaced.OrderByDescending(s => s.Length).ToList();

        if (spaced.Count == 0)
            return (formula, variableName, others);

        var normFormula = formula;
        var aliases     = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        int idx         = 0;
        foreach (var name in spaced)
        {
            var alias     = $"__p{idx++}__";
            normFormula   = normFormula.Replace(name, alias);
            aliases[name] = alias;
        }

        string normVarName = aliases.TryGetValue(variableName, out var av) ? av : variableName;
        var normOthers     = new Dictionary<string, double>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var kv in others)
        {
            var key = aliases.TryGetValue(kv.Key, out var a) ? a : kv.Key;
            normOthers[key] = kv.Value;
        }
        return (normFormula, normVarName, normOthers);
    }

    /// <summary>
    /// ╨Я╨░╤А╤Б╨╕╨╜╨│ ╨▓╤Л╨╖╨╛╨▓╨░ size_lookup:
    ///   size_lookup(TableName, TargetParam, "DefaultValue", QueryParam1, QueryParam2, ...)
    /// ╨Т╨╛╨╖╨▓╤А╨░╤Й╨░╨╡╤В (TableName, TargetParameter, QueryParameters) ╨╕╨╗╨╕ null ╨╡╤Б╨╗╨╕ ╤Б╤В╤А╨╛╨║╨░
    /// ╨╜╨╡ ╤П╨▓╨╗╤П╨╡╤В╤Б╤П ╨▓╤Л╨╖╨╛╨▓╨╛╨╝ size_lookup.
    /// QueryParameters тАФ ╨░╤А╨│╤Г╨╝╨╡╨╜╤В╤Л ╨╜╨░╤З╨╕╨╜╨░╤П ╤Б ╨┐╨╛╨╖╨╕╤Ж╨╕╨╕ 3 (0-based).
    /// </summary>
    internal static (string TableName, string TargetParameter,
                     IReadOnlyList<string> QueryParameters)? ParseSizeLookup(string formula)
    {
        var trimmed = formula.Trim();
        var idx = trimmed.IndexOf("size_lookup", System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var parenStart = trimmed.IndexOf('(', idx);
        if (parenStart < 0) return null;

        // ╨Э╨░╨╣╤В╨╕ ╨╖╨░╨║╤А╤Л╨▓╨░╤О╤Й╤Г╤О ╤Б╨║╨╛╨▒╨║╤Г (╤Б ╤Г╤З╤С╤В╨╛╨╝ ╨▓╨╗╨╛╨╢╨╡╨╜╨╜╨╛╤Б╤В╨╕)
        int depth    = 1;
        int parenEnd = parenStart + 1;
        while (parenEnd < trimmed.Length && depth > 0)
        {
            if      (trimmed[parenEnd] == '(') depth++;
            else if (trimmed[parenEnd] == ')') depth--;
            parenEnd++;
        }
        if (depth != 0) return null;

        var argsStr = trimmed.Substring(parenStart + 1, parenEnd - parenStart - 2);
        var args    = SplitArgs(argsStr);

        // ╨Ь╨╕╨╜╨╕╨╝╤Г╨╝: TableName, TargetParam, Default, QueryParam1
        if (args.Count < 4) return null;

        var tableName   = args[0].Trim().Trim('"');
        var targetParam = args[1].Trim().Trim('"');
        // args[2] = default value (╤Б╤В╤А╨╛╨║╨╛╨▓╤Л╨╣ ╨╗╨╕╤В╨╡╤А╨░╨╗) тАФ ╨┐╤А╨╛╨┐╤Г╤Б╨║╨░╨╡╨╝
        var queryParams = args.Skip(3)
                              .Select(a => a.Trim().Trim('"'))
                              .Where(a => a.Length > 0)
                              .ToList();

        return (tableName, targetParam, queryParams);
    }

    /// <summary>
    /// ╨Ш╨╖╨▓╨╗╨╡╤З╤М ╨╕╨╝╨╡╨╜╨░ ╨▓╤Б╨╡╤Е ╨┐╨╡╤А╨╡╨╝╨╡╨╜╨╜╤Л╤Е ╨╕╨╖ ╤Д╨╛╤А╨╝╤Г╨╗╤Л (╨╕╤Б╨║╨╗╤О╤З╨░╤П ╨▓╤Л╨╖╨╛╨▓╤Л ╤Д╤Г╨╜╨║╤Ж╨╕╨╣).
    /// ╨Ш╤Б╨┐╨╛╨╗╤М╨╖╤Г╨╡╤В╤Б╤П FamilyParameterAnalyzer ╨┤╨╗╤П ╨┐╨╛╨╕╤Б╨║╨░ ╨║╨╛╤А╨╜╨╡╨▓╨╛╨│╨╛ ╨┐╨░╤А╨░╨╝╨╡╤В╤А╨░.
    /// </summary>
    internal static IReadOnlyList<string> ExtractVariables(string formula)
    {
        var tokens = Tokenize(formula);
        var result = new System.Collections.Generic.HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase);

        var knownFunctions = new System.Collections.Generic.HashSet<string>(
            System.StringComparer.OrdinalIgnoreCase)
        {
            "size_lookup", "if", "and", "or", "not", "abs",
            "round", "roundup", "rounddown",
            "sin", "cos", "tan", "asin", "acos", "atan",
            "sqrt", "min", "max", "pi", "e"
        };

        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Type != TokenType.Identifier) continue;

            var name = tokens[i].Text;
            if (knownFunctions.Contains(name)) continue;

            // ╨Х╤Б╨╗╨╕ ╤Б╨╗╨╡╨┤╤Г╤О╤Й╨╕╨╣ ╤В╨╛╨║╨╡╨╜ тАФ '(' тЖТ ╤Н╤В╨╛ ╨▓╤Л╨╖╨╛╨▓ ╤Д╤Г╨╜╨║╤Ж╨╕╨╕, ╨╜╨╡ ╨┐╨╡╤А╨╡╨╝╨╡╨╜╨╜╨░╤П
            bool isCall = (i + 1 < tokens.Count && tokens[i + 1].Type == TokenType.LParen);
            if (!isCall)
                result.Add(name);
        }

        return result.ToList();
    }

    // тФАтФА ╨в╨╛╨║╨╡╨╜╨╕╨╖╨░╤В╨╛╤А тФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФА

    private enum TokenType
    {
        Number, Identifier, String,
        Plus, Minus, Star, Slash,
        LParen, RParen, Comma,
        EOF
    }

    private readonly record struct Token(TokenType Type, string Text, double NumValue = 0.0);

    private static List<Token> Tokenize(string input)
    {
        var result = new List<Token>();
        int i      = 0;

        while (i < input.Length)
        {
            char c = input[i];

            if (char.IsWhiteSpace(c)) { i++; continue; }

            // ╨б╤В╤А╨╛╨║╨╛╨▓╤Л╨╣ ╨╗╨╕╤В╨╡╤А╨░╨╗ "..."
            if (c == '"')
            {
                int start = i++;
                while (i < input.Length && input[i] != '"') i++;
                if (i < input.Length) i++; // closing "
                result.Add(new Token(TokenType.String, input.Substring(start, i - start)));
                continue;
            }

            // ╨з╨╕╤Б╨╗╨╛╨▓╨╛╨╣ ╨╗╨╕╤В╨╡╤А╨░╨╗
            if (char.IsDigit(c) || (c == '.' && i + 1 < input.Length && char.IsDigit(input[i + 1])))
            {
                int start = i;
                while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.')) i++;
                // ╨╜╨░╤Г╤З╨╜╨░╤П ╨╜╨╛╤В╨░╤Ж╨╕╤П
                if (i < input.Length && (input[i] == 'e' || input[i] == 'E'))
                {
                    i++;
                    if (i < input.Length && (input[i] == '+' || input[i] == '-')) i++;
                    while (i < input.Length && char.IsDigit(input[i])) i++;
                }
                var text = input.Substring(start, i - start);
                double.TryParse(text,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out double val);
                result.Add(new Token(TokenType.Number, text, val));
                continue;
            }

            // ╨Ш╨┤╨╡╨╜╤В╨╕╤Д╨╕╨║╨░╤В╨╛╤А
            if (char.IsLetter(c) || c == '_')
            {
                int start = i;
                while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_')) i++;
                result.Add(new Token(TokenType.Identifier, input.Substring(start, i - start)));
                continue;
            }

            switch (c)
            {
                case '+': result.Add(new Token(TokenType.Plus,   "+")); i++; break;
                case '-': result.Add(new Token(TokenType.Minus,  "-")); i++; break;
                case '*': result.Add(new Token(TokenType.Star,   "*")); i++; break;
                case '/': result.Add(new Token(TokenType.Slash,  "/")); i++; break;
                case '(': result.Add(new Token(TokenType.LParen, "(")); i++; break;
                case ')': result.Add(new Token(TokenType.RParen, ")")); i++; break;
                case ',': result.Add(new Token(TokenType.Comma,  ",")); i++; break;
                default:  i++; break; // ╨┐╤А╨╛╨┐╤Г╤Б╨║╨░╨╡╨╝ ╨╜╨╡╨╕╨╖╨▓╨╡╤Б╤В╨╜╤Л╨╡ ╤Б╨╕╨╝╨▓╨╛╨╗╤Л
            }
        }

        result.Add(new Token(TokenType.EOF, string.Empty));
        return result;
    }

    // тФАтФА ╨Я╨░╤А╤Б╨╡╤А (recursive descent) тФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФА

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private readonly IReadOnlyDictionary<string, double> _variables;
        private int _pos;

        internal Parser(List<Token> tokens, IReadOnlyDictionary<string, double> variables)
        {
            _tokens    = tokens;
            _variables = variables;
        }

        private Token Current => _tokens[_pos];

        private Token Consume()
        {
            var t = _tokens[_pos];
            if (_pos < _tokens.Count - 1) _pos++;
            return t;
        }

        // expr = term (('+' | '-') term)*
        internal double ParseExpr()
        {
            double left = ParseTerm();
            while (Current.Type is TokenType.Plus or TokenType.Minus)
            {
                var op    = Consume();
                double right = ParseTerm();
                left = op.Type == TokenType.Plus ? left + right : left - right;
            }
            return left;
        }

        // term = factor (('*' | '/') factor)*
        private double ParseTerm()
        {
            double left = ParseFactor();
            while (Current.Type is TokenType.Star or TokenType.Slash)
            {
                var op    = Consume();
                double right = ParseFactor();
                if (op.Type == TokenType.Star)
                    left *= right;
                else
                    left = System.Math.Abs(right) < 1e-300 ? 0.0 : left / right;
            }
            return left;
        }

        // factor = number | string | '(' expr ')' | '-' factor | identifier ['(' arglist ')']
        private double ParseFactor()
        {
            // ╨г╨╜╨░╤А╨╜╤Л╨╣ ╨╝╨╕╨╜╤Г╤Б
            if (Current.Type == TokenType.Minus)
            {
                Consume();
                return -ParseFactor();
            }

            // ╨з╨╕╤Б╨╗╨╛
            if (Current.Type == TokenType.Number)
                return Consume().NumValue;

            // ╨б╤В╤А╨╛╨║╨╛╨▓╤Л╨╣ ╨╗╨╕╤В╨╡╤А╨░╨╗ тЖТ 0
            if (Current.Type == TokenType.String)
            {
                Consume();
                return 0.0;
            }

            // ╨б╨║╨╛╨▒╨║╨╕: ( expr )
            if (Current.Type == TokenType.LParen)
            {
                Consume(); // (
                double val = ParseExpr();
                if (Current.Type == TokenType.RParen) Consume(); // )
                return val;
            }

            // ╨Ш╨┤╨╡╨╜╤В╨╕╤Д╨╕╨║╨░╤В╨╛╤А ╨╕╨╗╨╕ ╨▓╤Л╨╖╨╛╨▓ ╤Д╤Г╨╜╨║╤Ж╨╕╨╕
            if (Current.Type == TokenType.Identifier)
            {
                var name = Consume().Text;

                // ╨Т╤Л╨╖╨╛╨▓ ╤Д╤Г╨╜╨║╤Ж╨╕╨╕: name ( ... ) тЖТ ╨┐╤А╨╛╨┐╤Г╤Б╨║╨░╨╡╨╝ ╨░╤А╨│╤Г╨╝╨╡╨╜╤В╤Л, ╨▓╨╛╨╖╨▓╤А╨░╤Й╨░╨╡╨╝ 0
                if (Current.Type == TokenType.LParen)
                {
                    Consume(); // (
                    int depth = 1;
                    while (Current.Type != TokenType.EOF && depth > 0)
                    {
                        if      (Current.Type == TokenType.LParen) depth++;
                        else if (Current.Type == TokenType.RParen) depth--;
                        if (depth > 0) Consume();
                    }
                    if (Current.Type == TokenType.RParen) Consume(); // )
                    return 0.0;
                }

                // ╨Я╨╡╤А╨╡╨╝╨╡╨╜╨╜╨░╤П
                if (_variables.TryGetValue(name, out double v))
                    return v;

                return 0.0; // ╨╜╨╡╨╕╨╖╨▓╨╡╤Б╤В╨╜╨░╤П ╨┐╨╡╤А╨╡╨╝╨╡╨╜╨╜╨░╤П
            }

            return 0.0; // fallback
        }
    }

    // тФАтФА ╨Т╤Б╨┐╨╛╨╝╨╛╨│╨░╤В╨╡╨╗╤М╨╜╤Л╨╡ ╨╝╨╡╤В╨╛╨┤╤Л тФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФАтФА

    private static Dictionary<string, double> BuildVars(
        IReadOnlyDictionary<string, double> others,
        string varName,
        double varValue)
    {
        var result = new Dictionary<string, double>(
            System.StringComparer.OrdinalIgnoreCase);
        foreach (var kv in others)
            result[kv.Key] = kv.Value;
        result[varName] = varValue;
        return result;
    }

    /// <summary>
    /// ╨а╨░╨╖╨▒╨╕╤В╤М ╤Б╤В╤А╨╛╨║╤Г ╨░╤А╨│╤Г╨╝╨╡╨╜╤В╨╛╨▓ ╨┐╨╛ ╨╖╨░╨┐╤П╤В╨╛╨╣ ╤Б ╤Г╤З╤С╤В╨╛╨╝ ╨▓╨╗╨╛╨╢╨╡╨╜╨╜╤Л╤Е ╤Б╨║╨╛╨▒╨╛╨║.
    /// </summary>
    private static List<string> SplitArgs(string argsStr)
    {
        var args  = new List<string>();
        int depth = 0;
        int start = 0;

        for (int i = 0; i < argsStr.Length; i++)
        {
            char c = argsStr[i];
            if      (c == '(' || c == '[') depth++;
            else if (c == ')' || c == ']') depth--;
            else if (c == ',' && depth == 0)
            {
                args.Add(argsStr.Substring(start, i - start));
                start = i + 1;
            }
        }
        args.Add(argsStr.Substring(start));
        return args;
    }
}
