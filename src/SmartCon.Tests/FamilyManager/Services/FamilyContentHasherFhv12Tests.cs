using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// FHV12 (Issue #249, Phase 3): the DEF section (definition wiring) and
/// the strengthened GEOM section (centroid, face-kind histogram, edge
/// lengths, resolved RGBA, visibility flags, nested instance placements)
/// participate in the identity hash deterministically.
/// </summary>
public sealed class FamilyContentHasherFhv12Tests
{
    private readonly FamilyContentHasher _hasher = new();

    private static FamilySnapshot CreateBaseSnapshot() => new(
        FamilyName: "Fam",
        Category: "Pipe Fittings",
        Parameters: [],
        Types: [],
        Geometry: new GeometryMetrics(0, []),
        SharedNestedFamilyNames: [],
        CategoryId: -2008049);

    private static DefinitionMetrics CreateDefinitions() => new(
        Forms:
        [
            new FormDefinitionSnapshot(
                "Extrusion", true, null,
                VisibilityParameterName: "ShowBody",
                MaterialParameterName: "BodyMaterial",
                ExtrusionStartParameterName: "StartOff",
                ExtrusionEndParameterName: null,
                ExtrusionStartOffset: 0.1,
                ExtrusionEndOffset: 0.5),
        ],
        Dimensions:
        [
            new DimensionDefinitionSnapshot("Diameter", "Размерный|стиль", 1),
        ],
        ReferencePlanes:
        [
            new ReferencePlaneDefinitionSnapshot("Центр (влево/вправо)", true),
            new ReferencePlaneDefinitionSnapshot("Front", false),
        ]);

    private static GeometryMetrics CreateRichGeometry() => new(
        TotalFormCount: 1,
        Forms:
        [
            new FormMetrics("Extrusion", true, 0.5, 6, 9, null, 2.5,
                new BoundingBoxSnapshot(0, 0, 0, 1, 1, 1),
                Centroid: new PointSnapshot(0.5, 0.5, 0.5),
                FaceTypes: [new FaceTypeCount("PlanarFace", 6)],
                TotalEdgeLength: 12.0,
                MaterialColor: new MaterialColorSnapshot(255, 128, 0, 255),
                Visibility: new FormVisibilitySnapshot(1, true)),
        ],
        NestedInstances:
        [
            new NestedInstanceSnapshot(
                "Вложенное", "Тип|А",
                0.1, 0.2, 0.3,
                1, 0, 0,
                0, 1, 0,
                0, 0, 1,
                1),
        ]);

    [Fact]
    public void DefSection_ParticipatesInHash()
    {
        var without = CreateBaseSnapshot();
        var with = CreateBaseSnapshot() with { Definitions = CreateDefinitions() };

        Assert.NotEqual(
            _hasher.ComputeForLoadable(without)!.HexString,
            _hasher.ComputeForLoadable(with)!.HexString);

        var canonical = FamilyContentHasher.BuildLoadableCanonicalString(with);
        Assert.Contains("DEF|1|", canonical);
        Assert.Contains("DIMS|", canonical);
        Assert.Contains("PLANES|", canonical);
        Assert.Contains("ShowBody", canonical);
        Assert.Contains("Размерный%7Cстиль", canonical);
    }

    [Fact]
    public void DefSection_BindingRebind_ShiftsHash()
    {
        // The pre-FHV12 blind spot: re-binding the form's visibility to a
        // DIFFERENT parameter — every current value stays identical.
        var a = CreateBaseSnapshot() with { Definitions = CreateDefinitions() };
        var b = CreateBaseSnapshot() with
        {
            Definitions = CreateDefinitions() with
            {
                Forms = [CreateDefinitions().Forms[0] with { VisibilityParameterName = "OtherParam" }],
            },
        };

        Assert.NotEqual(
            _hasher.ComputeForLoadable(a)!.HexString,
            _hasher.ComputeForLoadable(b)!.HexString);
    }

    [Fact]
    public void DefSection_DimensionLabelRebind_ShiftsHash()
    {
        var a = CreateBaseSnapshot() with { Definitions = CreateDefinitions() };
        var b = CreateBaseSnapshot() with
        {
            Definitions = CreateDefinitions() with
            {
                Dimensions = [CreateDefinitions().Dimensions[0] with { LabelParameterName = "OtherDiameter" }],
            },
        };

        Assert.NotEqual(
            _hasher.ComputeForLoadable(a)!.HexString,
            _hasher.ComputeForLoadable(b)!.HexString);
    }

