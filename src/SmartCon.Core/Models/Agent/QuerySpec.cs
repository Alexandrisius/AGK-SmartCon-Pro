using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartCon.Core.Models.Agent;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueryOperator
{
    Eq = 0,
    Neq = 1,
    Gt = 2,
    Gte = 3,
    Lt = 4,
    Lte = 5,
    Contains = 6,
    StartsWith = 7,
    EndsWith = 8,
    In = 9,
    NotIn = 10,
    Exists = 11,
    NotExists = 12
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum QueryLogic
{
    And = 0,
    Or = 1
}

public sealed class QueryCondition
{
    public required string Parameter { get; init; }

    public QueryOperator Op { get; init; }

    public JsonElement? Value { get; init; }

    public string? Unit { get; init; }
}

public sealed class QuerySpec
{
    public const int DefaultLimit = 50;
    public const int MaxLimit = 200;

    public IReadOnlyList<string> Categories { get; init; } = Array.Empty<string>();

    public IReadOnlyList<long> LevelIds { get; init; } = Array.Empty<long>();

    public IReadOnlyList<long> TypeIds { get; init; } = Array.Empty<long>();

    public long? ViewId { get; init; }

    public long? RoomId { get; init; }

    public IReadOnlyList<QueryCondition> Conditions { get; init; } = Array.Empty<QueryCondition>();

    public QueryLogic Logic { get; init; } = QueryLogic.And;

    public bool IncludeTypeParameters { get; init; }

    public int Limit { get; init; } = DefaultLimit;

    public int Offset { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (Limit <= 0)
            errors.Add("limit must be greater than 0");
        if (Limit > MaxLimit)
            errors.Add($"limit must not exceed {MaxLimit}");
        if (Offset < 0)
            errors.Add("offset must not be negative");
        if (LevelIds.Count > 50)
            errors.Add("levelIds must not contain more than 50 items");
        if (TypeIds.Count > 200)
            errors.Add("typeIds must not contain more than 200 items");

        for (var i = 0; i < Conditions.Count; i++)
        {
            var condition = Conditions[i];
            if (string.IsNullOrWhiteSpace(condition.Parameter))
                errors.Add($"conditions[{i}].parameter is required");

            var needsValue = condition.Op != QueryOperator.Exists
                && condition.Op != QueryOperator.NotExists;
            if (needsValue && condition.Value is null)
                errors.Add($"conditions[{i}].value is required for operator '{condition.Op}'");

            if ((condition.Op == QueryOperator.In || condition.Op == QueryOperator.NotIn)
                && condition.Value is { } value
                && value.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"conditions[{i}].value must be an array for operator '{condition.Op}'");
            }
        }

        return errors;
    }
}