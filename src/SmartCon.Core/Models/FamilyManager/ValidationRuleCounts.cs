namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Aggregated validation-rule counters for one category-attribute binding.
/// A disabled rule still exists (it survives in the catalog and counts
/// toward <see cref="Total"/>) but does not participate in the gate.
/// </summary>
/// <param name="Total">All rules of the binding.</param>
/// <param name="Disabled">Rules with <c>IsEnabled == false</c>.</param>
public sealed record ValidationRuleCounts(int Total, int Disabled)
{
    public int Enabled => Total - Disabled;
}
