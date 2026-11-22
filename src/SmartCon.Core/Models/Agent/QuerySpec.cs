using System.Text;
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

    public const string NameField = "name";

    public const string FamilyField = "family";

    public const string TypeField = "type";

    public const string CategoryField = "category";

    public const string LevelField = "level";

    /// <summary>Accepted spelling of <see cref="LevelField"/>: the element-card field name.</summary>
    public const string LevelNameField = "levelName";

    /// <summary>
    /// Structural fields a condition's <see cref="QueryCondition.Parameter"/> may
    /// name instead of a Revit parameter. Reserved words of the query language:
    /// they are recognised before any parameter lookup, are text-only and match
    /// case- and whitespace-insensitively, so a project parameter of the same
    /// name is unreachable in conditions.
    /// </summary>
    public static readonly IReadOnlyList<string> StructuralFields = new[]
    {
        NameField, FamilyField, TypeField, CategoryField, LevelField
    };

    private static readonly IReadOnlyList<QueryOperator> StructuralFieldOperators = new[]
    {
        QueryOperator.Eq, QueryOperator.Neq, QueryOperator.Contains, QueryOperator.StartsWith,
        QueryOperator.EndsWith, QueryOperator.In, QueryOperator.NotIn,
        QueryOperator.Exists, QueryOperator.NotExists
    };

    // Normalized spelling (see NormalizeParameterName) to the canonical field.
    private static readonly Dictionary<string, string> StructuralFieldByName = new(StringComparer.Ordinal)
    {
        [NameField] = NameField,
        [FamilyField] = FamilyField,
        [TypeField] = TypeField,
        [CategoryField] = CategoryField,
        [LevelField] = LevelField,
        ["levelname"] = LevelField
    };

    /// <summary>Every spelling the structural fields accept, for name suggestions.</summary>
    internal static readonly IReadOnlyList<string> StructuralFieldNames = new[]
    {
        NameField, FamilyField, TypeField, CategoryField, LevelField, LevelNameField
    };

    private static readonly string StructuralFieldsText = string.Join(", ", StructuralFields);

    private static readonly string StructuralFieldOperatorsText =
        string.Join(", ", StructuralFieldOperators.Select(op => OperatorName(op)));

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

            if (TryGetStructuralField(condition.Parameter, out var field))
            {
                if (!StructuralFieldOperators.Contains(condition.Op))
                {
                    errors.Add(
                        $"conditions[{i}].op '{OperatorName(condition.Op)}' is not supported on " +
                        $"structural field '{field}': structural fields ({StructuralFieldsText}) are " +
                        $"text, allowed ops are {StructuralFieldOperatorsText}");
                }

                if (!string.IsNullOrWhiteSpace(condition.Unit))
                {
                    errors.Add(
                        $"conditions[{i}].unit is not supported on structural field '{field}': " +
                        $"structural fields ({StructuralFieldsText}) are text");
                }
            }
        }

        return errors;
    }

    /// <summary>
    /// Canonical name of the structural field a condition parameter names, or
    /// false when the parameter names a Revit parameter.
    /// </summary>
    public static bool TryGetStructuralField(string? parameter, out string field)
    {
        field = string.Empty;
        var key = NormalizeParameterName(parameter);
        if (key.Length == 0 || !StructuralFieldByName.TryGetValue(key, out var canonical))
            return false;

        field = canonical;
        return true;
    }

    /// <summary>The operator as the query JSON spells it (contains, notIn).</summary>
    private static string OperatorName(QueryOperator op)
    {
        var text = op.ToString();
        return char.ToLowerInvariant(text[0]) + text.Substring(1);
    }

    /// <summary>Lower-cased copy of a name without whitespace.</summary>
    public static string NormalizeParameterName(string? parameter)
    {
        if (string.IsNullOrEmpty(parameter))
            return string.Empty;

        // IsNullOrEmpty does not narrow the nullable on net48.
        var builder = new StringBuilder(parameter!.Length);
        foreach (var character in parameter)
        {
            if (!char.IsWhiteSpace(character))
                builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    /// <summary>
    /// Closest candidates to a parameter name that resolved on no element:
    /// the names within half the typed length by edit distance, nearest first.
    /// The typed name itself is never offered back.
    /// </summary>
    public static IReadOnlyList<string> SuggestParameterNames(
        string requested,
        IEnumerable<string> candidates,
        int max = 3)
    {
        var target = NormalizeParameterName(requested);
        if (target.Length == 0 || max <= 0)
            return Array.Empty<string>();

        var typed = requested.Trim();
        var limit = System.Math.Max(2, (target.Length + 1) / 2);
        return candidates
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!.Trim())
            // Never suggest the string the caller already passed: "Did you mean
            // 'Классификация систем'?" for the same request is noise (A1).
            .Where(name => !string.Equals(name, typed, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => (
                Name: name,
                Distance: EditDistance(target, NormalizeParameterName(name))))
            .Where(pair => pair.Distance <= limit)
            .OrderBy(pair => pair.Distance)
            .ThenBy(pair => pair.Name, StringComparer.Ordinal)
            .Take(max)
            .Select(pair => pair.Name)
            .ToList();
    }

    internal static int EditDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];
        for (var index = 0; index <= right.Length; index++)
            previous[index] = index;

        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
            {
                var cost = left[row - 1] == right[column - 1] ? 0 : 1;
                current[column] = System.Math.Min(
                    System.Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + cost);
            }

            var swap = previous;
            previous = current;
            current = swap;
        }

        return previous[right.Length];
    }
}