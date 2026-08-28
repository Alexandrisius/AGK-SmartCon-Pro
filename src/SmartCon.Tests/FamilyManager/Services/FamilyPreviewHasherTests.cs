using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using Xunit;

namespace SmartCon.Tests.FamilyManager.Services;

/// <summary>
/// Issue #249, Phase 5: the VIEW3D preview hash — normalized per-type
/// GLB inputs, rename-independent, extraction-order-independent.
/// </summary>
public sealed class FamilyPreviewHasherTests
{
    private static FormMetrics Box(double origin = 0, int r = 255) => new(
        "Extrusion", true, 0.5, 6, 9, null, 2.5,
        new BoundingBoxSnapshot(origin, 0, 0, origin + 1, 1, 1),
        Centroid: new PointSnapshot(origin + 0.5, 0.5, 0.5),
        FaceTypes: [new FaceTypeCount("PlanarFace", 6)],
        TotalEdgeLength: 12.0,
        MaterialColor: new MaterialColorSnapshot(r, 128, 0, 255),
        Visibility: new FormVisibilitySnapshot(1, true));

    private static NestedInstanceSnapshot Nested(double x = 2.0) => new(
        "Вложенное", "ТипА",
        x, 3.0, 0,
        1, 0, 0, 0, 1, 0, 0, 0, 1,
        1);

    [Fact]
    public void Hash_TypeRename_SameHash()
    {
        // The type name is the asset row's key, NOT content — renaming a
        // type reuses the same pooled GLB.
        var a = new PreviewTypeSnapshot("Ду50", [Box()], [Nested()]);
        var b = new PreviewTypeSnapshot("DN50", [Box()], [Nested()]);

        Assert.Equal(
            FamilyPreviewHasher.ComputeForType(a),
            FamilyPreviewHasher.ComputeForType(b));
    }

    [Fact]
    public void Hash_CrossFamilyIdenticalContent_SameHash()
    {
        // Cross-family dedup: two families with identical preview content
        // share one pooled file (no family name in the input).
        var a = new PreviewTypeSnapshot("T", [Box()], [Nested()]);
        var b = new PreviewTypeSnapshot("T", [Box()], [Nested()]);

        Assert.Equal(
            FamilyPreviewHasher.ComputeForType(a),
            FamilyPreviewHasher.ComputeForType(b));
    }

    [Fact]
    public void Hash_FormOrderIrrelevant()
    {
        var formA = Box(r: 255);
        var formB = Box(r: 10);
        var a = new PreviewTypeSnapshot("T", [formA, formB], []);
        var b = new PreviewTypeSnapshot("T", [formB, formA], []);

        Assert.Equal(
            FamilyPreviewHasher.ComputeForType(a),
            FamilyPreviewHasher.ComputeForType(b));
    }

    [Fact]
    public void Hash_GeometryChange_DifferentHash()
    {
        var a = new PreviewTypeSnapshot("T", [Box()], [Nested(x: 2.0)]);
        var b = new PreviewTypeSnapshot("T", [Box()], [Nested(x: 9.0)]);

        Assert.NotEqual(
            FamilyPreviewHasher.ComputeForType(a),
            FamilyPreviewHasher.ComputeForType(b));
    }

    [Fact]
    public void Hash_MaterialColorChange_DifferentHash()
    {
        var a = new PreviewTypeSnapshot("T", [Box(r: 255)], []);
        var b = new PreviewTypeSnapshot("T", [Box(r: 254)], []);

        Assert.NotEqual(
            FamilyPreviewHasher.ComputeForType(a),
            FamilyPreviewHasher.ComputeForType(b));
    }

    [Fact]
    public void Hash_EmptySnapshot_Deterministic()
    {
        var a = new PreviewTypeSnapshot("T", [], []);
        var b = new PreviewTypeSnapshot("T", [], []);

        Assert.Equal(
            FamilyPreviewHasher.ComputeForType(a),
            FamilyPreviewHasher.ComputeForType(b));
        Assert.NotNull(FamilyPreviewHasher.ComputeForType(a));
    }

    [Fact]
    public void Hash_NullSnapshot_ReturnsNull()
    {
        Assert.Null(FamilyPreviewHasher.ComputeForType(null));
    }

