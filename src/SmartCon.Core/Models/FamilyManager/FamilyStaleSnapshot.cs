namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Session-scoped snapshot of stale-check results (ADR-030, D-06).
/// Populated by <see cref="SmartCon.Core.Services.Interfaces.IStaleDetector"/>
/// and invalidated on Load/Update/Edit operations and DB switch.
/// </summary>
/// <param name="Results">Per-catalog-item-id stale check result.</param>
/// <param name="CheckedAtUtc">When the snapshot was built.</param>
public sealed record FamilyStaleSnapshot(
    IReadOnlyDictionary<string, StaleCheckResult> Results,
    DateTimeOffset CheckedAtUtc)
{
    public static FamilyStaleSnapshot Empty { get; } =
        new(new Dictionary<string, StaleCheckResult>(), DateTimeOffset.MinValue);
}
