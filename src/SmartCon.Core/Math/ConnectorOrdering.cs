namespace SmartCon.Core.Math;

/// <summary>
/// Deterministic geometric ordering for connector-like items.
/// Revit's <c>ConnectorManager.Connectors</c> (ConnectorSet) returns connectors in a
/// random order that changes from call to call — any algorithm that iterates or cycles
/// connectors must not rely on it (issue #163).
/// <para>
/// Sort order: X ascending (left-to-right), then Z descending (top-to-bottom),
/// then Y ascending, then an integer tie-breaker (e.g. Connector.Id) which makes
/// the order total and reproducible regardless of input order.
/// </para>
/// <para>
/// The caller supplies the position key. To keep the order stable while the element
/// is moved/rotated (PipeConnectEditor re-aligns the element on every cycle), the key
/// must be invariant to rigid transforms: family-local coordinates for FamilyInstance,
/// projection onto the location axis for MEPCurve — see
/// SmartCon.Revit/Selection/ConnectorService.cs.
/// </para>
/// </summary>
public static class ConnectorOrdering
{
    /// <summary>
    /// Axis comparison tolerance in internal units (decimal feet, ~0.3 µm).
    /// Coordinates within this tolerance are treated as equal and the next axis
    /// decides. Family symmetry is preserved well below this threshold even after
    /// a transform inverse round-trip.
    /// </summary>
    public const double PositionTolerance = 1e-6;

    /// <summary>
    /// Returns the items sorted by position (X asc → Z desc → Y asc) with an
    /// integer tie-breaker for coincident positions. The result is deterministic:
    /// the same set of (position, tie-breaker) pairs always yields the same order,
    /// regardless of the input enumeration order.
    /// </summary>
    /// <typeparam name="T">Item type (e.g. Connector, ConnectorProxy).</typeparam>
    /// <param name="items">Items to sort. Not modified; a new list is returned.</param>
    /// <param name="positionSelector">Geometric position key (internal units).</param>
    /// <param name="tieBreakerSelector">Stable integer key (e.g. Connector.Id) used
    /// when positions coincide — guarantees a total, reproducible order.</param>
    public static IReadOnlyList<T> OrderByPosition<T>(
        IEnumerable<T> items,
        Func<T, Vec3> positionSelector,
        Func<T, int> tieBreakerSelector)
    {
#if NETFRAMEWORK
        if (items is null) throw new ArgumentNullException(nameof(items));
        if (positionSelector is null) throw new ArgumentNullException(nameof(positionSelector));
        if (tieBreakerSelector is null) throw new ArgumentNullException(nameof(tieBreakerSelector));
#else
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(positionSelector);
        ArgumentNullException.ThrowIfNull(tieBreakerSelector);
#endif

        var list = new List<T>(items);
        list.Sort(new PositionComparer<T>(positionSelector, tieBreakerSelector));
        return list;
    }

    private sealed class PositionComparer<T>(
        Func<T, Vec3> positionSelector,
        Func<T, int> tieBreakerSelector) : IComparer<T>
    {
        public int Compare(T? x, T? y)
        {
            var a = positionSelector(x!);
            var b = positionSelector(y!);

            int cmp = CompareAxis(a.X, b.X);
            if (cmp != 0) return cmp;

            cmp = CompareAxis(b.Z, a.Z);
            if (cmp != 0) return cmp;

            cmp = CompareAxis(a.Y, b.Y);
            if (cmp != 0) return cmp;

            return tieBreakerSelector(x!).CompareTo(tieBreakerSelector(y!));
        }

        private static int CompareAxis(double first, double second)
        {
            if (System.Math.Abs(first - second) <= PositionTolerance) return 0;
            return first.CompareTo(second);
        }
    }
}
