using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Services;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Unit tests for <see cref="SnapshotExtractionMapper"/> — verifies that
/// the in-memory snapshot from Phase 1 Prepare is correctly converted to
/// the <see cref="FamilyExtractionResult"/> / <see cref="FamilyTypeDescriptor"/>
/// shapes the catalog persistence layer expects, WITHOUT re-opening any file.
/// </summary>
public sealed class SnapshotExtractionMapperTests
{
    // ---- ToExtractionResult (loadable) ----

    [Fact]
    public void ToExtractionResult_Loadable_NullSnapshot_ReturnsFailure()
    {
        var result = SnapshotExtractionMapper.ToExtractionResult(
            (FamilySnapshot)null!, revitMajorVersion: 2025);

        Assert.False(result.Success);
        Assert.Equal("Snapshot is null", result.ErrorMessage);
        Assert.Empty(result.Types);
    }

    [Fact]
    public void ToExtractionResult_Loadable_MapsTypesAndValues()
    {
        var snapshot = new FamilySnapshot(
            FamilyName: "Valve",
            Category: "Pipe Fittings",
            Parameters: new[]
            {
                new FamilyParameterInfo("Width", "Double", "", IsInstance: false, IsShared: false,
                    null, false, false, null, null),
                new FamilyParameterInfo("Material", "ElementId", "", IsInstance: false, IsShared: false,
                    null, false, false, null, null),
            },
            Types: new[]
            {
                new FamilyTypeSnapshot("DN50", new[]
                {
                    new FamilyParameterValue("Width", "Double", HasValue: true, "50", 50.0, null),
                    new FamilyParameterValue("Material", "ElementId", HasValue: true, "Steel", null, "Steel"),
                }),
            },
            Geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: new[] { "NestedValve" });

        var result = SnapshotExtractionMapper.ToExtractionResult(snapshot, revitMajorVersion: 2025);

        Assert.True(result.Success);
        Assert.Equal(2025, result.RevitMajorVersion);
        Assert.Single(result.Types);
        var type = result.Types[0];
        Assert.Equal("DN50", type.TypeName);
        Assert.Equal(0, type.SortOrder);
        Assert.Equal(2, type.Values.Count);

        var width = type.Values.Single(v => v.ParameterName == "Width");
        Assert.Equal(AttributeScope.Type, width.ParameterScope);
        Assert.Equal("Double", width.StorageType);
        Assert.Equal(AttributeValueStatus.Found, width.Status);
        Assert.Equal("50", width.ValueText);
        Assert.Equal(50.0, width.ValueNumber);
        Assert.Null(width.Message);

        var material = type.Values.Single(v => v.ParameterName == "Material");
        Assert.Equal("ElementId", material.StorageType);
        Assert.Equal(AttributeValueStatus.Found, material.Status);
        Assert.Equal("Steel", material.ValueText);
        Assert.Null(material.ValueNumber);
    }

    [Fact]
    public void ToExtractionResult_Loadable_SortsTypesByNameOrdinal()
    {
        var snapshot = new FamilySnapshot(
            FamilyName: "F", Category: "C",
            Parameters: Array.Empty<FamilyParameterInfo>(),
            Types: new[]
            {
                new FamilyTypeSnapshot("Zeta", Array.Empty<FamilyParameterValue>()),
                new FamilyTypeSnapshot("Alpha", Array.Empty<FamilyParameterValue>()),
                new FamilyTypeSnapshot("Beta", Array.Empty<FamilyParameterValue>()),
            },
            Geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: Array.Empty<string>());

        var result = SnapshotExtractionMapper.ToExtractionResult(snapshot, 2025);

        Assert.Equal(3, result.Types.Count);
        Assert.Equal("Alpha", result.Types[0].TypeName);
        Assert.Equal(0, result.Types[0].SortOrder);
        Assert.Equal("Beta", result.Types[1].TypeName);
        Assert.Equal(1, result.Types[1].SortOrder);
        Assert.Equal("Zeta", result.Types[2].TypeName);
        Assert.Equal(2, result.Types[2].SortOrder);
    }

