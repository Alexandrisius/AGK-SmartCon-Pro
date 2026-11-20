using System.Globalization;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>Typed rule factories (text, integer, element id) plus id/name resolution.</summary>
internal static partial class AgentFilterRuleBuilder
{
    private static FilterRule? CreateStringRule(
        ElementId parameterId,
        string operation,
        string value,
        out string? error)
    {
        error = null;
        switch (operation)
        {
            case "eq":
                return ParameterFilterRuleFactory.CreateEqualsRule(parameterId, value);
            case "neq":
                return ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, value);
            case "contains":
                return ParameterFilterRuleFactory.CreateContainsRule(parameterId, value);
            case "notcontains":
                return ParameterFilterRuleFactory.CreateNotContainsRule(parameterId, value);
            case "beginswith":
                return ParameterFilterRuleFactory.CreateBeginsWithRule(parameterId, value);
            case "notbeginswith":
                return ParameterFilterRuleFactory.CreateNotBeginsWithRule(parameterId, value);
            case "endswith":
                return ParameterFilterRuleFactory.CreateEndsWithRule(parameterId, value);
            case "notendswith":
                return ParameterFilterRuleFactory.CreateNotEndsWithRule(parameterId, value);
            case "gt":
                return ParameterFilterRuleFactory.CreateGreaterRule(parameterId, value);
            case "ge":
                return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameterId, value);
            case "lt":
                return ParameterFilterRuleFactory.CreateLessRule(parameterId, value);
            case "le":
                return ParameterFilterRuleFactory.CreateLessOrEqualRule(parameterId, value);
            default:
                error = "Unsupported operator '" + operation + "' for a text parameter. " +
                        "Use eq, neq, contains, notcontains, beginswith, endswith, gt, ge, lt, le.";
                return null;
        }
    }

    private static FilterRule? CreateIntegerRule(
        ElementId parameterId,
        string operation,
        string value,
        out string? error)
    {
        error = null;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            error = "Parameter is integer; '" + value + "' is not a whole number.";
            return null;
        }

        switch (operation)
        {
            case "eq":
                return ParameterFilterRuleFactory.CreateEqualsRule(parameterId, number);
            case "neq":
                return ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, number);
            case "gt":
                return ParameterFilterRuleFactory.CreateGreaterRule(parameterId, number);
            case "ge":
                return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameterId, number);
            case "lt":
                return ParameterFilterRuleFactory.CreateLessRule(parameterId, number);
            case "le":
                return ParameterFilterRuleFactory.CreateLessOrEqualRule(parameterId, number);
            default:
                error = "Unsupported operator '" + operation + "' for an integer parameter.";
                return null;
        }
    }

    private static FilterRule? CreateElementIdRule(
        Document doc,
        string field,
        ElementId parameterId,
        string operation,
        string value,
        out string? error)
    {
        error = null;
        if (!TryResolveElementId(doc, field, value, out var target, out var reason))
        {
            error = reason;
            return null;
        }

        switch (operation)
        {
            case "eq":
                return ParameterFilterRuleFactory.CreateEqualsRule(parameterId, target);
            case "neq":
                return ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, target);
            case "gt":
                return ParameterFilterRuleFactory.CreateGreaterRule(parameterId, target);
            case "ge":
                return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(parameterId, target);
            case "lt":
                return ParameterFilterRuleFactory.CreateLessRule(parameterId, target);
            case "le":
                return ParameterFilterRuleFactory.CreateLessOrEqualRule(parameterId, target);
            default:
                error = "Unsupported operator '" + operation + "' for an element-id parameter.";
                return null;
        }
    }

    private static bool TryResolveElementId(
        Document doc,
        string field,
        string value,
        out ElementId id,
        out string? reason)
    {
        id = ElementId.InvalidElementId;
        reason = null;

        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
        {
            var candidate = doc.GetElement(ElementIdCompat.Create(numeric));
            if (candidate is not null)
            {
                id = candidate.Id;
                return true;
            }

            reason = "Element " + numeric + " does not exist.";
            return false;
        }

        if (field is "level" or "parameter")
        {
            foreach (var level in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>())
            {
                if (string.Equals(level.Name, value, StringComparison.OrdinalIgnoreCase))
                {
                    id = level.Id;
                    return true;
                }
            }

            reason = "Level '" + value + "' was not found; pass its element id.";
            return false;
        }

        if (field == "room")
        {
            foreach (var room in new FilteredElementCollector(doc).OfClass(typeof(Room)).Cast<Room>())
            {
                if (string.Equals(room.Number, value, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(room.Name, value, StringComparison.OrdinalIgnoreCase))
                {
                    id = room.Id;
                    return true;
                }
            }

            reason = "Room '" + value + "' was not found; pass its element id.";
            return false;
        }

        reason = "The parameter stores an element id; pass the numeric id or a known name.";
        return false;
    }

}
