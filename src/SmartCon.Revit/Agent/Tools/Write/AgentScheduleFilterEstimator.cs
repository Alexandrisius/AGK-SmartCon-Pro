using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Agent.Query;
using SmartCon.Revit.Agent.Tools.Read;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Dry-run row estimate for set_schedule_filter (#452): walks the elements a
/// schedule draws its rows from (AgentScheduleReader.CollectSourceElements) and
/// evaluates the (already applied) ScheduleFilters against their parameter
/// values. A view-scoped collector proved unreliable here (returned 0 for a
/// schedule view inside the dry-run transaction), so the rules are evaluated
/// manually — the same AND semantics Revit applies. The count is an element
/// count: grouped (non-itemized) schedules merge elements into fewer rows, and
/// view-level restrictions (phases) are not modelled. Multi-category schedules
/// are estimated too: they used to get no estimate at all because their
/// CategoryId is InvalidElementId.
/// </summary>
internal static class AgentScheduleFilterEstimator
{
    private const int SampleSize = 5;
    private const int MaxDistinctValues = 2000;
    internal const int MaxScannedElements = 20000;

    internal static (int Count, List<string> Sample)? Estimate(
        Document doc,
        ViewSchedule schedule,
        CancellationToken ct)
    {
        try
        {
            var definition = schedule.Definition;
            try
            {
                if (definition.IsKeySchedule)
                    return null;
            }
            catch (Exception)
            {
                return null;
            }

            var rules = ReadRules(definition);
            if (rules is null)
                return null;

            return Estimate(doc, definition, rules, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The estimate is best-effort: without it the dry-run report is
            // still a valid preview of the rule changes.
            return null;
        }
    }

    /// <summary>
    /// Estimate for an explicit rule list: the entry of a hypothetical filter
    /// that exists on no schedule yet (the explication pre-flight of
    /// build_system_sheets). Null when the definition cannot be evaluated.
    /// </summary>
    internal static (int Count, List<string> Sample)? Estimate(
        Document doc,
        ScheduleDefinition definition,
        IReadOnlyList<(long ParameterId, ScheduleFilter Filter)> rules,
        CancellationToken ct)
    {
        try
        {
            var count = 0;
            var sample = new List<string>(SampleSize);
            foreach (var element in AgentScheduleReader.CollectSourceElements(
                         doc, definition, MaxScannedElements))
            {
                ct.ThrowIfCancellationRequested();
                if (!PassesAll(doc, element, rules))
                    continue;

                count++;
                if (sample.Count < SampleSize)
                    sample.Add(element.Name);
            }

            return (count, sample);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Rules of the filters already on the definition. Null when one of them
    /// has no parameter (a calculated field): such a set cannot be evaluated
    /// element by element.
    /// </summary>
    internal static List<(long ParameterId, ScheduleFilter Filter)>? ReadRules(
        ScheduleDefinition definition)
    {
        var rules = new List<(long ParameterId, ScheduleFilter Filter)>();
        foreach (var filter in definition.GetFilters())
        {
            ScheduleField? field;
            try
            {
                field = filter.FieldId is null ? null : definition.GetField(filter.FieldId);
            }
            catch (Exception)
            {
                field = null;
            }

            var parameterId = field?.ParameterId;
            if (parameterId is null || parameterId == ElementId.InvalidElementId)
                return null;

            rules.Add((parameterId.GetValue(), filter));
        }

        return rules;
    }

    /// <summary>
    /// Distinct filled values of a parameter over the elements the schedule
    /// draws its rows from (insertion order), at most <paramref name="max"/>.
    /// </summary>
    internal static List<string> DistinctValues(
        Document doc,
        ScheduleDefinition definition,
        long parameterId,
        int max,
        CancellationToken ct = default)
    {
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var element in AgentScheduleReader.CollectSourceElements(
                         doc, definition, MaxScannedElements))
            {
                ct.ThrowIfCancellationRequested();
                var text = ParameterText(ResolveParameter(doc, element, parameterId));
                if (string.IsNullOrWhiteSpace(text))
                    continue;

                // net48 ref assemblies carry no [NotNullWhen] on IsNullOrWhiteSpace.
                distinct.Add(text!);
                if (distinct.Count >= MaxDistinctValues)
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The values are a hint: whatever was collected before the failure is returned.
        }

        var values = new List<string>(distinct);
        if (values.Count > max)
            values.RemoveRange(max, values.Count - max);

        return values;
    }

    /// <summary>
    /// The values closest to <paramref name="requested"/> (case-insensitive
    /// edit distance): what the caller could have meant by a filter value that
    /// matched nothing.
    /// </summary>
    internal static List<string> NearestValues(
        Document doc,
        ScheduleDefinition definition,
        long parameterId,
        string requested,
        int max = SampleSize,
        CancellationToken ct = default)
    {
        var values = DistinctValues(doc, definition, parameterId, MaxDistinctValues, ct);
        values.Sort((left, right) =>
        {
            var byDistance = Distance(requested, left).CompareTo(Distance(requested, right));
            return byDistance != 0
                ? byDistance
                : string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        });
        if (values.Count > max)
            values.RemoveRange(max, values.Count - max);

        return values;
    }

    /// <summary>
    /// The hint of a filter value that matched nothing: the closest values
    /// that do exist, or a plain statement that the field carries no filled
    /// value at all. Empty when the values cannot be read.
    /// </summary>
    internal static string DescribeClosestValues(
        Document doc,
        ScheduleDefinition definition,
        long parameterId,
        string requested)
    {
        var nearest = NearestValues(doc, definition, parameterId, requested ?? string.Empty);
        if (nearest.Count == 0)
            return " The field has no filled value on the elements of this schedule.";

        return " Closest existing values: " +
               string.Join(", ", nearest.Select(value => "'" + value + "'")) + ".";
    }

    private static Parameter? ResolveParameter(Document doc, Element element, long parameterId)
    {
        Definition? definition = null;
        try
        {
            if (parameterId >= 0)
            {
                definition = (doc.GetElement(ElementIdCompat.Create(parameterId))
                    as ParameterElement)?.GetDefinition();
                if (definition is null)
                    return null;
            }

            // The same lift query_elements applies: a blank or valueless instance
            // view of a type-bound parameter reads the type's value, while a blank
            // the element stores itself (a two-sided built-in) stays its own.
            var parameter = ParameterOf(element, definition, parameterId);

            return parameter is null
                ? null
                : AgentParameterResolver.LiftValuelessToType(doc, element, parameter);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static Parameter? ParameterOf(Element element, Definition? definition, long parameterId) =>
        definition is not null
            ? element.get_Parameter(definition)
            : element.get_Parameter((BuiltInParameter)parameterId);

    private static string? ParameterText(Parameter? parameter)
    {
        if (parameter is null)
            return null;

        try
        {
            if (!parameter.HasValue)
                return null;

            return parameter.StorageType == StorageType.String
                ? parameter.AsString()
                : parameter.AsValueString();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int Distance(string left, string right)
    {
        if (left.Length == 0)
            return right.Length;
        if (right.Length == 0)
            return left.Length;

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var column = 0; column <= right.Length; column++)
            previous[column] = column;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = char.ToLowerInvariant(left[row - 1])
                           == char.ToLowerInvariant(right[column - 1])
                    ? 0
                    : 1;
                current[column] = Math.Min(
                    Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[right.Length];
    }

    private static bool PassesAll(
        Document doc,
        Element element,
        IReadOnlyList<(long ParameterId, ScheduleFilter Filter)> rules)
    {
        foreach (var (parameterId, filter) in rules)
        {
            var parameter = ResolveParameter(doc, element, parameterId);
            if (parameter is null || !Matches(filter, parameter))
                return false;
        }

        return true;
    }

    private static bool Matches(ScheduleFilter filter, Parameter parameter)
    {
        try
        {
            if (filter.IsStringValue)
                return MatchesString(filter.FilterType, parameter.AsString(), filter.GetStringValue());
            if (filter.IsDoubleValue)
                return MatchesNumber(filter.FilterType, parameter.AsDouble(), filter.GetDoubleValue());
            if (filter.IsIntegerValue)
                return MatchesNumber(
                    filter.FilterType, parameter.AsInteger(), filter.GetIntegerValue());
            if (filter.IsElementIdValue)
            {
                var actual = Key(parameter.AsElementId());
                var expected = Key(filter.GetElementIdValue());
                return filter.FilterType == ScheduleFilterType.NotEqual
                    ? actual != expected
                    : actual == expected;
            }

            return false;
        }
        catch (Exception)
        {
            // A value kind the parameter cannot produce never passes.
            return false;
        }
    }

    private static bool MatchesString(ScheduleFilterType op, string? actual, string? expected)
    {
        var left = actual ?? string.Empty;
        var right = expected ?? string.Empty;
        var comparison = StringComparison.OrdinalIgnoreCase;

        switch (op)
        {
            case ScheduleFilterType.Equal:
                return string.Equals(left, right, comparison);
            case ScheduleFilterType.NotEqual:
                return !string.Equals(left, right, comparison);
            case ScheduleFilterType.Contains:
                return left.IndexOf(right, comparison) >= 0;
            case ScheduleFilterType.BeginsWith:
                return left.StartsWith(right, comparison);
            case ScheduleFilterType.EndsWith:
                return left.EndsWith(right, comparison);
            default:
                return false;
        }
    }

    private static bool MatchesNumber(ScheduleFilterType op, double actual, double expected)
    {
        switch (op)
        {
            case ScheduleFilterType.Equal:
                return Math.Abs(actual - expected) < 1e-9;
            case ScheduleFilterType.NotEqual:
                return Math.Abs(actual - expected) >= 1e-9;
            case ScheduleFilterType.GreaterThan:
                return actual > expected;
            case ScheduleFilterType.GreaterThanOrEqual:
                return actual >= expected;
            case ScheduleFilterType.LessThan:
                return actual < expected;
            case ScheduleFilterType.LessThanOrEqual:
                return actual <= expected;
            default:
                return false;
        }
    }

    private static long Key(ElementId? id) =>
        id is null || id == ElementId.InvalidElementId ? -1 : id.GetValue();
}
