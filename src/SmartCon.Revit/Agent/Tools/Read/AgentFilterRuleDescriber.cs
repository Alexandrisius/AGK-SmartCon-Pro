using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Read;

/// <summary>
/// Turns an ElementFilter into a JSON-friendly description so the model can see
/// what a view filter actually tests. Defensive: unknown rule kinds degrade to
/// their type name instead of failing the read.
/// </summary>
internal static class AgentFilterRuleDescriber
{
    internal static Dictionary<string, object?> Describe(
        ElementFilter? filter,
        Document? doc = null,
        IReadOnlyDictionary<long, ForgeTypeId>? parameters = null)
    {
        if (filter is null)
            return new Dictionary<string, object?> { ["type"] = "none" };

        try
        {
            if (filter is ElementLogicalFilter logical)
            {
                var children = new List<object?>();
                foreach (var child in logical.GetFilters())
                    children.Add(Describe(child, doc, parameters));

                return new Dictionary<string, object?>
                {
                    ["type"] = filter is LogicalOrFilter ? "or" : "and",
                    ["filters"] = children
                };
            }

            if (filter is ElementParameterFilter parameterFilter)
            {
                var rules = new List<object?>();
                foreach (var rule in parameterFilter.GetRules())
                    rules.Add(DescribeRule(rule, doc, parameters));

                return new Dictionary<string, object?>
                {
                    ["type"] = "parameter",
                    ["rules"] = rules
                };
            }

            return new Dictionary<string, object?> { ["type"] = filter.GetType().Name };
        }
        catch (Exception ex)
        {
            return new Dictionary<string, object?>
            {
                ["type"] = filter.GetType().Name,
                ["error"] = ex.Message
            };
        }
    }

