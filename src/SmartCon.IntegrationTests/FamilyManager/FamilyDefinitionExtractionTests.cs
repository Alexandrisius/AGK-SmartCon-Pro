using Autodesk.Revit.DB;
using Nice3point.TUnit.Revit;
using Nice3point.TUnit.Revit.Executors;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Implementation;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.IntegrationTests.Support;
using SmartCon.Revit.FamilyManager;
using TUnit.Core.Executors;

namespace SmartCon.IntegrationTests.FamilyManager;

/// <summary>
/// FHV12 (Issue #249, Phase 3): extraction of the DEF section (form
/// visibility/material/offset bindings, dimension labels, reference
/// planes) and the strengthened GEOM metrics (centroid, face-kind
/// histogram, edge lengths, visibility flags, nested instance
/// placements) on a REAL family document — plus the raw-vs-embedded
/// context-stability contract the unified hash relies on.
/// </summary>
public sealed class FamilyDefinitionExtractionTests : RevitApiTest
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
            Skip.Test("Family templates (.rft) not found — cannot seed the definition-extraction contracts");
            return;
        }

        _tempDir = Path.Combine(Path.GetTempPath(), $"SmartConDef_{Guid.NewGuid().ToString("N")}");
        Directory.CreateDirectory(_tempDir);
        _childPath = Path.Combine(_tempDir, "SmartConDefChild.rfa");
        _openDocs = new List<Document>();

        var doc = Application.NewFamilyDocument(_template);
        Dimension? pendingDim = null;
        using (var tx = new Transaction(doc, "seed definition child"))
        {
            tx.Start();
#if REVIT2022_OR_GREATER
            var l = doc.FamilyManager.AddParameter("L", GroupTypeId.General, SpecTypeId.Length, false);
            var showBox = doc.FamilyManager.AddParameter("ShowBox", GroupTypeId.General, SpecTypeId.Boolean.YesNo, false);
            var boxMaterial = doc.FamilyManager.AddParameter("BoxMaterial", GroupTypeId.Materials, SpecTypeId.Reference.Material, false);
#else
            var l = doc.FamilyManager.AddParameter("L", BuiltInParameterGroup.PG_GENERAL, ParameterType.Length, false);
            var showBox = doc.FamilyManager.AddParameter("ShowBox", BuiltInParameterGroup.PG_GENERAL, ParameterType.YesNo, false);
            var boxMaterial = doc.FamilyManager.AddParameter("BoxMaterial", BuiltInParameterGroup.PG_MATERIALS, ParameterType.Material, false);
#endif

            var extrusion = CreateBox(doc, 100 * MmToFt);
            doc.FamilyManager.AssociateElementParameterToFamilyParameter(
                extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), l);
            doc.FamilyManager.AssociateElementParameterToFamilyParameter(
                extrusion.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM), showBox);
            doc.FamilyManager.AssociateElementParameterToFamilyParameter(
                extrusion.get_Parameter(BuiltInParameter.MATERIAL_ID_PARAM), boxMaterial);

            // Labeled dimension between the extrusion's SIDE FACES.
            // Datum-plane references from API-created reference planes get
            // invalidated by ANY FamilyLabel assignment ("One or more
            // dimension references are or have become invalid" — probe
            // 2026-08-27); surface references are the standard parametric
            // pattern. The label is assigned in a SECOND transaction.
            var view = FindPlanView(doc);
            if (view is not null)
            {
                var geoOptions = new Options { ComputeReferences = true, DetailLevel = ViewDetailLevel.Fine };
                var geo = extrusion.get_Geometry(geoOptions);
                var refs = new ReferenceArray();
                foreach (var obj in geo!)
                {
                    if (obj is Solid solid)
                    {
                        foreach (Face face in solid.Faces)
                        {
                            if (face is PlanarFace { FaceNormal: not null } pf
                                && (pf.FaceNormal.IsAlmostEqualTo(XYZ.BasisX)
                                    || pf.FaceNormal.IsAlmostEqualTo(-XYZ.BasisX)))
                            {
                                refs.Append(pf.Reference);
                            }
                        }
                    }
                }
                if (refs.Size >= 2)
                {
                    var dimLine = Line.CreateBound(new XYZ(0, -0.5, 0), new XYZ(1, -0.5, 0));
                    pendingDim = doc.FamilyCreate.NewDimension(view, dimLine, refs);
                }
            }
            else
            {
                SmartConLogger.Warn("No plan view in the family template — dimension fixture skipped. [Action: проверьте шаблон семейства]");
            }

            doc.FamilyManager.NewType("A");
            doc.FamilyManager.Set(l, 100 * MmToFt);
            tx.Commit();
        }

        // tx2: a REPORTING label (the dimension drives the parameter — a
        // DRIVING label fails to solve constraints for freshly created
        // geometry; the DEF extraction hashes the label NAME either way).
        if (pendingDim is not null)
        {
            using var tx2 = new Transaction(doc, "label dimension");
            tx2.Start();
#if REVIT2022_OR_GREATER
            var lRep = doc.FamilyManager.AddParameter("LRep", GroupTypeId.General, SpecTypeId.Length, true);
#else
            var lRep = doc.FamilyManager.AddParameter("LRep", BuiltInParameterGroup.PG_GENERAL, ParameterType.Length, true);
#endif
            doc.FamilyManager.MakeReporting(lRep);
            pendingDim.FamilyLabel = lRep;
            tx2.Commit();
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
    public async Task Extract_Definitions_FormBindingsCaptured()
    {
        var doc = OpenChild();
        var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc);

        var form = snapshot.Definitions?.Forms.FirstOrDefault(f => f.FormKind == "Extrusion");
        SmartConLogger.Info(
            $"DEF form: vis={form?.VisibilityParameterName} mat={form?.MaterialParameterName} " +
            $"end={form?.ExtrusionEndParameterName} startOff={form?.ExtrusionStartOffset} endOff={form?.ExtrusionEndOffset}");
        await Assert.That(form).IsNotNull();
        await Assert.That(form!.VisibilityParameterName).IsEqualTo("ShowBox");
        await Assert.That(form.MaterialParameterName).IsEqualTo("BoxMaterial");
        await Assert.That(form.ExtrusionEndParameterName).IsEqualTo("L");
        await Assert.That(form.ExtrusionStartOffset).IsNotNull();
        await Assert.That(form.ExtrusionEndOffset).IsNotNull();
    }

    [Test]
    public async Task Extract_Definitions_DimensionLabelCaptured()
    {
        var doc = OpenChild();
        var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc);

        var dims = snapshot.Definitions?.Dimensions ?? [];
        SmartConLogger.Info($"DEF dims: {dims.Count} ({string.Join(";", dims.Select(d => $"{d.LabelParameterName}/{d.StyleName}/{d.SegmentCount}"))})");
        var dim = dims.FirstOrDefault(d => d.LabelParameterName == "LRep");
        await Assert.That(dim).IsNotNull();
        await Assert.That(dim!.StyleName).IsNotEmpty();
        await Assert.That(dim.SegmentCount).IsGreaterThanOrEqualTo(0);
    }

    [Test]
    public async Task Extract_Definitions_ReferencePlanesCaptured()
    {
        var doc = OpenChild();
        var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc);

        var planes = snapshot.Definitions?.ReferencePlanes ?? [];
        SmartConLogger.Info($"DEF planes: {planes.Count} ({string.Join(";", planes.Select(p => $"{p.Name}/{p.DefinesOrigin}"))})");
        // The generic template ships at least the two origin planes plus
        // our two fixture planes.
        await Assert.That(planes.Count).IsGreaterThanOrEqualTo(2);
        await Assert.That(planes.Any(p => p.DefinesOrigin == true)).IsTrue();
    }

    [Test]
    public async Task Extract_Geometry_StrengthenedMetricsOnBox()
    {
        var doc = OpenChild();
        var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc);

        var form = snapshot.Geometry.Forms.FirstOrDefault(f => f.FormKind == "Extrusion");
        await Assert.That(form).IsNotNull();
        await Assert.That(form!.Centroid).IsNotNull();
        // Box 1ft × 1ft × 100mm at the origin: centroid ≈ (0.5, 0.5, ~0.164).
        await Assert.That(Math.Abs(form.Centroid!.X - 0.5) < 0.01).IsTrue();
        await Assert.That(Math.Abs(form.Centroid.Y - 0.5) < 0.01).IsTrue();
        var planar = form.FaceTypes?.FirstOrDefault(t => t.FaceKind == "PlanarFace");
        await Assert.That(planar).IsNotNull();
        await Assert.That(planar!.Count).IsEqualTo(6);
        await Assert.That(form.TotalEdgeLength > 8.0).IsTrue();
        await Assert.That(form.Visibility).IsNotNull();
        await Assert.That(form.Visibility!.IsVisibleParamValue).IsEqualTo(1);
    }

    [Test]
    public async Task Extract_Geom2d_SketchOwnedElementsExcluded()
    {
        // FHV13 (#249 follow-up): the extrusion's sketch curves and any
        // automatic sketch dimensions are 3D-form wiring — GEOM2D counts
        // only FREE 2D content. The fixture child has ONE extrusion
        // (4 sketch curves) and ONE free labeled dimension; the generic
        // template ships no free model lines.
        var doc = OpenChild();
        var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc);

        SmartConLogger.Info(
            $"GEOM2D: model={snapshot.Geometry.ModelCurveCount}/{snapshot.Geometry.TotalModelCurveLength}, " +
            $"dim={snapshot.Geometry.DimensionCount}, refPlanes={snapshot.Geometry.ReferencePlaneCount}");
        await Assert.That(snapshot.Geometry.ModelCurveCount).IsEqualTo(0);
        await Assert.That(snapshot.Geometry.DimensionCount).IsEqualTo(1);
    }

    [Test]
    public async Task Extract_Geometry_ConditionallyHiddenForm_IncludedWithFlag()
    {
        // FHV12 blind-spot fix: a form invisible on the CURRENT type
        // (IS_VISIBLE_PARAM = 0) still enters the metrics — with its flag
        // recorded — instead of vanishing from the hash.
        var doc = Application.OpenDocumentFile(_childPath!);
        _openDocs!.Add(doc);
        using (var tx = new Transaction(doc, "hide box"))
        {
            tx.Start();
            // Unbind first: the value is driven by ShowBox — set the
            // FAMILY parameter to 0 instead (current type is "A").
            var showBox = doc.FamilyManager.get_Parameter("ShowBox");
            doc.FamilyManager.Set(showBox, 0);
            tx.Commit();
        }

        var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc);

        var hiddenForm = snapshot.Geometry.Forms.FirstOrDefault(f => f.FormKind == "Extrusion");
        await Assert.That(hiddenForm).IsNotNull();
        await Assert.That(hiddenForm!.Visibility?.IsVisibleParamValue).IsEqualTo(0);
        // IncludeNonVisibleObjects = true — the solid is still measured.
        await Assert.That(hiddenForm.Volume > 0).IsTrue();
    }

    [Test]
    public async Task Extract_NestedInstance_PlacementCaptured()
    {
        // Host with the child placed as a nested instance — the placement
        // (symbol identity + transform) enters GEOM.
        var host = Application.NewFamilyDocument(_template!);
        _openDocs!.Add(host);
        using (var tx = new Transaction(host, "load nested"))
        {
            tx.Start();
            if (!host.LoadFamily(_childPath!, out _))
            {
                throw new InvalidOperationException("LoadFamily(child) returned false");
            }
            var symbol = new FilteredElementCollector(host)
                .OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .First(s => string.Equals(s.Family?.Name, "SmartConDefChild", StringComparison.OrdinalIgnoreCase));
            if (!symbol.IsActive)
            {
                symbol.Activate();
            }
            // Non-view-based generic model → the (origin, symbol,
            // StructuralType) overload, NOT the View one ("Family cannot
            // be placed as hosted on an input face reference, because its
            // FamilyPlacementType is not ViewBased").
            var placed = host.FamilyCreate.NewFamilyInstance(
                new XYZ(2, 3, 0), symbol, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
            if (placed is null)
            {
                throw new InvalidOperationException("NewFamilyInstance(child) returned null");
            }
            tx.Commit();
        }

        var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(host);

        // Diagnostic: what instances does the host actually hold?
        var allInstances = new FilteredElementCollector(host)
            .OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
            .Select(fi => $"{fi.Symbol?.Family?.Name ?? "<null-family>"}/{fi.Symbol?.Name ?? "<null-symbol>"}")
            .ToList();
        SmartConLogger.Info(
            $"Nested diagnostics: collector={allInstances.Count} [{string.Join(";", allInstances)}], " +
            $"snapshot={(snapshot.Geometry.NestedInstances is null ? "<null>" : string.Join(";", snapshot.Geometry.NestedInstances.Select(n => n.FamilyName + "/" + n.SymbolName)))}");

        // The template ships its own nested instances (section head
        // symbols) — find OUR child placement.
        var instance = snapshot.Geometry.NestedInstances?.FirstOrDefault(
            n => n.FamilyName == "SmartConDefChild");
        SmartConLogger.Info(
            $"Nested instance: {(instance is null ? "<none>" : $"{instance.FamilyName}/{instance.SymbolName} @ {instance.OriginX},{instance.OriginY}")}");
        await Assert.That(instance).IsNotNull();
        await Assert.That(instance!.FamilyName).IsEqualTo("SmartConDefChild");
        await Assert.That(Math.Abs(instance.OriginX - 2.0) < 0.001).IsTrue();
        await Assert.That(Math.Abs(instance.OriginY - 3.0) < 0.001).IsTrue();
    }

    [Test]
    public async Task Extract_RawVsEmbedded_IdenticalFhv12Hash()
    {
        // THE context-stability contract of the unified hash: the same
        // family extracted from the raw .rfa and from the embedded
        // EditFamily copy must produce the identical FHV12 hash —
        // DEF bindings and the strengthened GEOM fields included.
        var rawDoc = OpenChild();
        var rawSnapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(rawDoc);

        var host = Application.NewFamilyDocument(_template!);
        _openDocs!.Add(host);
        using (var tx = new Transaction(host, "load child"))
        {
            tx.Start();
            if (!host.LoadFamily(_childPath!, out _))
            {
                throw new InvalidOperationException("LoadFamily(child) returned false");
            }
            tx.Commit();
        }
        var nested = new FilteredElementCollector(host)
            .OfClass(typeof(Family)).Cast<Family>()
            .First(f => string.Equals(f.Name, "SmartConDefChild", StringComparison.OrdinalIgnoreCase));
        var embeddedDoc = host.EditFamily(nested);
        _openDocs!.Add(embeddedDoc);
        var embeddedSnapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(embeddedDoc);

        var hasher = new FamilyContentHasher();
        var rawHash = hasher.ComputeForLoadable(rawSnapshot)?.HexString;
        var embeddedHash = hasher.ComputeForLoadable(embeddedSnapshot)?.HexString;
        SmartConLogger.Info($"Raw vs embedded FHV12: raw={rawHash} embedded={embeddedHash}");
        await Assert.That(rawHash).IsNotNull();
        await Assert.That(embeddedHash).IsEqualTo(rawHash);
    }

    [Test]
    public async Task Extract_Geom2d_FreeLinesCounted_FormSketchExcluded()
    {
        // FHV14 contract (probe-proven 2026-08-28): Revit wraps FREE
        // model/symbolic lines in their own sketches with an INVALID
        // OwnerId, while a form's sketch has OwnerId = the GenericForm.
        // The sketch-curve exclusion must eat ONLY form-sketch curves —
        // free 2D lines stay counted in GEOM2D.
        var doc = Application.NewFamilyDocument(_template!);
        _openDocs!.Add(doc);
        using (var tx = new Transaction(doc, "seed free curves"))
        {
            tx.Start();
            CreateBox(doc, 100 * MmToFt);
            var plane = Plane.CreateByNormalAndOrigin(XYZ.BasisZ, XYZ.Zero);
            var sketchPlane = SketchPlane.Create(doc, plane);
            doc.FamilyCreate.NewModelCurve(
                Line.CreateBound(new XYZ(0, 2, 0), new XYZ(1, 2, 0)), sketchPlane);
            doc.FamilyCreate.NewSymbolicCurve(
                Line.CreateBound(new XYZ(0, 3, 0), new XYZ(1, 3, 0)), sketchPlane);
            tx.Commit();
        }

        var snapshot = new RevitFamilySnapshotExtractor().ExtractFromFamilyDocument(doc);
        SmartConLogger.Info(
            $"GEOM2D free-vs-sketch: model={snapshot.Geometry.ModelCurveCount} symbolic={snapshot.Geometry.SymbolicCurveCount}");
        // The box's 4 sketch curves are excluded; the FREE lines are counted.
        await Assert.That(snapshot.Geometry.ModelCurveCount).IsEqualTo(1);
        await Assert.That(snapshot.Geometry.SymbolicCurveCount).IsEqualTo(1);
    }

    [Test]
    public async Task Extract_ReferenceType_HashIsCurrentTypeIndependent()
    {
        // FHV15 contract (manual-test round 3): a single-type value edit
        // implies switching the current type in the editor — the measured
        // hash must NOT move. The family has two types with DIFFERENT
        // geometry-driving L, so a naive current-type measurement would
        // flip the GEOM section; measured at the deterministic reference
        // (first-Ordinal named type), both extractions agree.
        var doc = Application.NewFamilyDocument(_template!);
        _openDocs!.Add(doc);
        using (var tx = new Transaction(doc, "seed two types"))
        {
            tx.Start();
#if REVIT2022_OR_GREATER
            var l = doc.FamilyManager.AddParameter("L", GroupTypeId.General, SpecTypeId.Length, false);
#else
            var l = doc.FamilyManager.AddParameter("L", BuiltInParameterGroup.PG_GENERAL, ParameterType.Length, false);
#endif
            var extrusion = CreateBox(doc, 100 * MmToFt);
            doc.FamilyManager.AssociateElementParameterToFamilyParameter(
                extrusion.get_Parameter(BuiltInParameter.EXTRUSION_END_PARAM), l);
            doc.FamilyManager.NewType("A");
            doc.FamilyManager.Set(l, 100 * MmToFt);
            doc.FamilyManager.NewType("B");
            doc.FamilyManager.Set(l, 250 * MmToFt);
            tx.Commit();
        }

        var extractor = new RevitFamilySnapshotExtractor();
        var hasher = new FamilyContentHasher();

        // current = B (the last created type).
        var hashAtB = hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(doc))!.HexString;

        FamilyType? typeA = null;
        foreach (FamilyType t in doc.FamilyManager.Types)
        {
            if (t.Name == "A") typeA = t;
        }
        using (var tx2 = new Transaction(doc, "switch current to A"))
        {
            tx2.Start();
            doc.FamilyManager.CurrentType = typeA!;
            tx2.Commit();
        }
        var hashAtA = hasher.ComputeForLoadable(extractor.ExtractFromFamilyDocument(doc))!.HexString;

        SmartConLogger.Info($"FHV15 reference-type: hashAtB={hashAtB} hashAtA={hashAtA}");
        await Assert.That(hashAtA).IsEqualTo(hashAtB);
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

    private static View? FindPlanView(Document doc)
    {
        return new FilteredElementCollector(doc)
            .OfClass(typeof(View)).Cast<View>()
            .FirstOrDefault(v => !v.IsTemplate && v.ViewType is ViewType.FloorPlan
                or ViewType.CeilingPlan or ViewType.EngineeringPlan);
    }
}
