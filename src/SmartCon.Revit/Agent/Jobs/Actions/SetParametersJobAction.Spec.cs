using System.Text.Json;
using System.Text.Json.Serialization;
using Autodesk.Revit.DB;
using SmartCon.Core.Models.Agent;
using SmartCon.Core.Models.Agent.Jobs;

namespace SmartCon.Revit.Agent.Jobs.Actions;

/// <summary>Typed spec, strict keys and validation of set_parameters.</summary>
public sealed partial class SetParametersJobAction
{
    private static readonly JobSpecObjectRule[] SpecRules =
    {
        new()
        {
            Path = string.Empty,
            AllowedKeys = new[]
            {
                "select", "values", "parameter", "guid", "builtInParameter", "scope", "value", "unit",
                "allowUnverifiedClassification"
            }
        },
        new()
        {
            Path = "select",
            AllowedKeys = new[]
            {
                "elementIds", "categoryIds", "categoryNames", "categories", "levelIds", "typeIds",
                "familyName", "familyNames", "typeName", "nameContains", "conditions", "logic",
                "includeTypeParameters", "limit", "systemName", "systemDomain",
                "includeSubComponents"
            }
        },
        new()
        {
            Path = "select.conditions[]",
            AllowedKeys = new[] { "parameter", "op", "value", "unit" }
        },
        new()
        {
            Path = "values[]",
            AllowedKeys = new[] { "select", "value" }
        },
        new()
        {
            Path = "values[].select",
            AllowedKeys = new[]
            {
                "elementIds", "categoryIds", "categoryNames", "categories", "levelIds", "typeIds",
                "familyName", "familyNames", "typeName", "nameContains", "conditions", "logic",
                "includeTypeParameters", "limit", "systemName", "systemDomain",
                "includeSubComponents"
            }
        },
        new()
        {
            Path = "values[].select.conditions[]",
            AllowedKeys = new[] { "parameter", "op", "value", "unit" }
        }
    };

