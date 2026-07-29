using System.Globalization;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

public sealed class FamilyValidationEngine : IFamilyValidationEngine
{
    private const double Epsilon = 1e-9;

    public FamilyValidationReport Validate(FamilyValidationInput input, IReadOnlyList<EffectiveValidationRule> rules)
    {
        var enabledRules = rules.Where(r => r.Rule.IsEnabled).ToList();
        if (enabledRules.Count == 0 || input.Types.Count == 0)
        {
            return new FamilyValidationReport(true, Array.Empty<RuleViolation>(), 0);
        }

        var violations = new List<RuleViolation>();
        var evaluated = 0;

        foreach (var type in input.Types)
        {
            foreach (var effectiveRule in enabledRules)
            {
                evaluated++;
                var value = FindValue(type, effectiveRule.AttributeName);
                if (!IsSatisfied(value, effectiveRule.Rule))
                {
                    violations.Add(CreateViolation(type.TypeName, effectiveRule, value));
                }
            }
        }

        return new FamilyValidationReport(violations.Count == 0, violations, evaluated);
    }

    private static ParameterValidationValue? FindValue(FamilyTypeValidationData type, string parameterName)
    {
        foreach (var value in type.Values)
        {
            if (string.Equals(value.ParameterName, parameterName, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static bool IsSatisfied(ParameterValidationValue? value, ValidationRule rule)
    {
        var isPresent = value?.IsPresent == true;
        var hasValue = isPresent && HasNonEmptyValue(value!);

        return rule.Operator switch
        {
            ValidationRuleOperator.IsPresent => isPresent,
            ValidationRuleOperator.HasValue => hasValue,
            ValidationRuleOperator.IsEmpty => !hasValue,
            ValidationRuleOperator.Equals => hasValue && ValueEquals(value!, rule),
            ValidationRuleOperator.NotEquals => !hasValue || !ValueEquals(value!, rule),
            ValidationRuleOperator.Contains => hasValue && ValueContains(value!, rule),
            ValidationRuleOperator.NotContains => !hasValue || !ValueContains(value!, rule),
            ValidationRuleOperator.GreaterThan => hasValue && NumberCompare(value!, rule.ValueNumber, c => c > 0),
            ValidationRuleOperator.GreaterOrEqual => hasValue && NumberCompare(value!, rule.ValueNumber, c => c >= 0),
            ValidationRuleOperator.LessThan => hasValue && NumberCompare(value!, rule.ValueNumber, c => c < 0),
            ValidationRuleOperator.LessOrEqual => hasValue && NumberCompare(value!, rule.ValueNumber, c => c <= 0),
            ValidationRuleOperator.Between => hasValue && NumberBetween(value!, rule.MinValue, rule.MaxValue),
            _ => true,
        };
    }

    private static bool HasNonEmptyValue(ParameterValidationValue value)
    {
        if (value.ValueNumber.HasValue || value.DisplayNumber.HasValue)
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(value.ValueText);
    }

    /// <summary>
    /// Numeric value for rule comparison: the display-unit number (what
    /// the user sees and what rules are authored in) with the raw
    /// internal-unit value as fallback (imperial display strings that do
    /// not parse, e.g. "1'-6\"").
    /// </summary>
    private static double? EffectiveNumber(ParameterValidationValue value) =>
        value.DisplayNumber ?? value.ValueNumber;

    private static bool ValueEquals(ParameterValidationValue value, ValidationRule rule)
    {
        if (rule.ValueNumber.HasValue)
        {
            var number = EffectiveNumber(value);
            return number.HasValue && NumberEquals(number.Value, rule.ValueNumber.Value);
        }

        if (rule.ValueText is not null && value.ValueText is not null)
        {
            return string.Equals(value.ValueText, rule.ValueText, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool ValueContains(ParameterValidationValue value, ValidationRule rule)
    {
        if (rule.ValueText is null || value.ValueText is null)
        {
            return false;
        }

#if NET8_0_OR_GREATER
        return value.ValueText.Contains(rule.ValueText, StringComparison.OrdinalIgnoreCase);
#else
        return value.ValueText.IndexOf(rule.ValueText, StringComparison.OrdinalIgnoreCase) >= 0;
#endif
    }

    private static bool NumberCompare(ParameterValidationValue value, double? target, Func<int, bool> compare)
    {
        var number = EffectiveNumber(value);
        if (!target.HasValue || !number.HasValue)
        {
            return false;
        }

        if (NumberEquals(number.Value, target.Value))
        {
            return compare(0);
        }

        return compare(number.Value.CompareTo(target.Value));
    }

    private static bool NumberBetween(ParameterValidationValue value, double? min, double? max)
    {
        var number = EffectiveNumber(value);
        if (!min.HasValue || !max.HasValue || !number.HasValue)
        {
            return false;
        }

        var v = number.Value;
        return (v > min.Value || NumberEquals(v, min.Value))
            && (v < max.Value || NumberEquals(v, max.Value));
    }

    private static bool NumberEquals(double a, double b) =>
        global::System.Math.Abs(a - b) <= Epsilon * global::System.Math.Max(1.0, global::System.Math.Max(global::System.Math.Abs(a), global::System.Math.Abs(b)));

    private static RuleViolation CreateViolation(string typeName, EffectiveValidationRule effectiveRule, ParameterValidationValue? value)
    {
        var rule = effectiveRule.Rule;
        var expectedValue = rule.ValueText
            ?? rule.ValueNumber?.ToString("G15", CultureInfo.InvariantCulture);

        string? actualValue = null;
        if (value is not null && value.IsPresent && HasNonEmptyValue(value))
        {
            actualValue = !string.IsNullOrWhiteSpace(value.ValueText)
                ? value.ValueText
                : EffectiveNumber(value)?.ToString("G15", CultureInfo.InvariantCulture);
        }

        return new RuleViolation(
            typeName,
            effectiveRule.AttributeName,
            rule.Operator,
            expectedValue,
            rule.MinValue,
            rule.MaxValue,
            actualValue,
            rule.UnitTypeId ?? value?.UnitTypeId);
    }
}
