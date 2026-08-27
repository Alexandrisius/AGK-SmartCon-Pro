using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

public class FamilyContentHasherTests
{
    private readonly FamilyContentHasher _hasher = new();

    private static FamilySnapshot CreateLoadableSnapshot(
        string familyName = "TestFamily",
        string category = "Pipe Fittings",
        IReadOnlyList<FamilyParameterInfo>? parameters = null,
        IReadOnlyList<FamilyTypeSnapshot>? types = null,
        GeometryMetrics? geometry = null,
        IReadOnlyList<string>? sharedNested = null)
    {
        return new FamilySnapshot(
            FamilyName: familyName,
            Category: category,
            Parameters: parameters ?? Array.Empty<FamilyParameterInfo>(),
            Types: types ?? Array.Empty<FamilyTypeSnapshot>(),
            Geometry: geometry ?? new GeometryMetrics(0, Array.Empty<FormMetrics>()),
            SharedNestedFamilyNames: sharedNested ?? Array.Empty<string>());
    }

    private static SystemFamilySnapshot CreateSystemSnapshot(
        string categoryName = "Трубы",
        int categoryId = -1959380011,
        IReadOnlyList<SystemTypeSnapshot>? types = null)
    {
        return new SystemFamilySnapshot(
            CategoryName: categoryName,
            CategoryId: categoryId,
            Types: types ?? Array.Empty<SystemTypeSnapshot>());
    }

