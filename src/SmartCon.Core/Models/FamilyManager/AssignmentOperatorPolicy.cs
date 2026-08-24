namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Operator whitelist for auto-assignment conditions (#241). Deliberately
/// NARROWER than the validation-rule operator set: negative operators
/// (<see cref="ValidationRuleOperator.NotEquals"/>,
/// <see cref="ValidationRuleOperator.NotContains"/>,
/// <see cref="ValidationRuleOperator.IsEmpty"/>) are satisfied by an
/// ABSENT parameter in <see cref="Services.Implementation.FamilyValidationEngine"/>
/// — an auto-assign condition must never match a family for NOT having a
/// parameter, so they are banned for attribute conditions. System fields
/// always have a known value, so <c>NotEquals</c> is safe there.
/// </summary>
public static class AssignmentOperatorPolicy
{
    /// <summary>Operators allowed for attribute conditions — positive
    /// matches only (absent parameter never satisfies any of them in the
    /// validation engine).</summary>
    public static IReadOnlyList<ValidationRuleOperator> AttributeOperators { get; } = new[]
    {
        ValidationRuleOperator.IsPresent,
        ValidationRuleOperator.HasValue,
        ValidationRuleOperator.Equals,
        ValidationRuleOperator.Contains,
        ValidationRuleOperator.GreaterThan,
        ValidationRuleOperator.GreaterOrEqual,
        ValidationRuleOperator.LessThan,
        ValidationRuleOperator.LessOrEqual,
        ValidationRuleOperator.Between,
    };

    /// <summary>Operators allowed for a given system field.</summary>
    public static IReadOnlyList<ValidationRuleOperator> SystemOperators(AssignmentSystemField field) =>
        field switch
        {
            AssignmentSystemField.FamilyName => TextOperators,
            AssignmentSystemField.SystemFamilyKey => TextOperators,
            _ => OrdinalOperators,
        };

    private static readonly IReadOnlyList<ValidationRuleOperator> OrdinalOperators = new[]
    {
        ValidationRuleOperator.Equals,
        ValidationRuleOperator.NotEquals,
    };

    private static readonly IReadOnlyList<ValidationRuleOperator> TextOperators = new[]
    {
        ValidationRuleOperator.Equals,
        ValidationRuleOperator.NotEquals,
        ValidationRuleOperator.Contains,
    };

    /// <summary>Whether the operator may be persisted for the given source.
    /// The engine additionally treats a disallowed operator as an
    /// unsatisfied condition (defense in depth).</summary>
    public static bool IsAllowed(
        AssignmentConditionSourceKind sourceKind,
        AssignmentSystemField? systemField,
        ValidationRuleOperator op) =>
        sourceKind switch
        {
            AssignmentConditionSourceKind.Attribute => AttributeOperators.Contains(op),
            AssignmentConditionSourceKind.System => systemField.HasValue && SystemOperators(systemField.Value).Contains(op),
            _ => false,
        };
}