    [Fact]
    public void ToExtractionResult_Loadable_HasValueFalse_EmptyValue()
    {
        var snapshot = new FamilySnapshot(
            FamilyName: "F", Category: "C",
            Parameters: new[]
            {
                new FamilyParameterInfo("Width", "Double", "", false, false, null, false, false, null, null),
            },
            Types: new[]
            {
                new FamilyTypeSnapshot("T", new[]
                {
                    new FamilyParameterValue("Width", "Double", HasValue: false, null, null, null),
                }),
            },
            Geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: Array.Empty<string>());

        var result = SnapshotExtractionMapper.ToExtractionResult(snapshot, 2025);

        Assert.True(result.Success);
        var val = result.Types[0].Values[0];
        Assert.Equal(AttributeValueStatus.EmptyValue, val.Status);
        Assert.Null(val.ValueText);
        Assert.Null(val.ValueRaw);
        Assert.Null(val.ValueNumber);
        Assert.Equal("Parameter has no value", val.Message);
    }

    [Fact]
    public void ToExtractionResult_Loadable_ParameterScope_Instance()
    {
        var snapshot = new FamilySnapshot(
            FamilyName: "F", Category: "C",
            Parameters: new[]
            {
                new FamilyParameterInfo("InstParam", "String", "", IsInstance: true, IsShared: false, null, false, false, null, null),
                new FamilyParameterInfo("TypeParam", "String", "", IsInstance: false, IsShared: false, null, false, false, null, null),
            },
            Types: new[]
            {
                new FamilyTypeSnapshot("T", new[]
                {
                    new FamilyParameterValue("InstParam", "String", HasValue: true, "abc", null, null),
                    new FamilyParameterValue("TypeParam", "String", HasValue: true, "xyz", null, null),
                }),
            },
            Geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: Array.Empty<string>());

        var result = SnapshotExtractionMapper.ToExtractionResult(snapshot, 2025);

        var inst = result.Types[0].Values.Single(v => v.ParameterName == "InstParam");
        Assert.Equal(AttributeScope.Instance, inst.ParameterScope);
        var type = result.Types[0].Values.Single(v => v.ParameterName == "TypeParam");
        Assert.Equal(AttributeScope.Type, type.ParameterScope);
    }

    [Fact]
    public void ToExtractionResult_Loadable_SharedNestedNames_Preserved()
    {
        var snapshot = new FamilySnapshot(
            FamilyName: "F", Category: "C",
            Parameters: Array.Empty<FamilyParameterInfo>(),
            Types: new[]
            {
                new FamilyTypeSnapshot("T", Array.Empty<FamilyParameterValue>()),
            },
            Geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: new[] { "NestedA", "NestedB" });

        var result = SnapshotExtractionMapper.ToExtractionResult(snapshot, 2025);

        Assert.Equal(2, result.SharedNestedFamilyNamesSafe.Count);
        Assert.Contains("NestedA", result.SharedNestedFamilyNamesSafe);
        Assert.Contains("NestedB", result.SharedNestedFamilyNamesSafe);
    }

    [Fact]
    public void ToExtractionResult_Loadable_ZeroValue_NotBlank()
    {
        // "0" must NOT be treated as empty (business rule: empty vs 0 are different)
        var snapshot = new FamilySnapshot(
            FamilyName: "F", Category: "C",
            Parameters: new[]
            {
                new FamilyParameterInfo("Width", "Double", "", false, false, null, false, false, null, null),
            },
            Types: new[]
            {
                new FamilyTypeSnapshot("T", new[]
                {
                    new FamilyParameterValue("Width", "Double", HasValue: true, "0", 0.0, null),
                }),
            },
            Geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: Array.Empty<string>());

        var result = SnapshotExtractionMapper.ToExtractionResult(snapshot, 2025);

        var val = result.Types[0].Values[0];
        Assert.Equal(AttributeValueStatus.Found, val.Status);
        Assert.Equal(0.0, val.ValueNumber);
        Assert.Equal("0", val.ValueText);
    }

    // ---- ToExtractionResult (system) ----

