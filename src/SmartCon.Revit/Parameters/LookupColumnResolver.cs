using SmartCon.Core.Services;

namespace SmartCon.Revit.Parameters;

internal static class LookupColumnResolver
{
    public static List<string> FindQueryParamsForTable(
        string tableName,
        IReadOnlyList<(string? Name, string? Formula)> paramSnapshot,
        IReadOnlyDictionary<string, string> formulaByName)
        => SmartCon.Core.Services.LookupColumnResolver.FindQueryParamsForTable(tableName, paramSnapshot, formulaByName);

    public static (int ColIndex, IReadOnlyList<SmartCon.Core.Math.CsvColumnMapping> AllQueryColumns, bool FoundViaDependsOn) FindColumnIndex(
        string tableName, string searchParamName,
        IReadOnlyList<(string? Name, string? Formula)> paramSnapshot,
        IReadOnlyDictionary<string, string> formulaByName)
        => SmartCon.Core.Services.LookupColumnResolver.FindColumnIndex(tableName, searchParamName, paramSnapshot, formulaByName);

    public static string ResolveTableAlias(IReadOnlyDictionary<string, string> formulaByName, string token)
        => SmartCon.Core.Services.LookupColumnResolver.ResolveTableAlias(formulaByName, token);

    public static bool DependsOn(IReadOnlyDictionary<string, string> formulaByName, string paramName, string target)
        => SmartCon.Core.Services.LookupColumnResolver.DependsOn(formulaByName, paramName, target);

    public static string? ExtractTrailingDigits(string name)
        => SmartCon.Core.Services.LookupColumnResolver.ExtractTrailingDigits(name);
}
