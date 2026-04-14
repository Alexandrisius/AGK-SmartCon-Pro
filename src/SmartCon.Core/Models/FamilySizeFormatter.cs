using SmartCon.Core;

using static SmartCon.Core.Units;
namespace SmartCon.Core.Models;

public static class FamilySizeFormatter
{

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

    public static string BuildAutoSelectDisplayName(
        IReadOnlyList<double> queryParamRadiiFt,
        int targetColumnIndex)
    {
        if (queryParamRadiiFt.Count == 0)
            return "АВТОПОДБОР (DN 0)";

        if (queryParamRadiiFt.Count == 1)
            return $"АВТОПОДБОР (DN {ToDn(queryParamRadiiFt[0])})";

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

    public static int ToDn(double radiusFt)
    {
        return (int)System.Math.Round(radiusFt * 2.0 * FeetToMm);
    }

    public static double DnToRadiusFt(int dn)
    {
        return (dn / 2.0) * MmToFeet;
    }
}
