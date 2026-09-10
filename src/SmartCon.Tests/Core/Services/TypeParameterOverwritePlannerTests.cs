using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.Core.Services;

/// <summary>
/// Issue #239: <see cref="TypeParameterOverwritePlanner"/> — mapping of
/// catalog <see cref="ExtractedAttributeValue"/> rows to overwrite operations
/// (status filter, storage-type mapping, ElementId-by-name, type join).
/// </summary>
public sealed class TypeParameterOverwritePlannerTests
{
    private static readonly IReadOnlyDictionary<string, string> TypeMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type-1"] = "T1",
            ["type-2"] = "T2",
        };

    private static ExtractedAttributeValue Value(
        string parameterName,
        string? storageType,
        string? typeId = "type-1",
        double? number = null,
        string? text = null,
        AttributeScope? scope = AttributeScope.Type,
        AttributeValueStatus status = AttributeValueStatus.Found)
    {
        return new ExtractedAttributeValue(
            "id", "item", "ver", "file", typeId, null, null,
            parameterName, scope, storageType, text, null, number, null,
            status, null, "run", DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Plan_DoubleValue_MapsToSetDouble()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Score", "Double", number: 100.5)], TypeMap);

        var op = Assert.Single(ops);
        Assert.Equal("T1", op.TypeName);
        Assert.Equal("Score", op.ParameterName);
        Assert.Equal(TypeParameterOverwriteKind.SetDouble, op.Kind);
        Assert.Equal(100.5, op.ValueNumber);
        Assert.Null(op.ValueText);
    }

    [Fact]
    public void Plan_IntegerValue_MapsToSetInteger()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Flag", "Integer", number: 1)], TypeMap);

        var op = Assert.Single(ops);
        Assert.Equal(TypeParameterOverwriteKind.SetInteger, op.Kind);
        Assert.Equal(1, op.ValueNumber);
    }

    [Fact]
    public void Plan_StringValue_MapsToSetString()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Label", "String", text: "abc")], TypeMap);

        var op = Assert.Single(ops);
        Assert.Equal(TypeParameterOverwriteKind.SetString, op.Kind);
        Assert.Equal("abc", op.ValueText);
    }

    [Fact]
    public void Plan_StringNullText_MapsToEmptyString()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Label", "String", text: null)], TypeMap);

        var op = Assert.Single(ops);
        Assert.Equal(string.Empty, op.ValueText);
    }

    [Fact]
    public void Plan_ElementIdWithName_MapsToResolveElementByName()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Material", "ElementId", text: "Steel")], TypeMap);

        var op = Assert.Single(ops);
        Assert.Equal(TypeParameterOverwriteKind.ResolveElementByName, op.Kind);
        Assert.Equal("Steel", op.ValueText);
    }

    [Theory]
    [InlineData("INVALID")]
    [InlineData("READERROR")]
    [InlineData(null)]
    public void Plan_ElementIdUnreadableReference_Skipped(string? text)
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Material", "ElementId", text: text)], TypeMap);

        Assert.Empty(ops);
    }

    [Fact]
    public void Plan_NonFoundStatus_Skipped()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Score", "Double", number: 1, status: AttributeValueStatus.EmptyValue)], TypeMap);

        Assert.Empty(ops);
    }

    [Fact]
    public void Plan_UntypedRow_Skipped()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Score", "Double", typeId: null, number: 1)], TypeMap);

        Assert.Empty(ops);
    }

    [Fact]
    public void Plan_UnknownTypeId_Skipped()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Score", "Double", typeId: "type-999", number: 1)], TypeMap);

        Assert.Empty(ops);
    }

    [Fact]
    public void Plan_UnknownStorageType_Skipped()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Geo", "XYZ", number: 1)], TypeMap);

        Assert.Empty(ops);
    }

    [Fact]
    public void Plan_NumericWithoutNumber_Skipped()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Score", "Double", number: null)], TypeMap);

        Assert.Empty(ops);
    }

    [Fact]
    public void Plan_InstanceScope_Included()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Mark", "String", text: "m1", scope: AttributeScope.Instance)], TypeMap);

        var op = Assert.Single(ops);
        Assert.Equal("Mark", op.ParameterName);
    }

    [Fact]
    public void Plan_JoinsTypeNameFromMap()
    {
        var ops = TypeParameterOverwritePlanner.Plan(
            [Value("Score", "Double", typeId: "type-2", number: 2)], TypeMap);

        var op = Assert.Single(ops);
        Assert.Equal("T2", op.TypeName);
    }

    [Fact]
    public void Plan_EmptyInput_ReturnsEmpty()
    {
        var ops = TypeParameterOverwritePlanner.Plan([], TypeMap);

        Assert.Empty(ops);
    }
}
