using SmartCon.Core.Models.FamilyManager;
using SmartCon.FamilyManager.Services.Validation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public sealed class SnapshotValidationMapperTests
{
    [Fact]
    public void LoadableSnapshot_MapsTypesAndValues()
    {
        var snapshot = new FamilySnapshot(
            FamilyName: "Elbow",
            Category: "Pipe Fittings",
            CategoryId: -2008055,
            Parameters: [],
            Types:
            [
                new FamilyTypeSnapshot("DN50",
                [
                    new FamilyParameterValue("DN", "Double", true, "50", 50.0, null, "50 мм", null, "unit:mm"),
                    new FamilyParameterValue("Comment", "String", false, null, null, null),
                ]),
                new FamilyTypeSnapshot("DN65",
                [
                    new FamilyParameterValue("DN", "Double", true, "65", 65.0, null, "65 мм", null, "unit:mm"),
                    new FamilyParameterValue("Comment", "String", false, null, null, null),
                ]),
            ],
            Geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: [],
            NonSharedNestedFamilyNames: [],
            Facts: null,
            Connectors: [],
            BehaviorFlags: null);

        var input = SnapshotValidationMapper.ToValidationInput(snapshot);

        Assert.Equal(2, input.Types.Count);
        Assert.Equal("DN50", input.Types[0].TypeName);

        var dn = Assert.Single(input.Types[0].Values, v => v.ParameterName == "DN");
        Assert.True(dn.IsPresent);
        Assert.Equal(50.0, dn.ValueNumber);
        Assert.Equal("unit:mm", dn.UnitTypeId);

        var comment = Assert.Single(input.Types[0].Values, v => v.ParameterName == "Comment");
        Assert.True(comment.IsPresent);
        Assert.Null(comment.ValueText);
        Assert.Null(comment.ValueNumber);
    }

    [Fact]
    public void SystemSnapshot_MapsTypesAndValues()
    {
        var snapshot = new SystemFamilySnapshot(
            CategoryName: "Трубы",
            CategoryId: -2008044,
            Types:
            [
                new SystemTypeSnapshot("Стандартный",
                [
                    new SystemParameterValue("Diameter", "Double", true, "0.164", 0.164, null),
                ]),
            ]);

        var input = SnapshotValidationMapper.ToValidationInput(snapshot);

        var type = Assert.Single(input.Types);
        Assert.Equal("Стандартный", type.TypeName);
        var diameter = Assert.Single(type.Values);
        Assert.True(diameter.IsPresent);
        Assert.Equal(0.164, diameter.ValueNumber);
    }
}
