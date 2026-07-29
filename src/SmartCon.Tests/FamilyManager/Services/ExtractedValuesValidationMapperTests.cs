using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Validation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public sealed class ExtractedValuesValidationMapperTests
{
    private static ExtractedAttributeValue Row(
        string? typeId,
        string parameterName,
        AttributeValueStatus status,
        string? valueText = null,
        double? valueNumber = null) =>
        new(
            Id: Guid.NewGuid().ToString(),
            CatalogItemId: "item1",
            VersionId: "v1",
            FileId: null,
            TypeId: typeId,
            AttributeId: null,
            BindingId: null,
            ParameterName: parameterName,
            ParameterScope: AttributeScope.Type,
            StorageType: "String",
            ValueText: valueText,
            ValueRaw: null,
            ValueNumber: valueNumber,
            UnitTypeId: null,
            Status: status,
            Message: null,
            ExtractionRunId: "run1",
            ExtractedAtUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void GroupsByType_WithTypeNames()
    {
        var typeNames = new Dictionary<string, string> { ["t1"] = "DN50", ["t2"] = "DN65" };
        var values = new[]
        {
            Row("t1", "DN", AttributeValueStatus.Found, "50", 50.0),
            Row("t2", "DN", AttributeValueStatus.Found, "65", 65.0),
        };

        var input = ExtractedValuesValidationMapper.ToValidationInput(values, typeNames);

        Assert.Equal(2, input.Types.Count);
        Assert.Contains(input.Types, t => t.TypeName == "DN50");
        Assert.Contains(input.Types, t => t.TypeName == "DN65");
    }

    [Theory]
    [InlineData(AttributeValueStatus.Found, true, true)]
    [InlineData(AttributeValueStatus.EmptyValue, true, false)]
    [InlineData(AttributeValueStatus.ReadError, true, false)]
    [InlineData(AttributeValueStatus.UnsupportedStorageType, true, false)]
    [InlineData(AttributeValueStatus.MissingParameter, false, false)]
    [InlineData(AttributeValueStatus.NotInFamily, false, false)]
    public void StatusMapping(AttributeValueStatus status, bool expectedPresent, bool expectedHasValue)
    {
        var values = new[] { Row("t1", "P", status, "text", 1.0) };

        var input = ExtractedValuesValidationMapper.ToValidationInput(
            values, new Dictionary<string, string> { ["t1"] = "T" });

        var value = Assert.Single(input.Types[0].Values);
        Assert.Equal(expectedPresent, value.IsPresent);
        Assert.Equal(expectedHasValue, value.ValueText is not null || value.ValueNumber is not null);
    }

    [Fact]
    public void NullTypeId_WithTypedRowsPresent_UntypedRowsSkipped()
    {
        var typeNames = new Dictionary<string, string> { ["t1"] = "DN50" };
        var values = new[]
        {
            Row("t1", "DN", AttributeValueStatus.Found, "50", 50.0),
            Row(null, "InstanceParam", AttributeValueStatus.Found, "leftover"),
        };

        var input = ExtractedValuesValidationMapper.ToValidationInput(values, typeNames);

        var type = Assert.Single(input.Types);
        Assert.Equal("DN50", type.TypeName);
        Assert.DoesNotContain(type.Values, v => v.ParameterName == "InstanceParam");
    }

    [Fact]
    public void NullTypeId_OnlyUntypedRows_FallbackToDefaultType()
    {
        var values = new[] { Row(null, "P", AttributeValueStatus.Found, "x") };

        var input = ExtractedValuesValidationMapper.ToValidationInput(values, new Dictionary<string, string>());

        var type = Assert.Single(input.Types);
        Assert.Equal(FamilyTypeSnapshot.DefaultTypeName, type.TypeName);
        Assert.Single(type.Values);
    }

    [Fact]
    public void FoundStatus_CarriesValues()
    {
        var values = new[] { Row("t1", "DN", AttributeValueStatus.Found, "50", 50.0) };

        var input = ExtractedValuesValidationMapper.ToValidationInput(
            values, new Dictionary<string, string> { ["t1"] = "T" });

        var value = Assert.Single(input.Types[0].Values);
        Assert.Equal("50", value.ValueText);
        Assert.Equal(50.0, value.ValueNumber);
    }
}
