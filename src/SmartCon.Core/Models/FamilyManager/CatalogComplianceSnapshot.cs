namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Session-scoped snapshot of catalog compliance verdicts (#259), modelled
/// after <see cref="FamilyStaleSnapshot"/> (ADR-030 D-06): in-memory only,
/// NO schema migration. Populated by
/// <see cref="SmartCon.Core.Services.Interfaces.ICatalogComplianceService"/>
/// and invalidated on validation-rule edits (metadata changed), database
/// switch, database actualization («Обновить базу») and re-import of an item.
/// </summary>
/// <param name="Results">Per-catalog-item-id compliance verdict.</param>
/// <param name="CheckedAtUtc">When the snapshot was last mutated.</param>
/// <remarks>
/// Value-based equality, same contract as <see cref="FamilyStaleSnapshot"/>:
/// the default record equality would use reference equality for the
/// dictionary interface, so <see cref="Equals"/> is overridden.
/// </remarks>
public sealed record CatalogComplianceSnapshot
{
    public IReadOnlyDictionary<string, ComplianceCheckResult> Results { get; }
    public DateTimeOffset CheckedAtUtc { get; }

    public CatalogComplianceSnapshot(
        IReadOnlyDictionary<string, ComplianceCheckResult> results,
        DateTimeOffset checkedAtUtc)
    {
        Results = results ?? throw new ArgumentNullException(nameof(results));
        CheckedAtUtc = checkedAtUtc;
    }

    /// <summary>Singleton "no entries" sentinel. <c>CheckedAtUtc = MinValue</c>.</summary>
    public static CatalogComplianceSnapshot Empty { get; } = new(
        new Dictionary<string, ComplianceCheckResult>(), DateTimeOffset.MinValue);

    public bool Equals(CatalogComplianceSnapshot? other)
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
        unchecked
        {
            return (Results.Count * 397) ^ CheckedAtUtc.GetHashCode();
        }
    }
}
