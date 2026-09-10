using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Pure planner for the stale-update overwrite post-pass (Issue #239).
/// Maps the catalog's <see cref="ExtractedAttributeValue"/> rows of the
/// update target version to per-type <see cref="TypeParameterOverwriteOperation"/>s.
/// Pure C# — no Revit API, no logging; fully unit-testable.
/// </summary>
/// <remarks>
/// Rules:
/// <list type="bullet">
/// <item>Only rows with <see cref="AttributeValueStatus.Found"/> are planned
/// (blank/cleared values are skipped — the post-verify arbitrates).</item>
/// <item>Both Type and Instance scopes are included: instance values on a
/// <c>FamilySymbol</c> are the per-type defaults, which the merge likewise
/// fails to overwrite for the 2nd..Nth types.</item>
/// <item>StorageType Double/Integer → numeric set (Revit internal units,
/// applied via <c>Parameter.Set(double/int)</c> directly), String → text set.</item>
/// <item>StorageType ElementId → <see cref="TypeParameterOverwriteKind.ResolveElementByName"/>
/// using the resolved element name; rows with unreadable references
/// (<c>INVALID</c>/<c>READERROR</c>/null) are skipped.</item>
/// <item>Rows without a type id (untyped-family values) or with a type id
/// unknown to <paramref name="typeNameById"/> are skipped.</item>
/// </list>
/// </remarks>
public static class TypeParameterOverwritePlanner
{
    public static IReadOnlyList<TypeParameterOverwriteOperation> Plan(
        IReadOnlyList<ExtractedAttributeValue> catalogValues,
        IReadOnlyDictionary<string, string> typeNameById)
    {
#if NET8_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(catalogValues);
        ArgumentNullException.ThrowIfNull(typeNameById);
#else
        if (catalogValues is null) throw new ArgumentNullException(nameof(catalogValues));
        if (typeNameById is null) throw new ArgumentNullException(nameof(typeNameById));
#endif

        var result = new List<TypeParameterOverwriteOperation>(catalogValues.Count);
        foreach (var value in catalogValues)
        {
            if (value.Status != AttributeValueStatus.Found) continue;
            if (string.IsNullOrEmpty(value.ParameterName)) continue;
            if (value.TypeId is null || !typeNameById.TryGetValue(value.TypeId, out var typeName))
                continue;

            switch (value.StorageType)
            {
                case "Double":
                case "Integer":
                    if (value.ValueNumber.HasValue)
                    {
                        result.Add(new TypeParameterOverwriteOperation(
                            typeName,
                            value.ParameterName,
                            value.StorageType == "Double"
                                ? TypeParameterOverwriteKind.SetDouble
                                : TypeParameterOverwriteKind.SetInteger,
                            value.ValueNumber,
                            null));
                    }
                    break;

                case "String":
                    result.Add(new TypeParameterOverwriteOperation(
                        typeName,
                        value.ParameterName,
                        TypeParameterOverwriteKind.SetString,
                        null,
                        value.ValueText ?? string.Empty));
                    break;

                case "ElementId":
                    if (!string.IsNullOrEmpty(value.ValueText)
                        && value.ValueText is not "INVALID" and not "READERROR")
                    {
                        result.Add(new TypeParameterOverwriteOperation(
                            typeName,
                            value.ParameterName,
                            TypeParameterOverwriteKind.ResolveElementByName,
                            null,
                            value.ValueText));
                    }
                    break;

                default:
                    // Unknown/unsupported storage type — skip; the post-verify
                    // content proof arbitrates any genuine divergence.
                    break;
            }
        }

        return result;
    }
}
