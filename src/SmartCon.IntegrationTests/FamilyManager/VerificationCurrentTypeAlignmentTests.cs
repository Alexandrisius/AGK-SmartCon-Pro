using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Implementation;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using SmartCon.Revit.Transactions;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// Current-type independence regression (owner manual test 2026-08-23 for
/// the #240 verification-side alignment; FHV15, #249 round 3, moved the
/// mechanism INTO the extractor): the GEOM/CONN snapshot sections were
/// evaluated at the family document's CURRENT type, so a multi-type family
/// whose project-side current type differed from the resolved file's saved
/// active type produced a false POST-RELOAD VERIFICATION FAILED (ADSK
/// channel fan: project at Ф250 vs file saved at Ф100 — VERIFY-DIFF showed
/// GEOM-only diffs; the single-type household fan passed). Since FHV15 the
/// extractor measures the evaluated sections at the deterministic reference
/// type (first-Ordinal named type, rolled-back switch), so BOTH the catalog
/// hash and the verification proofs are current-type-independent. Pins:
///  0. the raw file hash is identical for different active types (the
///     FHV15 reference-type contract);
///  1. same content, file re-saved with a different active type →
///     <see cref="EmbeddedContentVerifier.VerifyEmbeddedAgainstFile"/> ==
///     true;
///  2. genuinely changed content (geometry-driving parameter) → false —
///     the reference type must not make the verification vacuous;
///  3. the ORCHESTRATED family-document update path (single-open
///     ComputeFullFileHash in StaleFamilyUpdater) works too: a host
///     family document updating a nested shared child whose file was
///     re-saved with a different active type updates successfully and
///     writes the marker.
/// </summary>
public sealed class VerificationCurrentTypeAlignmentTests : RevitApiTest
{
    private const string FamilyName = "AlignProbe";
    private const string ChildName = "AlignChild";
    private const string ChildItemId = "align-child-item";
    private const double MmToFt = 1.0 / 304.8;

    private string? _template;
    private string? _tempDir;
    private string? _rfaPath;
    private List<Document>? _openDocs;

