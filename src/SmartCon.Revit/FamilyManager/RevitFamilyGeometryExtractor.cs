using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

/// <summary>
/// Implements <see cref="IFamilyGeometryExtractor"/> by opening the managed
/// .rfa, traversing ALL geometry-bearing elements (both top-level
/// <c>GenericForm</c> solids AND nested <c>FamilyInstance</c> elements),
/// tessellating each <c>Solid</c> via <c>SolidUtils.TessellateSolidOrShell</c>
/// (with <c>Face.Triangulate(1.0)</c> fallback), and returning
/// <see cref="MeshData"/> entries ready to be written as GLB.
/// </summary>
/// <remarks>
/// <b>Threading (I-01):</b> all Revit API calls
/// (<c>OpenDocumentFile</c>, <c>get_Geometry</c>, <c>TessellateSolidOrShell</c>)
/// happen on the Revit UI thread. The caller must invoke <c>ExtractAsync</c>
/// via <c>IFamilyManagerAwaitableEvent.RaiseAsync&lt;T&gt;</c>. The class
/// itself is thread-safe (single-use instance, no shared state).
/// <para>
/// <b>Open-close pattern:</b> mirrors <c>RevitFamilyDataExtractionService
/// .ExtractFromManagedFile</c> — OpenDocumentFile wrapped in
/// <c>SmartConLogger.Measure</c>, try/finally with Close(false), Marshal
/// cleanup is skipped (managed wrapper, not COM), and <see cref="RevitBalloonNudge"/>
/// fires after Close to resync the WPF render thread (REVIT-236376 /
/// REVIT-237190).
/// </para>
/// <para>
/// <b>Geometry sources (ADR-042 fix):</b> a family document can contain two
/// kinds of 3D geometry: (1) direct <c>GenericForm</c> elements (Extrusion,
/// Revolution, Sweep, Blend, SweptBlend) and (2) nested <c>FamilyInstance</c>
/// elements — families loaded into the current family. Complex families
/// (doors with handles, windows with frames, furniture with decorative
/// elements) typically use nested families. The previous implementation only
/// collected <c>GenericForm</c>, missing all nested-family geometry.
/// <see href="https://thebuildingcoder.typepad.com">Jeremy Tammik</see>
/// confirms that <c>FilteredElementCollector.OfClass(typeof(FamilyInstance))</c>
/// in a family document returns nested family instances, whose
/// <c>get_Geometry()</c> yields <c>GeometryInstance</c> objects that can be
/// recursed into via <c>GetSymbolGeometry()</c>.
/// </para>
/// <para>
/// <b>Void forms:</b> <c>GenericForm.IsSolid == false</c> identifies void
/// forms (cutting geometry). Voids are SKIPPED because they define cuts, not
/// solid geometry — the solid forms already have the void cuts applied.
/// Including voids would produce duplicate or "negative" geometry.
/// </para>
/// <para>
/// <b>Triangulation (Jeremy Tammik / The Building Coder):</b>
/// <c>SolidUtils.TessellateSolidOrShell</c> processes the entire solid at
/// once, producing matched vertices at edges (no gaps between faces). This
/// is the recommended approach over per-face <c>Face.Triangulate</c> which
/// produces independent triangulations per face with mismatched edge
/// vertices. <c>SolidOrShellTessellationControls.LevelOfDetail = 1.0</c>
/// requests maximum detail (0=coarse, 1=finest). If the method throws, we
/// fall back to <c>Face.Triangulate(1.0)</c> per face.
/// </para>
/// <para>
/// <b>Symbol vs instance geometry (ADR-042 fix):</b> for nested
/// <c>GeometryInstance</c>, this extractor calls <c>GetInstanceGeometry()</c>
/// (NOT <c>GetSymbolGeometry()</c>) — this applies the instance transform,
/// placing nested-family geometry at its correct position within the
/// parent family document. <c>GetSymbolGeometry()</c> returns geometry in
/// symbol-local coords (origin 0,0,0), which causes nested families to
/// overlap the parent form. Jeremy Tammik (The Building Coder) notes that
/// <c>GetInstanceGeometry()</c> returns copies (not original geometry objects),
/// but for a preview this is irrelevant — we immediately tessellate and
/// discard the Revit geometry.
/// </para>
/// </remarks>
public sealed class RevitFamilyGeometryExtractor : IFamilyGeometryExtractor
{
    private readonly IRevitContext _revitContext;

    private static readonly Vector4 FallbackColor = new(0.65f, 0.65f, 0.65f, 1f);

