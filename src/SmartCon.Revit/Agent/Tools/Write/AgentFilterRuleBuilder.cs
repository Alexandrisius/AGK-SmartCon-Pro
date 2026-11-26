using System.Globalization;
using System.Text.Json;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SmartCon.Core.Compatibility;
using SmartCon.Revit.Compatibility;
using SmartCon.Revit.Agent.Query;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Turns the generic filter DSL into a Revit ElementFilter: one
/// ElementParameterFilter per condition, combined with AND or OR.
/// Unit conversion lives in <c>AgentFilterRuleBuilder.Units.cs</c>.
/// </summary>
internal static partial class AgentFilterRuleBuilder
{
    internal const int MaxConditions = 10;

    /// <summary>
    /// Storage and data type of a filterable parameter. Type parameters are not in
    /// <c>Element.Parameters</c>, so the probe walks instance в†’ type в†’ project/shared
    /// ParameterElement; without it a numeric type parameter silently became a text
    /// rule and "500" was written as 500 feet (external run, B1).
    /// </summary>
    internal sealed class ParameterProbe
    {
        public StorageType? Storage { get; init; }

        public ForgeTypeId? DataType { get; init; }

        public string? Name { get; init; }
    }

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
                    "Filtering by workset is not implemented: use a parameter rule or " +
                    "set_view_visibility worksets to hide a workset."
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
        var probe = ProbeParameter(doc, categories, parameterId);
        var rule = CreateRule(
            doc, field, parameterId, condition.Op, valueText, condition.Unit, probe, out var error);
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

        // System name/abbreviation are text parameters; no probe needed.
        var probe = new ParameterProbe { Storage = StorageType.String };
        var filters = new List<ElementFilter>(2);
        foreach (var id in new[] { nameId, abbreviationId })
        {
            if (!catalog.Contains(id))
                continue;

            var rule = CreateRule(
                doc, "system", id, condition.Op, valueText, unit: null, probe, out _);
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

    /// <summary>
    /// Probes the parameter through every place it can live: the instance, its
    /// element type, and (for project/shared parameters with a positive id) the
    /// ParameterElement definition.
    /// </summary>
    private static ParameterProbe? ProbeParameter(
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

            var instanceParameter = FindParameter(sample, parameterId);
            if (instanceParameter is not null)
                return Describe(instanceParameter);

            var type = doc.GetElement(sample.GetTypeId());
            var typeParameter = type is null ? null : FindParameter(type, parameterId);
            if (typeParameter is not null)
                return Describe(typeParameter);
        }

        if (parameterId.GetValue() > 0 && doc.GetElement(parameterId) is ParameterElement element)
        {
            var dataType = SafeDataType(element.GetDefinition());
            return new ParameterProbe
            {
                Name = element.Name,
                DataType = dataType,
                Storage = StorageFromSpec(dataType)
            };
        }

        // A category can have types loaded but no placed instances (doors, windows).
        // Type parameters live on the element type, so scan those too.
        foreach (var categoryId in categories)
        {
            Element? type;
            try
            {
                type = new FilteredElementCollector(doc)
                    .OfCategoryId(categoryId)
                    .WhereElementIsElementType()
                    .FirstElement();
            }
            catch (Exception)
            {
                continue;
            }

            if (type is null)
                continue;

            var typeParameter = FindParameter(type, parameterId);
            if (typeParameter is not null)
                return Describe(typeParameter);
        }

        return null;
    }

    private static Parameter? FindParameter(Element element, ElementId parameterId)
    {
        try
        {
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter.Id.GetValue() == parameterId.GetValue())
                    return parameter;
            }
        }
        catch (Exception)
        {
            // An element that rejects enumeration is skipped.
        }

        return null;
    }

    private static ParameterProbe Describe(Parameter parameter) =>
        new()
        {
            Storage = parameter.StorageType,
            DataType = SafeDataType(parameter.Definition),
            Name = parameter.Definition?.Name
        };

    private static ForgeTypeId? SafeDataType(Definition? definition)
    {
        try
        {
            return RevitUnitsCompat.GetDataType(definition);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static FilterRule? CreateRule(
        Document doc,
        string field,
        ElementId parameterId,
        string? op,
        string? value,
        string? unit,
        ParameterProbe? probe,
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

        // Fail closed when neither an instance nor a type nor a ParameterElement
        // reveals the parameter: guessing String wrote 500 as text, guessing Double
        // wrote 500 as feet. unit="raw" is explicit, so it is allowed.
        if (probe?.Storage is null)
        {
            var rawUnit = string.Equals(unit?.Trim(), "raw", StringComparison.OrdinalIgnoreCase);
            if (rawUnit)
                return CreateDoubleRule(doc, parameterId, operation, value!, unit, probe, out error);

            var looksNumeric = TryParseNumber(value!, out _);
            if (!string.IsNullOrWhiteSpace(unit) || looksNumeric)
            {
                error = "Cannot determine the storage and spec of parameter '" +
                        (probe?.Name ?? parameterId.GetValue().ToString(CultureInfo.InvariantCulture)) +
                        "': the categories have no elements and it is not a project parameter " +
                        "element. Add one element, or pass unit=\"raw\" to write internal units.";
                return null;
            }
        }

        var effectiveStorage = probe?.Storage ?? StorageType.String;
        try
        {
            switch (effectiveStorage)
            {
                case StorageType.Integer:
                    return CreateIntegerRule(parameterId, operation, value!, out error);
                case StorageType.Double:
                    return CreateDoubleRule(doc, parameterId, operation, value!, unit, probe, out error);
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
