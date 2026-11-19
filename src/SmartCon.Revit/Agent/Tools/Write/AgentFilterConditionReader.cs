using System.Text.Json;
using SmartCon.Revit.Agent.Tools.Write;

namespace SmartCon.Revit.Agent.Tools.Write;

/// <summary>
/// Shared reader for the filter condition DSL used by create_view_filter and
/// set_filter_rules.
/// </summary>
internal static class AgentFilterConditionReader
{
    internal static List<FilterConditionSpec> Read(JsonElement args, WriteOutcome outcome)
    {
        var conditions = new List<FilterConditionSpec>();
        if (args.ValueKind != JsonValueKind.Object
            || !args.TryGetProperty("conditions", out var array)
            || array.ValueKind != JsonValueKind.Array)
        {
            return conditions;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                outcome.Success = false;
                outcome.ErrorCode = "invalid_arguments";
                outcome.Message = "Each condition must be an object.";
                return conditions;
            }

            var spec = new FilterConditionSpec
            {
                Field = AgentJson.GetString(item, "field"),
                Parameter = AgentJson.GetString(item, "parameter"),
                Op = AgentJson.GetString(item, "op") ?? "eq"
            };

            if (string.IsNullOrWhiteSpace(spec.Parameter)
                && (string.IsNullOrWhiteSpace(spec.Field)
                    || string.Equals(spec.Field, "parameter", StringComparison.OrdinalIgnoreCase)))
            {
                outcome.Success = false;
                outcome.ErrorCode = "invalid_arguments";
                outcome.Message =
                    "conditions[].parameter is required when field is \"parameter\" " +
                    "(Revit then reports the confusing 'Parameter parameter is not filterable').";
                return conditions;
            }

            if (item.TryGetProperty("value", out var value)
                && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            {
                spec.Value = value.Clone();
                spec.HasValue = true;
            }

            conditions.Add(spec);
        }

        return conditions;
    }
}