    public RevitFamilyGeometryExtractor(IRevitContext revitContext)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
    }

    public Task<IReadOnlyList<FamilyGeometryPerType>?> ExtractAsync(
        string managedRfaPath,
        string familyName,
        CancellationToken ct = default)
    {
#pragma warning disable CA1510
        if (string.IsNullOrEmpty(managedRfaPath))
            throw new ArgumentNullException(nameof(managedRfaPath));
#pragma warning restore CA1510

        var rfaFileName = Path.GetFileName(managedRfaPath);

        if (!File.Exists(managedRfaPath))
        {
            SmartConLogger.Warn(
                $"Geometry extraction skipped: file not found '{rfaFileName}' " +
                "[Action: verify managed storage state; the import succeeded but no 3D preview will be available]");
            return Task.FromResult<IReadOnlyList<FamilyGeometryPerType>?>(null);
        }

        using var _scope = SmartConLogger.BeginScope("Geo3DExtract",
            ("Method", nameof(ExtractAsync)),
            ("FilePath", rfaFileName));

        SmartConLogger.Debug($"Starting 3D geometry extraction for '{rfaFileName}'");

        Document? doc = null;
        try
        {
            SmartConLogger.FreezeThreadPool("Geo3DExtract.beforeOpen");

            using (var _openMs = SmartConLogger.Measure("Geo3DExtract.OpenDocumentFile"))
            {
                try
                {
                    SmartConLogger.Freeze($"Geo3D: starting OpenDocumentFile for '{rfaFileName}'");
                    doc = _revitContext.GetDocument().Application.OpenDocumentFile(managedRfaPath);
                    SmartConLogger.Freeze($"Geo3D: OpenDocumentFile completed in {_openMs.GetElapsedMilliseconds()}ms");
                }
                catch (Exception ex)
                {
                    SmartConLogger.FreezeFail("Geo3D.OpenDocumentFile",
                        $"{ex.GetType().Name}: {ex.Message}");
                    SmartConLogger.Warn(
                        $"OpenDocumentFile failed for '{rfaFileName}': {ex.Message} " +
                        "[Action: verify file is a valid Revit .rfa; 3D preview will be skipped]");
                    return Task.FromResult<IReadOnlyList<FamilyGeometryPerType>?>(null);
                }
            }

            if (doc is null)
            {
                SmartConLogger.Warn(
                    $"OpenDocumentFile returned null for '{rfaFileName}' " +
                    "[Action: check for corrupted .rfa or Revit version mismatch]");
                return Task.FromResult<IReadOnlyList<FamilyGeometryPerType>?>(null);
            }

            if (!doc.IsFamilyDocument)
            {
                SmartConLogger.Warn(
                    $"Document '{rfaFileName}' is not a family document — skipping geometry extraction " +
                    "[Action: 3D preview is only generated for loadable .rfa families]");
                return Task.FromResult<IReadOnlyList<FamilyGeometryPerType>?>(null);
            }

            // Delegate per-type extraction to RevitFamilySnapshotExtractor
            // which uses Transaction+RollBack to iterate FamilyType entries.
            var snapshotExtractor = new RevitFamilySnapshotExtractor();
            var geometryPerType = snapshotExtractor.ExtractGeometryPerType(doc, ct);

            if (geometryPerType is null || geometryPerType.Count == 0)
            {
                SmartConLogger.Warn(
                    $"No visible geometry found in '{rfaFileName}' " +
                    "[Action: verify family has visible GenericForm elements with 3D solids; preview will be empty]");
                return Task.FromResult<IReadOnlyList<FamilyGeometryPerType>?>(null);
            }

            var totalMeshes = geometryPerType.Sum(g => g.Meshes.Count);
            var totalTris = geometryPerType.Sum(g => g.TotalTriangleCount);
            SmartConLogger.Info(
                $"Geometry extraction complete: '{rfaFileName}', {geometryPerType.Count} types, " +
                $"{totalMeshes} total meshes, {totalTris} total triangles");

            return Task.FromResult<IReadOnlyList<FamilyGeometryPerType>?>(geometryPerType);
        }
        finally
        {
            if (doc is not null)
            {
                SmartConLogger.Freeze("Geo3D: starting Close");
                using (var _closeMs = SmartConLogger.Measure("Geo3DExtract.Close"))
                {
                    try { doc.Close(false); }
                    catch (Exception ex)
                    {
                        SmartConLogger.FreezeFail("Geo3D.Close",
                            $"{ex.GetType().Name}: {ex.Message}");
                    }
                    SmartConLogger.Freeze($"Geo3D: Close completed in {_closeMs.GetElapsedMilliseconds()}ms");
                }

                try { Marshal.ReleaseComObject(doc); }
                catch (Exception ex)
                {
                    SmartConLogger.Debug(
                        $"Marshal.ReleaseComObject skipped (managed wrapper, not COM): {ex.Message}");
                }
            }
        }
    }

    internal static List<MeshData> ExtractMeshesFromFamilyDoc(
        Document familyDoc,
        CancellationToken ct)
    {
        var result = new List<MeshData>();

        var options = new Options
        {
            ComputeReferences = false,
            DetailLevel = ViewDetailLevel.Fine,
            // IncludeNonVisibleObjects=true: some GenericForm extrusions have
            // Visible=true but their solid geometry is marked "conditionally
            // visible" by Revit. With false (default), get_Geometry returns
            // an empty GeometryElement even though the BoundingBox is valid.
            // Jeremy Tammik (The Building Coder): "some of this conditionally
            // visible geometry represents real-world objects." We already
            // filter by GenericForm.Visible above, so truly hidden forms are
            // excluded — this flag only recovers conditionally-visible solids
            // within visible forms.
            IncludeNonVisibleObjects = true
        };

        var forms = new FilteredElementCollector(familyDoc)
            .OfClass(typeof(GenericForm))
            .Cast<GenericForm>()
            .ToList();

        var solidForms = forms.Where(f => f.IsSolid).ToList();
        var voidForms = forms.Where(f => !f.IsSolid).ToList();

            SmartConLogger.Info(
                $"GenericForm scan: {forms.Count} total ({solidForms.Count} solid, " +
                $"{voidForms.Count} void — voids are skipped because solid forms " +
                "already have void cuts applied)");

            // Type-driven visibility parameters (e.g. "Visible when Type = X") only
            // evaluate against the currently active family type.
            try
            {
                var fm = familyDoc.FamilyManager;
                var currentType = fm.CurrentType;
                var currentTypeName = currentType?.Name ?? "<none>";
                var typeCount = fm.Types.Size;
                SmartConLogger.Debug(
                    $"FamilyManager: ActiveType='{currentTypeName}', TotalTypes={typeCount}");
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"FamilyManager type info unavailable: {ex.Message}");
            }

            var nestedInstances = new FilteredElementCollector(familyDoc)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .ToList();

            SmartConLogger.Info(
                $"FamilyInstance (nested families) scan: {nestedInstances.Count} found");

        var totalProcessed = 0;
        var totalSkipped = 0;
        var totalEmpty = 0;

        foreach (var form in solidForms)
        {
            ct.ThrowIfCancellationRequested();
            var formType = form.GetType().Name;
            var formId = GetElementIdInt(form.Id);
            var nodeName = $"{formType}_{formId}";

            bool isVisible;
            try { isVisible = form.Visible; }
            catch { isVisible = true; }

            var isVisibleParam = GetIsVisibleParam(form);
            SmartConLogger.Debug(
                $"  Scanning {nodeName}: IsSolid={form.IsSolid}, Visible={isVisible}, " +
                $"IS_VISIBLE_PARAM={FormatNullableInt(isVisibleParam)}, " +
                $"Category={form.Category?.Name ?? "<null>"}");

            if (!isVisible)
            {
                SmartConLogger.Info($"  · {nodeName}: skipped (Visible=false)");
                continue;
            }

            if (isVisibleParam == 0)
            {
                SmartConLogger.Info(
                    $"  · {nodeName}: skipped (IS_VISIBLE_PARAM=0)");
                continue;
            }

            // Issue #102: detail-level pre-filter. IncludeNonVisibleObjects=true
            // (recovery of conditionally-visible solids, commit 5283765) bypasses
            // Revit's detail-level filtering — coarse-only symbolic graphics would
            // otherwise leak into the Fine-detail preview. Check GenericForm visibility
            // BEFORE get_Geometry to drop them at source.
            if (!IsShownAtDetailLevel(form, ViewDetailLevel.Fine, out var detailSkipReason))
            {
                SmartConLogger.Info(
                    $"  · {nodeName}: skipped (not visible at Fine — {detailSkipReason})");
                continue;
            }

            try
            {
                var mesh = ExtractMeshFromElement(form, options, nodeName);
                if (mesh is not null && !mesh.IsEmpty)
                {
                    result.Add(mesh);
                    totalProcessed++;
                    SmartConLogger.Debug(
                        $"  ✔ {nodeName}: {mesh.VertexCount} verts, {mesh.TriangleCount} tris");
                }
                else
                {
                    totalEmpty++;
                    SmartConLogger.Debug($"  · {nodeName}: no geometry extracted");
                }
            }
            catch (Exception ex)
            {
                totalSkipped++;
                SmartConLogger.Debug(
                    $"  ✗ {nodeName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        foreach (var inst in nestedInstances)
        {
            ct.ThrowIfCancellationRequested();
            var symbolName = "?";
            try { symbolName = inst.Symbol?.Name ?? "?"; } catch { }
            var instId = GetElementIdInt(inst.Id);
            var nodeName = $"Nested[{symbolName}]_{instId}";

            var instVisibleParam = GetIsVisibleParam(inst);
            SmartConLogger.Debug(
                $"  Scanning {nodeName}: IS_VISIBLE_PARAM={FormatNullableInt(instVisibleParam)}");

            // Issue #102: detail-level pre-filter for nested FamilyInstance.
            // This is the primary entry path for coarse-only symbolic graphics
            // (e.g. "Низкая детализация" families) into the Fine-detail 3D preview:
            // the nested FamilyInstance loop had NO visibility filter at all before #102.
            // GEOM_VISIBILITY_PARAM bitfield: Coarse=1<<13, Medium=1<<14, Fine=1<<15.
            // Value 0 = detail component family with unconditional visibility.
            if (instVisibleParam == 0)
            {
                SmartConLogger.Info(
                    $"  · {nodeName}: skipped (IS_VISIBLE_PARAM=0)");
                continue;
            }

            if (!IsShownAtDetailLevel(inst, ViewDetailLevel.Fine, out var detailSkipReason))
            {
                SmartConLogger.Info(
                    $"  · {nodeName}: skipped (not visible at Fine — {detailSkipReason})");
                continue;
            }

            try
            {
                var mesh = ExtractMeshFromElement(inst, options, nodeName);
                if (mesh is not null && !mesh.IsEmpty)
                {
                    result.Add(mesh);
                    totalProcessed++;
                    SmartConLogger.Debug(
                        $"  ✔ {nodeName}: {mesh.VertexCount} verts, {mesh.TriangleCount} tris");
                }
                else
                {
                    totalEmpty++;
                    SmartConLogger.Debug($"  · {nodeName}: no geometry extracted");
                }
            }
            catch (Exception ex)
            {
                totalSkipped++;
                SmartConLogger.Debug(
                    $"  ✗ {nodeName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        SmartConLogger.Info(
            $"Geometry extraction summary: {totalProcessed} meshes produced, " +
            $"{totalEmpty} empty, {totalSkipped} failed");

        return result;
    }

    private static MeshData? ExtractMeshFromElement(
        Element element, Options options, string nodeName)
    {
        GeometryElement? geomElem;
        try
        {
            geomElem = element.get_Geometry(options);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"  get_Geometry failed for '{nodeName}': {ex.Message}");
            return null;
        }

        if (geomElem is null)
        {
            SmartConLogger.Debug($"  '{nodeName}': get_Geometry returned null");
            return null;
        }

        var positions = new List<float>(256);
        var indices = new List<int>(512);
        var normals = new List<float>(768);

        try
        {
            CollectMesh(geomElem, positions, indices, normals, ct: default);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"  Geometry traversal failed for '{nodeName}': {ex.Message}");
            return null;
        }

        if (positions.Count < 3 || indices.Count < 3)
        {
            SmartConLogger.Debug(
                $"  '{nodeName}': traversal produced no triangles " +
                $"(positions={positions.Count}, indices={indices.Count})");
            return null;
        }

        return new MeshData(
            Positions: positions.ToArray(),
            Normals: normals.Count == positions.Count ? normals.ToArray() : null,
            Indices: indices.ToArray(),
            DiffuseColor: GetColorFromGeometry(geomElem, element.Document, nodeName),
            NodeName: nodeName);
    }

    /// <summary>
    /// Recursively collect mesh triangles from a GeometryElement. Handles:
    /// - <see cref="Solid"/>: tessellated via <c>SolidUtils.TessellateSolidOrShell</c>
    ///   (fallback: <c>Face.Triangulate(1.0)</c>)
    /// - <see cref="GeometryInstance"/>: recurse into GetInstanceGeometry()
    ///   which applies the instance transform, placing nested-family geometry
    ///   at its correct position within the parent family document.
    ///   (Previous GetSymbolGeometry() returned geometry in symbol-local coords
    ///   at origin (0,0,0), causing nested families to overlap the parent form.)
    /// - <see cref=" Autodesk.Revit.DB.Mesh"/>: faces from imported geometry
    ///   are added directly (topography-style)
    /// </summary>
    private static void CollectMesh(
        GeometryElement geomElem,
        List<float> positions,
        List<int> indices,
        List<float> normals,
        CancellationToken ct)
    {
        int solidCount = 0;
        int instanceCount = 0;
        int directMeshCount = 0;
        int skippedEmptySolid = 0;
        int unknownTypeCount = 0;

        foreach (var geomObj in geomElem)
        {
            ct.ThrowIfCancellationRequested();

            switch (geomObj)
            {
                case Solid solid:
                    if (solid.SurfaceArea <= 0)
                    {
                        // Diagnostic: Revit sometimes returns "null Solid" placeholders
                        // (SurfaceArea=0, Faces.Size=0, Volume=0) when geometry is
                        // type-dependent or absent. Log explicitly so we can
                        // distinguish "skipped empty" from "no objects at all".
                        skippedEmptySolid++;
                        SmartConLogger.Debug(
                            $"    Solid #{solidCount}: SurfaceArea={solid.SurfaceArea:F4} " +
                            $"Faces={solid.Faces?.Size ?? 0} Edges={solid.Edges?.Size ?? 0} " +
                            $"Volume={solid.Volume:F4} → skipped (empty/degenerate)");
                        break;
                    }
                    AddSolid(solid, positions, indices, normals);
                    solidCount++;
                    break;

                case GeometryInstance geomInst:
                    // GetInstanceGeometry() applies the instance Transform,
                    // placing nested-family geometry at its correct position
                    // within the parent family document. GetSymbolGeometry()
                    // returns geometry in symbol-local coords (origin 0,0,0),
                    // which causes nested families to overlap the parent form.
                    var instanceGeom = geomInst.GetInstanceGeometry();
                    if (instanceGeom is not null)
                    {
                        var t = geomInst.Transform;
                        SmartConLogger.Debug(
                            $"    GeometryInstance: Transform origin=({t.Origin.X:F3},{t.Origin.Y:F3},{t.Origin.Z:F3}) " +
                            $"BasisX=({t.BasisX.X:F2},{t.BasisX.Y:F2},{t.BasisX.Z:F2})");
                        CollectMesh(instanceGeom, positions, indices, normals, ct);
                        instanceCount++;
                    }
                    break;

                case Mesh directMesh:
                    AddRevitMesh(directMesh, positions, indices, normals);
                    directMeshCount++;
                    break;

                default:
                    unknownTypeCount++;
                    SmartConLogger.Debug(
                        $"    Unknown geometry type '{geomObj?.GetType().Name ?? "<null>"}' → skipped");
                    break;
            }
        }

        if (solidCount > 0 || instanceCount > 0 || directMeshCount > 0 || skippedEmptySolid > 0 || unknownTypeCount > 0)
        {
            SmartConLogger.Debug(
                $"    CollectMesh: {solidCount} solids, {instanceCount} geometry instances, " +
                $"{directMeshCount} direct meshes, {skippedEmptySolid} empty solids skipped, " +
                $"{unknownTypeCount} unknown types skipped → {positions.Count / 3} verts, {indices.Count / 3} tris");
        }

        // Issue #99 fix: each triangle accumulated its face-normal into shared
        // vertex positions (AccumulateTriangleNormal). Final pass renormalises
        // every accumulated vector to unit length so the GLB consumer (HelixToolkit
        // Phong shader) interprets them as standard glTF normals. Skipping this
        // pass would leave shared vertices with a magnitude proportional to the
        // number of contributing triangles, producing inconsistent diffuse shading.
        if (positions.Count > 0)
        {
            NormalizeVertexNormals(positions, normals);
        }
    }

    private static void AddSolid(Solid solid, List<float> positions, List<int> indices, List<float> normals)
    {
        if (solid.Faces is null || solid.Faces.Size == 0) return;

        var faceCount = solid.Faces.Size;
        var beforeTris = indices.Count / 3;

        // Issue #99 fix (verified via logs 2026-07-03):
        // SolidUtils.TessellateSolidOrShell with LevelOfDetail=1.0 (default,
        // without an explicit Accuracy) produces too few axial segments
        // for small cylinders (10-20mm diameter) due to Revit's internal
        // area-scaled tessellation heuristic. Cylinders > ~30mm display fine
        // because the larger surface area forces more triangles.
        //
        // Setting controls.Accuracy = 0.0001 ft throws an InternalException
        // ("Input 'accuracy' is invalid"), causing the old code to fall back
        // to Face.Triangulate(1.0). That fallback path produced visually
        // superior results (1747 tris for the 16mm pipe bend vs 412 from
        // TessellateSolidOrShell) — so we promote it to the primary path.
        // Per-face triangulation also gives shared vertices that the dedup
        // pass + AccumulateTriangleNormal + NormalizeVertexNormals pipeline
        // turns into smooth (Gouraud-style) normals across face boundaries.
        foreach (Face face in solid.Faces)
        {
            try
            {
                var mesh = face.Triangulate(1.0);
                if (mesh is null || mesh.NumTriangles == 0) continue;
                AddRevitMesh(mesh, positions, indices, normals);
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"    AddSolid: Face.Triangulate(1.0) failed: {ex.Message}");
            }
        }

        var producedTris = indices.Count / 3 - beforeTris;
        SmartConLogger.Debug(
            $"    AddSolid(Face.Triangulate): {faceCount} face(s) → {producedTris} triangles");
    }

    private static void AddRevitMesh(Mesh mesh, List<float> positions, List<int> indices, List<float> normals)
    {
        var vertices = mesh.Vertices;
        var numTriangles = mesh.NumTriangles;
        if (vertices is null || vertices.Count == 0 || numTriangles == 0) return;

        var dedup = new Dictionary<string, int>(vertices.Count);

        for (int i = 0; i < numTriangles; i++)
        {
            MeshTriangle tri;
            try
            {
                tri = mesh.get_Triangle(i);
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"Mesh.get_Triangle({i}) failed: {ex.Message}");
                continue;
            }

            uint i0 = tri.get_Index(0);
            uint i1 = tri.get_Index(1);
            uint i2 = tri.get_Index(2);

            var v0 = vertices[(int)i0];
            var v1 = vertices[(int)i1];
            var v2 = vertices[(int)i2];

            int idx0 = AddVertex(v0, dedup, positions);
            int idx1 = AddVertex(v1, dedup, positions);
            int idx2 = AddVertex(v2, dedup, positions);

            AccumulateTriangleNormal(v0, v1, v2, idx0, idx1, idx2, positions, normals);

            indices.Add(idx0);
            indices.Add(idx1);
            indices.Add(idx2);
        }
    }

    /// <summary>
    /// Accumulates a triangle's face-normal into the per-vertex normal buffers.
    /// Issue #99 fix: previously this method OVERWROTE (assign '=') each shared
    /// vertex's normal with the triangle's face normal, producing flat shading —
    /// each visible facet stood out even at high triangle counts. Switching to
    /// accumulation ('+=') plus a final <see cref="NormalizeVertexNormals"/> pass
    /// produces Gouraud-style smooth shading across shared vertices.
    /// </summary>
    /// <remarks>
    /// Winding: Jeremy Tammik (The Building Coder) documents that Revit's
    /// tessellated mesh vertices are oriented consistently so the cross-product
    /// (v1-v0) × (v2-v0) always points outward from the solid. This guarantees
    /// that accumulating normals from neighbouring triangles averages compatible
    /// outward-facing vectors rather than producing "folded" or inverted normals.
    /// <para>
    /// Degenerate triangles (zero area; cross-product length ≤ 1e-12) are
    /// skipped entirely. The old fallback of "(0,0,1)" on degenerate triangles
    /// corrupted accumulated normals on shared vertices and produced rendering
    /// artefacts on edge seams.
    /// </para>
    /// </remarks>
    private static void AccumulateTriangleNormal(
        XYZ v0, XYZ v1, XYZ v2,
        int idx0, int idx1, int idx2,
        List<float> positions, List<float> normals)
    {
        var ax = v1.X - v0.X;
        var ay = v1.Y - v0.Y;
        var az = v1.Z - v0.Z;
        var bx = v2.X - v0.X;
        var by = v2.Y - v0.Y;
        var bz = v2.Z - v0.Z;
        var nx = ay * bz - az * by;
        var ny = az * bx - ax * bz;
        var nz = ax * by - ay * bx;
        var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
        if (len <= 1e-12)
        {
            // Degenerate triangle (zero area). Skip — do not corrupt the
            // accumulated normals of shared vertices with a fallback vector.
            return;
        }
        nx /= len; ny /= len; nz /= len;

        // Pad normals buffer lazily to match positions buffer length. Each new
        // vertex added via AddVertex pushes 3 floats into positions, so we grow
        // normals to the same length on first write to that index.
        while (normals.Count < positions.Count) normals.Add(0f);
        normals[idx0 * 3] += (float)nx; normals[idx0 * 3 + 1] += (float)ny; normals[idx0 * 3 + 2] += (float)nz;
        normals[idx1 * 3] += (float)nx; normals[idx1 * 3 + 1] += (float)ny; normals[idx1 * 3 + 2] += (float)nz;
        normals[idx2 * 3] += (float)nx; normals[idx2 * 3 + 1] += (float)ny; normals[idx2 * 3 + 2] += (float)nz;
    }

    /// <summary>
    /// Normalises every accumulated vertex normal to unit length. Called after
    /// all triangle contributions have been added via
    /// <see cref="AccumulateTriangleNormal"/>. Without this pass, vertices shared
    /// by many triangles carry an un-normalised sum-of-face-normals vector, which
    /// glTF viewers interpret as a non-unit normal — producing diffuse lighting
    /// brighter than intended (or NaN if magnitude is zero).
    /// </summary>
    private static void NormalizeVertexNormals(List<float> positions, List<float> normals)
    {
        // Pad to the same length as positions to cover vertices that were never
        // touched by a triangle (rare, but possible for orphan vertices created
        // during dedup races).
        while (normals.Count < positions.Count) normals.Add(0f);

        for (int i = 0; i < positions.Count; i += 3)
        {
            var nx = (double)normals[i];
            var ny = (double)normals[i + 1];
            var nz = (double)normals[i + 2];
            var len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            if (len > 1e-12)
            {
                normals[i] = (float)(nx / len);
                normals[i + 1] = (float)(ny / len);
                normals[i + 2] = (float)(nz / len);
            }
            else
            {
                // Vertex with no contributing triangle (degenerate). Use a
                // safe default upward normal rather than leaving (0,0,0).
                normals[i] = 0f;
                normals[i + 1] = 0f;
                normals[i + 2] = 1f;
            }
        }
    }

    private static int AddVertex(XYZ vertex, Dictionary<string, int> dedup, List<float> positions)
    {
        var key = FormattableString.Invariant($"{vertex.X:0.##########}|{vertex.Y:0.##########}|{vertex.Z:0.##########}");
        if (dedup.TryGetValue(key, out var existing))
            return existing;

        var newIdx = positions.Count / 3;
        positions.Add((float)vertex.X);
        positions.Add((float)vertex.Y);
        positions.Add((float)vertex.Z);
        dedup[key] = newIdx;
        return newIdx;
    }

    /// <summary>
    /// Resolves a diffuse color from the element's geometry. Language-independent
    /// and works in family documents where <c>element.Category</c> is null.
    /// Iterates the geometry, finds the first <c>Solid</c> with faces, and reads
    /// <c>Face.MaterialElementId</c> — the material assigned to that face.
    /// Per Jeremy Tammik (The Building Coder): face-level material is the most
    /// reliable source, especially for family documents.
    /// If face material is InvalidElementId ("By Category"), falls back to
    /// <c>doc.OwnerFamily.Category.Material</c>, then <c>FallbackColor</c>.
    /// </summary>
    private static Vector4 GetColorFromGeometry(GeometryElement geomElem, Document doc, string nodeName)
    {
        try
        {
            foreach (var geomObj in geomElem)
            {
                if (geomObj is Solid solid && solid.SurfaceArea > 0 && solid.Faces is not null)
                {
                    foreach (Face face in solid.Faces)
                    {
                        try
                        {
                            var materialId = face.MaterialElementId;
                            if (materialId is not null && materialId != ElementId.InvalidElementId)
                            {
                                var material = doc.GetElement(materialId) as Material;
                                if (material is not null)
                                {
                                    var color = material.Color;
                                    if (color is not null && color.IsValid)
                                    {
                                        var result = new Vector4(
                                            color.Red / 255f,
                                            color.Green / 255f,
                                            color.Blue / 255f,
                                            1f);
                                        SmartConLogger.Debug(
                                            $"GetColorFromGeometry: '{nodeName}' → face material " +
                                            $"'{material.Name}' → RGB({color.Red},{color.Green},{color.Blue}) → {result}");
                                        return result;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            Autodesk.Revit.DB.Family? ownerFamily = null;
            try { ownerFamily = doc.OwnerFamily; } catch { }
            if (ownerFamily?.Category is { } familyCat)
            {
                var catMaterial = familyCat.Material;
                if (catMaterial is not null)
                {
                    var color = catMaterial.Color;
                    if (color is not null && color.IsValid)
                    {
                        var result = new Vector4(
                            color.Red / 255f,
                            color.Green / 255f,
                            color.Blue / 255f,
                            1f);
                        SmartConLogger.Debug(
                            $"GetColorFromGeometry: '{nodeName}' → OwnerFamily.Category " +
                            $"'{familyCat.Name}' material '{catMaterial.Name}' → " +
                            $"RGB({color.Red},{color.Green},{color.Blue}) → {result}");
                        return result;
                    }
                }
            }

            SmartConLogger.Debug(
                $"GetColorFromGeometry: '{nodeName}' → no face material, no category material → FallbackColor");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"GetColorFromGeometry: failed for '{nodeName}': {ex.Message} " +
                "[Action: using fallback gray color for this mesh]");
        }

        return FallbackColor;
    }

    /// <summary>
    /// Resolves the integer value of a Revit <see cref="ElementId"/> across
    /// target frameworks. The IntegerValue property is deprecated in
    /// Revit 2024+ in favor of <see cref="ElementId.Value"/> (which returns
    /// a long). Use of this helper avoids CS0618 across the multi-version build.
    /// </summary>
    private static int GetElementIdInt(ElementId id)
    {
#if REVIT2024_OR_GREATER
        return (int)id.Value;
#else
        return id.IntegerValue;
#endif
    }

    private static int? GetIsVisibleParam(Element element)
    {
        try
        {
            var p = element.get_Parameter(BuiltInParameter.IS_VISIBLE_PARAM);
            if (p is null || !p.HasValue) return null;
            return p.AsInteger();
        }
        catch
        {
            return null;
        }
    }

    private static string FormatNullableInt(int? value) => value.HasValue ? value.Value.ToString() : "<null>";

    /// <summary>
    /// Checks whether a family element is visible at the given detail level.
    /// See #102 for root cause and rationale.
    /// </summary>
    /// <param name="element">A <see cref="GenericForm"/> or nested
    /// <see cref="FamilyInstance"/> in a family document.</param>
    /// <param name="level">Detail level to test (typically
    /// <see cref="ViewDetailLevel.Fine"/> for 3D preview).</param>
    /// <param name="reason">Human-readable reason when element is not visible
    /// at <paramref name="level"/>; <c>null</c> when element is visible or on
    /// fail-open.</param>
    /// <returns><c>true</c> if element is shown at <paramref name="level"/>;
    /// otherwise <c>false</c>.</returns>
    /// <remarks>
    /// <para>
    /// <b>Background:</b> <see cref="Options.IncludeNonVisibleObjects"/> = true
    /// recovers conditionally-visible solids (see commit 5283765) but bypasses
    /// Revit's detail-level filtering. Nested family instances intended only
    /// for Coarse detail level (e.g. "Низкая детализация" symbolic graphics)
    /// would otherwise leak into the Fine-detail 3D preview (see #102).
    /// </para>
    /// <para>
    /// <b>Two paths:</b>
    /// <list type="bullet">
    /// <item><description><see cref="GenericForm"/>: uses
    /// <see cref="GenericForm.GetVisibility()"/> returning a typed
    /// <see cref="FamilyElementVisibility"/> with
    /// <c>IsShownInCoarse/Medium/Fine</c>.</description></item>
    /// <item><description><see cref="FamilyInstance"/> / other: reads
    /// <see cref="BuiltInParameter.GEOM_VISIBILITY_PARAM"/> as a bitfield
    /// (Coarse = 1 &lt;&lt; 13 = 8192, Medium = 1 &lt;&lt; 14 = 16384,
    /// Fine = 1 &lt;&lt; 15 = 32768). Value <c>0</c> means "detail component"
    /// with unconditional visibility (Tammik / Autodesk forum / RevitLookup).
    /// </description></item>
    /// </list>
    /// </para>
    /// <para>
    /// <b>Fail-open:</b> if neither the typed visibility nor the parameter is
    /// available, the element is considered visible — never silently drop
    /// geometry.
    /// </para>
    /// </remarks>
    private static bool IsShownAtDetailLevel(
        Element element, ViewDetailLevel level, out string? reason)
    {
        reason = null;

        // Path 1: GenericForm — typed visibility API
        if (element is GenericForm form)
        {
            FamilyElementVisibility? vis;
            try
            {
                vis = form.GetVisibility();
            }
            catch (Exception ex)
            {
                reason = $"GetVisibility() threw {ex.GetType().Name}";
                return true;
            }

            if (vis is null)
            {
                reason = "GetVisibility() returned null";
                return true;
            }

            bool shown = level switch
            {
                ViewDetailLevel.Coarse => vis.IsShownInCoarse,
                ViewDetailLevel.Medium => vis.IsShownInMedium,
                ViewDetailLevel.Fine => vis.IsShownInFine,
                _ => true
            };

            if (!shown)
            {
                reason = $"GenericForm visibility " +
                         $"Coarse={vis.IsShownInCoarse} " +
                         $"Medium={vis.IsShownInMedium} " +
                         $"Fine={vis.IsShownInFine}";
            }

            return shown;
        }

        // Path 2: FamilyInstance / other — GEOM_VISIBILITY_PARAM bitfield
        Parameter? p;
        try
        {
            p = element.get_Parameter(BuiltInParameter.GEOM_VISIBILITY_PARAM);
        }
        catch (Exception ex)
        {
            reason = $"get_Parameter(GEOM_VISIBILITY_PARAM) threw {ex.GetType().Name}";
            return true;
        }

        if (p is null)
        {
            reason = "GEOM_VISIBILITY_PARAM not found";
            return true;
        }

        int visValue;
        try
        {
            visValue = p.AsInteger();
        }
        catch (Exception ex)
        {
            reason = $"AsInteger() threw {ex.GetType().Name}";
            return true;
        }

        if (visValue == 0)
        {
            // Detail component family — unconditional visibility across all
            // detail levels (Tammik / Autodesk forum / RevitLookup).
            return true;
        }

        const int CoarseBit = 1 << 13; // 8192
        const int MediumBit = 1 << 14; // 16384
        const int FineBit   = 1 << 15; // 32768

        bool result = level switch
        {
            ViewDetailLevel.Coarse => (visValue & CoarseBit) != 0,
            ViewDetailLevel.Medium => (visValue & MediumBit) != 0,
            ViewDetailLevel.Fine   => (visValue & FineBit)   != 0,
            _ => true
        };

        if (!result)
        {
            reason = $"GEOM_VISIBILITY_PARAM value={visValue} " +
                     $"(Coarse={(visValue & CoarseBit) != 0}, " +
                     $"Medium={(visValue & MediumBit) != 0}, " +
                     $"Fine={(visValue & FineBit) != 0})";
        }

        return result;
    }

}
