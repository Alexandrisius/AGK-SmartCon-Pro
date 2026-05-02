using System;

namespace SmartCon.Core.Services;

public static class FormulaParamMatcher
{
    public static bool ContainsParamReference(string formula, string paramName)
    {
        int idx = 0;
        while ((idx = formula.IndexOf(paramName, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool leftOk = idx == 0 || !IsIdentChar(formula[idx - 1]);
            bool rightOk = idx + paramName.Length >= formula.Length
                        || !IsIdentChar(formula[idx + paramName.Length]);
            if (leftOk && rightOk) return true;
            idx += paramName.Length;
        }
        return false;
    }

    public static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
