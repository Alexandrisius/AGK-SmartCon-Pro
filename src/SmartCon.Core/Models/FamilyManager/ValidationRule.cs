namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One validation rule attached to a category-attribute binding.
/// Numeric thresholds (<paramref name="ValueNumber"/>,
/// <paramref name="MinValue"/>, <paramref name="MaxValue"/>) are stored
/// in the parameter's DISPLAY units (what the user types in the rules
/// editor and sees in family properties) and compared against the
/// parsed display number of the parameter value (DisplayNumber-first,
/// internal-unit fallback in the engine).
/// <paramref name="UnitTypeId"/> is reserved (always <c>null</c> today):
/// a rule does NOT pin a unit — changing the parameter's display format
/// silently re-scales numeric rules (documented limitation).
/// </summary>
public sealed record ValidationRule(
    string Id,
    string BindingId,
    ValidationRuleOperator Operator,
    string? ValueText,
    double? ValueNumber,
    double? MinValue,
    double? MaxValue,
    string? UnitTypeId,
    int SortOrder,
    bool IsEnabled);
