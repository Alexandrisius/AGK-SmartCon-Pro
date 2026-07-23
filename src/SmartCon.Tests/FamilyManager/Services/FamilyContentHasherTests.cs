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
    public void ComputeForLoadable_CategoryIdAndFacts_DoNotShiftHash()
    {
        // ADR-055: CategoryId/Facts are display metadata appended to the
        // snapshot — the FHV2 canonical string is built from explicit
        // content fields only, so facts must never affect dedup identity.
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
        Assert.Equal(hash1!.HexString, hash2!.HexString);
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
            types: [new SystemTypeSnapshot("Стандартный", [new SystemParameterValue("Diameter", "Double", true, "25", 25.0, null)])]);
        var snapshot2 = CreateSystemSnapshot(
            categoryName: "Воздуховоды",
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
}
