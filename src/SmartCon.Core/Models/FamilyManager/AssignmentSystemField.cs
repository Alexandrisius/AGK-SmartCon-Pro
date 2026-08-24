namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Built-in family-level value an auto-assignment condition (#241) can
/// target when <see cref="AssignmentConditionSourceKind.System"/>. All
/// values are locale-invariant: the Revit category and the Part Type fact
/// are compared by ordinal, the system family key by its invariant key
/// (ADR-064); only <see cref="FamilyName"/> is user text.
/// </summary>
public enum AssignmentSystemField
{
    /// <summary><c>BuiltInCategory</c> ordinal of the family
    /// (<see cref="FamilySnapshot.CategoryId"/> /
    /// <see cref="SystemFamilySnapshot.CategoryId"/>). Stored in
    /// <see cref="AssignmentCondition.ValueText"/> as the invariant ordinal
    /// string.</summary>
    RevitCategory = 0,

    /// <summary>Part Type fact
    /// (<see cref="FamilyFactRuleSet.PartTypeFactKey"/>,
    /// <see cref="FamilyFact.ValueKey"/> ordinal string). Stored in
    /// <see cref="AssignmentCondition.ValueText"/> as the invariant ordinal
    /// string.</summary>
    PartType = 1,

    /// <summary>Family / import item name (what the user sees in the
    /// batch dialog).</summary>
    FamilyName = 2,

    /// <summary>Locale-invariant system family identity
    /// (<see cref="SystemTypeSnapshot.FamilyKey"/>, ADR-064).</summary>
    SystemFamilyKey = 3,
}