    [Fact]
    public void ToExtractionResult_System_NullSnapshot_ReturnsFailure()
    {
        var result = SnapshotExtractionMapper.ToExtractionResult(
            (SystemFamilySnapshot)null!, revitMajorVersion: 2025);

        Assert.False(result.Success);
        Assert.Equal("System snapshot is null", result.ErrorMessage);
        Assert.Empty(result.Types);
    }

    [Fact]
    public void ToExtractionResult_System_MapsTypesAndValues()
    {
        var snapshot = new SystemFamilySnapshot(
            CategoryName: "Трубы",
            CategoryId: -2008044,
            Types: new[]
            {
                new SystemTypeSnapshot("DN50", new[]
                {
                    new SystemParameterValue("Diameter", "Double", HasValue: true, "50", 50.0, null),
                }),
            });

        var result = SnapshotExtractionMapper.ToExtractionResult(snapshot, revitMajorVersion: 2025);

        Assert.True(result.Success);
        Assert.Single(result.Types);
        Assert.Equal("DN50", result.Types[0].TypeName);
        Assert.Equal(0, result.Types[0].SortOrder);
        Assert.Single(result.Types[0].Values);
        var val = result.Types[0].Values[0];
        Assert.Equal(AttributeScope.Type, val.ParameterScope);
        Assert.Equal(AttributeValueStatus.Found, val.Status);
        Assert.Equal(50.0, val.ValueNumber);
        // System extraction must not carry shared-nested names
        Assert.Empty(result.SharedNestedFamilyNamesSafe);
    }

    [Fact]
    public void ToExtractionResult_System_EmptyTypes_StillSuccess()
    {
        var snapshot = new SystemFamilySnapshot(
            CategoryName: "Empty", CategoryId: 0,
            Types: Array.Empty<SystemTypeSnapshot>());

        var result = SnapshotExtractionMapper.ToExtractionResult(snapshot, 2025);

        Assert.True(result.Success);
        Assert.Empty(result.Types);
    }

    // ---- ToTypeDescriptors ----

    [Fact]
    public void ToTypeDescriptors_NullSnapshot_ReturnsEmpty()
    {
        var result = SnapshotExtractionMapper.ToTypeDescriptors(
            null!, "cat-1", "v-1", "f-1");

        Assert.Empty(result);
    }

    [Fact]
    public void ToTypeDescriptors_MapsNamesSortOrderAndUniqueId()
    {
        var snapshot = new FamilySnapshot(
            FamilyName: "Valve", Category: "Pipe Fittings",
            Parameters: Array.Empty<FamilyParameterInfo>(),
            Types: new[]
            {
                new FamilyTypeSnapshot("Zeta", Array.Empty<FamilyParameterValue>(), UniqueId: "uid-zeta"),
                new FamilyTypeSnapshot("Alpha", Array.Empty<FamilyParameterValue>(), UniqueId: "uid-alpha"),
            },
            Geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: Array.Empty<string>());

        var result = SnapshotExtractionMapper.ToTypeDescriptors(snapshot, "cat-1", "v-1", "f-1");

        Assert.Equal(2, result.Count);
        // Sorted by name
        Assert.Equal("Alpha", result[0].Name);
        Assert.Equal(0, result[0].SortOrder);
        Assert.Equal("uid-alpha", result[0].UniqueId);
        Assert.Equal("cat-1", result[0].CatalogItemId);
        Assert.Equal("v-1", result[0].VersionId);
        Assert.Equal("f-1", result[0].FileId);

        Assert.Equal("Zeta", result[1].Name);
        Assert.Equal(1, result[1].SortOrder);
        Assert.Equal("uid-zeta", result[1].UniqueId);
    }

    [Fact]
    public void ToTypeDescriptors_NullUniqueId_PreservedAsNull()
    {
        var snapshot = new FamilySnapshot(
            FamilyName: "F", Category: "C",
            Parameters: Array.Empty<FamilyParameterInfo>(),
            Types: new[]
            {
                new FamilyTypeSnapshot("T", Array.Empty<FamilyParameterValue>(), UniqueId: null),
            },
            Geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: Array.Empty<string>());

        var result = SnapshotExtractionMapper.ToTypeDescriptors(snapshot, "cat-1");

        Assert.Single(result);
        Assert.Null(result[0].UniqueId);
    }
}
