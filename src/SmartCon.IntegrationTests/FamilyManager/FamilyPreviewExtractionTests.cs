using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Issue #249, Phase 5: the per-type preview INPUT extraction (VIEW3D
/// hash inputs) on a REAL family — per-type isolation, type-rename
/// independence, GLB-filter mirroring, and content-pure GLB node names
/// (no ElementIds in pooled bytes).
/// </summary>
public sealed class FamilyPreviewExtractionTests : RevitApiTest
{
    private const double MmToFt = 1.0 / 304.8;

    private string? _tempDir;
    private string? _template;
    private string? _childPath;
    private List<Document>? _openDocs;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        _template = SampleFiles.FindFamilyTemplate(Application);
        if (_template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the preview-extraction contracts");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConPreview_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _childPath = Path.Combine(_tempDir, "SmartConPreviewChild.rfa");
        _openDocs = new List<Document>();

        // Two types (A/B), one solid box whose extrusion depth is driven
        // by the Length type-parameter 'L' — the preview content differs
        // per type by construction.
        var doc = Application.NewFamilyDocument(_template);
        using (var tx = new Transaction(doc, "seed preview child"))
        {
            tx.Start();
#if REVIT2022_OR_GREATER
            var l = doc.FamilyManager.AddParameter("L", GroupTypeId.General, SpecTypeId.Length, false);
#else
            var l = doc.FamilyManager.AddParameter("L", BuiltInParameterGroup.PG_GENERAL, ParameterType.Length, false);
#endif
            var extrusion = CreateBox(doc, 100 * MmToFt);
            doc.FamilyManager.AssociateElementParameterToFamilyParameter(
                extrusion!.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), l);

            doc.FamilyManager.NewType("A");
            doc.FamilyManager.Set(l, 100 * MmToFt);
            doc.FamilyManager.NewType("B");
            doc.FamilyManager.Set(l, 200 * MmToFt);
            tx.Commit();
        }
        doc.SaveAs(_childPath!, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);
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
    public async Task ExtractPreview_PerTypeHashes_IsolateTypeGeometry()
    {
        var doc = OpenChild();
        var perType = new RevitFamilySnapshotExtractor().ExtractGeometryPerType(doc, CancellationToken.None);

        await Assert.That(perType.Count).IsEqualTo(2);
        var previewA = perType.First(g => g.TypeName == "A").Preview;
        var previewB = perType.First(g => g.TypeName == "B").Preview;
        await Assert.That(previewA).IsNotNull();
        await Assert.That(previewB).IsNotNull();

        var hashA = FamilyPreviewHasher.ComputeForType(previewA);
        var hashB = FamilyPreviewHasher.ComputeForType(previewB);
        SmartConLogger.Info($"VIEW3D per type: A={hashA?[..12]} B={hashB?[..12]}");
        await Assert.That(hashA).IsNotNull();
        await Assert.That(hashB).IsNotNull();
        await Assert.That(hashA).IsNotEqualTo(hashB);

        // Type A's form carries A's depth: centroid Z ≈ 50mm.
        var formA = previewA!.Forms.Count > 0 ? previewA.Forms[0] : null;
        await Assert.That(formA).IsNotNull();
        await Assert.That(Math.Abs(formA!.Centroid!.Z - 50 * MmToFt) < 0.01).IsTrue();
    }

    [Test]
    public async Task ExtractPreview_TypeRename_SameView3DHash()
    {
        // The VIEW3D hash is rename-independent: the type name is the
        // asset row's key, never content (Plan v3).
        var doc = OpenChild();
        var before = new RevitFamilySnapshotExtractor().ExtractGeometryPerType(doc, CancellationToken.None);
        var hashBefore = FamilyPreviewHasher.ComputeForType(
            before.First(g => g.TypeName == "A").Preview);

        using (var tx = new Transaction(doc, "rename type"))
        {
            tx.Start();
            var typeA = doc.FamilyManager.Types.Cast<FamilyType>()
                .First(t => t.Name == "A");
            // The rename API acts on the CURRENT type only.
            doc.FamilyManager.CurrentType = typeA;
            doc.FamilyManager.RenameCurrentType("RenamedA");
            tx.Commit();
        }

        var after = new RevitFamilySnapshotExtractor().ExtractGeometryPerType(doc, CancellationToken.None);
        var hashAfter = FamilyPreviewHasher.ComputeForType(
            after.First(g => g.TypeName == "RenamedA").Preview);

        await Assert.That(hashAfter).IsEqualTo(hashBefore);
    }

    [Test]
    public async Task ExtractPreview_RepeatedExtraction_StableView3DHash()
    {
        // The same document extracted twice produces the same per-type
        // hashes (determinism — the CAS key never drifts on a no-op).
        var doc = OpenChild();
        var first = new RevitFamilySnapshotExtractor().ExtractGeometryPerType(doc, CancellationToken.None);
        var second = new RevitFamilySnapshotExtractor().ExtractGeometryPerType(doc, CancellationToken.None);

        foreach (var typeName in new[] { "A", "B" })
        {
            var h1 = FamilyPreviewHasher.ComputeForType(first.First(g => g.TypeName == typeName).Preview);
            var h2 = FamilyPreviewHasher.ComputeForType(second.First(g => g.TypeName == typeName).Preview);
            await Assert.That(h2).IsEqualTo(h1);
        }
    }

    [Test]
    public async Task ExtractMeshes_NodeNames_AreContentPure()
    {
        // #249 (Phase 5): no ElementIds in GLB node names — the ordinal
        // suffix is per name-prefix within one extraction pass.
        var doc = OpenChild();
        var perType = new RevitFamilySnapshotExtractor().ExtractGeometryPerType(doc, CancellationToken.None);
        var meshes = perType.SelectMany(g => g.Meshes).ToList();

        await Assert.That(meshes.Count).IsGreaterThan(0);
        foreach (var mesh in meshes)
        {
            SmartConLogger.Info($"GLB node: {mesh.NodeName}");
            // "Extrusion_0" style — a short ordinal, never an ElementId
            // (ElementIds in this document are 4+ digits).
            await Assert.That(mesh.NodeName).Matches(@"^[A-Za-z]+(\[.*\])?_\d{1,2}(__mat\d+)?$");
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private Document OpenChild()
    {
        var doc = Application.OpenDocumentFile(_childPath!);
        if (!_openDocs!.Contains(doc))
        {
            _openDocs.Add(doc);
        }
        return doc;
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
