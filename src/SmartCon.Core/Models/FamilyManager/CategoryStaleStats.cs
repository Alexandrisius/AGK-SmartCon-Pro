namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Per-category roll-up statistics for stale check results (ADR-030, Phase 24).
/// </summary>
/// <param name="HasStale">
/// <c>true</c> if any catalog item assigned to this category is stale (after
/// recursive parent expansion). A leaf category with no stale items has
/// <c>HasStale = false</c>; a parent category has <c>HasStale = true</c> if
/// any descendant is stale.
/// </param>
/// <param name="StaleCount">
/// Number of stale catalog items directly under this category. Does NOT count
/// stale items in sub-categories — that roll-up is encoded in
/// <see cref="HasStale"/>.
/// </param>
public sealed record CategoryStaleStats(bool HasStale, int StaleCount)
{
    public static CategoryStaleStats Empty { get; } = new(false, 0);
}