    private static bool IsKnownSystemDomain(string? domain) =>
        string.Equals(domain, "piping", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(domain, "mechanical", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(domain, "electrical", StringComparison.OrdinalIgnoreCase);

    private static string? ValidateSpec(SetParametersSpec? spec)
    {
        if (spec is null)
            return "set_parameters requires {\"select\": {...}, \"value\": \"...\"}";

        // values[] form: several {select, value} pairs in one job. Mutually
        // exclusive with the classic select/value pair (S.47, #532).
        if (spec.Values is { Count: > 0 })
        {
            if (spec.Select is not null || spec.Value is not null)
                return "pass either {select, value} or values[] - not both";
            if (spec.Values.Count > MaxValueEntries)
                return "values[] holds at most " + MaxValueEntries + " entries; split into several jobs";
            for (var index = 0; index < spec.Values.Count; index++)
            {
                if (spec.Values[index].Select is null)
                    return "values[" + index + "].select is required";
                if (spec.Values[index].Value is null)
                    return "values[" + index + "].value is required (pass \"\" to clear a text parameter)";
                var entryError = ValidateSelect(spec.Values[index].Select!, "values[" + index + "].select");
                if (entryError is not null)
                    return entryError;
            }

            return ValidateShared(spec);
        }

        if (spec.Select is null)
            return "set_parameters requires {\"select\": {...}, \"value\": \"...\"}";
        var selectError = ValidateSelect(spec.Select, "select");
        if (selectError is not null)
            return selectError;
        if (spec.Value is null)
            return "set_parameters requires a string 'value'; pass \"\" to clear a text parameter";
        return ValidateShared(spec);
    }

    private static string? ValidateSelect(ParametersSelectSpec select, string path)
    {
        if (select.ElementIds is not { Count: > 0 } && select.CategoryIds is not { Count: > 0 } &&
            select.CategoryNames is not { Count: > 0 } && select.Categories is not { Count: > 0 } &&
            select.LevelIds is not { Count: > 0 } &&
            select.TypeIds is not { Count: > 0 } && select.Conditions is not { Count: > 0 } &&
            select.FamilyNames is not { Count: > 0 } &&
            string.IsNullOrWhiteSpace(select.FamilyName) && string.IsNullOrWhiteSpace(select.TypeName) &&
            string.IsNullOrWhiteSpace(select.NameContains) && string.IsNullOrWhiteSpace(select.SystemName))
        {
            return path + " must contain at least one of elementIds, categoryIds, categoryNames, levelIds, " +
                   "typeIds, conditions, familyName, familyNames, typeName, nameContains or systemName; " +
                   "an empty selector is refused";
        }
        if (select.Limit is < 1)
            return path + ".limit must be a positive number";
        if (!string.IsNullOrWhiteSpace(select.SystemDomain))
        {
            if (!IsKnownSystemDomain(select.SystemDomain))
                return path + ".systemDomain must be \"piping\", \"mechanical\" or \"electrical\", not '" +
                       select.SystemDomain + "'";
            if (string.IsNullOrWhiteSpace(select.SystemName))
                return path + ".systemDomain requires systemName";
        }

        for (var index = 0; index < (select.Conditions?.Count ?? 0); index++)
        {
            var condition = select.Conditions![index];
            if (string.IsNullOrWhiteSpace(condition.Parameter))
                return path + ".conditions[" + index + "].parameter is required";

            var needsValue = condition.Op != QueryOperator.Exists && condition.Op != QueryOperator.NotExists;
            if (needsValue && condition.Value is null)
                return path + ".conditions[" + index + "].value is required for operator '" + condition.Op + "'";

            if ((condition.Op == QueryOperator.In || condition.Op == QueryOperator.NotIn) &&
                condition.Value is { } value && value.ValueKind != System.Text.Json.JsonValueKind.Array)
            {
                return path + ".conditions[" + index + "].value must be an array for operator '" + condition.Op + "'";
            }
        }

        return null;
    }

    private static string? ValidateShared(SetParametersSpec spec)
    {
        if (string.IsNullOrWhiteSpace(spec.Parameter) && string.IsNullOrWhiteSpace(spec.SharedParameterGuid) &&
            string.IsNullOrWhiteSpace(spec.BuiltInParameter))
            return "set_parameters requires 'parameter', 'guid' or 'builtInParameter'";
        if (!string.IsNullOrWhiteSpace(spec.SharedParameterGuid) && !Guid.TryParse(spec.SharedParameterGuid, out _))
            return "'" + spec.SharedParameterGuid + "' is not a valid shared-parameter GUID";
        if (!string.IsNullOrWhiteSpace(spec.Scope) &&
            !string.Equals(spec.Scope, "instance", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(spec.Scope, "type", StringComparison.OrdinalIgnoreCase))
            return "scope must be \"instance\" or \"type\", not '" + spec.Scope + "'";
        return null;
    }

    /// <summary>
    /// The flag must be a JSON boolean: a string or a number would only fail
    /// later, inside the deserializer, with a message about types instead of
    /// the spec.
    /// </summary>
    private static string? ValidateFlagShape(string? specJson)
    {
        if (string.IsNullOrWhiteSpace(specJson))
            return null;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(specJson!);
        }
        catch (JsonException)
        {
            // Malformed JSON is reported by the deserializer.
            return null;
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!string.Equals(
                        property.Name, "allowUnverifiedClassification", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return null;

                return "allowUnverifiedClassification must be true or false, not " +
                       property.Value.ValueKind.ToString().ToLowerInvariant();
            }
        }

        return null;
    }

    private static string ParameterKey(SetParametersSpec spec)
    {
        if (!string.IsNullOrWhiteSpace(spec.SharedParameterGuid) && Guid.TryParse(spec.SharedParameterGuid, out var guid))
            return guid.ToString("D");
        if (!string.IsNullOrWhiteSpace(spec.BuiltInParameter))
            return spec.BuiltInParameter!.Trim();
        return (spec.Parameter ?? string.Empty).Trim();
    }

    private static string NameOf(Parameter parameter, SetParameterUnitArgs args) =>
        parameter.Definition?.Name ?? args.Parameter ?? args.BuiltInParameter ?? args.SharedParameterGuid ?? "?";
    private static string TargetText(string? value, string? unit) =>
        string.IsNullOrWhiteSpace(unit) ? value ?? string.Empty : (value ?? string.Empty) + " " + unit;
    private static string Hint(string action) => "[Action: " + action + "]";

    private static JobUnitApply ApplyFail(string code, string detail) =>
        new() { ErrorCode = code, ErrorDetail = detail };
    private static JobUnitVerification VerifyFail(string code, string detail, string reason) =>
        new() { Verified = false, ErrorCode = code, ErrorDetail = detail, MismatchReason = reason };
    private static JobUnitUndo UndoFail(string code, string detail) =>
        new() { ErrorCode = code, ErrorDetail = detail };

    public sealed class SetParametersSpec
    {
        public ParametersSelectSpec? Select { get; set; }

        /// <summary>
        /// Alternative form: several {select, value} pairs in one job - one
        /// preview/apply for N groups instead of N sequential jobs (S.47, #532).
        /// The parameter/guid/scope/unit are shared by every entry.
        /// </summary>
        public List<SetValueEntry>? Values { get; set; }

        public string? Parameter { get; set; }
        [JsonPropertyName("guid")]
        public string? SharedParameterGuid { get; set; }

        public string? BuiltInParameter { get; set; }
        public string? Scope { get; set; }
        public string? Value { get; set; }
        public string? Unit { get; set; }

        /// <summary>
        /// Writes an Assembly Code that no row of the loaded classification table
        /// backs (and any code while no table is loaded). Off by default.
        /// </summary>
        public bool? AllowUnverifiedClassification { get; set; }
    }

    /// <summary>At most this many {select, value} pairs per job.</summary>
    public const int MaxValueEntries = 50;

    public sealed class SetValueEntry
    {
        public ParametersSelectSpec? Select { get; set; }

        public string? Value { get; set; }
    }

    public sealed class ParametersSelectSpec
    {
        public List<long>? ElementIds { get; set; }
        public List<long>? CategoryIds { get; set; }
        public List<string>? CategoryNames { get; set; }

        /// <summary>Alias of <see cref="CategoryNames"/>: plural form models write by analogy.</summary>
        [JsonPropertyName("categories")]
        public List<string>? Categories { get; set; }

        public List<long>? LevelIds { get; set; }
        public List<long>? TypeIds { get; set; }
        public string? FamilyName { get; set; }

        /// <summary>Any-of family names: one job over several families (S.46, #531).</summary>
        public List<string>? FamilyNames { get; set; }

        public string? TypeName { get; set; }
        public string? NameContains { get; set; }
        public List<QueryCondition>? Conditions { get; set; }
        public QueryLogic? Logic { get; set; }
        public bool? IncludeTypeParameters { get; set; }
        public int? Limit { get; set; }

        /// <summary>Exact MEP system name: the selection becomes the system members.</summary>
        public string? SystemName { get; set; }

        /// <summary>Optional domain filter for <see cref="SystemName"/>: piping|mechanical|electrical.</summary>
        public string? SystemDomain { get; set; }

        /// <summary>Recursively add nested sub-components (shared nested families), depth 3.</summary>
        public bool? IncludeSubComponents { get; set; }
    }

    public sealed class SetParameterUnitArgs
    {
        public long ElementId { get; set; }
        public string? UniqueId { get; set; }
        public string? OwnerUniqueId { get; set; }
        public string? Parameter { get; set; }
        [JsonPropertyName("guid")]
        public string? SharedParameterGuid { get; set; }

        public string? BuiltInParameter { get; set; }
        public string? Scope { get; set; }
        public string? Value { get; set; }
        public string? Unit { get; set; }
        public string? ErrorCode { get; set; }
        public string? Error { get; set; }

        // Omitted unless true: the flag would otherwise be stored with every
        // one of up to 5000 units.
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool? AllowUnverifiedClassification { get; set; }
    }
}
