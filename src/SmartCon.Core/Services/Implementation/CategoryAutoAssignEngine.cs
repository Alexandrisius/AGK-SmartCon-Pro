using System.Globalization;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// Pure auto-assignment engine (#241). A category matches when at least
/// one of its enabled groups has all enabled conditions satisfied:
/// attribute conditions are delegated to <see cref="IFamilyValidationEngine"/>
/// (identical operator semantics — display-units-first numbers, ε
/// comparison, all-types AND), system conditions are evaluated here by
/// locale-invariant ordinal/invariant-key comparison. A condition with a
/// disallowed operator (per <see cref="AssignmentOperatorPolicy"/>) or a
/// dangling attribute id never matches — defense in depth against
/// hand-edited databases.
/// </summary>
public sealed class CategoryAutoAssignEngine : ICategoryAutoAssignEngine
{
    private readonly IFamilyValidationEngine _validationEngine;

    public CategoryAutoAssignEngine(IFamilyValidationEngine validationEngine)
    {
        _validationEngine = validationEngine ?? throw new ArgumentNullException(nameof(validationEngine));
    }

    public CategoryAutoAssignResult Evaluate(CategoryAutoAssignInput input, IReadOnlyList<AssignmentRuleGroup> groups)
    {
        Common.Guard.ThrowIfNull(input);
        Common.Guard.ThrowIfNull(groups);

        var enabledGroups = groups
            .Where(g => g.IsEnabled && g.Conditions.Any(c => c.IsEnabled))
            .ToList();
        if (enabledGroups.Count == 0)
        {
            return CategoryAutoAssignResult.NoMatch;
        }

        var matched = new List<string>();
        foreach (var categoryGroups in enabledGroups
                     .GroupBy(g => g.CategoryId)
                     .OrderBy(g => g.Min(x => x.SortOrder)))
        {
            if (categoryGroups.Any(g => GroupSatisfied(input, g)))
            {
                matched.Add(categoryGroups.Key);
            }
        }

        // Specificity tie-break (#241): a rule on a subcategory is more
        // precise than the one on its parent, so when both match the
        // deepest category wins and the parent rule acts as the fallback
        // for families no child rule claims. Ambiguous remains for honest
        // ties (same-depth siblings). Without depths (legacy direct calls)
        // any 2+ matches stay ambiguous.
        if (matched.Count > 1 && input.CategoryDepthsById is { } depths)
        {
            var maxDepth = 0;
            foreach (var id in matched)
            {
                var depth = depths.TryGetValue(id, out var d) ? d : 0;
                if (depth > maxDepth)
                {
                    maxDepth = depth;
                }
            }

            var deepest = matched
                .Where(id => depths.TryGetValue(id, out var d) && d == maxDepth)
                .ToList();
            if (deepest.Count > 0)
            {
                // Defense in depth: an id missing from the depth map must
                // not silently turn Ambiguous into NoMatch.
                matched = deepest;
            }
        }

        return matched.Count switch
        {
            0 => CategoryAutoAssignResult.NoMatch,
            1 => CategoryAutoAssignResult.Matched(matched[0]),
            _ => CategoryAutoAssignResult.Ambiguous(matched),
        };
    }

    private bool GroupSatisfied(CategoryAutoAssignInput input, AssignmentRuleGroup group)
    {
        var conditions = group.Conditions.Where(c => c.IsEnabled).ToList();

        var attributeRules = new List<EffectiveValidationRule>();
        foreach (var condition in conditions)
        {
            if (condition.SourceKind != AssignmentConditionSourceKind.Attribute)
            {
                continue;
            }

            if (!AssignmentOperatorPolicy.IsAllowed(condition.SourceKind, condition.SystemField, condition.Operator))
            {
                return false;
            }

            if (condition.AttributeId is null ||
                !input.AttributeNamesById.TryGetValue(condition.AttributeId, out var attributeName))
            {
                return false;
            }

            attributeRules.Add(ToEffectiveRule(attributeName, condition));
        }

        if (attributeRules.Count > 0 && !_validationEngine.Validate(input.ValidationInput, attributeRules).IsValid)
        {
            return false;
        }

        return conditions.Where(c => c.SourceKind == AssignmentConditionSourceKind.System)
            .All(c => SystemSatisfied(input, c));
    }

    private static EffectiveValidationRule ToEffectiveRule(string attributeName, AssignmentCondition condition) =>
        new(
            attributeName,
            IsInherited: false,
            new ValidationRule(
                condition.Id,
                BindingId: condition.GroupId,
                condition.Operator,
                condition.ValueText,
                condition.ValueNumber,
                condition.MinValue,
                condition.MaxValue,
                UnitTypeId: null,
                condition.SortOrder,
                condition.IsEnabled));

    private static bool SystemSatisfied(CategoryAutoAssignInput input, AssignmentCondition condition)
    {
        if (condition.SystemField is not { } field)
        {
            return false;
        }

        if (!AssignmentOperatorPolicy.IsAllowed(condition.SourceKind, condition.SystemField, condition.Operator))
        {
            return false;
        }

        return field switch
        {
            AssignmentSystemField.RevitCategory => OrdinalSatisfied(input.RevitCategoryOrdinal, condition),
            AssignmentSystemField.PartType => PartTypeSatisfied(input.Facts, condition),
            AssignmentSystemField.FamilyName => TextSatisfied(input.FamilyName, condition),
            AssignmentSystemField.SystemFamilyKey => TextSatisfied(input.SystemFamilyKey, condition),
            _ => false,
        };
    }

    private static bool OrdinalSatisfied(int? actual, AssignmentCondition condition)
    {
        if (actual is null || !TryParseOrdinal(condition.ValueText, out var target))
        {
            return false;
        }

        return condition.Operator == ValidationRuleOperator.Equals
            ? actual.Value == target
            : actual.Value != target;
    }

    private static bool PartTypeSatisfied(IReadOnlyList<FamilyFact> facts, AssignmentCondition condition)
    {
        string? actual = null;
        foreach (var fact in facts)
        {
            if (string.Equals(fact.FactKey, FamilyFactRuleSet.PartTypeFactKey, StringComparison.Ordinal))
            {
                actual = fact.ValueKey;
                break;
            }
        }

        if (actual is null || condition.ValueText is null)
        {
            return false;
        }

        return condition.Operator == ValidationRuleOperator.Equals
            ? string.Equals(actual, condition.ValueText, StringComparison.Ordinal)
            : !string.Equals(actual, condition.ValueText, StringComparison.Ordinal);
    }

    private static bool TextSatisfied(string? actual, AssignmentCondition condition)
    {
        if (actual is null || condition.ValueText is null)
        {
            return false;
        }

        return condition.Operator switch
        {
            ValidationRuleOperator.Equals => string.Equals(actual, condition.ValueText, StringComparison.OrdinalIgnoreCase),
            ValidationRuleOperator.NotEquals => !string.Equals(actual, condition.ValueText, StringComparison.OrdinalIgnoreCase),
#if NET8_0_OR_GREATER
            ValidationRuleOperator.Contains => actual.Contains(condition.ValueText, StringComparison.OrdinalIgnoreCase),
#else
            ValidationRuleOperator.Contains => actual.IndexOf(condition.ValueText, StringComparison.OrdinalIgnoreCase) >= 0,
#endif
            _ => false,
        };
    }

    private static bool TryParseOrdinal(string? text, out int ordinal)
    {
        if (text is null)
        {
            ordinal = 0;
            return false;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out ordinal);
    }
}
