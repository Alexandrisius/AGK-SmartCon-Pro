using Xunit;
using System.Text.Json;
using SmartCon.Core.Models.Agent;

namespace SmartCon.Tests.Agent.Models;

public sealed class QuerySpecTests
{
    [Fact]
    public void Validate_DefaultSpec_ReturnsNoErrors()
    {
        var spec = new QuerySpec();

        var errors = spec.Validate();

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_LimitAboveMaximum_ReturnsError()
    {
        var spec = new QuerySpec { Limit = QuerySpec.MaxLimit + 1 };

        var errors = spec.Validate();

        Assert.Contains(errors, error => error.Contains("limit", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NegativeOffset_ReturnsError()
    {
        var spec = new QuerySpec { Offset = -1 };

        var errors = spec.Validate();

        Assert.Contains(errors, error => error.Contains("offset", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ConditionWithoutParameter_ReturnsError()
    {
        var spec = new QuerySpec
        {
            Conditions = new[]
            {
                new QueryCondition { Parameter = " ", Op = QueryOperator.Eq, Value = JsonValue("60") }
            }
        };

        var errors = spec.Validate();

        Assert.Contains(errors, error => error.Contains("parameter", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_EqWithoutValue_ReturnsError()
    {
        var spec = new QuerySpec
        {
            Conditions = new[] { new QueryCondition { Parameter = "FireRating", Op = QueryOperator.Eq } }
        };

        var errors = spec.Validate();

        Assert.Contains(errors, error => error.Contains("value", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ExistsWithoutValue_ReturnsNoErrors()
    {
        var spec = new QuerySpec
        {
            Conditions = new[] { new QueryCondition { Parameter = "FireRating", Op = QueryOperator.Exists } }
        };

        var errors = spec.Validate();

        Assert.Empty(errors);
    }

    [Fact]
    public void Validate_InWithNonArrayValue_ReturnsError()
    {
        var spec = new QuerySpec
        {
            Conditions = new[]
            {
                new QueryCondition { Parameter = "FireRating", Op = QueryOperator.In, Value = JsonValue("60") }
            }
        };

        var errors = spec.Validate();

        Assert.Contains(errors, error => error.Contains("array", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_InWithArrayValue_ReturnsNoErrors()
    {
        using var document = JsonDocument.Parse("[\"60\",\"90\"]");
        var spec = new QuerySpec
        {
            Conditions = new[]
            {
                new QueryCondition
                {
                    Parameter = "FireRating",
                    Op = QueryOperator.In,
                    Value = document.RootElement.Clone()
                }
            }
        };

        var errors = spec.Validate();

        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("name", QueryOperator.Gt)]
    [InlineData("Family", QueryOperator.Gte)]
    [InlineData(" TYPE ", QueryOperator.Lt)]
    [InlineData("levelName", QueryOperator.Lte)]
    public void Validate_NumericOperatorOnStructuralField_ReturnsErrorWithAllowedFields(
        string parameter,
        QueryOperator op)
    {
        var spec = Condition(parameter, op, JsonValue("10"));

        var errors = spec.Validate();

        var error = Assert.Single(
            errors,
            entry => entry.Contains("structural field", StringComparison.Ordinal));
        Assert.Contains("name, family, type, category, level", error, StringComparison.Ordinal);
        Assert.Contains(
            "eq, neq, contains, startsWith, endsWith, in, notIn, exists, notExists",
            error,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("name")]
    [InlineData("category")]
    [InlineData("Level")]
    public void Validate_UnitOnStructuralField_ReturnsError(string parameter)
    {
        var spec = Condition(parameter, QueryOperator.Contains, JsonValue("x"), unit: "mm");

        var errors = spec.Validate();

        Assert.Contains(
            errors,
            entry => entry.Contains("unit is not supported on structural field", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(QueryOperator.Eq)]
    [InlineData(QueryOperator.Neq)]
    [InlineData(QueryOperator.Contains)]
    [InlineData(QueryOperator.StartsWith)]
    [InlineData(QueryOperator.EndsWith)]
    [InlineData(QueryOperator.In)]
    [InlineData(QueryOperator.NotIn)]
    [InlineData(QueryOperator.Exists)]
    [InlineData(QueryOperator.NotExists)]
    public void Validate_StringOperatorsOnStructuralField_ReturnNoErrors(QueryOperator op)
    {
        var spec = Condition("type", op, op switch
        {
            QueryOperator.In or QueryOperator.NotIn => JsonArray("[\"a\"]"),
            QueryOperator.Exists or QueryOperator.NotExists => null,
            _ => JsonValue("a")
        });

        Assert.Empty(spec.Validate());
    }

    [Fact]
    public void Validate_NumericOperatorOnRealParameter_ReturnsNoErrors()
    {
        var spec = Condition("Width", QueryOperator.Gt, JsonValue("200"), unit: "mm");

        Assert.Empty(spec.Validate());
    }

    [Theory]
    [InlineData("name", "name")]
    [InlineData(" Name ", "name")]
    [InlineData("NAME", "name")]
    [InlineData("Fa mily", "family")]
    [InlineData("levelName", "level")]
    [InlineData("Level Name", "level")]
    [InlineData("Category", "category")]
    [InlineData("Type", "type")]
    public void TryGetStructuralField_StructuralName_ReturnsCanonicalField(
        string parameter,
        string expected)
    {
        Assert.True(QuerySpec.TryGetStructuralField(parameter, out var field));
        Assert.Equal(expected, field);
    }

    [Theory]
    [InlineData("nam")]
    [InlineData("typeName")]
    [InlineData("ALL_MODEL_MARK")]
    [InlineData("Код по классификатору")]
    [InlineData(" ")]
    [InlineData("")]
    public void TryGetStructuralField_RealParameter_ReturnsFalse(string parameter)
    {
        Assert.False(QuerySpec.TryGetStructuralField(parameter, out var field));
        Assert.Equal(string.Empty, field);
    }

    [Fact]
    public void TryGetStructuralField_NullParameter_ReturnsFalse()
    {
        Assert.False(QuerySpec.TryGetStructuralField(null, out _));
    }

    [Theory]
    [InlineData("Comments", "comments")]
    [InlineData(" Type  Name ", "typename")]
    [InlineData("", "")]
    public void NormalizeParameterName_DropsWhitespaceAndCase(string parameter, string expected)
    {
        Assert.Equal(expected, QuerySpec.NormalizeParameterName(parameter));
    }

    [Fact]
    public void SuggestParameterNames_MisspelledName_ReturnsClosestFirst()
    {
        var suggestions = QuerySpec.SuggestParameterNames(
            "Coments",
            new[] { "Mark", "Type Comments", "Comments", "System Name" });

        Assert.Equal(new[] { "Comments" }, suggestions);
    }

    [Fact]
    public void SuggestParameterNames_TypedNameInAnotherCase_IsNotSuggestedBack()
    {
        var suggestions = QuerySpec.SuggestParameterNames("mark", new[] { "Mark", "Remark" });

        Assert.Equal(new[] { "Remark" }, suggestions);
    }

    [Fact]
    public void SuggestParameterNames_RespectsMaxAndOrdersByDistanceThenName()
    {
        var suggestions = QuerySpec.SuggestParameterNames(
            "nam",
            new[] { "game", "nams", "family", "category", "level", "name" },
            max: 2);

        Assert.Equal(new[] { "name", "nams" }, suggestions);
    }

    [Fact]
    public void SuggestParameterNames_DistantNames_AreNotSuggested()
    {
        var suggestions = QuerySpec.SuggestParameterNames("xyz", new[] { "Comments" });

        Assert.Empty(suggestions);
    }

    [Theory]
    [InlineData("famil", "family")]
    [InlineData("levelnam", "levelName")]
    [InlineData("categor", "category")]
    public void SuggestParameterNames_StructuralPool_OffersStructuralFieldFirst(
        string requested,
        string expected)
    {
        var suggestions = QuerySpec.SuggestParameterNames(requested, QuerySpec.StructuralFieldNames);

        Assert.NotEmpty(suggestions);
        Assert.Equal(expected, suggestions[0]);
    }

    [Fact]
    public void SuggestParameterNames_EmptyRequestOrZeroMax_ReturnsNothing()
    {
        Assert.Empty(QuerySpec.SuggestParameterNames("  ", new[] { "Comments" }));
        Assert.Empty(QuerySpec.SuggestParameterNames("Coments", new[] { "Comments" }, max: 0));
    }

    [Theory]
    [InlineData("", "", 0)]
    [InlineData("abc", "abc", 0)]
    [InlineData("", "abc", 3)]
    [InlineData("kitten", "sitting", 3)]
    public void EditDistance_KnownPairs_ReturnsLevenshteinDistance(
        string left,
        string right,
        int expected)
    {
        Assert.Equal(expected, QuerySpec.EditDistance(left, right));
    }

    private static QuerySpec Condition(
        string parameter,
        QueryOperator op,
        JsonElement? value,
        string? unit = null)
    {
        return new QuerySpec
        {
            Conditions = new[]
            {
                new QueryCondition { Parameter = parameter, Op = op, Value = value, Unit = unit }
            }
        };
    }

    private static JsonElement JsonValue(string raw)
    {
        using var document = JsonDocument.Parse("\"" + raw + "\"");
        return document.RootElement.Clone();
    }

    private static JsonElement JsonArray(string raw)
    {
        using var document = JsonDocument.Parse(raw);
        return document.RootElement.Clone();
    }
}