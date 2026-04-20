using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Core.Models;

/// <summary>
/// Equality comparer for ElementId that uses the stable integer value
/// instead of reference equality. Required because Revit 2025 ElementId is a class.
/// </summary>
public sealed class ElementIdEqualityComparer : IEqualityComparer<ElementId>
{
    /// <summary>Singleton instance.</summary>
    public static readonly ElementIdEqualityComparer Instance = new();

    public bool Equals(ElementId? x, ElementId? y)
    {
        if (x is null && y is null) return true;
        if (x is null || y is null) return false;
        return x.GetValue() == y.GetValue();
    }

    public int GetHashCode(ElementId obj) => obj.GetStableHashCode();
}
