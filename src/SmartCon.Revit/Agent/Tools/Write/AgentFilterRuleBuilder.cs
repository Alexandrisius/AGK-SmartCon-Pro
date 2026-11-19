using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SmartCon.Core.Compatibility;

namespace SmartCon.Revit.Agent.Tools.Write;


/// <summary>
/// Turns the generic filter DSL into a Revit ElementFilter: one
/// ElementParameterFilter per condition, combined with AND or OR.
/// </summary>
internal static class AgentFilterRuleBuilder
{
    internal const int MaxConditions = 10;

    internal static FilterRuleBuildResult Build(
        Document doc,
        IReadOnlyList<ElementId> categories,
        IReadOnlyList<FilterConditionSpec> conditions,
        string logic)
    {
        if (conditions.Count == 0)
            return new FilterRuleBuildResult { Filter = null };

        if (string.Equals(logic, "or", StringComparison.OrdinalIgnoreCase))
            return BuildCombined(doc, categories, conditions, orLogic: true);

        return BuildCombined(doc, categories, conditions, orLogic: false);
    }

    private static FilterRuleBuildResult BuildCombined(
        Document doc,
        IReadOnlyList<ElementId> categories,
        IReadOnlyList<FilterConditionSpec> conditions,
        bool orLogic)
    {
        var catalog = FilterParameterCatalog.Build(doc, categories);
        var filters = new List<ElementFilter>(conditions.Count);

        foreach (var condition in conditions)
        {
            var built = BuildCondition(doc, categories, catalog, condition);
            if (!built.Ok)
                return built;

            if (built.Filter is not null)
                filters.Add(built.Filter);
        }

        if (filters.Count == 0)
            return new FilterRuleBuildResult { Filter = null };

        if (filters.Count == 1)
            return new FilterRuleBuildResult { Filter = filters[0] };

        var combined = orLogic
            ? (ElementFilter)new LogicalOrFilter(filters)
            : new LogicalAndFilter(filters);
        return new FilterRuleBuildResult { Filter = combined };
    }

    private static FilterRuleBuildResult BuildCondition(
        Document doc,
        IReadOnlyList<ElementId> categories,
        FilterParameterCatalog catalog,
        FilterConditionSpec condition)
    {
        var field = (condition.Field ?? "parameter").Trim().ToLowerInvariant();

        if (field == "workset")
        {
            return new FilterRuleBuildResult
            {
                ErrorCode = "workset_not_supported",
                Reason =
                    "Filtering by workset is unavailable: SmartCon write tools do not support " +
                    "workshared documents."
            };
        }

        if (field == "system")
        {
            return BuildSystemCondition(doc, categories, catalog, condition);
        }

        var parameterId = ResolveParameterId(field, condition, catalog);
        if (parameterId is null)
        {
            var name = condition.Parameter ?? field;
            if (catalog.ExistsInDocument(name))
            {
                return new FilterRuleBuildResult
                {
                    ErrorCode = "parameter_not_filterable",
                    Reason = "Parameter '" + name + "' exists in the model but Revit does not " +
                             "allow filtering the requested categories by it.",
                    AvailableParameters = catalog.Sample(60)
                };
            }

            return new FilterRuleBuildResult
            {
                ErrorCode = "parameter_not_found",
                Reason = "Parameter '" + name + "' is not filterable for the requested categories.",
                AvailableParameters = catalog.Sample(60)
            };
        }

        if (!catalog.Contains(parameterId))
        {
            return new FilterRuleBuildResult
            {
                ErrorCode = "parameter_not_filterable",
                Reason = "Parameter '" + (condition.Parameter ?? field) +
                         "' does not apply to the requested categories.",
                AvailableParameters = catalog.Sample(60)
            };
        }

        var valueText = ReadValueText(condition);
        var storage = ProbeStorage(doc, categories, parameterId);
        var rule = CreateRule(doc, field, parameterId, condition.Op, valueText, storage,
            out var error);
        if (rule is null)
        {
            return new FilterRuleBuildResult
            {
                ErrorCode = "invalid_condition",
                Reason = error ?? "The condition could not be converted into a filter rule.",
                AvailableParameters = catalog.Sample(60)
            };
        }

        return new FilterRuleBuildResult { Filter = new ElementParameterFilter(rule) };
    }

    private static FilterRuleBuildResult BuildSystemCondition(
        Document doc,
        IReadOnlyList<ElementId> categories,
        FilterParameterCatalog catalog,
        FilterConditionSpec condition)
    {
        var nameId = new ElementId(BuiltInParameter.RBS_SYSTEM_NAME_PARAM);
        var abbreviationId = new ElementId(BuiltInParameter.RBS_SYSTEM_ABBREVIATION_PARAM);
        var valueText = ReadValueText(condition);
        if (string.IsNullOrWhiteSpace(valueText))
        {
            return new FilterRuleBuildResult
            {
                ErrorCode = "invalid_condition",
                Reason = "The system condition requires a value."
            };
        }

        var filters = new List<ElementFilter>(2);
        foreach (var id in new[] { nameId, abbreviationId })
        {
            if (!catalog.Contains(id))
                continue;

            var rule = CreateRule(
                doc, "system", id, condition.Op, valueText, StorageType.String, out _);
            if (rule is not null)
                filters.Add(new ElementParameterFilter(rule));
        }

        if (filters.Count == 0)
        {
            return new FilterRuleBuildResult
            {
                ErrorCode = "parameter_not_filterable",
                Reason = "The categories have no filterable system name parameter.",
                AvailableParameters = catalog.Sample(60)
            };
        }

        return new FilterRuleBuildResult
        {
            Filter = filters.Count == 1 ? filters[0] : new LogicalOrFilter(filters)
        };
    }

