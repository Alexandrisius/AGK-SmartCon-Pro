namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// One condition of an auto-assignment rule group (#241). Exactly one of
/// <paramref name="AttributeId"/> / <paramref name="SystemField"/> is set,
/// per <paramref name="SourceKind"/> (enforced by the storage CHECK
/// constraint). Value fields follow <see cref="ValidationRule"/> semantics:
/// numeric thresholds in display units, ordinal values (Revit category,
/// Part Type) in <paramref name="ValueText"/> as invariant ordinal strings.
/// </summary>
public sealed record AssignmentCondition(
    string Id,
    string GroupId,
    AssignmentConditionSourceKind SourceKind,
    string? AttributeId,
    AssignmentSystemField? SystemField,
    ValidationRuleOperator Operator,
    string? ValueText,
    double? ValueNumber,
    double? MinValue,
    double? MaxValue,
    int SortOrder,
    bool IsEnabled);
