using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// View-specific (annotation/detail) families never display in 3D views, so
/// the preview pipeline skips their per-type geometry extraction (stress test
/// 2026-09-07: ~16 s wasted on a 4-type title block with 13 nested annotation
/// instances producing 0 meshes). Contracts: the category predicate on REAL
/// categories, the <c>ExtractGeometryPerType</c> early-out, and the
/// <c>ExtractAsync</c> import-path skip. Model families keep extracting —
/// covered by <see cref="FamilyPreviewExtractionTests"/>.
/// </summary>
public sealed class FamilyPreviewCategorySkipTests : RevitApiTest
{
    private const double MmToFt = 1.0 / 304.8;

    private string? _tempDir;
    private string? _annotationPath;
    private string? _annotationPath2;
    private bool _solidSeeded;
    private List<Document>? _openDocs;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        var template = SampleFiles.FindAnnotationFamilyTemplate(Application);
        if (template is null)
        {
            Skip.Test("Annotation family template (.rft) not found — cannot seed the category-skip contracts");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConAnnoSkip_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _annotationPath = Path.Combine(_tempDir, "SmartConAnnoSkip.rfa");
        _annotationPath2 = Path.Combine(_tempDir, "SmartConAnnoSkip2.rfa");
        _openDocs = new List<Document>();
        _solidSeeded = false;

        // Two named types → the multi-type branch of ExtractGeometryPerType
        // (the expensive per-type transaction loop the skip protects). The
        // second saved copy lets the ExtractAsync test open its target file
        // while ANOTHER document of the same family is live (Revit dislikes
        // double opens of one path).
        var doc = Application.NewFamilyDocument(template);
        try
        {
            using (var tx = new Transaction(doc, "seed annotation family"))
            {
                tx.Start();
                try
                {
                    // If the template allows 3D forms, seed one — the skip
                    // must ignore even REAL geometry on a view-specific family.
                    CreateBox(doc, 100 * MmToFt);
                    _solidSeeded = true;
                }
                catch (Exception ex)
                {
                    SmartConLogger.Info(
                        $"PROBE: annotation-template extrusion rejected ({ex.GetType().Name}: {ex.Message}) — seeding types only");
                }
                doc.FamilyManager.NewType("A");
                doc.FamilyManager.NewType("B");
                tx.Commit();
            }
            doc.SaveAs(_annotationPath, new SaveAsOptions { OverwriteExistingFile = true });
            doc.SaveAs(_annotationPath2, new SaveAsOptions { OverwriteExistingFile = true });
        }
        finally
        {
            try { doc.Close(false); } catch { }
        }
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Cleanup()
    {
        try
        {
            if (_openDocs is not null)
            {
                foreach (var doc in _openDocs)
                {
                    try
                    {
                        if (doc.IsValidObject)
                        {
                            doc.Close(false);
                        }
                    }
                    catch
                    {
                        // best-effort cleanup
                    }
                }
            }
        }
        finally
        {
            try
            {
                if (_tempDir is not null && Directory.Exists(_tempDir))
                {
                    Directory.Delete(_tempDir, recursive: true);
                }
            }
            catch
            {
                // best-effort cleanup
            }
        }
    }

    [Test]
    public async Task IsViewSpecificPreviewCategory_RealCategories_MatchesAnnotationAndDetail_NotModel()
    {
        var doc = OpenSeeded(_annotationPath!);

        // The production contract: the ROOT category of an annotation family
        // document (what both skip points read via OwnerFamily.FamilyCategory).
        var root = doc.OwnerFamily?.FamilyCategory;
        SmartConLogger.Info($"PROBE: seeded annotation root category '{root?.Name}'");
        await Assert.That(root).IsNotNull();
        await Assert.That(RevitFamilyGeometryExtractor.IsViewSpecificPreviewCategory(root)).IsTrue();

        // Built-in categories present in this document's settings.
        await Assert.That(RevitFamilyGeometryExtractor.IsViewSpecificPreviewCategory(
            GetCategoryOrSkip(doc, BuiltInCategory.OST_TitleBlocks))).IsTrue();
        await Assert.That(RevitFamilyGeometryExtractor.IsViewSpecificPreviewCategory(
            GetCategoryOrSkip(doc, BuiltInCategory.OST_GenericAnnotation))).IsTrue();
        await Assert.That(RevitFamilyGeometryExtractor.IsViewSpecificPreviewCategory(
            GetCategoryOrSkip(doc, BuiltInCategory.OST_DetailComponents))).IsTrue();

        await Assert.That(RevitFamilyGeometryExtractor.IsViewSpecificPreviewCategory(
            GetCategoryOrSkip(doc, BuiltInCategory.OST_GenericModel))).IsFalse();
        await Assert.That(RevitFamilyGeometryExtractor.IsViewSpecificPreviewCategory(
            GetCategoryOrSkip(doc, BuiltInCategory.OST_PipeCurves))).IsFalse();

        await Assert.That(RevitFamilyGeometryExtractor.IsViewSpecificPreviewCategory(null)).IsFalse();
    }

    [Test]
    public async Task ExtractGeometryPerType_AnnotationFamily_SkipsEvenWithSeededGeometry()
    {
        var doc = OpenSeeded(_annotationPath!);

        var perType = new RevitFamilySnapshotExtractor().ExtractGeometryPerType(doc, CancellationToken.None);

        SmartConLogger.Info(
            $"PROBE: annotation family per-type count={perType.Count}, solidSeeded={_solidSeeded}");
        // The contract is unconditional: a view-specific family NEVER yields
        // preview geometry (when the solid seed succeeded this also proves the
        // skip won over real 3D content — the old code extracted it).
        await Assert.That(perType.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ExtractAsync_AnnotationFamily_ReturnsNoPreview()
    {
        // A live document for the stub context; the extracted target is the
        // SECOND copy so ExtractAsync's OpenDocumentFile does not collide.
        var contextDoc = OpenSeeded(_annotationPath!);
        var extractor = new RevitFamilyGeometryExtractor(new StubRevitContext(contextDoc));

        var result = await extractor.ExtractAsync(_annotationPath2!, "SmartConAnnoSkip2", CancellationToken.None);

        await Assert.That(result is null || result.Count == 0).IsTrue();
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private Document OpenSeeded(string path)
    {
        var doc = Application.OpenDocumentFile(path);
        if (!_openDocs!.Contains(doc))
        {
            _openDocs.Add(doc);
        }
        return doc;
    }

    private static Category GetCategoryOrSkip(Document doc, BuiltInCategory builtIn)
    {
        var category = doc.Settings.Categories.get_Item(builtIn);
        if (category is null)
        {
            Skip.Test($"Category {builtIn} is not available in this document — predicate contract untestable here");
        }
        return category!;
    }

    private static Extrusion CreateBox(Document doc, double depth)
    {
        var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
        var sketch = SketchPlane.Create(doc, plane);
        var p0 = XYZ.Zero;
        var p1 = new XYZ(1, 0, 0);
        var p2 = new XYZ(1, 1, 0);
        var p3 = new XYZ(0, 1, 0);
        var loop = new CurveArray();
        loop.Append(Line.CreateBound(p0, p1));
        loop.Append(Line.CreateBound(p1, p2));
        loop.Append(Line.CreateBound(p2, p3));
        loop.Append(Line.CreateBound(p3, p0));
        var profile = new CurveArrArray();
        profile.Append(loop);
        return doc.FamilyCreate.NewExtrusion(true, profile, sketch, depth);
    }
}