    [Fact]
    public void ComputeForLoadable_NullSnapshot_ReturnsNull()
    {
        var result = _hasher.ComputeForLoadable(null!);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeForLoadable_EmptySnapshot_ReturnsStableHash()
    {
        var snapshot = CreateLoadableSnapshot();
        var result = _hasher.ComputeForLoadable(snapshot);
        Assert.NotNull(result);
        Assert.Equal("loadable", result!.SourceKind);

        var result2 = _hasher.ComputeForLoadable(CreateLoadableSnapshot());
        Assert.NotNull(result2);
        Assert.Equal(result!.HexString, result2!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_CategoryIdAndFacts_ShiftHash()
    {
        // ADR-056 (FHV3): category ordinal and facts ARE content — the
        // ordinal defines identity locale-independently, and the Part
        // Type defines a fitting's function ("Отвод" vs "Тройник").
        var baseline = CreateLoadableSnapshot();
        var withFacts = baseline with
        {
            CategoryId = -2008049,
            Facts = [new FamilyFact("part_type", "5", "Elbow")],
        };

        var hash1 = _hasher.ComputeForLoadable(baseline);
        var hash2 = _hasher.ComputeForLoadable(withFacts);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1!.HexString, hash2!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_SameContent_DifferentNames_SameHash()
    {
        // Issue #126: hash format v2 is rename-invariant — the family name
        // is mutable metadata and must NOT affect content identity.
        var param = new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null);
        var type = new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)]);
        var geometry = new GeometryMetrics(1, [new FormMetrics("Extrusion", true, 1250.0, 6, 12, null)]);

        var snapshot1 = CreateLoadableSnapshot(familyName: "LogoA", parameters: [param], types: [type], geometry: geometry);
        var snapshot2 = CreateLoadableSnapshot(familyName: "LogoB", parameters: [param], types: [type], geometry: geometry);

        var hash1 = _hasher.ComputeForLoadable(snapshot1);
        var hash2 = _hasher.ComputeForLoadable(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_EmptySnapshot_DifferentNames_SameHash()
    {
        var hash1 = _hasher.ComputeForLoadable(CreateLoadableSnapshot(familyName: "LogoA"));
        var hash2 = _hasher.ComputeForLoadable(CreateLoadableSnapshot(familyName: "LogoB"));

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_2D_Metrics_AffectHash()
    {
        var param = new FamilyParameterInfo("W", "Double", "", false, false, null, false, false, null, null);
        var type = new FamilyTypeSnapshot("T",
            [new FamilyParameterValue("W", "Double", true, "1", 1.0, null)]);

        var snapshotWithout2D = CreateLoadableSnapshot(
            parameters: [param],
            types: [type],
            geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>()));

        var snapshotWith2D = CreateLoadableSnapshot(
            parameters: [param],
            types: [type],
            geometry: new GeometryMetrics(0, Array.Empty<FormMetrics>(),
                SymbolicCurveCount: 5, DetailCurveCount: 3, TextNoteCount: 2));

        var hashWithout2D = _hasher.ComputeForLoadable(snapshotWithout2D);
        var hashWith2D = _hasher.ComputeForLoadable(snapshotWith2D);

        Assert.NotNull(hashWithout2D);
        Assert.NotNull(hashWith2D);
        Assert.NotEqual(hashWithout2D.HexString, hashWith2D.HexString);
    }

    [Fact]
    public void ComputeForLoadable_ValidSnapshot_ReturnsNonNullHash()
    {
        var snapshot = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])],
            geometry: new GeometryMetrics(1, [new FormMetrics("Extrusion", true, 1250.0, 6, 12, null)]));

        var result = _hasher.ComputeForLoadable(snapshot);

        Assert.NotNull(result);
        Assert.Equal("loadable", result.SourceKind);
        Assert.Equal(FamilyContentHashFormat.CurrentVersion, result.FormatVersion);
        Assert.NotEmpty(result.HexString);
        Assert.Equal(64, result.HexString.Length);
    }

    [Fact]
    public void ComputeForLoadable_SameSnapshotTwice_ReturnsSameHash()
    {
        var snapshot = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])]);

        var hash1 = _hasher.ComputeForLoadable(snapshot);
        var hash2 = _hasher.ComputeForLoadable(snapshot);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_DifferentParameters_ReturnsDifferentHash()
    {
        var snapshot1 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])]);
        var snapshot2 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Height", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Height", "Double", true, "50", 50.0, null)])]);

        var hash1 = _hasher.ComputeForLoadable(snapshot1);
        var hash2 = _hasher.ComputeForLoadable(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_DifferentTypes_ReturnsDifferentHash()
    {
        var snapshot1 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])]);
        var snapshot2 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN100", [new FamilyParameterValue("Width", "Double", true, "100", 100.0, null)])]);

        var hash1 = _hasher.ComputeForLoadable(snapshot1);
        var hash2 = _hasher.ComputeForLoadable(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_DifferentGeometry_ReturnsDifferentHash()
    {
        var geom1 = new GeometryMetrics(1, [new FormMetrics("Extrusion", true, 1250.0, 6, 12, null)]);
        var geom2 = new GeometryMetrics(1, [new FormMetrics("Extrusion", true, 1300.0, 6, 12, null)]);

        var snapshot1 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])],
            geometry: geom1);
        var snapshot2 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])],
            geometry: geom2);

        var hash1 = _hasher.ComputeForLoadable(snapshot1);
        var hash2 = _hasher.ComputeForLoadable(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_DifferentFormula_ReturnsDifferentHash()
    {
        var snapshot1 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Area", "Double", "PG_GEOMETRY", false, false, "Width * Height", true, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])]);
        var snapshot2 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Area", "Double", "PG_GEOMETRY", false, false, "Width * Height * 2", true, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])]);

        var hash1 = _hasher.ComputeForLoadable(snapshot1);
        var hash2 = _hasher.ComputeForLoadable(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_EmptyValue_Vs_ZeroValue_ReturnsDifferentHash()
    {
        var snapshotEmpty = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", false, null, null, null)])]);
        var snapshotZero = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "0", 0.0, null)])]);

        var hashEmpty = _hasher.ComputeForLoadable(snapshotEmpty);
        var hashZero = _hasher.ComputeForLoadable(snapshotZero);

        Assert.NotNull(hashEmpty);
        Assert.NotNull(hashZero);
        Assert.NotEqual(hashEmpty.HexString, hashZero.HexString);
    }

    [Fact]
    public void ComputeForLoadable_ParameterOrderIndependence_ReturnsSameHash()
    {
        var paramA = new FamilyParameterInfo("Alpha", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null);
        var paramB = new FamilyParameterInfo("Beta", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null);

        var snapshot1 = CreateLoadableSnapshot(parameters: [paramA, paramB]);
        var snapshot2 = CreateLoadableSnapshot(parameters: [paramB, paramA]);

        var hash1 = _hasher.ComputeForLoadable(snapshot1);
        var hash2 = _hasher.ComputeForLoadable(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_TypeOrderIndependence_ReturnsSameHash()
    {
        var param = new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null);
        var typeA = new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)]);
        var typeB = new FamilyTypeSnapshot("DN100", [new FamilyParameterValue("Width", "Double", true, "100", 100.0, null)]);

        var snapshot1 = CreateLoadableSnapshot(parameters: [param], types: [typeA, typeB]);
        var snapshot2 = CreateLoadableSnapshot(parameters: [param], types: [typeB, typeA]);

        var hash1 = _hasher.ComputeForLoadable(snapshot1);
        var hash2 = _hasher.ComputeForLoadable(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_CrossSource_PrefixDiffersFromSystem()
    {
        var loadableSnapshot = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])]);
        var systemSnapshot = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("DN50", [new SystemParameterValue("Width", "Double", true, "50", 50.0, null)])]);

        var loadableHash = _hasher.ComputeForLoadable(loadableSnapshot);
        var systemHash = _hasher.ComputeForSystem(systemSnapshot);

        Assert.NotNull(loadableHash);
        Assert.NotNull(systemHash);
        Assert.NotEqual(loadableHash.HexString, systemHash.HexString);
        Assert.NotEqual(loadableHash.SourceKind, systemHash.SourceKind);
    }

    [Fact]
    public void ComputeForLoadable_FormatVersion_IsCurrentVersion()
    {
        var snapshot = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])]);

        var result = _hasher.ComputeForLoadable(snapshot);

        Assert.NotNull(result);
        Assert.Equal(FamilyContentHashFormat.CurrentVersion, result.FormatVersion);
    }

    [Fact]
    public void ComputeForLoadable_DifferentSharedNested_ReturnsDifferentHash()
    {
        var snapshot1 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])],
            sharedNested: ["NestedA"]);
        var snapshot2 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])],
            sharedNested: ["NestedB"]);

        var hash1 = _hasher.ComputeForLoadable(snapshot1);
        var hash2 = _hasher.ComputeForLoadable(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_ElementIdValue_ResolvedNameInHash()
    {
        var snapshot1 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Material", "ElementId", "PG_MATERIALS", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Material", "ElementId", true, null, null, "Steel")])]);
        var snapshot2 = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("Material", "ElementId", "PG_MATERIALS", false, false, null, false, false, null, null)],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Material", "ElementId", true, null, null, "Copper")])]);

        var hash1 = _hasher.ComputeForLoadable(snapshot1);
        var hash2 = _hasher.ComputeForLoadable(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForSystem_NullSnapshot_ReturnsNull()
    {
        var result = _hasher.ComputeForSystem(null!);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeForSystem_EmptyTypes_ReturnsNull()
    {
        var snapshot = CreateSystemSnapshot();
        var result = _hasher.ComputeForSystem(snapshot);
        Assert.Null(result);
    }

    [Fact]
    public void ComputeForSystem_ValidSnapshot_ReturnsNonNullHash()
    {
        var snapshot = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)])]);

        var result = _hasher.ComputeForSystem(snapshot);

        Assert.NotNull(result);
        Assert.Equal("system", result.SourceKind);
        Assert.Equal(FamilyContentHashFormat.CurrentVersion, result.FormatVersion);
        Assert.NotEmpty(result.HexString);
    }

    [Fact]
    public void ComputeForSystem_SameSnapshotTwice_ReturnsSameHash()
    {
        var snapshot = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)])]);

        var hash1 = _hasher.ComputeForSystem(snapshot);
        var hash2 = _hasher.ComputeForSystem(snapshot);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForSystem_DifferentTypes_ReturnsDifferentHash()
    {
        var snapshot1 = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)])]);
        var snapshot2 = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Нестандартный", [new SystemParameterValue("Diameter", "Double", true, "50", 50.0, null)])]);

        var hash1 = _hasher.ComputeForSystem(snapshot1);
        var hash2 = _hasher.ComputeForSystem(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForSystem_DifferentCategory_ReturnsDifferentHash()
    {
        var snapshot1 = CreateSystemSnapshot(
            categoryName: "Трубы",
            categoryId: -2008044,
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)])]);
        var snapshot2 = CreateSystemSnapshot(
            categoryName: "Воздуховоды",
            categoryId: -2008001,
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)])]);

        var hash1 = _hasher.ComputeForSystem(snapshot1);
        var hash2 = _hasher.ComputeForSystem(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForSystem_DifferentParameterValue_ReturnsDifferentHash()
    {
        var snapshot1 = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)])]);
        var snapshot2 = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("Diameter", "Double", true, "32", 32.0, null)])]);

        var hash1 = _hasher.ComputeForSystem(snapshot1);
        var hash2 = _hasher.ComputeForSystem(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForSystem_EmptyParameter_DoesNotAffectHash()
    {
        var snapshotWithoutEmpty = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)])]);
        var snapshotWithEmpty = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный",
            [
                new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null),
                new SystemParameterValue("ADSK_Mark", "String", false, null, null, null),
            ])]);

        var hash1 = _hasher.ComputeForSystem(snapshotWithoutEmpty);
        var hash2 = _hasher.ComputeForSystem(snapshotWithEmpty);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForSystem_AllParametersEmpty_HashStillComputed()
    {
        var snapshot = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный",
            [
                new SystemParameterValue("Diameter", "Double", false, null, null, null),
                new SystemParameterValue("ADSK_Mark", "String", false, null, null, null),
            ])]);

        var result = _hasher.ComputeForSystem(snapshot);

        Assert.NotNull(result);
        Assert.NotEmpty(result.HexString);
    }

    [Fact]
    public void ComputeForSystem_FilledParameterVsEmpty_ReturnsDifferentHash()
    {
        var snapshotEmpty = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный",
            [
                new SystemParameterValue("ADSK_Mark", "String", false, null, null, null),
            ])]);
        var snapshotFilled = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный",
            [
                new SystemParameterValue("ADSK_Mark", "String", true, "Vendor1", null, null),
            ])]);

        var hashEmpty = _hasher.ComputeForSystem(snapshotEmpty);
        var hashFilled = _hasher.ComputeForSystem(snapshotFilled);

        Assert.NotNull(hashEmpty);
        Assert.NotNull(hashFilled);
        Assert.NotEqual(hashEmpty.HexString, hashFilled.HexString);
    }

    [Fact]
    public void ComputeForLoadable_EmptyParameterValue_DoesNotAffectHash()
    {
        var param = new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null);
        var snapshotWithoutEmpty = CreateLoadableSnapshot(
            parameters: [param],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])]);
        var snapshotWithEmpty = CreateLoadableSnapshot(
            parameters: [param],
            types: [new FamilyTypeSnapshot("DN50",
            [
                new FamilyParameterValue("Width", "Double", true, "50", 50.0, null),
                new FamilyParameterValue("Height", "Double", false, null, null, null),
            ])]);

        var hash1 = _hasher.ComputeForLoadable(snapshotWithoutEmpty);
        var hash2 = _hasher.ComputeForLoadable(snapshotWithEmpty);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForSystem_BlankStringValue_DoesNotAffectHash()
    {
        var snapshotWithout = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)])]);
        var snapshotWith = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный",
            [
                new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null),
                new SystemParameterValue("ADSK_Mark", "String", true, "", null, null),
            ])]);

        var hash1 = _hasher.ComputeForSystem(snapshotWithout);
        var hash2 = _hasher.ComputeForSystem(snapshotWith);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForSystem_InvalidElementIdValue_DoesNotAffectHash()
    {
        var snapshotWithout = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)])]);
        var snapshotWith = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный",
            [
                new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null),
                new SystemParameterValue("Material", "ElementId", true, "INVALID", null, null),
            ])]);

        var hash1 = _hasher.ComputeForSystem(snapshotWithout);
        var hash2 = _hasher.ComputeForSystem(snapshotWith);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForSystem_NumericZero_IsMeaningful_AffectsHash()
    {
        var snapshotZero = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("IfcCode", "Integer", true, "0", 0.0, null)])]);
        var snapshotMissing = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный", [])]);

        var hashZero = _hasher.ComputeForSystem(snapshotZero);
        var hashMissing = _hasher.ComputeForSystem(snapshotMissing);

        Assert.NotNull(hashZero);
        Assert.NotNull(hashMissing);
        Assert.NotEqual(hashZero.HexString, hashMissing.HexString);
    }

    [Fact]
    public void ComputeForLoadable_BlankStringValue_DoesNotAffectHash()
    {
        var param = new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null);
        var snapshotWithout = CreateLoadableSnapshot(
            parameters: [param],
            types: [new FamilyTypeSnapshot("DN50", [new FamilyParameterValue("Width", "Double", true, "50", 50.0, null)])]);
        var snapshotWith = CreateLoadableSnapshot(
            parameters: [param],
            types: [new FamilyTypeSnapshot("DN50",
            [
                new FamilyParameterValue("Width", "Double", true, "50", 50.0, null),
                new FamilyParameterValue("ADSK_Mark", "String", true, "", null, null),
            ])]);

        var hash1 = _hasher.ComputeForLoadable(snapshotWithout);
        var hash2 = _hasher.ComputeForLoadable(snapshotWith);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForSystem_IfcGUID_DoesNotAffectHash()
    {
        var snapshot1 = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный",
            [
                new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null),
                new SystemParameterValue("Код IfcGUID", "String", true, "07pqzDwibFqRrKFG$dg4$g", null, null),
            ])]);
        var snapshot2 = CreateSystemSnapshot(
            types: [new SystemTypeSnapshot("Стандартный",
            [
                new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null),
                new SystemParameterValue("Код IfcGUID", "String", true, "19t75Gyzb19w$H_0FkkXqo", null, null),
            ])]);

        var hash1 = _hasher.ComputeForSystem(snapshot1);
        var hash2 = _hasher.ComputeForSystem(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_IfcGUID_DoesNotAffectHash()
    {
        var param = new FamilyParameterInfo("Width", "Double", "PG_GEOMETRY", false, false, null, false, false, null, null);
        var snapshot1 = CreateLoadableSnapshot(
            parameters: [param],
            types: [new FamilyTypeSnapshot("DN50",
            [
                new FamilyParameterValue("Width", "Double", true, "50", 50.0, null),
                new FamilyParameterValue("Код IfcGUID", "String", true, "07pqzDwibFqRrKFG$dg4$g", null, null),
            ])]);
        var snapshot2 = CreateLoadableSnapshot(
            parameters: [param],
            types: [new FamilyTypeSnapshot("DN50",
            [
                new FamilyParameterValue("Width", "Double", true, "50", 50.0, null),
                new FamilyParameterValue("Код IfcGUID", "String", true, "19t75Gyzb19w$H_0FkkXqo", null, null),
            ])]);

        var hash1 = _hasher.ComputeForLoadable(snapshot1);
        var hash2 = _hasher.ComputeForLoadable(snapshot2);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1.HexString, hash2.HexString);
    }

    [Fact]
    public void ComputeForLoadable_DefaultType_AffectsHashVsNoTypes()
    {
        var snapshotNoTypes = CreateLoadableSnapshot(
            familyName: "Annotation",
            parameters: [],
            types: []);

        var snapshotWithDefaultType = CreateLoadableSnapshot(
            familyName: "Annotation",
            parameters: [],
            types: [new FamilyTypeSnapshot("<default>", Array.Empty<FamilyParameterValue>())]);

        var hashNoTypes = _hasher.ComputeForLoadable(snapshotNoTypes);
        var hashWithDefault = _hasher.ComputeForLoadable(snapshotWithDefaultType);

        Assert.NotNull(hashNoTypes);
        Assert.NotNull(hashWithDefault);
        Assert.NotEqual(hashNoTypes.HexString, hashWithDefault.HexString);
    }

    // ---------- FHV3 (ADR-056, Issue #159) ----------

    [Fact]
    public void ComputeForLoadable_SameOrdinalDifferentCategoryDisplayName_SameHash()
    {
        // Locale-invariance: the ordinal is the identity, the display
        // name ("Pipe Fittings" / "Трубопроводные фитинги") is not hashed.
        var en = CreateLoadableSnapshot(category: "Pipe Fittings") with { CategoryId = -2008049 };
        var ru = CreateLoadableSnapshot(category: "Трубопроводные фитинги") with { CategoryId = -2008049 };

        var hashEn = _hasher.ComputeForLoadable(en);
        var hashRu = _hasher.ComputeForLoadable(ru);

        Assert.NotNull(hashEn);
        Assert.NotNull(hashRu);
        Assert.Equal(hashEn!.HexString, hashRu!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_NullOrdinal_FallsBackToDisplayName()
    {
        var a = CreateLoadableSnapshot(category: "Pipe Fittings");
        var b = CreateLoadableSnapshot(category: "Трубопроводные фитинги");

        var hashA = _hasher.ComputeForLoadable(a);
        var hashB = _hasher.ComputeForLoadable(b);

        Assert.NotNull(hashA);
        Assert.NotNull(hashB);
        Assert.NotEqual(hashA!.HexString, hashB!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_PartTypeChange_ShiftsHash()
    {
        var elbow = CreateLoadableSnapshot() with
        {
            CategoryId = -2008049,
            Facts = [new FamilyFact("part_type", "5", "Elbow")],
        };
        var tee = CreateLoadableSnapshot() with
        {
            CategoryId = -2008049,
            Facts = [new FamilyFact("part_type", "6", "Tee")],
        };

        var hashElbow = _hasher.ComputeForLoadable(elbow);
        var hashTee = _hasher.ComputeForLoadable(tee);

        Assert.NotNull(hashElbow);
        Assert.NotNull(hashTee);
        Assert.NotEqual(hashElbow!.HexString, hashTee!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_ConnectorSizeChange_ShiftsHash()
    {
        var small = CreateLoadableSnapshot() with
        {
            Connectors = [new ConnectorSnapshot(2, 0, 7, true, null, null, 0.05, 0, 0, 0, -1)],
        };
        var large = CreateLoadableSnapshot() with
        {
            Connectors = [new ConnectorSnapshot(2, 0, 7, true, null, null, 0.10, 0, 0, 0, -1)],
        };

        var hashSmall = _hasher.ComputeForLoadable(small);
        var hashLarge = _hasher.ComputeForLoadable(large);

        Assert.NotNull(hashSmall);
        Assert.NotNull(hashLarge);
        Assert.NotEqual(hashSmall!.HexString, hashLarge!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_ConnectorSystemClassificationChange_ShiftsHash()
    {
        var coldWater = CreateLoadableSnapshot() with
        {
            Connectors = [new ConnectorSnapshot(2, 0, 7, true, null, null, 0.05, 0, 0, 0, -1)],
        };
        var hotWater = CreateLoadableSnapshot() with
        {
            Connectors = [new ConnectorSnapshot(2, 0, 8, true, null, null, 0.05, 0, 0, 0, -1)],
        };

        var hashCold = _hasher.ComputeForLoadable(coldWater);
        var hashHot = _hasher.ComputeForLoadable(hotWater);

        Assert.NotNull(hashCold);
        Assert.NotNull(hashHot);
        Assert.NotEqual(hashCold!.HexString, hashHot!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_ConnectorOriginChange_ShiftsHash()
    {
        var here = CreateLoadableSnapshot() with
        {
            Connectors = [new ConnectorSnapshot(2, 0, 7, true, null, null, 0.05, 1.0, 0, 0, -1)],
        };
        var there = CreateLoadableSnapshot() with
        {
            Connectors = [new ConnectorSnapshot(2, 0, 7, true, null, null, 0.05, 2.0, 0, 0, -1)],
        };

        var hashHere = _hasher.ComputeForLoadable(here);
        var hashThere = _hasher.ComputeForLoadable(there);

        Assert.NotNull(hashHere);
        Assert.NotNull(hashThere);
        Assert.NotEqual(hashHere!.HexString, hashThere!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_ConnectorOriginWithinRounding_SameHash()
    {
        // 1e-4 ft rounding absorbs regen noise (ADR-056 §Risks).
        var a = CreateLoadableSnapshot() with
        {
            Connectors = [new ConnectorSnapshot(2, 0, 7, true, null, null, 0.05, 0.12342, 0, 0, -1)],
        };
        var b = CreateLoadableSnapshot() with
        {
            Connectors = [new ConnectorSnapshot(2, 0, 7, true, null, null, 0.05, 0.12344, 0, 0, -1)],
        };

        var hashA = _hasher.ComputeForLoadable(a);
        var hashB = _hasher.ComputeForLoadable(b);

        Assert.NotNull(hashA);
        Assert.NotNull(hashB);
        Assert.Equal(hashA!.HexString, hashB!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_ConnectorLinkedIndexChange_ShiftsHash()
    {
        var unlinked = CreateLoadableSnapshot() with
        {
            Connectors =
            [
                new ConnectorSnapshot(2, 0, 7, true, null, null, 0.05, 0, 0, 0, -1),
                new ConnectorSnapshot(2, 0, 7, false, null, null, 0.05, 1, 0, 0, -1),
            ],
        };
        var linked = CreateLoadableSnapshot() with
        {
            Connectors =
            [
                new ConnectorSnapshot(2, 0, 7, true, null, null, 0.05, 0, 0, 0, 1),
                new ConnectorSnapshot(2, 0, 7, false, null, null, 0.05, 1, 0, 0, 0),
            ],
        };

        var hashUnlinked = _hasher.ComputeForLoadable(unlinked);
        var hashLinked = _hasher.ComputeForLoadable(linked);

        Assert.NotNull(hashUnlinked);
        Assert.NotNull(hashLinked);
        Assert.NotEqual(hashUnlinked!.HexString, hashLinked!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_BehaviorFlagsChange_ShiftsHash()
    {
        var shared = CreateLoadableSnapshot() with
        {
            BehaviorFlags = new FamilyBehaviorFlags(true, false, false, false),
        };
        var notShared = CreateLoadableSnapshot() with
        {
            BehaviorFlags = new FamilyBehaviorFlags(false, false, false, false),
        };

        var hashShared = _hasher.ComputeForLoadable(shared);
        var hashNotShared = _hasher.ComputeForLoadable(notShared);

        Assert.NotNull(hashShared);
        Assert.NotNull(hashNotShared);
        Assert.NotEqual(hashShared!.HexString, hashNotShared!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_BoundingBoxChange_SameVolume_ShiftsHash()
    {
        var still = new GeometryMetrics(1,
        [
            new FormMetrics("Extrusion", true, 1250.0, 6, 12, null,
                SurfaceArea: 500.0,
                Bounds: new BoundingBoxSnapshot(0, 0, 0, 10, 10, 12.5)),
        ]);
        var moved = new GeometryMetrics(1,
        [
            new FormMetrics("Extrusion", true, 1250.0, 6, 12, null,
                SurfaceArea: 500.0,
                Bounds: new BoundingBoxSnapshot(5, 0, 0, 15, 10, 12.5)),
        ]);

        var hashStill = _hasher.ComputeForLoadable(CreateLoadableSnapshot(geometry: still));
        var hashMoved = _hasher.ComputeForLoadable(CreateLoadableSnapshot(geometry: moved));

        Assert.NotNull(hashStill);
        Assert.NotNull(hashMoved);
        Assert.NotEqual(hashStill!.HexString, hashMoved!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_SurfaceAreaChange_ShiftsHash()
    {
        var a = new GeometryMetrics(1,
        [
            new FormMetrics("Extrusion", true, 1250.0, 6, 12, null, SurfaceArea: 500.0),
        ]);
        var b = new GeometryMetrics(1,
        [
            new FormMetrics("Extrusion", true, 1250.0, 6, 12, null, SurfaceArea: 600.0),
        ]);

        var hashA = _hasher.ComputeForLoadable(CreateLoadableSnapshot(geometry: a));
        var hashB = _hasher.ComputeForLoadable(CreateLoadableSnapshot(geometry: b));

        Assert.NotNull(hashA);
        Assert.NotNull(hashB);
        Assert.NotEqual(hashA!.HexString, hashB!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_CurveLengthChange_ShiftsHash()
    {
        var short_ = new GeometryMetrics(0, [], SymbolicCurveCount: 2, TotalSymbolicCurveLength: 1.0);
        var long_ = new GeometryMetrics(0, [], SymbolicCurveCount: 2, TotalSymbolicCurveLength: 2.0);

        var hashShort = _hasher.ComputeForLoadable(CreateLoadableSnapshot(geometry: short_));
        var hashLong = _hasher.ComputeForLoadable(CreateLoadableSnapshot(geometry: long_));

        Assert.NotNull(hashShort);
        Assert.NotNull(hashLong);
        Assert.NotEqual(hashShort!.HexString, hashLong!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_NonSharedNestedChange_ShiftsHash()
    {
        var a = CreateLoadableSnapshot() with { NonSharedNestedFamilyNames = ["NestedA"] };
        var b = CreateLoadableSnapshot() with { NonSharedNestedFamilyNames = ["NestedB"] };

        var hashA = _hasher.ComputeForLoadable(a);
        var hashB = _hasher.ComputeForLoadable(b);

        Assert.NotNull(hashA);
        Assert.NotNull(hashB);
        Assert.NotEqual(hashA!.HexString, hashB!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_SeparatorInContent_Escaped_NoFieldInjection()
    {
        // Without escaping, param name "A|Double" would inject an extra
        // field and could collide with param "A" + storage "Double".
        var injected = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("A|Double", "Double", "G", false, false, null, false, false, null, null)]);
        var plain = CreateLoadableSnapshot(
            parameters: [new FamilyParameterInfo("A", "Double", "Double", false, false, null, false, false, null, null)]);

        var canonicalInjected = FamilyContentHasher.BuildLoadableCanonicalString(injected);
        var canonicalPlain = FamilyContentHasher.BuildLoadableCanonicalString(plain);

        Assert.Contains("A%7CDouble", canonicalInjected);
        Assert.NotEqual(canonicalInjected, canonicalPlain);
    }

    [Fact]
    public void IsBlankValue_UserLiteralInvalid_StringStorage_IsNotBlank()
    {
        // The extractor emits "INVALID" only for ElementId storage — a
        // user's literal "INVALID" text parameter is real content (v3).
        Assert.False(FamilyContentHasher.IsBlankValue(true, "INVALID", "String"));
        Assert.True(FamilyContentHasher.IsBlankValue(true, "INVALID", "ElementId"));
        Assert.False(FamilyContentHasher.IsBlankValue(true, "UNSUPPORTED", "String"));
        Assert.True(FamilyContentHasher.IsBlankValue(true, "UNSUPPORTED", "None"));
        Assert.True(FamilyContentHasher.IsBlankValue(true, "READERROR", "String"));
    }

    [Fact]
    public void ComputeForSystem_SameCategoryIdDifferentDisplayName_SameHash()
    {
        var ru = CreateSystemSnapshot(
            categoryName: "Трубы",
            categoryId: -2008044,
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("D", "Double", true, "25", 25.0, null)])]);
        var en = CreateSystemSnapshot(
            categoryName: "Pipes",
            categoryId: -2008044,
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("D", "Double", true, "25", 25.0, null)])]);

        var hashRu = _hasher.ComputeForSystem(ru);
        var hashEn = _hasher.ComputeForSystem(en);

        Assert.NotNull(hashRu);
        Assert.NotNull(hashEn);
        Assert.Equal(hashRu!.HexString, hashEn!.HexString);
    }

    [Fact]
    public void ComputeForSystem_CompoundLayerMaterialChange_ShiftsHash()
    {
        var brick = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Wall", [],
                Structure: new CompoundStructureSnapshot(0, 1,
                [
                    new CompoundLayerSnapshot(1, 0.5, "Brick", false),
                    new CompoundLayerSnapshot(5, 0.1, "Gypsum", false),
                ])),
        ]);
        var concrete = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Wall", [],
                Structure: new CompoundStructureSnapshot(0, 1,
                [
                    new CompoundLayerSnapshot(1, 0.5, "Concrete", false),
                    new CompoundLayerSnapshot(5, 0.1, "Gypsum", false),
                ])),
        ]);

        var hashBrick = _hasher.ComputeForSystem(brick);
        var hashConcrete = _hasher.ComputeForSystem(concrete);

        Assert.NotNull(hashBrick);
        Assert.NotNull(hashConcrete);
        Assert.NotEqual(hashBrick!.HexString, hashConcrete!.HexString);
    }

    [Fact]
    public void ComputeForSystem_CompoundLayerOrderChange_ShiftsHash()
    {
        var ab = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Wall", [],
                Structure: new CompoundStructureSnapshot(0, 0,
                [
                    new CompoundLayerSnapshot(1, 0.5, "A", false),
                    new CompoundLayerSnapshot(5, 0.1, "B", false),
                ])),
        ]);
        var ba = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Wall", [],
                Structure: new CompoundStructureSnapshot(0, 0,
                [
                    new CompoundLayerSnapshot(5, 0.1, "B", false),
                    new CompoundLayerSnapshot(1, 0.5, "A", false),
                ])),
        ]);

        var hashAb = _hasher.ComputeForSystem(ab);
        var hashBa = _hasher.ComputeForSystem(ba);

        Assert.NotNull(hashAb);
        Assert.NotNull(hashBa);
        Assert.NotEqual(hashAb!.HexString, hashBa!.HexString);
    }

    [Fact]
    public void ComputeForSystem_RoutingRulePartChange_ShiftsHash()
    {
        var elbowA = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [],
                Routing: new RoutingPreferencesSnapshot(0,
                [
                    new RoutingRuleSnapshot(1, "ElbowA:Standard", "", []),
                ])),
        ]);
        var elbowB = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [],
                Routing: new RoutingPreferencesSnapshot(0,
                [
                    new RoutingRuleSnapshot(1, "ElbowB:Standard", "", []),
                ])),
        ]);

        var hashA = _hasher.ComputeForSystem(elbowA);
        var hashB = _hasher.ComputeForSystem(elbowB);

        Assert.NotNull(hashA);
        Assert.NotNull(hashB);
        Assert.NotEqual(hashA!.HexString, hashB!.HexString);
    }

    [Fact]
    public void ComputeForSystem_RoutingRuleOrderChange_ShiftsHash()
    {
        // First matching rule wins — order is content, never sorted.
        var first = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [],
                Routing: new RoutingPreferencesSnapshot(0,
                [
                    new RoutingRuleSnapshot(0, "SegA:Standard", "", []),
                    new RoutingRuleSnapshot(0, "SegB:Standard", "", []),
                ])),
        ]);
        var swapped = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [],
                Routing: new RoutingPreferencesSnapshot(0,
                [
                    new RoutingRuleSnapshot(0, "SegB:Standard", "", []),
                    new RoutingRuleSnapshot(0, "SegA:Standard", "", []),
                ])),
        ]);

        var hashFirst = _hasher.ComputeForSystem(first);
        var hashSwapped = _hasher.ComputeForSystem(swapped);

        Assert.NotNull(hashFirst);
        Assert.NotNull(hashSwapped);
        Assert.NotEqual(hashFirst!.HexString, hashSwapped!.HexString);
    }

    [Fact]
    public void ComputeForSystem_RoutingCriterionChange_ShiftsHash()
    {
        var small = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [],
                Routing: new RoutingPreferencesSnapshot(0,
                [
                    new RoutingRuleSnapshot(1, "Elbow:Std", "",
                        [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.0, 0.1)]),
                ])),
        ]);
        var large = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [],
                Routing: new RoutingPreferencesSnapshot(0,
                [
                    new RoutingRuleSnapshot(1, "Elbow:Std", "",
                        [new RoutingCriterionSnapshot("PrimarySizeCriterion", 0.0, 0.2)]),
                ])),
        ]);

        var hashSmall = _hasher.ComputeForSystem(small);
        var hashLarge = _hasher.ComputeForSystem(large);

        Assert.NotNull(hashSmall);
        Assert.NotNull(hashLarge);
        Assert.NotEqual(hashSmall!.HexString, hashLarge!.HexString);
    }

    [Fact]
    public void ComputeForSystem_PreferredJunctionTypeChange_ShiftsHash()
    {
        var tee = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [], Routing: new RoutingPreferencesSnapshot(0, [])),
        ]);
        var tap = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [], Routing: new RoutingPreferencesSnapshot(1, [])),
        ]);

        var hashTee = _hasher.ComputeForSystem(tee);
        var hashTap = _hasher.ComputeForSystem(tap);

        Assert.NotNull(hashTee);
        Assert.NotNull(hashTap);
        Assert.NotEqual(hashTee!.HexString, hashTap!.HexString);
    }

    [Fact]
    public void ComputeForSystem_NullPartRule_DiffersFromNamedRule()
    {
        // InvalidElementId ("no part allowed") is real content — distinct
        // from any named part.
        var noPart = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [],
                Routing: new RoutingPreferencesSnapshot(0,
                [
                    new RoutingRuleSnapshot(1, null, "", []),
                ])),
        ]);
        var named = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [],
                Routing: new RoutingPreferencesSnapshot(0,
                [
                    new RoutingRuleSnapshot(1, "Elbow:Std", "", []),
                ])),
        ]);

        var hashNoPart = _hasher.ComputeForSystem(noPart);
        var hashNamed = _hasher.ComputeForSystem(named);

        Assert.NotNull(hashNoPart);
        Assert.NotNull(hashNamed);
        Assert.NotEqual(hashNoPart!.HexString, hashNamed!.HexString);
    }

    [Fact]
    public void ComputeForSystem_FamilyKey_IsHashContent()
    {
        // ADR-064/065 (FHV4): the locale-invariant family key is identity —
        // same type name in two families must hash differently.
        var withFittings = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Стандарт", [], FamilyKey: SystemFamilyKeys.ConduitWithFittings),
        ]);
        var withoutFittings = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Стандарт", [], FamilyKey: SystemFamilyKeys.ConduitWithoutFittings),
        ]);

        var hash1 = _hasher.ComputeForSystem(withFittings);
        var hash2 = _hasher.ComputeForSystem(withoutFittings);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1!.HexString, hash2!.HexString);
    }

    [Fact]
    public void ComputeForSystem_StructExtras_ShiftHash()
    {
        // #179 (FHV4): StructuralMaterialIndex/EndCap/OpeningWrapping and
        // per-layer LayerCapFlag/ParticipatesInWrapping are hash content.
        var baseline = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Wall", [],
                Structure: new CompoundStructureSnapshot(1, 1,
                    [new CompoundLayerSnapshot(1, 0.5, "Concrete", false, false, false)],
                    StructuralMaterialIndex: 0, EndCap: 1, OpeningWrapping: 2)),
        ]);
        var changed = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Wall", [],
                Structure: new CompoundStructureSnapshot(1, 1,
                    [new CompoundLayerSnapshot(1, 0.5, "Concrete", false, true, true)],
                    StructuralMaterialIndex: 1, EndCap: 2, OpeningWrapping: 3)),
        ]);

        var hash1 = _hasher.ComputeForSystem(baseline);
        var hash2 = _hasher.ComputeForSystem(changed);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1!.HexString, hash2!.HexString);
    }

    [Fact]
    public void ComputeForSystem_StairsSubtypes_ShiftHash()
    {
        // #184 (FHV4): subtype references are identity — changing the run
        // type in the reference changes the hash.
        var baseline = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Stair", [],
                Stairs: new StairsSubtypesSnapshot("Run A", "Landing A", null, null, null, "Cut A")),
        ]);
        var changed = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Stair", [],
                Stairs: new StairsSubtypesSnapshot("Run B", "Landing A", null, null, null, "Cut A")),
        ]);

        var hash1 = _hasher.ComputeForSystem(baseline);
        var hash2 = _hasher.ComputeForSystem(changed);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1!.HexString, hash2!.HexString);
    }

    [Fact]
    public void ComputeForSystem_RailingStructure_ShiftHash()
    {
        var balusters = new RailingBalusterSnapshot(0.5, 0, 0, ["Bal:Std"], false, 0, null);
        var baseline = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Railing", [],
                Railing: new RailingStructureSnapshot("TopRail A", 0.9, null, null, null, null,
                    null, null, null, null,
                    [new RailingRailSnapshot("Rail 1", 0.5, 0.0, "Profile:Rect", "Steel")],
                    balusters)),
        ]);
        var changed = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Railing", [],
                Railing: new RailingStructureSnapshot("TopRail A", 1.0, null, null, null, null,
                    null, null, null, null,
                    [new RailingRailSnapshot("Rail 1", 0.5, 0.0, "Profile:Rect", "Steel")],
                    balusters)),
        ]);

        var hash1 = _hasher.ComputeForSystem(baseline);
        var hash2 = _hasher.ComputeForSystem(changed);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1!.HexString, hash2!.HexString);
    }

    [Fact]
    public void ComputeForSystem_SegmentSizeTables_ShiftHash()
    {
        var baseline = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [],
                Segments:
                [
                    new SegmentSnapshot("Steel", "Steel", null, 0.00015,
                        [new SegmentSizeSnapshot(0.05, 0.04, 0.05, true, true)]),
                ]),
        ]);
        var changed = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Pipe", [],
                Segments:
                [
                    new SegmentSnapshot("Steel", "Steel", null, 0.00015,
                        [new SegmentSizeSnapshot(0.06, 0.04, 0.06, true, true)]),
                ]),
        ]);

        var hash1 = _hasher.ComputeForSystem(baseline);
        var hash2 = _hasher.ComputeForSystem(changed);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1!.HexString, hash2!.HexString);
    }

    [Fact]
    public void ComputeForSystem_OptionalSections_NullVsPresent_Differ()
    {
        // A type without the FHV4 sections must not collide with the same
        // type carrying them (section marker '-' vs real content).
        var bare = CreateSystemSnapshot(types: [new SystemTypeSnapshot("Type", [])]);
        var enriched = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Type", [], FamilyKey: SystemFamilyKeys.SingleFamily),
        ]);

        var hash1 = _hasher.ComputeForSystem(bare);
        var hash2 = _hasher.ComputeForSystem(enriched);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1!.HexString, hash2!.HexString);
    }

    [Fact]
    public void ComputeForSystem_WireSettings_ShiftHash()
    {
        // FHV5: the wire settings graph is identity — changing the material
        // of a wire type in the reference changes the hash (manual test
        // 2026-08-04: the change was invisible to FHV4).
        var baseline = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Wire", [],
                Wire: new WireSettingsSnapshot("Медь", "60°C", "ПВХ", "2.5", "Steel", 1.0, true)),
        ]);
        var changed = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Wire", [],
                Wire: new WireSettingsSnapshot("Алюминий", "60°C", "ПВХ", "2.5", "Steel", 1.0, true)),
        ]);

        var hash1 = _hasher.ComputeForSystem(baseline);
        var hash2 = _hasher.ComputeForSystem(changed);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1!.HexString, hash2!.HexString);
    }

    [Fact]
    public void ComputeForSystem_SameNamedTypes_ExtractionOrderDoesNotShiftHash()
    {
        // Stress test 2026-08-05 (conduit «Короб» bug): both conduit
        // families name their type identically — the FHV6 canon must tie-
        // break by family identity, so the source project and the staged
        // mini-project (different ElementId/extraction order) produce the
        // SAME hash for identical content → «Дубликат», not «Существующая».
        var withFittings = new SystemTypeSnapshot("Короб",
            [new SystemParameterValue("P", "Double", true, "1", 1.0, null)],
            FamilyKey: SystemFamilyKeys.ConduitWithFittings,
            FamilyName: "Conduit with Fittings");
        var withoutFittings = new SystemTypeSnapshot("Короб",
            [new SystemParameterValue("P", "Double", true, "2", 2.0, null)],
            FamilyKey: SystemFamilyKeys.ConduitWithoutFittings,
            FamilyName: "Conduit without Fittings");

        var hash1 = _hasher.ComputeForSystem(CreateSystemSnapshot(types: [withoutFittings, withFittings]));
        var hash2 = _hasher.ComputeForSystem(CreateSystemSnapshot(types: [withFittings, withoutFittings]));

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.Equal(hash1!.HexString, hash2!.HexString);
    }

    [Fact]
    public void ComputeForSystem_Fhv8GoldenCanon_IsStable()
    {
        // Golden: a FIXED snapshot must always produce this exact hash — any
        // drift in the FHV8 canon (escaping, culture, ordering, section
        // layout, WIRE fields, duct FAMKEY) fails loudly here instead of
        // silently re-flagging every field catalog. When the canon changes ON
        // PURPOSE, bump FamilyContentHashFormat.CurrentVersion and update
        // the golden in the same commit.
        var snapshot = CreateSystemSnapshot(types:
        [
            new SystemTypeSnapshot("Wire", [new SystemParameterValue("Diameter", "Double", true, "2.5", 2.5, null)],
                Wire: new WireSettingsSnapshot("Медь", "60°C", "ПВХ", "2.5", "Steel", 1.0, true)),
        ]);

        var hash = _hasher.ComputeForSystem(snapshot);

        Assert.NotNull(hash);
        Assert.Equal(FamilyContentHashFormat.CurrentVersion, hash!.FormatVersion);
        Assert.Equal("2C3DE661B90241DE85A278EEB3060A3D36F4C0450FEC39DE6655369862386877", hash.HexString);
    }

    [Fact]
    public void ComputeForLoadable_Fhv12GoldenCanon_IsStable()
    {
        // Golden: a FIXED loadable snapshot must always produce this exact
        // hash — any drift in the FHV12 loadable canon (escaping, culture,
        // ordering, section layout, NESTEDHASH pairs, PHANTOM values, DEF
        // wiring, LOOKUP section) fails loudly here. When the canon changes
        // ON PURPOSE, bump FamilyContentHashFormat.CurrentVersion and update
        // the golden in the same commit.
        var snapshot = new FamilySnapshot(
            FamilyName: "GoldenFamily",
            Category: "Pipe Fittings",
            Parameters:
            [
                new FamilyParameterInfo(
                    "Diameter", "Double", "PG_GEOMETRY", false, false, null, false, false, null, "ALL_MODEL_TYPE_NAME"),
            ],
            Types:
            [
                new FamilyTypeSnapshot("DN50",
                    [new FamilyParameterValue("Diameter", "Double", true, "50", 50.0, null)]),
            ],
            Geometry: new GeometryMetrics(1,
                [new FormMetrics("Extrusion", true, 0.5, 6, 9, "Pipes", 1.25,
                    new BoundingBoxSnapshot(0, 0, 0, 1, 1, 1))]),
            SharedNestedFamilyNames: ["Flange"],
            CategoryId: -2008049,
            NonSharedNestedFamilyNames: ["PrivatePart"],
            SharedNestedContentHashes: [new NestedContentHash("Flange", new string('A', 64))],
            PhantomTypeValues: [new FamilyParameterValue("Модель", "String", true, "M-1", null, null)]);

        var hash = _hasher.ComputeForLoadable(snapshot);

        Assert.NotNull(hash);
        Assert.Equal(FamilyContentHashFormat.CurrentVersion, hash!.FormatVersion);
        Assert.Equal("4859FDD8C16DDFBACA96163554A8A2E9CA063083BDF2BF748C1457A8E4365D3E", hash.HexString);
    }

    [Fact]
    public void ComputeForLoadable_LookupTableContent_ShiftsHash()
    {
        // FHV11 (Issue #238): an edit of a lookup table's VALUES must shift
        // the hash — the pre-FHV11 bug was a false Duplicate on a table-only
        // edit.
        var baseSnapshot = CreateLookupSnapshot(csv: ",Dn##length##millimeters\n50,50\n");
        var editedSnapshot = CreateLookupSnapshot(csv: ",Dn##length##millimeters\n50,51\n");

        var baseHash = _hasher.ComputeForLoadable(baseSnapshot);
        var editedHash = _hasher.ComputeForLoadable(editedSnapshot);

        Assert.NotNull(baseHash);
        Assert.NotNull(editedHash);
        Assert.NotEqual(baseHash!.HexString, editedHash!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_NoLookupTables_SectionOmitted()
    {
        // Table-less families get NO LOOKUP section: null and empty list are
        // the same hash (and differ from any snapshot WITH a table).
        var withoutTables = CreateLookupSnapshot(csv: null);
        var emptyTables = CreateLookupSnapshot(csv: null) with { LookupTables = [] };
        var withTable = CreateLookupSnapshot(csv: ",Dn##length##millimeters\n50,50\n");

        var hashWithout = _hasher.ComputeForLoadable(withoutTables);
        var hashEmpty = _hasher.ComputeForLoadable(emptyTables);
        var hashWith = _hasher.ComputeForLoadable(withTable);

        Assert.NotNull(hashWithout);
        Assert.NotNull(hashEmpty);
        Assert.NotNull(hashWith);
        Assert.Equal(hashWithout!.HexString, hashEmpty!.HexString);
        Assert.NotEqual(hashWithout.HexString, hashWith!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_LookupTableOrder_IsDeterministic()
    {
        // Multiple tables per family are normal (MEP fittings): the section
        // sorts tables by name (Ordinal), so extraction order never leaks
        // into the hash.
        var tableA = new LookupTableSnapshot("A_table", ",X##number##general\n1\n");
        var tableB = new LookupTableSnapshot("B_table", ",Y##number##general\n2\n");
        var ordered = CreateLookupSnapshot(csv: null) with { LookupTables = [tableA, tableB] };
        var reversed = CreateLookupSnapshot(csv: null) with { LookupTables = [tableB, tableA] };

        var hashOrdered = _hasher.ComputeForLoadable(ordered);
        var hashReversed = _hasher.ComputeForLoadable(reversed);

        Assert.NotNull(hashOrdered);
        Assert.NotNull(hashReversed);
        Assert.Equal(hashOrdered!.HexString, hashReversed!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_LookupTableNameWithDelimiter_IsEscaped()
    {
        // The canonical format escapes '|' (and '%') — a table named with a
        // delimiter must not collide with a structurally shifted name.
        var withPipe = CreateLookupSnapshot(csv: null) with
        {
            LookupTables = [new LookupTableSnapshot("A|B", ",X##number##general\n1\n")],
        };
        var withoutPipe = CreateLookupSnapshot(csv: null) with
        {
            LookupTables = [new LookupTableSnapshot("A", "|B,X##number##general\n1\n")],
        };

        var hashPipe = _hasher.ComputeForLoadable(withPipe);
        var hashNoPipe = _hasher.ComputeForLoadable(withoutPipe);

        Assert.NotNull(hashPipe);
        Assert.NotNull(hashNoPipe);
        Assert.NotEqual(hashPipe!.HexString, hashNoPipe!.HexString);
    }

    private static FamilySnapshot CreateLookupSnapshot(string? csv)
    {
        return new FamilySnapshot(
            FamilyName: "LookupFamily",
            Category: "Pipe Fittings",
            Parameters:
            [
                new FamilyParameterInfo(
                    "Dn", "Double", "PG_GEOMETRY", false, false, "size_lookup(Lookup, \"Dn\", \"\", Dn)", true, false, null, null),
            ],
            Types:
            [
                new FamilyTypeSnapshot("DN50",
                    [new FamilyParameterValue("Dn", "Double", true, "50", 50.0, null)]),
            ],
            Geometry: new GeometryMetrics(0, []),
            SharedNestedFamilyNames: [],
            CategoryId: -2008049,
            LookupTables: csv is null ? null : [new LookupTableSnapshot("Lookup", csv)]);
    }

    private static FamilySnapshot CreateVerificationBaseline()
    {
        return new FamilySnapshot(
            FamilyName: "Nut",
            Category: "Pipe Accessories",
            Parameters:
            [
                new FamilyParameterInfo("DN", "Double", "autodesk.parameter.group:constraints-1.0.0",
                    false, false, null, false, false, null, null),
                new FamilyParameterInfo("ADSK_Материал обозначение", "String", "autodesk.parameter.group:materials-1.0.0",
                    true, true, null, false, false, "dbe7f282-3606-44cf-ac51-0f274c34c07b", null),
            ],
            Types:
            [
                new FamilyTypeSnapshot(" ",
                    [new FamilyParameterValue("DN", "Double", true, "50", 50.0, null)]),
            ],
            Geometry: new GeometryMetrics(1,
                [new FormMetrics("Revolution", true, 0.5, 6, 9, null, 1.25,
                    new BoundingBoxSnapshot(0, 0, 0, 1, 1, 1))],
                SymbolicCurveCount: 4, ModelCurveCount: 6, ReferencePlaneCount: 8, DimensionCount: 3,
                TotalSymbolicCurveLength: 10.5, TotalModelCurveLength: 20.25),
            SharedNestedFamilyNames: [],
            CategoryId: -2008055);
    }

    [Fact]
    public void UnifiedHash_GeometryMetricChange_Detected()
    {
        // FHV10 unified hash (probe 2026-08-12,
        // DrivenEmbeddedPollutionProbeTests): the "host-driven regen
        // pollutes the embedded document" hypothesis is DISPROVED —
        // associations live on instances in the host, the EditFamily
        // document keeps the authored state byte-for-byte. So geometry
        // metrics are hashed like any other content: a metric diff means
        // a REAL content diff (incl. #180 free-form local edits).
        var baseline = CreateVerificationBaseline();
        var metricChanged = baseline with
        {
            Geometry = baseline.Geometry with
            {
                Forms =
                [
                    new FormMetrics("Revolution", true, 1.75, 6, 9, null, 4.9,
                        new BoundingBoxSnapshot(-1, -1, 0, 2, 2, 1)),
                ],
                TotalSymbolicCurveLength = 33.3,
                TotalModelCurveLength = 77.7,
            },
        };

        var verifyA = _hasher.ComputeForLoadable(baseline);
        var verifyB = _hasher.ComputeForLoadable(metricChanged);
        Assert.NotNull(verifyA);
        Assert.NotNull(verifyB);
        Assert.NotEqual(verifyA!.HexString, verifyB!.HexString);

        var identityA = _hasher.ComputeForLoadable(baseline);
        var identityB = _hasher.ComputeForLoadable(metricChanged);
        Assert.NotEqual(identityA!.HexString, identityB!.HexString);
    }

    [Fact]
    public void UnifiedHash_ParameterGroupChange_NeverShiftsHash()
    {
        // FHV10 (owner decision 2026-08-12): parameter groups are NOT
        // hashed at all — they are the only content field a reload merge
        // physically cannot transfer (probe-proven twice: UI/plain-merge
        // 2026-08-11, poke + doc-to-doc 2026-08-12), so versioning them
        // forked one identification into two divergent grades. The single
        // unified hash now ignores a regroup in EVERY context: import
        // dedup, versioning, embedded verification. Product tradeoff
        // (accepted): a group-only edit no longer version-bumps.
        var baseline = CreateVerificationBaseline();
        var regrouped = baseline with
        {
            Parameters =
            [
                baseline.Parameters[0],
                baseline.Parameters[1] with { ParameterGroup = "autodesk.parameter.group:identityData-1.0.0" },
            ],
        };

        var hashA = _hasher.ComputeForLoadable(baseline);
        var hashB = _hasher.ComputeForLoadable(regrouped);
        Assert.NotNull(hashA);
        Assert.NotNull(hashB);
        Assert.Equal(hashA!.HexString, hashB!.HexString);
    }

    [Fact]
    public void UnifiedHash_FormulaChange_Detected()
    {
        var baseline = CreateVerificationBaseline();
        var formulaChanged = baseline with
        {
            Parameters =
            [
                baseline.Parameters[0] with { Formula = "size_lookup(T, \"N\", \"?\", DN)", IsDeterminedByFormula = true },
                baseline.Parameters[1],
            ],
        };

        var verifyA = _hasher.ComputeForLoadable(baseline);
        var verifyB = _hasher.ComputeForLoadable(formulaChanged);
        Assert.NotEqual(verifyA!.HexString, verifyB!.HexString);
    }

    [Fact]
    public void UnifiedHash_TopologyChange_Detected()
    {
        // Face/edge counts and form kinds are hashed — they are topology,
        // not size.
        var baseline = CreateVerificationBaseline();
        var topologyChanged = baseline with
        {
            Geometry = baseline.Geometry with
            {
                Forms =
                [
                    new FormMetrics("Revolution", true, 0.5, 12, 18, null, 1.25,
                        new BoundingBoxSnapshot(0, 0, 0, 1, 1, 1)),
                ],
            },
        };

        var verifyA = _hasher.ComputeForLoadable(baseline);
        var verifyB = _hasher.ComputeForLoadable(topologyChanged);
        Assert.NotEqual(verifyA!.HexString, verifyB!.HexString);
    }

    [Fact]
    public void UnifiedHash_TypeValueChange_Detected()
    {
        // Type values are NOT host-drivable for shared nested families
        // (only instance parameters can be associated in the host).
        var baseline = CreateVerificationBaseline();
        var valueChanged = baseline with
        {
            Types =
            [
                new FamilyTypeSnapshot(" ",
                    [new FamilyParameterValue("DN", "Double", true, "65", 65.0, null)]),
            ],
        };

        var verifyA = _hasher.ComputeForLoadable(baseline);
        var verifyB = _hasher.ComputeForLoadable(valueChanged);
        Assert.NotEqual(verifyA!.HexString, verifyB!.HexString);
    }

    [Fact]
    public void UnifiedHash_NullSnapshot_ReturnsNull()
    {
        Assert.Null(_hasher.ComputeForLoadable(null!));
    }

    [Fact]
    public void UnifiedHash_MetricDifferentForms_Detected()
    {
        // Forms with the same topology but different metrics are a REAL
        // content diff (embedded pollution disproved — see
        // DrivenEmbeddedPollutionProbeTests) and must be detected. The
        // sort keys stay topology-based, so identical twins still cannot
        // false-fail on extraction order (validator H1).
        var formA = new FormMetrics("Extrusion", true, 0.5, 6, 9, null, 1.25,
            new BoundingBoxSnapshot(0, 0, 0, 1, 1, 1));
        var formB = new FormMetrics("Extrusion", true, 0.8, 6, 9, null, 1.6,
            new BoundingBoxSnapshot(0, 0, 0, 2, 2, 1));
        var baseline = CreateVerificationBaseline() with
        {
            Geometry = new GeometryMetrics(2, [formA, formB]),
        };
        var resized = baseline with
        {
            Geometry = new GeometryMetrics(2,
            [
                formB with { Volume = 0.4, SurfaceArea = 1.1, Bounds = new BoundingBoxSnapshot(0, 0, 0, 0.5, 0.5, 1) },
                formA with { Volume = 0.9, SurfaceArea = 1.8, Bounds = new BoundingBoxSnapshot(0, 0, 0, 1.5, 1.5, 1) },
            ]),
        };

        var verifyA = _hasher.ComputeForLoadable(baseline);
        var verifyB = _hasher.ComputeForLoadable(resized);
        Assert.NotEqual(verifyA!.HexString, verifyB!.HexString);
    }

    private static FamilyParameterValue PhantomValue(string name, string? text, double? number = null)
    {
        return new FamilyParameterValue(
            ParameterName: name,
            StorageType: number.HasValue ? "Double" : "String",
            HasValue: true,
            ValueText: text,
            ValueNumber: number,
            ResolvedElementName: null);
    }

    [Fact]
    public void ComputeForLoadable_PhantomValuesPresent_ShiftHash()
    {
        // FHV9 (#209 stress test 2026-08-12): typeless-family values are
        // content — without the PHANTOM section a «Модель» edit on a
        // typeless family never shifted the hash (false Duplicate).
        var baseline = CreateLoadableSnapshot();
        var withPhantom = baseline with
        {
            PhantomTypeValues = [PhantomValue("Модель", "M-100")],
        };

        var hash1 = _hasher.ComputeForLoadable(baseline);
        var hash2 = _hasher.ComputeForLoadable(withPhantom);

        Assert.NotNull(hash1);
        Assert.NotNull(hash2);
        Assert.NotEqual(hash1!.HexString, hash2!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_PhantomValueChange_ShiftsHash()
    {
        var v1 = CreateLoadableSnapshot() with
        {
            PhantomTypeValues = [PhantomValue("Модель", "M-100")],
        };
        var v2 = CreateLoadableSnapshot() with
        {
            PhantomTypeValues = [PhantomValue("Модель", "M-200")],
        };

        var hash1 = _hasher.ComputeForLoadable(v1);
        var hash2 = _hasher.ComputeForLoadable(v2);

        Assert.NotEqual(hash1!.HexString, hash2!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_PhantomValues_OrderIndependent()
    {
        var a = CreateLoadableSnapshot() with
        {
            PhantomTypeValues = [PhantomValue("A", "1"), PhantomValue("B", "2")],
        };
        var b = CreateLoadableSnapshot() with
        {
            PhantomTypeValues = [PhantomValue("B", "2"), PhantomValue("A", "1")],
        };

        Assert.Equal(
            _hasher.ComputeForLoadable(a)!.HexString,
            _hasher.ComputeForLoadable(b)!.HexString);
    }

    [Fact]
    public void ComputeForLoadable_PhantomBlankValues_Skipped()
    {
        var baseline = CreateLoadableSnapshot();
        var blankOnly = baseline with
        {
            PhantomTypeValues =
            [
                new FamilyParameterValue("EmptyText", "String", false, null, null, null),
                new FamilyParameterValue("EmptyString", "String", true, string.Empty, null, null),
            ],
        };

        Assert.Equal(
            _hasher.ComputeForLoadable(baseline)!.HexString,
            _hasher.ComputeForLoadable(blankOnly)!.HexString);
    }

    [Fact]
    public void UnifiedHash_PhantomValues_Detected()
    {
        // Phantom values are hashed (FHV9+/FHV10): the embedded document
        // keeps the authored phantom values even under a host drive
        // (probe), and the two extraction contexts (EditFamily current
        // type vs raw-open synthesized type) read the same defaults
        // (ADR-068 §6 probe). A phantom diff = real diff.
        var baseline = CreateVerificationBaseline();
        var withPhantom = baseline with
        {
            PhantomTypeValues = [PhantomValue("Модель", "M-100")],
        };

        var verifyA = _hasher.ComputeForLoadable(baseline);
        var verifyB = _hasher.ComputeForLoadable(withPhantom);

        Assert.NotEqual(verifyA!.HexString, verifyB!.HexString);
    }
}
