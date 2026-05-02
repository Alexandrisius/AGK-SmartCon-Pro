using System;
using System.Collections.Generic;
using System.Linq;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Math.FormulaEngine.Solver;

namespace SmartCon.Core.Services;

public static class LookupColumnResolver
{
    public static List<string> FindQueryParamsForTable(
        string tableName,
        IReadOnlyList<(string? Name, string? Formula)> paramSnapshot,
        IReadOnlyDictionary<string, string> formulaByName)
    {
        List<string>? widest = null;

        foreach (var (fpName, fpFormula) in paramSnapshot)
        {
            if (string.IsNullOrEmpty(fpFormula)) continue;
            var parsed = FormulaSolver.ParseSizeLookupStatic(fpFormula!);
            if (parsed is null) continue;
            var resolved = ResolveTableAlias(formulaByName, parsed.Value.TableName);
            if (!string.Equals(resolved, tableName, StringComparison.OrdinalIgnoreCase))
                continue;

            var qp = parsed.Value.QueryParameters;
            if (widest is null || qp.Count > widest.Count)
            {
                widest = qp.ToList();
                SmartConLogger.Debug($"    FindQueryParamsForTable: wider formula from '{fpName}' → [{string.Join(", ", widest)}] ({widest.Count} params)");
            }
        }

        if (widest is not null)
            SmartConLogger.Debug($"    FindQueryParamsForTable: table='{tableName}' → [{string.Join(", ", widest)}] ({widest.Count} params, widest)");

        return widest ?? [];
    }

    public static (int ColIndex, IReadOnlyList<CsvColumnMapping> AllQueryColumns, bool FoundViaDependsOn) FindColumnIndex(
        string tableName, string searchParamName,
        IReadOnlyList<(string? Name, string? Formula)> paramSnapshot,
        IReadOnlyDictionary<string, string> formulaByName)
    {
        SmartConLogger.Debug($"    FindColumnIndex: table='{tableName}', searchParam='{searchParamName}'");

        IReadOnlyList<CsvColumnMapping>? allQueryColumns = null;

        foreach (var (fpName, fpFormula) in paramSnapshot)
        {
            if (string.IsNullOrEmpty(fpFormula)) continue;

            var parsed = FormulaSolver.ParseSizeLookupStatic(fpFormula!);
            if (parsed is null) continue;

            var resolvedTableName = ResolveTableAlias(formulaByName, parsed.Value.TableName);
            if (!string.Equals(resolvedTableName, tableName, StringComparison.OrdinalIgnoreCase))
                continue;

            var queryParams = parsed.Value.QueryParameters;
            SmartConLogger.Debug($"      FP='{fpName}': query=[{string.Join(", ", queryParams)}]");

            if (allQueryColumns is null || queryParams.Count > allQueryColumns.Count)
                allQueryColumns = queryParams.Select((n, i) => new CsvColumnMapping(i + 1, n)).ToList();
        }

        if (allQueryColumns is null)
        {
            SmartConLogger.Debug("      → no formulas found for table");
            return (-1, [], false);
        }

        int targetCol = -1;
        bool foundViaDependsOn = false;

        for (int i = 0; i < allQueryColumns.Count; i++)
        {
            var paramName = allQueryColumns[i].ParameterName;
            bool direct = string.Equals(paramName, searchParamName, StringComparison.OrdinalIgnoreCase);
            bool depends = !direct && DependsOn(formulaByName, paramName, searchParamName);
            SmartConLogger.Debug($"        [{i}] '{paramName}': direct={direct}, depends={depends}");
            if (direct || depends)
            {
                targetCol = allQueryColumns[i].CsvColIndex;
                foundViaDependsOn = depends;
                SmartConLogger.Debug($"      → colIndex={targetCol}, viaDependsOn={depends}");
                break;
            }
        }

        if (targetCol < 0)
            SmartConLogger.Debug("      → colIndex=-1 (not found)");
        else
            SmartConLogger.Debug($"    FindColumnIndex: table='{tableName}' → widest allQueryColumns=[{string.Join(", ", allQueryColumns.Select(q => q.ParameterName))}] ({allQueryColumns.Count} cols)");

        return (targetCol, allQueryColumns, foundViaDependsOn);
    }

    public static string ResolveTableAlias(IReadOnlyDictionary<string, string> formulaByName, string token)
    {
        if (token.StartsWith("\"") && token.EndsWith("\""))
            return token.Trim('"');
        if (formulaByName.TryGetValue(token, out var formula))
        {
            var f = formula.Trim();
            if (f.StartsWith("\"") && f.EndsWith("\""))
            {
                var resolved = f.Trim('"');
                SmartConLogger.Debug($"      ResolveTableAlias: '{token}' → formula='{f}' → resolved='{resolved}'");
                return resolved;
            }
            SmartConLogger.Debug($"      ResolveTableAlias: '{token}' → formula(unquoted)='{f}'");
            return f;
        }
        SmartConLogger.Debug($"      ResolveTableAlias: '{token}' → not found in formulaByName");
        return token;
    }

    public static bool DependsOn(IReadOnlyDictionary<string, string> formulaByName, string paramName, string target)
    {
        if (string.Equals(paramName, target, StringComparison.OrdinalIgnoreCase)) return true;
        if (!formulaByName.TryGetValue(paramName, out var formula)) return false;

        var f = formula.Trim();
        if (f.StartsWith("\"") && f.EndsWith("\""))
            return false;

        var vars = FormulaSolver.ExtractVariablesStatic(formula);
        if (vars.Count > 0)
            return vars.Any(v => string.Equals(v, target, StringComparison.OrdinalIgnoreCase));

#if NETFRAMEWORK
        return formula.IndexOf(target, StringComparison.OrdinalIgnoreCase) >= 0;
#else
        return formula.Contains(target, StringComparison.OrdinalIgnoreCase);
#endif
    }

    public static string? ExtractTrailingDigits(string name)
    {
        int i = name.Length - 1;
        while (i >= 0 && char.IsDigit(name[i])) i--;
        return i < name.Length - 1 ? name[(i + 1)..] : null;
    }
}
