using System.Linq;
using SmartCon.Core;


using static SmartCon.Core.Units;
namespace SmartCon.Core.Models;

/// <summary>
/// Formatting utilities for family size display names (DN, multi-DN).
/// </summary>
public static class FamilySizeFormatter
{
    /// <summary>
    /// Build a display name from query parameter radii (e.g. "DN 65 × DN 50").
    /// Target connector DN comes first, then other connectors in order.
    /// </summary>
    public static string BuildDisplayName(
        IReadOnlyList<double> queryParamRadiiFt,
        int targetColumnIndex)
    {
        if (queryParamRadiiFt.Count == 0)
            return "DN ?";

        if (queryParamRadiiFt.Count == 1)
            return $"DN {ToDn(queryParamRadiiFt[0])}";

        int targetIdx = targetColumnIndex - 1;
        if (targetIdx < 0 || targetIdx >= queryParamRadiiFt.Count)
            targetIdx = 0;

        var targetDn = ToDn(queryParamRadiiFt[targetIdx]);
        var parts = new List<string> { $"DN {targetDn}" };
        for (int i = 0; i < queryParamRadiiFt.Count; i++)
        {
            if (i == targetIdx) continue;
            parts.Add($"DN {ToDn(queryParamRadiiFt[i])}");
        }
        return string.Join(" × ", parts);
    }

    /// <summary>
    /// Build a display name from connector radii dictionary (legacy path without lookup tables).
    /// </summary>
    public static string BuildDisplayNameLegacy(
        IReadOnlyDictionary<int, double> connectorRadiiFt,
        int targetConnectorIndex)
    {
        if (connectorRadiiFt.Count == 0)
            return "DN ?";

        if (connectorRadiiFt.TryGetValue(targetConnectorIndex, out var targetR))
        {
            var targetDn = ToDn(targetR);
            var parts = new List<string> { $"DN {targetDn}" };
            foreach (var kvp in connectorRadiiFt.OrderBy(k => k.Key))
            {
                if (kvp.Key == targetConnectorIndex) continue;
                parts.Add($"DN {ToDn(kvp.Value)}");
            }
            return string.Join(" × ", parts);
        }

        var allDn = connectorRadiiFt.Select(kvp => ToDn(kvp.Value)).ToList();
        return string.Join(" × ", allDn.Select(d => $"DN {d}"));
    }

    /// <summary>
    /// Build the "AUTO-SELECT" display name with current size parameters.
    /// </summary>
    public static string BuildAutoSelectDisplayName(
        IReadOnlyList<double> queryParamRadiiFt,
        int targetColumnIndex,
        string? symbolName = null)
    {
        string baseName = queryParamRadiiFt.Count == 0
            ? "АВТОПОДБОР (DN 0)"
            : queryParamRadiiFt.Count == 1
                ? $"АВТОПОДБОР (DN {ToDn(queryParamRadiiFt[0])})"
                : BuildAutoSelectMultiParam(queryParamRadiiFt, targetColumnIndex);

        return string.IsNullOrEmpty(symbolName)
            ? baseName
            : $"{baseName} ({symbolName})";
    }

    private static string BuildAutoSelectMultiParam(
        IReadOnlyList<double> queryParamRadiiFt,
        int targetColumnIndex)
    {
        int targetIdx = targetColumnIndex - 1;
        if (targetIdx < 0 || targetIdx >= queryParamRadiiFt.Count)
            targetIdx = 0;

        var targetDn = ToDn(queryParamRadiiFt[targetIdx]);
        var parts = new List<string> { $"DN {targetDn}" };
        for (int i = 0; i < queryParamRadiiFt.Count; i++)
        {
            if (i == targetIdx) continue;
            parts.Add($"DN {ToDn(queryParamRadiiFt[i])}");
        }
        return $"АВТОПОДБОР ({string.Join(" × ", parts)})";
    }

    /// <summary>Convert radius (feet) to nominal diameter (mm, rounded).</summary>
    public static int ToDn(double radiusFt)
    {
        return (int)System.Math.Round(radiusFt * 2.0 * FeetToMm);
    }

    /// <summary>Convert nominal diameter (mm) to radius (feet).</summary>
    public static double DnToRadiusFt(int dn)
    {
        return (dn / 2.0) * MmToFeet;
    }

    /// <summary>Remove duplicate size options that have identical connector radii and parameters.</summary>
    public static List<FamilySizeOption> DeduplicateFamilyOptions(List<FamilySizeOption> options)
    {
        var seen = new HashSet<string>();
        var result = new List<FamilySizeOption>();
        foreach (var opt in options)
        {
            var key = string.Join("|", opt.AllConnectorRadii
                .OrderBy(kvp => kvp.Key)
                .Select(kvp => $"{kvp.Key}:{kvp.Value:F8}"))
                + "|" + (opt.SymbolName ?? "")
                + "|" + string.Join("|", opt.NonSizeParameterValues
                    .OrderBy(kvp => kvp.Key)
                    .Select(kvp => $"{kvp.Key}={kvp.Value}"));
            if (seen.Add(key))
                result.Add(opt);
        }
        return result;
    }

    /// <summary>
    /// Append symbol name in parentheses when multiple options share the same display name.
    /// </summary>
    public static List<FamilySizeOption> AppendSymbolNameSuffix(List<FamilySizeOption> sorted)
    {
        var duplicateBaseNames = sorted
            .GroupBy(o => o.DisplayName)
            .Where(g => g.Count() > 1 && g.Any(o => !string.IsNullOrEmpty(o.SymbolName)))
            .Select(g => g.Key)
            .ToHashSet();

        if (duplicateBaseNames.Count == 0)
            return sorted;

        var result = new List<FamilySizeOption>(sorted.Count);
        for (int i = 0; i < sorted.Count; i++)
        {
            var opt = sorted[i];
            if (!string.IsNullOrEmpty(opt.SymbolName) && duplicateBaseNames.Contains(opt.DisplayName))
                result.Add(opt with { DisplayName = $"{opt.DisplayName} ({opt.SymbolName})" });
            else
                result.Add(opt);
        }
        return result;
    }
}
