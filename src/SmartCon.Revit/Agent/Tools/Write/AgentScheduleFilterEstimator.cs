using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Dry-run row estimate for set_schedule_filter (#452): walks the elements of
/// the schedule category and evaluates the (already applied) ScheduleFilters
/// against their parameter values. A view-scoped collector proved unreliable
/// here (returned 0 for a schedule view inside the dry-run transaction), so the
/// rules are evaluated manually — the same AND semantics Revit applies. The
/// count is an element count: grouped (non-itemized) schedules merge elements
/// into fewer rows, and view-level restrictions (phases) are not modelled.
/// </summary>
internal static class AgentScheduleFilterEstimator
{
    internal static (int Count, List<string> Sample)? Estimate(
        Document doc,
        ViewSchedule schedule,
        CancellationToken ct)
    {
        try
        {
            var definition = schedule.Definition;
            var categoryId = definition.CategoryId;
            if (categoryId is null || categoryId == ElementId.InvalidElementId)
                return null;

            try
            {
                if (definition.IsKeySchedule)
                    return null;
            }
            catch (Exception)
            {
                return null;
            }

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

            var categoryValue = categoryId.GetValue();
            if (categoryValue is < int.MinValue or > int.MaxValue)
                return null;

            var elements = new FilteredElementCollector(doc)
                .OfCategory((BuiltInCategory)(int)categoryValue)
                .WhereElementIsNotElementType()
                .ToElements();

            var count = 0;
            var sample = new List<string>(5);
            foreach (var element in elements)
            {
                ct.ThrowIfCancellationRequested();
                if (!PassesAll(doc, element, rules))
                    continue;

                count++;
                if (sample.Count < 5)
                    sample.Add(element.Name);
            }

            return (count, sample);
        }
        catch (Exception)
        {
            // The estimate is best-effort: without it the dry-run report is
            // still a valid preview of the rule changes.
            return null;
        }
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

    private static Parameter? ResolveParameter(Document doc, Element element, long parameterId)
    {
        try
        {
            if (parameterId < 0)
                return element.get_Parameter((BuiltInParameter)parameterId);

            var parameterElement = doc.GetElement(ElementIdCompat.Create(parameterId))
                as ParameterElement;
            return parameterElement is null
                ? null
                : element.get_Parameter(parameterElement.GetDefinition());
        }
        catch (Exception)
        {
            return null;
        }
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
