namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Session-scoped snapshot of stale-check results (ADR-030, D-06).
/// Populated by <see cref="SmartCon.Core.Services.Interfaces.IStaleDetector"/>
/// and invalidated on Load/Update/Edit operations and DB switch.
/// </summary>
/// <param name="Results">Per-catalog-item-id stale check result.</param>
/// <param name="CheckedAtUtc">When the snapshot was last mutated (merge, remove, etc.).</param>
/// <remarks>
/// Equality is value-based: two snapshots are equal if they contain the same
/// set of <see cref="StaleCheckResult"/> entries (compared by value) and the
/// same <see cref="CheckedAtUtc"/>. The default <c>record</c> equality would
/// use reference equality for the <see cref="IReadOnlyDictionary{TKey,TValue}"/>
/// property (interfaces do not implement structural equality in .NET), so we
/// override it. This makes <c>snap1 == snap2</c> behave intuitively even when
/// the two snapshots were built from independent dictionary instances.
/// </remarks>
public sealed record FamilyStaleSnapshot
{
    public IReadOnlyDictionary<string, StaleCheckResult> Results { get; }
    public DateTimeOffset CheckedAtUtc { get; }

    public FamilyStaleSnapshot(
        IReadOnlyDictionary<string, StaleCheckResult> results,
        DateTimeOffset checkedAtUtc)
    {
        Results = results ?? throw new ArgumentNullException(nameof(results));
        CheckedAtUtc = checkedAtUtc;
    }

    /// <summary>Singleton "no entries" sentinel. <c>CheckedAtUtc = MinValue</c>.</summary>
    public static FamilyStaleSnapshot Empty { get; } = new(
        new Dictionary<string, StaleCheckResult>(), DateTimeOffset.MinValue);

    public bool Equals(FamilyStaleSnapshot? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        if (CheckedAtUtc != other.CheckedAtUtc) return false;
        if (ReferenceEquals(Results, other.Results)) return true;
        if (Results.Count != other.Results.Count) return false;
        foreach (var kvp in Results)
        {
            if (!other.Results.TryGetValue(kvp.Key, out var otherValue)) return false;
            if (!kvp.Value.Equals(otherValue)) return false;
        }
        return true;
    }

    public override int GetHashCode()
    {
        // Hash combines a count signal + the timestamp; the per-entry
        // equality will short-circuit false-positives in HashSet/Equals
        // comparisons. Using just Count is safe because two distinct
        // snapshots with the same Count and timestamp are extremely rare
        // and Equals will sort them out.
        unchecked
        {
            return (Results.Count * 397) ^ CheckedAtUtc.GetHashCode();
        }
    }
}
