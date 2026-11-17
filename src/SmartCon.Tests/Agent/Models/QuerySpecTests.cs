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

    private static JsonElement JsonValue(string raw)
    {
        using var document = JsonDocument.Parse("\"" + raw + "\"");
        return document.RootElement.Clone();
    }
}