    [Fact]
    public void Hash_FaceColorHistogramChange_DifferentHash()
    {
        // #251: painting ONE face moves it into another resolved-color
        // bucket — the histogram changes while the single form-level color
        // stays identical (the pre-FHV18 blind spot).
        var unpainted = Box() with
        {
            FaceColors = [new FaceColorCount(new MaterialColorSnapshot(255, 128, 0, 255), 6)],
        };
        var oneFacePainted = Box() with
        {
            FaceColors =
            [
                new FaceColorCount(new MaterialColorSnapshot(255, 128, 0, 255), 5),
                new FaceColorCount(new MaterialColorSnapshot(0, 0, 255, 255), 1),
            ],
        };

        Assert.NotEqual(
            FamilyPreviewHasher.ComputeForType(new PreviewTypeSnapshot("T", [unpainted], [])),
            FamilyPreviewHasher.ComputeForType(new PreviewTypeSnapshot("T", [oneFacePainted], [])));
    }

    [Fact]
    public void Hash_FaceColorHistogram_BucketOrderIrrelevant()
    {
        var a = Box() with
        {
            FaceColors =
            [
                new FaceColorCount(new MaterialColorSnapshot(255, 128, 0, 255), 5),
                new FaceColorCount(new MaterialColorSnapshot(0, 0, 255, 255), 1),
            ],
        };
        var b = Box() with
        {
            FaceColors =
            [
                new FaceColorCount(new MaterialColorSnapshot(0, 0, 255, 255), 1),
                new FaceColorCount(new MaterialColorSnapshot(255, 128, 0, 255), 5),
            ],
        };

        Assert.Equal(
            FamilyPreviewHasher.ComputeForType(new PreviewTypeSnapshot("T", [a], [])),
            FamilyPreviewHasher.ComputeForType(new PreviewTypeSnapshot("T", [b], [])));
    }

    [Fact]
    public void Hash_NestedContentMetricsChange_DifferentHash()
    {
        // #250: a geometry/material edit INSIDE the nested child — the
        // placement (identity + transform) is byte-identical, but the
        // child's own content fingerprint re-keys the pool (the pre-fix
        // blind spot: the parent's VIEW3D hash never moved).
        var contentA = new FormMetrics("NestedContent", true, 0.5, 6, 9, null, 2.5,
            Centroid: new PointSnapshot(0.5, 0.5, 0.5), TotalEdgeLength: 12.0,
            FaceColors: [new FaceColorCount(new MaterialColorSnapshot(255, 128, 0, 255), 6)]);
        var contentB = contentA with { Volume = 0.75 }; // deeper child box
        var contentC = contentA with
        {
            FaceColors =
            [
                new FaceColorCount(new MaterialColorSnapshot(255, 128, 0, 255), 5),
                new FaceColorCount(new MaterialColorSnapshot(0, 0, 255, 255), 1),
            ],
        };

        var a = new PreviewTypeSnapshot("T", [], [Nested() with { ContentMetrics = contentA }]);
        var b = new PreviewTypeSnapshot("T", [], [Nested() with { ContentMetrics = contentB }]);
        var c = new PreviewTypeSnapshot("T", [], [Nested() with { ContentMetrics = contentC }]);

        Assert.NotEqual(FamilyPreviewHasher.ComputeForType(a), FamilyPreviewHasher.ComputeForType(b));
        Assert.NotEqual(FamilyPreviewHasher.ComputeForType(a), FamilyPreviewHasher.ComputeForType(c));
    }

    [Fact]
    public void Hash_NestedContentMetricsNull_Deterministic()
    {
        // GEOM-context snapshots carry no content metrics — the hasher
        // emits a deterministic marker and never crashes.
        var a = new PreviewTypeSnapshot("T", [], [Nested()]);
        var b = new PreviewTypeSnapshot("T", [], [Nested()]);

        Assert.Equal(
            FamilyPreviewHasher.ComputeForType(a),
            FamilyPreviewHasher.ComputeForType(b));
        Assert.NotNull(FamilyPreviewHasher.ComputeForType(a));
    }

    [Fact]
    public void CanonicalString_NoElementIds_NoNames()
    {
        var canonical = FamilyPreviewHasher.BuildCanonicalString(
            new PreviewTypeSnapshot("Тип|X", [Box()], [Nested()]));

        Assert.StartsWith("VIEW3D|2|FORMS|", canonical);
        Assert.Contains("NESTED|", canonical);
        Assert.DoesNotContain("Тип|X", canonical);       // type name excluded
        Assert.Contains("Вложенное", canonical);          // nested symbol IS content
        Assert.Contains("255,128,0,255", canonical);      // resolved RGBA
    }
}
