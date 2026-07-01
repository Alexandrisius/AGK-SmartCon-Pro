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

    public Task<FamilyGeometryPreview?> ExtractAsync(
        string managedRfaPath,
        string catalogItemId,
        string versionLabel,
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
            return Task.FromResult<FamilyGeometryPreview?>(null);
        }

        using var _scope = SmartConLogger.BeginScope("Geo3DExtract",
            ("Method", nameof(ExtractAsync)),
            ("CatalogItemId", catalogItemId),
            ("VersionLabel", versionLabel),
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
                    return Task.FromResult<FamilyGeometryPreview?>(null);
                }
            }

            if (doc is null)
            {
                SmartConLogger.Warn(
                    $"OpenDocumentFile returned null for '{rfaFileName}' " +
                    "[Action: check for corrupted .rfa or Revit version mismatch]");
                return Task.FromResult<FamilyGeometryPreview?>(null);
            }

            if (!doc.IsFamilyDocument)
            {
                SmartConLogger.Warn(
                    $"Document '{rfaFileName}' is not a family document — skipping geometry extraction " +
                    "[Action: 3D preview is only generated for loadable .rfa families]");
                return Task.FromResult<FamilyGeometryPreview?>(null);
            }

            var familyName = doc.Title.EndsWith(".rfa", StringComparison.OrdinalIgnoreCase)
                ? doc.Title[..^4]
                : doc.Title;

            var meshes = ExtractMeshesFromFamilyDoc(doc, ct);

            if (meshes.Count == 0)
            {
                SmartConLogger.Warn(
                    $"No visible geometry found in '{rfaFileName}' " +
                    "[Action: verify family has visible GenericForm elements with 3D solids; preview will be empty]");
                return Task.FromResult<FamilyGeometryPreview?>(null);
            }

            var preview = new FamilyGeometryPreview(catalogItemId, versionLabel, familyName, meshes);

            SmartConLogger.Info(
                $"Geometry extraction complete: '{rfaFileName}', {meshes.Count} meshes, " +
                $"{preview.TotalVertexCount} verts, {preview.TotalTriangleCount} tris");

            return Task.FromResult<FamilyGeometryPreview?>(preview);
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

                RevitBalloonNudge.Nudge($"SmartCon: extracted 3D preview for {rfaFileName}");
            }
        }
    }

    private static List<MeshData> ExtractMeshesFromFamilyDoc(Document familyDoc, CancellationToken ct)
    {
        var result = new List<MeshData>();

        var options = new Options
        {
            ComputeReferences = false,
            DetailLevel = ViewDetailLevel.Fine,
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

            SmartConLogger.Info(
                $"  Scanning {nodeName}: IsSolid={form.IsSolid}, Visible={isVisible}, " +
                $"Category={form.Category?.Name ?? "<null>"}");

            try
            {
                var bb = form.get_BoundingBox(null);
                SmartConLogger.Info(
                    $"  {nodeName} BoundingBox: " +
                    (bb is null
                        ? "null (no 3D geometry)"
                        : $"min=({bb.Min.X:F3},{bb.Min.Y:F3},{bb.Min.Z:F3}) " +
                          $"max=({bb.Max.X:F3},{bb.Max.Y:F3},{bb.Max.Z:F3})"));
            }
            catch { }

            try
            {
                var mesh = ExtractMeshFromElement(form, options, nodeName);
                if (mesh is not null && !mesh.IsEmpty)
                {
                    result.Add(mesh);
                    totalProcessed++;
                    SmartConLogger.Info(
                        $"  ✔ {nodeName}: {mesh.VertexCount} verts, {mesh.TriangleCount} tris");
                }
                else
                {
                    totalEmpty++;
                    SmartConLogger.Info($"  · {nodeName}: no geometry extracted");
                }
            }
            catch (Exception ex)
            {
                totalSkipped++;
                SmartConLogger.Info(
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

            try
            {
                var mesh = ExtractMeshFromElement(inst, options, nodeName);
                if (mesh is not null && !mesh.IsEmpty)
                {
                    result.Add(mesh);
                    totalProcessed++;
                    SmartConLogger.Info(
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

        int geomCount;
        try { geomCount = geomElem.Count(); }
        catch { geomCount = -1; }

        var typeHistogram = new Dictionary<string, int>();
        int solidWithFaces = 0;
        int solidEmpty = 0;
        int instanceCount = 0;
        int meshCount = 0;
        int curveCount = 0;
        int otherCount = 0;

        foreach (var g in geomElem)
        {
            var tn = g?.GetType().Name ?? "<null>";
            typeHistogram.TryGetValue(tn, out var tnCount);
            typeHistogram[tn] = tnCount + 1;

            if (g is Solid s)
            {
                if (s.Faces is not null && s.Faces.Size > 0 && s.SurfaceArea > 0)
                    solidWithFaces++;
                else
                    solidEmpty++;
            }
            else if (g is GeometryInstance) instanceCount++;
            else if (g is Mesh) meshCount++;
            else if (g is Curve) curveCount++;
            else otherCount++;
        }

        SmartConLogger.Info(
            $"  '{nodeName}': geomCount={geomCount}, solids(valid={solidWithFaces},empty={solidEmpty}), " +
            $"instances={instanceCount}, meshes={meshCount}, curves={curveCount}, other={otherCount}");

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
            SmartConLogger.Info(
                $"  '{nodeName}': traversal produced no triangles " +
                $"(positions={positions.Count}, indices={indices.Count})");
            return null;
        }

        return new MeshData(
            Positions: positions.ToArray(),
            Normals: normals.Count == positions.Count ? normals.ToArray() : null,
            Indices: indices.ToArray(),
            DiffuseColor: FallbackColor,
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

        foreach (var geomObj in geomElem)
        {
            ct.ThrowIfCancellationRequested();

            switch (geomObj)
            {
                case Solid solid:
                    if (solid.SurfaceArea <= 0) break;
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
            }
        }

        if (solidCount > 0 || instanceCount > 0 || directMeshCount > 0)
        {
            SmartConLogger.Debug(
                $"    CollectMesh: {solidCount} solids, {instanceCount} geometry instances, " +
                $"{directMeshCount} direct meshes → {positions.Count / 3} verts, {indices.Count / 3} tris");
        }
    }

    private static void AddSolid(Solid solid, List<float> positions, List<int> indices, List<float> normals)
    {
        if (solid.Faces is null || solid.Faces.Size == 0) return;

        var faceCount = solid.Faces.Size;
        var beforeTris = indices.Count / 3;

        try
        {
            using var controls = new SolidOrShellTessellationControls();
            controls.LevelOfDetail = 1.0;

            using var tessellated = SolidUtils.TessellateSolidOrShell(solid, controls);
            if (tessellated is not null && tessellated.ShellComponentCount > 0)
            {
                for (int ci = 0; ci < tessellated.ShellComponentCount; ci++)
                {
                    TriangulatedShellComponent? component;
                    try
                    {
                        component = tessellated.GetShellComponent(ci);
                    }
                    catch (Exception ex)
                    {
                        SmartConLogger.Debug(
                            $"    AddSolid: GetShellComponent({ci}) failed: {ex.Message}");
                        continue;
                    }

                    if (component is null || component.TriangleCount == 0) continue;
                    AddTriangulatedComponent(component, positions, indices, normals);
                }

                var afterTris = indices.Count / 3;
                SmartConLogger.Debug(
                    $"    AddSolid(TessellateSolidOrShell): {faceCount} faces → {afterTris - beforeTris} triangles");
                return;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"    AddSolid: TessellateSolidOrShell failed ({ex.GetType().Name}), " +
                $"falling back to Face.Triangulate: {ex.Message}");
        }

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
                SmartConLogger.Debug($"    AddSolid: Face.Triangulate failed: {ex.Message}");
            }
        }

        var fallbackTris = indices.Count / 3 - beforeTris;
        SmartConLogger.Debug(
            $"    AddSolid(Face.Triangulate fallback): {faceCount} faces → {fallbackTris} triangles");
    }

    private static void AddTriangulatedComponent(
        TriangulatedShellComponent component,
        List<float> positions,
        List<int> indices,
        List<float> normals)
    {
        var vertexCount = component.VertexCount;
        if (vertexCount == 0 || component.TriangleCount == 0) return;

        var vertices = new List<XYZ>(vertexCount);
        for (int vi = 0; vi < vertexCount; vi++)
        {
            try
            {
                vertices.Add(component.GetVertex(vi));
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug(
                    $"    AddTriangulatedComponent: GetVertex({vi}) failed: {ex.Message}");
                return;
            }
        }

        var numTriangles = component.TriangleCount;
        var dedup = new Dictionary<string, int>(vertexCount);

        for (int i = 0; i < numTriangles; i++)
        {
            TriangleInShellComponent tri;
            try
            {
                tri = component.GetTriangle(i);
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"    AddTriangulatedComponent: GetTriangle({i}) failed: {ex.Message}");
                continue;
            }

            var v0 = vertices[tri.VertexIndex0];
            var v1 = vertices[tri.VertexIndex1];
            var v2 = vertices[tri.VertexIndex2];

            int idx0 = AddVertex(v0, dedup, positions);
            int idx1 = AddVertex(v1, dedup, positions);
            int idx2 = AddVertex(v2, dedup, positions);

            AddTriangleNormals(v0, v1, v2, idx0, idx1, idx2, positions, normals);

            indices.Add(idx0);
            indices.Add(idx1);
            indices.Add(idx2);
        }
    }

    /// <summary>
    /// Read vertices and triangle indices from a Revit <see cref="Mesh"/>. The
    /// Revit Mesh provides a <see cref="Mesh.Vertices"/> array and an indexer
    /// <see cref="Mesh.this[int]"/> returning a <see cref="MeshTriangle"/> whose
    /// own indexer exposes the three vertex indices. We deduplicate vertices
    /// by exact position using a float-based key (Revit meshes are often
    /// heavily degenerate — same XYZ repeated across triangles).
    /// </summary>
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

            AddTriangleNormals(v0, v1, v2, idx0, idx1, idx2, positions, normals);

            indices.Add(idx0);
            indices.Add(idx1);
            indices.Add(idx2);
        }
    }

    private static void AddTriangleNormals(
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
        if (len > 1e-12)
        {
            nx /= len; ny /= len; nz /= len;
        }
        else
        {
            nx = 0; ny = 0; nz = 1;
        }

        while (normals.Count < positions.Count) normals.Add(0f);
        normals[idx0 * 3] = (float)nx; normals[idx0 * 3 + 1] = (float)ny; normals[idx0 * 3 + 2] = (float)nz;
        normals[idx1 * 3] = (float)nx; normals[idx1 * 3 + 1] = (float)ny; normals[idx1 * 3 + 2] = (float)nz;
        normals[idx2 * 3] = (float)nx; normals[idx2 * 3 + 1] = (float)ny; normals[idx2 * 3 + 2] = (float)nz;
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
}