    /// <summary>
    /// Stable short hash of the rule tree and the category set. Two filters with the
    /// same digest select the same elements (order of rules/categories is ignored),
    /// so duplicates can be grouped without opening every filter.
    /// </summary>
    internal static string Digest(
        ElementFilter? filter,
        IReadOnlyList<ElementId> categories)
    {
        var builder = new StringBuilder();
        builder.Append(Canonical(filter));
        builder.Append("|categories:");
        var ids = new List<long>();
        foreach (var category in categories)
            ids.Add(category.GetValue());
        ids.Sort();
        foreach (var id in ids)
            builder.Append(id).Append(',');

        try
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var hash = new StringBuilder(12);
                for (var index = 0; index < 6; index++)
                    hash.Append(bytes[index].ToString("x2"));
                return hash.ToString();
            }
        }
        catch (Exception)
        {
            return "unavailable";
        }
    }

    /// <summary>Short human-readable rule text for list_filters.</summary>
    internal static string Summary(ElementFilter? filter, Document? doc = null)
    {
        var text = Summarize(filter, doc);
        return text.Length > 160 ? text.Substring(0, 157) + "..." : text;
    }

    internal static int CountRules(ElementFilter? filter)
    {
        if (filter is null)
            return 0;

        try
        {
            if (filter is ElementLogicalFilter logical)
            {
                var total = 0;
                foreach (var child in logical.GetFilters())
                    total += CountRules(child);
                return total;
            }

            return filter is ElementParameterFilter parameterFilter
                ? parameterFilter.GetRules().Count
                : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static string Canonical(ElementFilter? filter)
    {
        if (filter is null)
            return "none";

        try
        {
            if (filter is ElementLogicalFilter logical)
            {
                var children = new List<string>();
                foreach (var child in logical.GetFilters())
                    children.Add(Canonical(child));
                children.Sort(StringComparer.Ordinal);
                return (filter is LogicalOrFilter ? "or(" : "and(") +
                       string.Join(";", children) + ")";
            }

            if (filter is ElementParameterFilter parameterFilter)
            {
                var rules = new List<string>();
                foreach (var rule in parameterFilter.GetRules())
                    rules.Add(CanonicalRule(rule));
                rules.Sort(StringComparer.Ordinal);
                return "param(" + string.Join(";", rules) + ")";
            }
        }
        catch (Exception ex)
        {
            return filter.GetType().Name + ":" + ex.GetType().Name;
        }

        return filter.GetType().Name;
    }

    private static string CanonicalRule(FilterRule rule)
    {
        var builder = new StringBuilder();
        try
        {
            var parameterId = rule.GetRuleParameter();
            builder.Append(parameterId is null ? 0 : parameterId.GetValue());
            builder.Append(':');
            builder.Append(EvaluatorName(rule));
            switch (rule)
            {
                case FilterStringRule stringRule:
                    builder.Append(':').Append(stringRule.RuleString);
                    break;
                case FilterDoubleRule doubleRule:
                    builder.Append(':').Append(
                            doubleRule.RuleValue.ToString("R", CultureInfo.InvariantCulture))
                        .Append(":eps=")
                        .Append(doubleRule.Epsilon.ToString("R", CultureInfo.InvariantCulture));
                    break;
                case FilterIntegerRule integerRule:
                    builder.Append(':').Append(integerRule.RuleValue);
                    break;
                case FilterElementIdRule elementRule:
                    builder.Append(':').Append(
                        elementRule.RuleValue is null ? 0 : elementRule.RuleValue.GetValue());
                    break;
                case FilterInverseRule inverseRule:
                    builder.Append(":inv(")
                        .Append(CanonicalRule(inverseRule.GetInnerRule()))
                        .Append(')');
                    break;
            }
        }
        catch (Exception)
        {
            builder.Append(":unreadable");
        }

        return builder.ToString();
    }

    private static string Summarize(ElementFilter? filter, Document? doc)
    {
        if (filter is null)
            return "no rules";

        try
        {
            if (filter is ElementLogicalFilter logical)
            {
                var parts = new List<string>();
                foreach (var child in logical.GetFilters())
                    parts.Add(Summarize(child, doc));
                return string.Join(filter is LogicalOrFilter ? " or " : " and ", parts);
            }

            if (filter is ElementParameterFilter parameterFilter)
            {
                var parts = new List<string>();
                foreach (var rule in parameterFilter.GetRules())
                    parts.Add(SummarizeRule(rule, doc));
                return string.Join(" and ", parts);
            }
        }
        catch (Exception)
        {
            return "unreadable";
        }

        return filter.GetType().Name;
    }

    private static string SummarizeRule(FilterRule rule, Document? doc)
    {
        try
        {
            if (rule is FilterInverseRule inverseRule)
                return "not(" + SummarizeRule(inverseRule.GetInnerRule(), doc) + ")";

            var parameterId = rule.GetRuleParameter();
            var name = parameterId is null
                ? "?"
                : ResolveParameterName(doc, parameterId) ??
                  parameterId.GetValue().ToString(CultureInfo.InvariantCulture);
            var op = OpName(EvaluatorOf(rule));
            string? value = rule switch
            {
                FilterStringRule stringRule => stringRule.RuleString,
                FilterDoubleRule doubleRule => doubleRule.RuleValue.ToString(
                    "R", CultureInfo.InvariantCulture) + " (internal)",
                FilterIntegerRule integerRule => integerRule.RuleValue.ToString(
                    CultureInfo.InvariantCulture),
                FilterElementIdRule elementRule => elementRule.RuleValue is null
                    ? null
                    : elementRule.RuleValue.GetValue().ToString(CultureInfo.InvariantCulture),
                _ => null
            };

            return value is null ? name + " " + op : name + " " + op + " " + value;
        }
        catch (Exception)
        {
            return "rule";
        }
    }

    private static object EvaluatorOf(FilterRule rule)
    {
        try
        {
            switch (rule)
            {
                case FilterStringRule stringRule:
                    return stringRule.GetEvaluator();
                case FilterDoubleRule doubleRule:
                    return doubleRule.GetEvaluator();
                case FilterIntegerRule integerRule:
                    return integerRule.GetEvaluator();
                case FilterElementIdRule elementRule:
                    return elementRule.GetEvaluator();
                default:
                    return rule.GetType().Name;
            }
        }
        catch (Exception)
        {
            return rule.GetType().Name;
        }
    }

    private static string EvaluatorName(FilterRule rule) => EvaluatorOf(rule).GetType().Name;

    private static string OpName(object evaluator)
    {
        var name = evaluator.GetType().Name;
        if (name.StartsWith("Filter", StringComparison.Ordinal))
            name = name.Substring(6);
        if (name.StartsWith("String", StringComparison.Ordinal))
            name = name.Substring(6);
        else if (name.StartsWith("Numeric", StringComparison.Ordinal))
            name = name.Substring(7);
        else if (name.StartsWith("Integer", StringComparison.Ordinal))
            name = name.Substring(7);
        return name;
    }

    private static Dictionary<string, object?> DescribeRule(
        FilterRule rule,
        Document? doc,
        IReadOnlyDictionary<long, ForgeTypeId>? parameters = null)
    {
        var result = new Dictionary<string, object?> { ["type"] = rule.GetType().Name };
        long? ruleParameterId = null;

        try
        {
            var parameterId = rule.GetRuleParameter();
            if (parameterId is not null && parameterId != ElementId.InvalidElementId)
            {
                ruleParameterId = parameterId.GetValue();
                result["parameterId"] = parameterId.GetValue();
                var parameterName = ResolveParameterName(doc, parameterId);
                if (!string.IsNullOrWhiteSpace(parameterName))
                    result["parameterName"] = parameterName;
            }
        }
        catch (Exception)
        {
            // A category rule has no parameter.
        }

        try
        {
            switch (rule)
            {
                case FilterStringRule stringRule:
                    result["op"] = stringRule.GetEvaluator().GetType().Name;
                    result["value"] = stringRule.RuleString;
                    break;

                case FilterDoubleRule doubleRule:
                    result["op"] = doubleRule.GetEvaluator().GetType().Name;
                    result["value"] = doubleRule.RuleValue;
                    result["epsilon"] = doubleRule.Epsilon;
                    // The raw number cannot be distinguished from feet by the model:
                    // 500 was read as 500 mm while the rule stored 500 ft (B1).
                    // previousRules round-trip must pass unit="raw" (B2).
                    result["unit"] = "raw";
                    if (doc is not null && parameters is not null
                        && ruleParameterId.HasValue
                        && parameters.TryGetValue(ruleParameterId.Value, out var dataType))
                    {
                        result["displayValue"] = AgentParameterValue.FormatDisplay(
                            doc, dataType, doubleRule.RuleValue);
                    }

                    break;

                case FilterIntegerRule integerRule:
                    result["op"] = integerRule.GetEvaluator().GetType().Name;
                    result["value"] = integerRule.RuleValue;
                    break;

                case FilterElementIdRule elementRule:
                    result["op"] = elementRule.GetEvaluator().GetType().Name;
                    result["value"] = elementRule.RuleValue?.GetValue();
                    break;

                case FilterInverseRule inverseRule:
                    result["inner"] = DescribeRule(inverseRule.GetInnerRule(), doc, parameters);
                    break;
            }
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
        }

        return result;
    }

    /// <summary>
    /// Human name of the parameter behind a rule: project/shared parameters via
    /// the element, built-ins via <see cref="LabelUtils"/>. Keeping the name next
    /// to parameterId is what makes a replaced rule set restorable by name.
    /// </summary>
    private static string? ResolveParameterName(Document? doc, ElementId parameterId)
    {
        if (doc is not null)
        {
            try
            {
                var element = doc.GetElement(parameterId);
                if (element is not null && !string.IsNullOrWhiteSpace(element.Name))
                    return element.Name;
            }
            catch (Exception)
            {
                // Fall through to the built-in label.
            }
        }

        try
        {
            var value = parameterId.GetValue();
            if (value < 0)
            {
                var label = LabelUtils.GetLabelFor((BuiltInParameter)value);
                return string.IsNullOrWhiteSpace(label) ? null : label;
            }
        }
        catch (Exception)
        {
            // No label available.
        }

        return null;
    }
}