    [Before(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Seed()
    {
        _template = SampleFiles.FindFamilyTemplate(Application);
        if (_template is null)
        {
            Skip.Test("Family templates (.rft) not found — cannot seed the alignment regression");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConAlign_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _rfaPath = Path.Combine(_tempDir, FamilyName + ".rfa");
        _openDocs = new List<Document>();
    }

    [After(Test)]
    [HookExecutor<RevitThreadExecutor>]
    public void Cleanup()
    {
        if (_openDocs is not null)
        {
            foreach (var doc in _openDocs)
            {
                try
                {
                    if (doc.IsValidObject) doc.Close(false);
                }
                catch
                {
                }
            }
        }

        try
        {
            if (_tempDir is not null && Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, true);
        }
        catch
        {
        }
    }

    [Test]
    public async Task VerifyEmbedded_FileResavedWithDifferentActiveType_Passes()
    {
        CreateFamilyWithDrivenGeometry(_rfaPath!, depthAMm: 100, depthBMm: 200, saveWithType: "A");

        // Pin 0: the fixture really exercises the mechanism — the raw file
        // hash is current-type-dependent (GEOM section).
        var hashActiveA = ComputeRawFileHashWithActiveType(_rfaPath!, "A");
        var hashActiveB = ComputeRawFileHashWithActiveType(_rfaPath!, "B");

        var project = LoadIntoProject(_rfaPath!);
        ResaveWithCurrentType(_rfaPath!, "B");

        var verdict = EmbeddedContentVerifier.VerifyEmbeddedAgainstFile(
            project, FamilyName, _rfaPath!,
            new RevitFamilySnapshotExtractor(), new FamilyContentHasher(), "AlignTest");

        SmartConLogger.Info(
            $"alignment regression: rawHashA={Short(hashActiveA)} rawHashB={Short(hashActiveB)} " +
            $"verdict={verdict?.ToString() ?? "null"}");

        using (Assert.Multiple())
        {
            await Assert.That(hashActiveA).IsNotNull();
            await Assert.That(hashActiveB).IsNotNull();
            // FHV15: the hash is measured at the deterministic reference
            // type — the current-type choice must NOT move it (the pre-FHV15
            // premise of this test was the exact opposite).
            await Assert.That(hashActiveA).IsEqualTo(hashActiveB)
                .Because("FHV15: the catalog hash is measured at the reference type — the active-type choice must not move it");
            await Assert.That(verdict).IsTrue()
                .Because("same content re-saved with a different active type must verify once both sides are aligned");
        }
    }

    [Test]
    public async Task VerifyEmbedded_ContentChanged_StillFails()
    {
        CreateFamilyWithDrivenGeometry(_rfaPath!, depthAMm: 100, depthBMm: 200, saveWithType: "A");
        var project = LoadIntoProject(_rfaPath!);
        MutateDepths(_rfaPath!, depthAMm: 100, depthBMm: 300);

        var verdict = EmbeddedContentVerifier.VerifyEmbeddedAgainstFile(
            project, FamilyName, _rfaPath!,
            new RevitFamilySnapshotExtractor(), new FamilyContentHasher(), "AlignTest");

        await Assert.That(verdict).IsFalse()
            .Because("a real content change must still mismatch — alignment must not make verification vacuous");
    }

    [Test]
    public async Task UpdateInFamilyDocument_FileResavedWithDifferentActiveType_PostVerifyPasses()
    {
        var childPath = Path.Combine(_tempDir!, ChildName + ".rfa");
        CreateSharedChildWithDrivenGeometry(childPath, saveWithType: "A");

        var host = Application.NewFamilyDocument(_template!);
        _openDocs!.Add(host);
        using (var tx = new Transaction(host, "load child"))
        {
            tx.Start();
            if (!host.LoadFamily(childPath, out _))
                throw new InvalidOperationException("LoadFamily(child) returned false");
            tx.Commit();
        }

        // v2: a REAL content change (new parameter — the reload must run)
        // plus the current-type skew: the file is re-saved with type B
        // active while the host loaded it with A.
        MutateChildToV2WithSkew(childPath, saveWithType: "B");

        var context = new StubRevitContext(host);
        var writer = new RecordingVersionWriter();
        var updater = new StaleFamilyUpdater(
            new RevitFamilyLoadService(context, new RevitTransactionService(context)),
            new StubFileResolver(childPath, "v2"),
            new RevitFamilyVersionStore(new RevitTransactionService(context)),
            new NullFamilyManagerDialogService(),
            new InlineAwaitableEvent(),
            context,
            writer,
            new StubClock(),
            nestedSharedRepository: null,
            catalog: null,
            typeRepository: null,
            systemSyncOrchestrator: null,
            dependencyRepository: null,
            snapshotExtractor: new RevitFamilySnapshotExtractor(),
            contentHasher: new FamilyContentHasher());

        var result = await updater.UpdateFamilyAsync(
            ChildItemId, overwriteParameterValues: true, fromVersionLabel: null, CancellationToken.None);

        SmartConLogger.Info(
            $"family-doc alignment: ok={result.Success} markerWrites={writer.Calls.Count}");

        using (Assert.Multiple())
        {
            await Assert.That(result.Success).IsTrue()
                .Because("the orchestrated family-doc path must current-type-align the single-open file hash (#240)");
            await Assert.That(writer.Calls.Count).IsEqualTo(1);
        }
    }

    /// <summary>
    /// Nested-shared variant of the driven-geometry fixture for the
    /// orchestrated family-document update path.
    /// </summary>
    private void CreateSharedChildWithDrivenGeometry(string path, string saveWithType)
    {
        var doc = Application.NewFamilyDocument(_template!);
        using (var tx = new Transaction(doc, "seed align child"))
        {
            tx.Start();
            doc.OwnerFamily?.get_Parameter(BuiltInParameter.FAMILY_SHARED)?.Set(1);
#if REVIT2022_OR_GREATER
            var l = doc.FamilyManager.AddParameter("L", GroupTypeId.General, SpecTypeId.Length, false);
            doc.FamilyManager.AddParameter("P0", GroupTypeId.General, SpecTypeId.Number, false);
#else
            var l = doc.FamilyManager.AddParameter("L", BuiltInParameterGroup.PG_GENERAL, ParameterType.Length, false);
            doc.FamilyManager.AddParameter("P0", BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, false);
#endif
            var extrusion = CreateBox(doc, 100 * MmToFt);
            var endParam = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
            doc.FamilyManager.AssociateElementParameterToFamilyParameter(endParam, l);

            doc.FamilyManager.NewType("A");
            doc.FamilyManager.Set(l, 100 * MmToFt);
            doc.FamilyManager.NewType("B");
            doc.FamilyManager.Set(l, 200 * MmToFt);
            SetCurrentType(doc, saveWithType);
            tx.Commit();
        }

        doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);
    }

    private void MutateChildToV2WithSkew(string path, string saveWithType)
    {
        var doc = Application.OpenDocumentFile(path);
        try
        {
            using (var tx = new Transaction(doc, "v2 with type skew"))
            {
                tx.Start();
#if REVIT2022_OR_GREATER
                doc.FamilyManager.AddParameter("P2", GroupTypeId.General, SpecTypeId.Number, false);
#else
                doc.FamilyManager.AddParameter("P2", BuiltInParameterGroup.PG_GENERAL, ParameterType.Number, false);
#endif
                SetCurrentType(doc, saveWithType);
                tx.Commit();
            }

            doc.Save();
        }
        finally
        {
            doc.Close(false);
        }
    }

    /// <summary>
    /// Two types (A/B), one solid box whose extrusion depth is driven by
    /// the Length type-parameter 'L' — the GEOM section differs per type.
    /// </summary>
    private void CreateFamilyWithDrivenGeometry(string path, double depthAMm, double depthBMm, string saveWithType)
    {
        var doc = Application.NewFamilyDocument(_template!);
        using (var tx = new Transaction(doc, "seed align probe"))
        {
            tx.Start();
#if REVIT2022_OR_GREATER
            var l = doc.FamilyManager.AddParameter("L", GroupTypeId.General, SpecTypeId.Length, false);
#else
            var l = doc.FamilyManager.AddParameter("L", BuiltInParameterGroup.PG_GENERAL, ParameterType.Length, false);
#endif
            var extrusion = CreateBox(doc, depthAMm * MmToFt);
            var endParam = extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM);
            doc.FamilyManager.AssociateElementParameterToFamilyParameter(endParam, l);

            doc.FamilyManager.NewType("A");
            doc.FamilyManager.Set(l, depthAMm * MmToFt);
            doc.FamilyManager.NewType("B");
            doc.FamilyManager.Set(l, depthBMm * MmToFt);
            SetCurrentType(doc, saveWithType);
            tx.Commit();
        }

        doc.SaveAs(path, new SaveAsOptions { OverwriteExistingFile = true });
        doc.Close(false);
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

    /// <summary>
    /// Raw (UNALIGNED — straight extractor, no verifier) file hash with the
    /// given active type: proves the fixture's geometry is current-type
    /// dependent.
    /// </summary>
    private string? ComputeRawFileHashWithActiveType(string path, string typeName)
    {
        var doc = Application.OpenDocumentFile(path);
        _openDocs!.Add(doc);
        using (var tx = new Transaction(doc, "switch active type"))
        {
            tx.Start();
            SetCurrentType(doc, typeName);
            tx.Commit();
        }

        var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc);
        return new FamilyContentHasher().ComputeForLoadable(snapshot)?.HexString;
    }

    private Document LoadIntoProject(string rfaPath)
    {
        var doc = Application.NewProjectDocument(UnitSystem.Metric);
        _openDocs!.Add(doc);
        using (var tx = new Transaction(doc, "load family"))
        {
            tx.Start();
            if (!doc.LoadFamily(rfaPath, out _))
                throw new InvalidOperationException("LoadFamily returned false");
            tx.Commit();
        }

        return doc;
    }

    private void ResaveWithCurrentType(string path, string typeName)
    {
        var doc = Application.OpenDocumentFile(path);
        try
        {
            using (var tx = new Transaction(doc, "switch active type"))
            {
                tx.Start();
                SetCurrentType(doc, typeName);
                tx.Commit();
            }

            doc.Save();
        }
        finally
        {
            doc.Close(false);
        }
    }

    private void MutateDepths(string path, double depthAMm, double depthBMm)
    {
        var doc = Application.OpenDocumentFile(path);
        try
        {
            using (var tx = new Transaction(doc, "mutate depths"))
            {
                tx.Start();
                var l = doc.FamilyManager.get_Parameter("L");
                SetCurrentType(doc, "A");
                doc.FamilyManager.Set(l, depthAMm * MmToFt);
                SetCurrentType(doc, "B");
                doc.FamilyManager.Set(l, depthBMm * MmToFt);
                tx.Commit();
            }

            doc.Save();
        }
        finally
        {
            doc.Close(false);
        }
    }

    private static void SetCurrentType(Document doc, string typeName)
    {
        foreach (FamilyType type in doc.FamilyManager.Types)
        {
            if (string.Equals(type.Name, typeName, StringComparison.Ordinal))
            {
                doc.FamilyManager.CurrentType = type;
                return;
            }
        }

        throw new InvalidOperationException($"Family type '{typeName}' not found");
    }

    private static string Short(string? hash)
    {
        return hash is null ? "null" : hash.Length > 8 ? hash[..8] : hash;
    }
}
