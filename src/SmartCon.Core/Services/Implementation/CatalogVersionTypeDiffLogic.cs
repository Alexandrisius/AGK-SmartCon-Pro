using System.Globalization;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// #222: pure per-type value diff between two catalog versions of one item,
/// computed over the extracted attribute rows (<see cref="ExtractedAttributeValue"/>).
/// Type identity across versions is the type NAME (family_types row ids are
/// per-version); the comparison key is (TypeName, ParameterName) and the
/// compared value is <c>ValueText</c> with <c>ValueRaw</c> / <c>ValueNumber</c>
/// fallbacks. A (type, parameter) pair present on only one side counts as
/// changed — so a type absent from one version is reported changed when it
/// carries any extracted values on the other side.
/// Kept in Core so unit tests cover the diff without Revit.
/// </summary>
public static class CatalogVersionTypeDiffLogic
{
    /// <summary>
    /// Names of the types whose extracted values differ between the two
    /// versions, sorted ascending (ordinal).
    /// </summary>
    public static IReadOnlyList<string> ComputeChangedTypeNames(
        IReadOnlyList<ExtractedAttributeValue> fromValues,
        IReadOnlyDictionary<string, string> fromTypeNames,
        IReadOnlyList<ExtractedAttributeValue> toValues,
        IReadOnlyDictionary<string, string> toTypeNames)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(fromValues);
        ArgumentNullException.ThrowIfNull(fromTypeNames);
        ArgumentNullException.ThrowIfNull(toValues);
        ArgumentNullException.ThrowIfNull(toTypeNames);
#else
        if (fromValues is null) throw new ArgumentNullException(nameof(fromValues));
        if (fromTypeNames is null) throw new ArgumentNullException(nameof(fromTypeNames));
        if (toValues is null) throw new ArgumentNullException(nameof(toValues));
        if (toTypeNames is null) throw new ArgumentNullException(nameof(toTypeNames));
#endif

        var from = ToComparableMap(fromValues, fromTypeNames);
        var to = ToComparableMap(toValues, toTypeNames);

        var changed = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var pair in from)
        {
            // Note: no KeyValuePair deconstruction here — net48 lacks the
            // Deconstruct extension (CS8129/CS8130 on R19-R24 builds).
            if (!to.TryGetValue(pair.Key, out var toValue)
                || !string.Equals(pair.Value, toValue, StringComparison.Ordinal))
            {
                changed.Add(pair.Key.TypeName);
            }
        }
        foreach (var key in to.Keys)
        {
            if (!from.ContainsKey(key))
            {
                changed.Add(key.TypeName);
            }
        }
        return changed.ToList();
    }

    private static Dictionary<(string TypeName, string ParameterName), string?> ToComparableMap(
        IReadOnlyList<ExtractedAttributeValue> values,
        IReadOnlyDictionary<string, string> typeNames)
    {
        var map = new Dictionary<(string TypeName, string ParameterName), string?>();
        foreach (var value in values)
        {
            if (value.TypeId is null || !typeNames.TryGetValue(value.TypeId, out var typeName))
            {
                // Family-level row (no type) or a row for a type absent from
                // this version's type list — not part of the per-type diff.
                continue;
            }
            map[(typeName, value.ParameterName)] = ComparableValue(value);
        }
        return map;
    }

    private static string? ComparableValue(ExtractedAttributeValue value) =>
        value.ValueText
        ?? value.ValueRaw
        ?? value.ValueNumber?.ToString("G17", CultureInfo.InvariantCulture);
}
