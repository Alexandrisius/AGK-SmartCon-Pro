using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Pure auto-assignment engine (#241): evaluates a family's
/// <see cref="CategoryAutoAssignInput"/> against all enabled
/// <see cref="AssignmentRuleGroup"/>s and returns the tri-state
/// <see cref="CategoryAutoAssignResult"/>. Attribute conditions reuse
/// <see cref="IFamilyValidationEngine"/> (identical operator semantics,
/// display-units-first numbers, all-types AND); system conditions are
/// evaluated internally by ordinal/invariant keys. No Revit API.
/// </summary>
public interface ICategoryAutoAssignEngine
{
    /// <summary>Evaluates the input against the rule groups. Multiple
    /// matching categories yield <see cref="CategoryAutoAssignOutcome.Ambiguous"/>
    /// — the caller must let the user pick (no silent priority).</summary>
    CategoryAutoAssignResult Evaluate(
        CategoryAutoAssignInput input,
        IReadOnlyList<AssignmentRuleGroup> groups);
}