    [Fact]
    public void DefSection_NullDefinitions_EmitsEmptySectionDeterministically()
    {
        var a = CreateBaseSnapshot();
        var b = CreateBaseSnapshot();

        var canonical = FamilyContentHasher.BuildLoadableCanonicalString(a);
        Assert.Contains("DEF|0|DIMS|PLANES|", canonical);
        Assert.Equal(
            _hasher.ComputeForLoadable(a)!.HexString,
            _hasher.ComputeForLoadable(b)!.HexString);
    }

    [Fact]
    public void GeomSection_StrengthenedFields_ParticipateInHash()
    {
        var plain = CreateBaseSnapshot();
        var rich = CreateBaseSnapshot() with { Geometry = CreateRichGeometry() };

        var canonical = FamilyContentHasher.BuildLoadableCanonicalString(rich);
        Assert.Contains("0.5,0.5,0.5", canonical);          // centroid
        Assert.Contains("PlanarFace:6", canonical);         // face histogram
        Assert.Contains("255,128,0,255", canonical);        // material RGBA
        Assert.Contains("NESTEDINST|", canonical);          // nested placements
        Assert.Contains("Вложенное", canonical);
        Assert.NotEqual(
            _hasher.ComputeForLoadable(plain)!.HexString,
            _hasher.ComputeForLoadable(rich)!.HexString);
    }

    [Fact]
    public void GeomSection_NestedInstanceMove_ShiftsHash()
    {
        // The pre-FHV12 blind spot: moving a nested part inside the family
        // (names unchanged) never shifted the hash.
        var a = CreateBaseSnapshot() with { Geometry = CreateRichGeometry() };
        var moved = CreateRichGeometry() with
        {
            NestedInstances = [CreateRichGeometry().NestedInstances![0] with { OriginX = 0.9 }],
        };
        var b = CreateBaseSnapshot() with { Geometry = moved };

        Assert.NotEqual(
            _hasher.ComputeForLoadable(a)!.HexString,
            _hasher.ComputeForLoadable(b)!.HexString);
    }

    [Fact]
    public void GeomSection_IdenticalPrefixForms_OrderIndependent()
    {
        // Two forms identical on the pre-FHV12 metric prefix but with
        // different materials: the FHV12 tie-break chain must order them
        // deterministically regardless of the extraction order.
        var formA = new FormMetrics("Extrusion", true, 0.5, 6, 9, null, 2.5,
            MaterialColor: new MaterialColorSnapshot(255, 0, 0, 255));
        var formB = new FormMetrics("Extrusion", true, 0.5, 6, 9, null, 2.5,
            MaterialColor: new MaterialColorSnapshot(0, 0, 255, 255));

        var snapshotAB = CreateBaseSnapshot() with
        {
            Geometry = new GeometryMetrics(2, [formA, formB]),
        };
        var snapshotBA = CreateBaseSnapshot() with
        {
            Geometry = new GeometryMetrics(2, [formB, formA]),
        };

        Assert.Equal(
            _hasher.ComputeForLoadable(snapshotAB)!.HexString,
            _hasher.ComputeForLoadable(snapshotBA)!.HexString);
    }

    [Fact]
    public void MetaSection_IsFhv13()
    {
        var canonical = FamilyContentHasher.BuildLoadableCanonicalString(CreateBaseSnapshot());
        Assert.StartsWith("FHV13|LOADABLE|", canonical);
    }

    [Fact]
    public void DefSection_UnlabeledDimensions_AreExcluded()
    {
        // FHV13 (#249 follow-up): an unlabeled dimension is not parameter
        // wiring — Revit creates automatic sketch dimensions on every
        // sketch, and listing them made every 3D edit fire the DEF section.
        var snapshot = CreateBaseSnapshot() with
        {
            Definitions = CreateDefinitions() with
            {
                Dimensions =
                [
                    CreateDefinitions().Dimensions[0],                              // labeled "Diameter"
                    new DimensionDefinitionSnapshot(null, "Линейный", 0),           // automatic junk
                ],
            },
        };

        var canonical = FamilyContentHasher.BuildLoadableCanonicalString(snapshot);
        Assert.Contains("DIMS|Diameter|", canonical);
        Assert.DoesNotContain("Линейный", canonical);
    }
}
