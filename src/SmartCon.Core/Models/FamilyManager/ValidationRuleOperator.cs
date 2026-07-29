namespace SmartCon.Core.Models.FamilyManager;

public enum ValidationRuleOperator
{
    IsPresent = 0,
    HasValue = 1,
    IsEmpty = 2,
    Equals = 3,
    NotEquals = 4,
    Contains = 5,
    NotContains = 6,
    GreaterThan = 7,
    GreaterOrEqual = 8,
    LessThan = 9,
    LessOrEqual = 10,
    Between = 11,
}
