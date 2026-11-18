using System.Text.Json;
using SmartCon.Core.Services.Agent;
using Xunit;

namespace SmartCon.Tests.Agent.Services;

public sealed class AgentToolInputRepairTests
{
    private const string ScalarSchema =
        """{"type":"object","properties":{"viewId":{"type":"integer"},"ratio":{"type":"number"},"enabled":{"type":"boolean"},"name":{"type":"string"}}}""";

    [Fact]
    public void StringToInteger_IsCoercedAndValidates()
    {
        var result = Repair(ScalarSchema, """{"viewId":"5"}""");

        Assert.True(result.Changed);
        Assert.Equal(5, result.Arguments.GetProperty("viewId").GetInt32());
        Assert.Contains(result.Repairs, repair => repair.Contains("integer"));
        Assert.Empty(Validate(ScalarSchema, result.Arguments));
    }

    [Fact]
    public void IntegerStringWithDecimalPart_IsCoerced()
    {
        var result = Repair(ScalarSchema, """{"viewId":"5.0"}""");

        Assert.True(result.Changed);
        Assert.Equal(5, result.Arguments.GetProperty("viewId").GetInt32());
        Assert.Empty(Validate(ScalarSchema, result.Arguments));
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void BooleanStrings_AreCoerced(string raw, bool expected)
    {
        var result = Repair(ScalarSchema, """{"enabled":"__VALUE__"}""".Replace("__VALUE__", raw));

        Assert.True(result.Changed);
        Assert.Equal(expected, result.Arguments.GetProperty("enabled").GetBoolean());
        Assert.Empty(Validate(ScalarSchema, result.Arguments));
    }

    [Fact]
    public void NumberIsCoercedToString()
    {
        var result = Repair(ScalarSchema, """{"name":42}""");

        Assert.True(result.Changed);
        Assert.Equal("42", result.Arguments.GetProperty("name").GetString());
        Assert.Empty(Validate(ScalarSchema, result.Arguments));
    }

    [Fact]
    public void BooleanIsCoercedToString()
    {
        var result = Repair(ScalarSchema, """{"name":true}""");

        Assert.True(result.Changed);
        Assert.Equal("true", result.Arguments.GetProperty("name").GetString());
        Assert.Empty(Validate(ScalarSchema, result.Arguments));
    }

    [Fact]
    public void NumericStringIsCoercedToNumber()
    {
        var result = Repair(ScalarSchema, """{"ratio":"2.5"}""");

        Assert.True(result.Changed);
        Assert.Equal(2.5, result.Arguments.GetProperty("ratio").GetDouble());
        Assert.Empty(Validate(ScalarSchema, result.Arguments));
    }

    [Fact]
    public void ScalarIsWrappedIntoStringArray()
    {
        const string schema =
            """{"type":"object","properties":{"items":{"type":"array","items":{"type":"string"}}}}""";

        var result = Repair(schema, """{"items":"view"}""");

        Assert.True(result.Changed);
        var items = result.Arguments.GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal("view", items[0].GetString());
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void ObjectIsWrappedIntoObjectArray()
    {
        const string schema =
            """{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"viewId":{"type":"integer"}}}}}}""";

        var result = Repair(schema, """{"items":{"viewId":1}}""");

        Assert.True(result.Changed);
        var items = result.Arguments.GetProperty("items");
        Assert.Equal(JsonValueKind.Array, items.ValueKind);
        Assert.Equal(1, items[0].GetProperty("viewId").GetInt32());
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void CommaSeparatedStringIsSplitIntoArray()
    {
        const string schema =
            """{"type":"object","properties":{"targets":{"type":"array","items":{"type":"string"}}}}""";

        var result = Repair(schema, """{"targets":"plan, section"}""");

        Assert.True(result.Changed);
        var targets = result.Arguments.GetProperty("targets");
        Assert.Equal(2, targets.GetArrayLength());
        Assert.Equal("plan", targets[0].GetString());
        Assert.Equal("section", targets[1].GetString());
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void SnakeCasePropertyIsRenamed()
    {
        const string schema =
            """{"type":"object","properties":{"viewIds":{"type":"array","items":{"type":"integer"}}}}""";

        var result = Repair(schema, """{"view_ids":[1,2]}""");

        Assert.True(result.Changed);
        Assert.False(result.Arguments.TryGetProperty("view_ids", out _));
        Assert.Equal(2, result.Arguments.GetProperty("viewIds").GetArrayLength());
        Assert.Contains(result.Repairs, repair => repair.Contains("renamed 'view_ids' -> 'viewIds'"));
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void KebabCasePropertyIsRenamed()
    {
        const string schema =
            """{"type":"object","properties":{"viewIds":{"type":"array","items":{"type":"integer"}}}}""";

        var result = Repair(schema, """{"view-ids":[3]}""");

        Assert.True(result.Changed);
        Assert.Equal(3, result.Arguments.GetProperty("viewIds")[0].GetInt32());
        Assert.Contains(result.Repairs, repair => repair.Contains("renamed 'view-ids' -> 'viewIds'"));
    }

    [Fact]
    public void CaseInsensitivePropertyIsRenamed()
    {
        const string schema =
            """{"type":"object","properties":{"viewId":{"type":"integer"}}}""";

        var result = Repair(schema, """{"ViewId":7}""");

        Assert.True(result.Changed);
        Assert.Equal(7, result.Arguments.GetProperty("viewId").GetInt32());
        Assert.Contains(result.Repairs, repair => repair.Contains("renamed 'ViewId' -> 'viewId'"));
    }

    [Fact]
    public void UnknownPropertyIsDroppedWhenAdditionalPropertiesIsFalse()
    {
        const string schema =
            """{"type":"object","properties":{"viewId":{"type":"integer"}},"additionalProperties":false}""";

        var result = Repair(schema, """{"viewId":1,"bogus":2}""");

        Assert.True(result.Changed);
        Assert.False(result.Arguments.TryGetProperty("bogus", out _));
        Assert.Contains(result.Repairs, repair => repair.Contains("dropped unknown property 'bogus'"));
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void UnknownPropertyIsKeptWhenAdditionalPropertiesAllowsIt()
    {
        const string schema =
            """{"type":"object","properties":{"viewId":{"type":"integer"}}}""";

        var result = Repair(schema, """{"viewId":1,"extra":"kept"}""");

        Assert.False(result.Changed);
        Assert.Empty(result.Repairs);
        Assert.Equal("kept", result.Arguments.GetProperty("extra").GetString());
    }

    [Fact]
    public void MissingRequiredPropertyIsNotInvented()
    {
        const string schema =
            """{"type":"object","properties":{"goal":{"type":"string"}},"required":["goal"]}""";

        var result = Repair(schema, "{}");

        Assert.False(result.Changed);
        Assert.Empty(result.Repairs);
        Assert.Contains(Validate(schema, result.Arguments), error => error.Contains("goal"));
    }

    [Fact]
    public void EnumValueIsNotBroken()
    {
        const string schema =
            """{"type":"object","properties":{"kind":{"type":"string","enum":["A","B"]}}}""";

        var result = Repair(schema, """{"kind":"A"}""");

        Assert.False(result.Changed);
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void CoercionStillMatchesNumericEnum()
    {
        const string schema =
            """{"type":"object","properties":{"kind":{"type":"integer","enum":[1,2]}}}""";

        var result = Repair(schema, """{"kind":"1"}""");

        Assert.True(result.Changed);
        Assert.Equal(1, result.Arguments.GetProperty("kind").GetInt32());
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void NestedObjectPropertyIsCoerced()
    {
        const string schema =
            """{"type":"object","properties":{"filter":{"type":"object","properties":{"limit":{"type":"integer"}},"additionalProperties":false}}}""";

        var result = Repair(schema, """{"filter":{"limit":"10"}}""");

        Assert.True(result.Changed);
        Assert.Equal(10, result.Arguments.GetProperty("filter").GetProperty("limit").GetInt32());
        Assert.Contains(result.Repairs, repair => repair.Contains("filter.limit"));
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void ArrayItemsAreCoerced()
    {
        const string schema =
            """{"type":"object","properties":{"viewIds":{"type":"array","items":{"type":"integer"}}}}""";

        var result = Repair(schema, """{"viewIds":["1","2"]}""");

        Assert.True(result.Changed);
        var viewIds = result.Arguments.GetProperty("viewIds");
        Assert.Equal(1, viewIds[0].GetInt32());
        Assert.Equal(2, viewIds[1].GetInt32());
        Assert.Contains(result.Repairs, repair => repair.Contains("viewIds[0]"));
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void EmptyObjectArgumentsAreUnchanged()
    {
        const string schema = """{"type":"object","properties":{}}""";

        var result = Repair(schema, "{}");

        Assert.False(result.Changed);
        Assert.Empty(result.Repairs);
    }

    [Theory]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    [InlineData("5")]
    [InlineData("null")]
    public void NonObjectArgumentsAreReturnedAsIs(string json)
    {
        var result = Repair(ScalarSchema, json);

        Assert.False(result.Changed);
        Assert.Empty(result.Repairs);
        Assert.Equal(json, result.Arguments.GetRawText());
    }

    [Fact]
    public void SchemaWithoutTypeIsUnchanged()
    {
        const string schema = """{"properties":{"viewId":{"type":"integer"}}}""";

        var result = Repair(schema, """{"viewId":"5"}""");

        Assert.False(result.Changed);
        Assert.Equal("5", result.Arguments.GetProperty("viewId").GetString());
    }

    [Fact]
    public void DeeplyNestedPropertiesAreRepaired()
    {
        const string schema =
            """{"type":"object","properties":{"items":{"type":"array","items":{"type":"object","properties":{"filter":{"type":"object","properties":{"limit":{"type":"integer"}}}}}}}}""";

        var result = Repair(schema, """{"items":[{"filter":{"limit":"3"}}]}""");

        Assert.True(result.Changed);
        Assert.Equal(
            3,
            result.Arguments.GetProperty("items")[0]
                .GetProperty("filter").GetProperty("limit").GetInt32());
        Assert.Contains(result.Repairs, repair => repair.Contains("items[0].filter.limit"));
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void CombinedRepairs_ValidateSuccessfully()
    {
        const string schema =
            """{"type":"object","properties":{"viewIds":{"type":"array","items":{"type":"integer"}},"enabled":{"type":"boolean"},"name":{"type":"string"}},"required":["viewIds"],"additionalProperties":false}""";

        var result = Repair(
            schema,
            """{"view_ids":["1","2"],"enabled":"1","name":5,"bogus":true}""");

        Assert.True(result.Changed);
        Assert.Equal(2, result.Arguments.GetProperty("viewIds").GetArrayLength());
        Assert.True(result.Arguments.GetProperty("enabled").GetBoolean());
        Assert.Equal("5", result.Arguments.GetProperty("name").GetString());
        Assert.False(result.Arguments.TryGetProperty("bogus", out _));
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void UnrepairableValue_KeepsValidationError()
    {
        const string schema =
            """{"type":"object","properties":{"viewId":{"type":"integer"}}}""";

        var result = Repair(schema, """{"viewId":"abc"}""");

        Assert.False(result.Changed);
        Assert.Contains(
            Validate(schema, result.Arguments),
            error => error.Contains("expected an integer"));
    }

    [Fact]
    public void RepairWithRemainingRequiredError_ReturnsRepairsAndErrors()
    {
        const string schema =
            """{"type":"object","properties":{"viewIds":{"type":"array","items":{"type":"integer"}},"goal":{"type":"string"}},"required":["viewIds","goal"]}""";

        var result = Repair(schema, """{"view_ids":[1]}""");

        Assert.True(result.Changed);
        Assert.Contains(result.Repairs, repair => repair.Contains("renamed 'view_ids' -> 'viewIds'"));
        var errors = Validate(schema, result.Arguments);
        Assert.Contains(errors, error => error.Contains("goal"));
        Assert.DoesNotContain(errors, error => error.Contains("viewIds"));
    }

    [Fact]
    public void SingleElementObjectArrayIsUnwrapped()
    {
        const string schema =
            """{"type":"object","properties":{"filter":{"type":"object","properties":{"limit":{"type":"integer"}}}}}""";

        var result = Repair(schema, """{"filter":[{"limit":"3"}]}""");

        Assert.True(result.Changed);
        Assert.Equal(3, result.Arguments.GetProperty("filter").GetProperty("limit").GetInt32());
        Assert.Contains(result.Repairs, repair => repair.Contains("unwrapped"));
        Assert.Empty(Validate(schema, result.Arguments));
    }

    [Fact]
    public void InvalidSchemaJson_ReturnsArgumentsUnchanged()
    {
        var result = Repair("{not json", """{"viewId":"5"}""");

        Assert.False(result.Changed);
        Assert.Empty(result.Repairs);
        Assert.Equal("5", result.Arguments.GetProperty("viewId").GetString());
    }

    private static AgentToolRepairResult Repair(string schemaJson, string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        return AgentToolInputRepair.Repair(schemaJson, document.RootElement);
    }

    private static IReadOnlyList<string> Validate(string schemaJson, JsonElement arguments) =>
        AgentToolSchemaValidator.Validate(schemaJson, arguments.GetRawText());
}
