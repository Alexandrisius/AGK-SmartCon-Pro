namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Newer-Revit-only pending info of an actualization task (ADR-054 §3a):
/// groups whose EVERY file variant requires a Revit newer than the running
/// one, and the minimum Revit major version that makes ALL of them
/// processable in a single pass (MAX over groups of the group's minimum
/// variant Revit version — a group is openable when the running Revit is
/// ≥ its oldest variant). 0 when nothing is pending.
/// </summary>
public sealed record NewerOnlyPendingInfo(int Count, int RequiredRevitVersion)
{
    public static NewerOnlyPendingInfo None { get; } = new(0, 0);
}
