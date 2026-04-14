using System;
using System.Collections.Generic;
using System.Linq;
using SmartCon.Core.Logging;
using SmartCon.Core.Math;
using SmartCon.Core.Math.FormulaEngine.Solver;

namespace SmartCon.Revit.Parameters;

internal static class LookupColumnResolver
{
    public static List<string> FindQueryParamsForTable(
        string tableName,
        IReadOnlyList<(string? Name, string? Formula)> paramSnapshot,
        IReadOnlyDictionary<string, string> formulaByName)
    {
        foreach (var (fpName, fpFormula) in paramSnapshot)
        {
            if (string.IsNullOrEmpty(fpFormula)) continue;
            var parsed = FormulaSolver.ParseSizeLookupStatic(fpFormula);
            if (parsed is null) continue;
            var resolved = ResolveTableAlias(formulaByName, parsed.Value.TableName);
            if (string.Equals(resolved, tableName, StringComparison.OrdinalIgnoreCase))
                return parsed.Value.QueryParameters.ToList();
        }
        return [];
    }

    public static (int ColIndex, IReadOnlyList<CsvColumnMapping> AllQueryColumns, bool FoundViaDependsOn) FindColumnIndex(
        string tableName, string searchParamName,
        IReadOnlyList<(string? Name, string? Formula)> paramSnapshot,
        IReadOnlyDictionary<string, string> formulaByName)
    {
        SmartConLogger.Debug($"    FindColumnIndex: table='{tableName}', searchParam='{searchParamName}'");

        int targetCol = -1;
        bool foundViaDependsOn = false;
        IReadOnlyList<CsvColumnMapping>? allQueryColumns = null;

        foreach (var (fpName, fpFormula) in paramSnapshot)
        {
            if (string.IsNullOrEmpty(fpFormula)) continue;

            var parsed = FormulaSolver.ParseSizeLookupStatic(fpFormula);
            if (parsed is null) continue;

            var resolvedTableName = ResolveTableAlias(formulaByName, parsed.Value.TableName);
            if (!string.Equals(resolvedTableName, tableName, StringComparison.OrdinalIgnoreCase))
                continue;

            var queryParams = parsed.Value.QueryParameters;
            SmartConLogger.Debug($"      FP='{fpName}': query=[{string.Join(", ", queryParams)}]");

            if (allQueryColumns is null)
                allQueryColumns = queryParams.Select((n, i) => new CsvColumnMapping(i + 1, n)).ToList();

            if (targetCol < 0)
            {
                for (int i = 0; i < queryParams.Count; i++)
                {
                    bool direct = string.Equals(queryParams[i], searchParamName, StringComparison.OrdinalIgnoreCase);
                    bool depends = !direct && DependsOn(formulaByName, queryParams[i], searchParamName);
                    SmartConLogger.Debug($"        [{i}] '{queryParams[i]}': direct={direct}, depends={depends}");
                    if (direct || depends)
                    {
                        targetCol = i + 1;
                        foundViaDependsOn = depends;
                        SmartConLogger.Debug($"      → colIndex={targetCol}, viaDependsOn={depends}");
                        break;
                    }
                }
            }

            if (targetCol >= 0 && allQueryColumns is not null) break;
        }

        if (targetCol < 0)
            SmartConLogger.Debug("      → colIndex=-1 (not found)");

        return (targetCol, allQueryColumns ?? [], foundViaDependsOn);
    }

    public static string ResolveTableAlias(IReadOnlyDictionary<string, string> formulaByName, string token)
    {
        if (token.StartsWith("\"") && token.EndsWith("\""))
            return token.Trim('"');
        if (formulaByName.TryGetValue(token, out var formula))
        {
            var f = formula.Trim();
            if (f.StartsWith("\"") && f.EndsWith("\""))
                return f.Trim('"');
        }
        return token;
    }

    public static bool DependsOn(IReadOnlyDictionary<string, string> formulaByName, string paramName, string target)
    {
        if (string.Equals(paramName, target, StringComparison.OrdinalIgnoreCase)) return true;
        if (!formulaByName.TryGetValue(paramName, out var formula)) return false;

        var vars = FormulaSolver.ExtractVariablesStatic(formula);
        if (vars.Count > 0)
            return vars.Any(v => string.Equals(v, target, StringComparison.OrdinalIgnoreCase));

        return formula.Contains(target, StringComparison.OrdinalIgnoreCase);
    }

    public static string? ExtractTrailingDigits(string name)
    {
        int i = name.Length - 1;
        while (i >= 0 && char.IsDigit(name[i])) i--;
        return i < name.Length - 1 ? name[(i + 1)..] : null;
    }
}
