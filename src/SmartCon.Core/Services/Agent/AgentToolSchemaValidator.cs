using System.Globalization;
using System.Text.Json;

namespace SmartCon.Core.Services.Agent;

/// <summary>
/// Validates tool arguments against the subset of JSON Schema used by
/// AgentToolDefinition.InputSchemaJson (type, properties, required, enum,
/// minimum/maximum, items, minItems/maxItems, additionalProperties).
/// Lives in Core so plan validation (L0) and the eval harness share one
/// implementation. Unknown schema keywords are ignored on purpose: the subset
/// is closed and documented by the tool catalog tests.
/// </summary>
public static class AgentToolSchemaValidator
{
    private const int MaxDepth = 32;

    public static IReadOnlyList<string> Validate(string schemaJson, string argumentsJson)
    {
        JsonDocument schemaDocument;
        try
        {
            schemaDocument = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(schemaJson) ? "{}" : schemaJson);
        }
        catch (JsonException ex)
        {
            return new[] { "Tool schema is not valid JSON: " + ex.Message };
        }

        using (schemaDocument)
        {
            JsonDocument argumentsDocument;
            try
            {
                argumentsDocument = JsonDocument.Parse(
                    string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            }
            catch (JsonException ex)
            {
                return new[] { "Arguments are not valid JSON: " + ex.Message };
            }

            using (argumentsDocument)
            {
                var errors = new List<string>();
                ValidateValue(
                    schemaDocument.RootElement,
                    argumentsDocument.RootElement,
                    "$",
                    0,
                    errors);
                return errors;
            }
        }
    }

    private static void ValidateValue(
        JsonElement schema,
        JsonElement value,
        string path,
        int depth,
        List<string> errors)
    {
        if (depth > MaxDepth)
        {
            errors.Add(path + ": schema nesting is too deep");
            return;
        }

        if (schema.ValueKind != JsonValueKind.Object)
            return;

        if (schema.TryGetProperty("enum", out var allowed)
            && allowed.ValueKind == JsonValueKind.Array)
        {
            var matched = false;
            foreach (var option in allowed.EnumerateArray())
            {
                if (JsonEquals(option, value))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                errors.Add(path + ": value is not one of the allowed options");
                return;
            }
        }

        if (!schema.TryGetProperty("type", out var typeElement)
            || typeElement.ValueKind != JsonValueKind.String)
            return;

        var type = typeElement.GetString();
        switch (type)
        {
            case "object":
                ValidateObject(schema, value, path, depth, errors);
                break;
            case "array":
                ValidateArray(schema, value, path, depth, errors);
                break;
            case "string":
                if (value.ValueKind != JsonValueKind.String)
                    errors.Add(path + ": expected a string");
                break;
            case "integer":
                if (!IsInteger(value))
                    errors.Add(path + ": expected an integer");
                break;
            case "number":
                if (!IsNumber(value))
                    errors.Add(path + ": expected a number");
                break;
            case "boolean":
                if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    errors.Add(path + ": expected a boolean");
                break;
            case "null":
                if (value.ValueKind != JsonValueKind.Null)
                    errors.Add(path + ": expected null");
                break;
        }
    }

    private static void ValidateObject(
        JsonElement schema,
        JsonElement value,
        string path,
        int depth,
        List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(path + ": expected an object");
            return;
        }

        if (schema.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var property in required.EnumerateArray())
            {
                if (property.ValueKind != JsonValueKind.String)
                    continue;

                var name = property.GetString()!;
                if (!value.TryGetProperty(name, out _))
                    errors.Add(path + "." + name + ": required property is missing");
            }
        }

        var hasProperties = schema.TryGetProperty("properties", out var properties)
                            && properties.ValueKind == JsonValueKind.Object;
        var allowAdditional = !schema.TryGetProperty("additionalProperties", out var additional)
                              || additional.ValueKind != JsonValueKind.False;

        foreach (var member in value.EnumerateObject())
        {
            if (hasProperties && properties.TryGetProperty(member.Name, out var propertySchema))
            {
                ValidateValue(
                    propertySchema,
                    member.Value,
                    path + "." + member.Name,
                    depth + 1,
                    errors);
                continue;
            }

            if (!allowAdditional)
                errors.Add(path + "." + member.Name + ": unknown property");
        }
    }

    private static void ValidateArray(
        JsonElement schema,
        JsonElement value,
        string path,
        int depth,
        List<string> errors)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            errors.Add(path + ": expected an array");
            return;
        }

        var length = value.GetArrayLength();
        if (schema.TryGetProperty("minItems", out var minItems)
            && minItems.ValueKind == JsonValueKind.Number
            && minItems.TryGetInt32(out var min)
            && length < min)
        {
            errors.Add(path + ": at least " + min + " item(s) are required");
        }

        if (schema.TryGetProperty("maxItems", out var maxItems)
            && maxItems.ValueKind == JsonValueKind.Number
            && maxItems.TryGetInt32(out var max)
            && length > max)
        {
            errors.Add(path + ": at most " + max + " item(s) are allowed");
        }

        if (!schema.TryGetProperty("items", out var items))
            return;

        var index = 0;
        foreach (var item in value.EnumerateArray())
        {
            ValidateValue(items, item, path + "[" + index + "]", depth + 1, errors);
            index++;
        }
    }

    private static bool IsInteger(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Number)
            return false;

        if (value.TryGetInt64(out _))
            return true;

        return value.TryGetDouble(out var number) && System.Math.Abs(number % 1) < double.Epsilon;
    }

    private static bool IsNumber(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number;

    private static bool JsonEquals(JsonElement left, JsonElement right)
    {
        if (left.ValueKind != right.ValueKind)
        {
            if (IsNumber(left) && IsNumber(right))
                return left.GetDouble() == right.GetDouble();
            return false;
        }

        return left.ValueKind switch
        {
            JsonValueKind.String => string.Equals(
                left.GetString(), right.GetString(), StringComparison.Ordinal),
            JsonValueKind.Number => left.GetDecimal().ToString(CultureInfo.InvariantCulture) ==
                                    right.GetDecimal().ToString(CultureInfo.InvariantCulture),
            JsonValueKind.True or JsonValueKind.False => true,
            JsonValueKind.Null => true,
            _ => left.GetRawText() == right.GetRawText()
        };
    }
}
