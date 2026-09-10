namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Одно условие расширенного поиска (#87): источник (атрибут библиотеки или
/// системное поле) + оператор + значение. Переиспользует словарь операторов
/// <see cref="ValidationRuleOperator"/> и системные поля
/// <see cref="AssignmentSystemField"/> из движка автоназначения (#241).
/// </summary>
public sealed record AttributeFilterCondition(
    AssignmentConditionSourceKind SourceKind,
    string? AttributeId,
    string? AttributeName,
    AssignmentSystemField? SystemField,
    ValidationRuleOperator Operator,
    string? Value)
{
    /// <summary>Операторы для условий по атрибутам библиотеки. Значение
    /// нужно всем, кроме HasValue/IsEmpty (заполнено / не заполнено).</summary>
    public static readonly IReadOnlyList<ValidationRuleOperator> AttributeOperators =
    [
        ValidationRuleOperator.Equals,
        ValidationRuleOperator.NotEquals,
        ValidationRuleOperator.Contains,
        ValidationRuleOperator.NotContains,
        ValidationRuleOperator.HasValue,
        ValidationRuleOperator.IsEmpty,
    ];

    /// <summary>Операторы для текстового системного поля (имя семейства):
    /// заполнено/не заполнено бессмысленны — имя есть всегда.</summary>
    public static readonly IReadOnlyList<ValidationRuleOperator> SystemTextOperators =
    [
        ValidationRuleOperator.Equals,
        ValidationRuleOperator.NotEquals,
        ValidationRuleOperator.Contains,
        ValidationRuleOperator.NotContains,
    ];

    /// <summary>Операторы для ординальных системных полей (категория Revit,
    /// тип детали): подстрочный поиск по ordinal не имеет смысла.</summary>
    public static readonly IReadOnlyList<ValidationRuleOperator> SystemOrdinalOperators =
    [
        ValidationRuleOperator.Equals,
        ValidationRuleOperator.NotEquals,
        ValidationRuleOperator.HasValue,
        ValidationRuleOperator.IsEmpty,
    ];

    public static IReadOnlyList<ValidationRuleOperator> OperatorsFor(
        AssignmentConditionSourceKind sourceKind,
        AssignmentSystemField? systemField) =>
        sourceKind == AssignmentConditionSourceKind.Attribute
            ? AttributeOperators
            : systemField is AssignmentSystemField.FamilyName or AssignmentSystemField.SystemFamilyKey
                ? SystemTextOperators
                : SystemOrdinalOperators;

    public static bool RequiresValue(ValidationRuleOperator op) =>
        op is not (ValidationRuleOperator.HasValue or ValidationRuleOperator.IsEmpty);
}