    private static ElementId? ResolveParameterId(
        string field,
        FilterConditionSpec condition,
        FilterParameterCatalog catalog)
    {
        switch (field)
        {
            case "family":
                return new ElementId(BuiltInParameter.SYMBOL_FAMILY_NAME_PARAM);
            case "type":
                return new ElementId(BuiltInParameter.SYMBOL_NAME_PARAM);
            case "level":
                return FirstFilterable(
                    catalog,
                    BuiltInParameter.LEVEL_PARAM,
                    BuiltInParameter.WALL_BASE_CONSTRAINT);
            case "room":
                return FirstFilterable(
                    catalog,
                    BuiltInParameter.ROOM_NAME,
                    BuiltInParameter.ROOM_NUMBER);
            case "parameter":
                return catalog.TryResolve(condition.Parameter, out var id) ? id : null;
            default:
                return catalog.TryResolve(field, out var byName) ? byName : null;
        }
    }

    private static ElementId? FirstFilterable(
        FilterParameterCatalog catalog,
        params BuiltInParameter[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var id = new ElementId(candidate);
            if (catalog.Contains(id))
                return id;
        }

        return null;
    }

    private static StorageType? ProbeStorage(
        Document doc,
        IReadOnlyList<ElementId> categories,
        ElementId parameterId)
    {
        foreach (var categoryId in categories)
        {
            Element? sample;
            try
            {
                sample = new FilteredElementCollector(doc)
                    .OfCategoryId(categoryId)
                    .WhereElementIsNotElementType()
                    .FirstElement();
            }
            catch (Exception)
            {
                continue;
            }

            if (sample is null)
                continue;

            try
            {
                foreach (Parameter parameter in sample.Parameters)
                {
                    if (parameter.Id.GetValue() == parameterId.GetValue())
                        return parameter.StorageType;
                }
            }
            catch (Exception)
            {
                // Parameter not present on this sample; try the next category.
            }
        }

        return null;
    }

    private static FilterRule? CreateRule(
        Document doc,
        string field,
        ElementId parameterId,
        string? op,
        string? value,
        StorageType? storage,
        out string? error)
    {
        error = null;
        var operation = (op ?? "eq").Trim().ToLowerInvariant();

        if (operation is "hasvalue" or "novalue")
        {
            return operation == "hasvalue"
                ? ParameterFilterRuleFactory.CreateHasValueParameterRule(parameterId)
                : ParameterFilterRuleFactory.CreateHasNoValueParameterRule(parameterId);
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Operator '" + operation + "' requires a value.";
            return null;
        }

        var effectiveStorage = storage ?? StorageType.String;
        try
        {
            switch (effectiveStorage)
            {
                case StorageType.Integer:
                    return CreateIntegerRule(parameterId, operation, value!, out error);
                case StorageType.Double:
                    return CreateDoubleRule(parameterId, operation, value!, out error);
                case StorageType.ElementId:
                    return CreateElementIdRule(doc, field, parameterId, operation, value!, out error);
                default:
                    return CreateStringRule(parameterId, operation, value!, out error);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            error = "Revit rejected the rule: " + ex.Message;
            return null;
        }
    }

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

    private static FilterRule? CreateDoubleRule(
        ElementId parameterId,
        string operation,
        string value,
        out string? error)
    {
        error = null;
        if (!double.TryParse(
                value.Replace(',', '.'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var number))
        {
            error = "Parameter is numeric; '" + value + "' is not a number. " +
                    "Values are compared in Revit internal units (feet).";
            return null;
        }

        const double epsilon = 0.0001;
        switch (operation)
        {
            case "eq":
                return ParameterFilterRuleFactory.CreateEqualsRule(parameterId, number, epsilon);
            case "neq":
                return ParameterFilterRuleFactory.CreateNotEqualsRule(parameterId, number, epsilon);
            case "gt":
                return ParameterFilterRuleFactory.CreateGreaterRule(parameterId, number, epsilon);
            case "ge":
                return ParameterFilterRuleFactory.CreateGreaterOrEqualRule(
                    parameterId, number, epsilon);
            case "lt":
                return ParameterFilterRuleFactory.CreateLessRule(parameterId, number, epsilon);
            case "le":
                return ParameterFilterRuleFactory.CreateLessOrEqualRule(
                    parameterId, number, epsilon);
            default:
                error = "Unsupported operator '" + operation + "' for a numeric parameter.";
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

    private static string? ReadValueText(FilterConditionSpec condition)
    {
        var value = condition.Value;
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                return value.GetString();
            case JsonValueKind.Number:
                return value.GetRawText();
            case JsonValueKind.True:
                return "True";
            case JsonValueKind.False:
                return "False";
            default:
                return null;
        }
    }
}