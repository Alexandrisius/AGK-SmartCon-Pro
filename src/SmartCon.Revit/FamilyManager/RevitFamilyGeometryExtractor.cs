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
/// tessellating each <c>Solid</c>'s faces via <c>Face.Triangulate(1.0)</c>,
/// grouping faces by <c>Face.MaterialElementId</c> into per-material
/// <see cref="MeshData"/> entries, and returning them ready for GLB export.
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
    /// <b>Triangulation (Issue #99 + #108):</b> each <c>Face</c> is
    /// tessellated via <c>Face.Triangulate(1.0)</c> per face. This produces
    /// consistent high-density tessellation regardless of solid size (Issue
    /// #99: 1747 tris for 16mm pipe bend vs 412 from
    /// <c>TessellateSolidOrShell</c>) and allows grouping faces by
    /// <c>Face.MaterialElementId</c> so each material becomes a separate
    /// <see cref="MeshData"/> with its own <c>DiffuseColor</c> (Issue #108:
    /// per-face material extraction). Vertices are deduplicated within a
    /// material group; <c>AccumulateTriangleNormal</c> +
    /// <c>NormalizeVertexNormals</c> produce smooth (Gouraud-style) normals
    /// across face boundaries inside the same material, with hard edges
    /// between materials (vertices are not shared between groups).
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
                var meshes = ExtractMeshesFromElement(form, options, nodeName);
                if (meshes.Count > 0)
                {
                    result.AddRange(meshes);
                    totalProcessed += meshes.Count;
                    var verts = meshes.Sum(m => m.VertexCount);
                    var tris = meshes.Sum(m => m.TriangleCount);
                    SmartConLogger.Debug(
                        $"  ✔ {nodeName}: {meshes.Count} mesh(es), {verts} verts, {tris} tris");
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
                var meshes = ExtractMeshesFromElement(inst, options, nodeName);
                if (meshes.Count > 0)
                {
                    result.AddRange(meshes);
                    totalProcessed += meshes.Count;
                    var verts = meshes.Sum(m => m.VertexCount);
                    var tris = meshes.Sum(m => m.TriangleCount);
                    SmartConLogger.Debug(
                        $"  ✔ {nodeName}: {meshes.Count} mesh(es), {verts} verts, {tris} tris");
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

        if (result.Count == 0)
        {
            SmartConLogger.Warn(
                $"No meshes extracted from '{familyDoc.Title}': " +
                $"solidForms={solidForms.Count}, nestedInstances={nestedInstances.Count}, " +
                $"totalProcessed={totalProcessed}, totalEmpty={totalEmpty}, totalSkipped={totalSkipped} " +
                "[Action: verify family has visible 3D solids at Fine detail level]");
        }

        return result;
    }

    /// <summary>
    /// Issue #108 fix: extracts one <see cref="MeshData"/> per distinct
    /// <c>Face.MaterialElementId</c> encountered in the element's geometry.
    /// Previously this method built a single mesh with one color (first face
    /// material), producing uniformly colored models for families with
    /// multiple materials. Per-face grouping also fixes nested
    /// <c>FamilyInstance</c>: <c>GetColorFromGeometry</c> only checked
    /// top-level <c>Solid</c> objects and missed <c>GeometryInstance</c>,
    /// so nested families always fell back to <c>FallbackColor</c>.
    /// </summary>
    /// <remarks>
    /// <b>Algorithm:</b>
    /// <list type="bullet">
    /// <item><description>Traverse <c>geomElem</c> recursively (Solid +
    /// GeometryInstance via <c>GetInstanceGeometry()</c> — Jeremy Tammik,
    /// The Building Coder a/0026_instance_materials.htm).</description></item>
    /// <item><description>For each <c>Face</c>: read
    /// <c>Face.MaterialElementId</c>, triangulate, append triangles to the
    /// <see cref="MaterialMeshGroup"/> for that material id.</description></item>
    /// <item><description>After traversal: normalise vertex normals per group
    /// (smooth shading within a material, hard edges between materials),
    /// resolve each material id to a <c>DiffuseColor</c> via
    /// <see cref="GetColorForMaterialId"/>, and emit one
    /// <see cref="MeshData"/> per non-empty group.</description></item>
    /// </list>
    /// </remarks>
    private static List<MeshData> ExtractMeshesFromElement(
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
            return new List<MeshData>();
        }

        if (geomElem is null)
        {
            SmartConLogger.Debug($"  '{nodeName}': get_Geometry returned null");
            return new List<MeshData>();
        }

        var groups = new Dictionary<int, MaterialMeshGroup>();
        try
        {
            CollectMeshWithMaterials(geomElem, groups, ct: default);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"  Geometry traversal failed for '{nodeName}': {ex.Message}");
            return new List<MeshData>();
        }

        if (groups.Count == 0)
        {
            SmartConLogger.Debug(
                $"  '{nodeName}': traversal produced no material groups");
            return new List<MeshData>();
        }

        var result = new List<MeshData>(groups.Count);
        foreach (var group in groups.Values)
        {
            if (group.Positions.Count < 3 || group.Indices.Count < 3)
            {
                SmartConLogger.Debug(
                    $"  '{nodeName}' material #{group.MaterialIndex}: no triangles " +
                    $"(positions={group.Positions.Count}, indices={group.Indices.Count})");
                continue;
            }

            var color = GetColorForMaterialId(
                group.MaterialId, element.Document, element, nodeName);

            var materialSuffix = groups.Count > 1
                ? "__mat" + group.MaterialIndex.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "";

            result.Add(new MeshData(
                Positions: group.Positions.ToArray(),
                Normals: group.Normals.Count == group.Positions.Count
                    ? group.Normals.ToArray() : null,
                Indices: group.Indices.ToArray(),
                DiffuseColor: color,
                NodeName: nodeName + materialSuffix));
        }

        if (result.Count == 0)
        {
            SmartConLogger.Debug(
                $"  '{nodeName}': all material groups empty after filtering");
        }
        else
        {
            SmartConLogger.Debug(
                $"  '{nodeName}': {result.Count} material group(s) → " +
                $"{result.Sum(m => m.TriangleCount)} total triangles");
        }

        return result;
    }

    /// <summary>
    /// Per-material accumulator. One instance per distinct
    /// <c>Face.MaterialElementId</c> encountered while traversing an element's
    /// geometry. Holds the vertex/index/normal buffers for a single material
    /// group so <see cref="ExtractMeshesFromElement"/> can emit one
    /// <see cref="MeshData"/> per material.
    /// </summary>
    /// <remarks>
    /// <b>No shared dedup dictionary:</b> vertex deduplication is performed
    /// per-<c>Face</c> inside <see cref="AddRevitMeshToGroup"/> (a fresh
    /// <c>Dictionary</c> per call), NOT stored on this group. This preserves
    /// hard edges between faces of the same material (e.g. a cylinder rim
    /// stays sharp) while keeping smooth shading within a single face. See
    /// <see cref="AddRevitMeshToGroup"/> doc for the Issue #108 regression
    /// rationale.
    /// </remarks>
    private sealed class MaterialMeshGroup
    {
        public ElementId MaterialId { get; }
        public int MaterialIndex { get; }
        public List<float> Positions { get; } = new();
        public List<int> Indices { get; } = new();
        public List<float> Normals { get; } = new();

        public MaterialMeshGroup(ElementId materialId, int index)
        {
            MaterialId = materialId;
            MaterialIndex = index;
        }
    }

    /// <summary>
    /// Recursively collect mesh triangles from a GeometryElement, grouping
    /// faces by <c>Face.MaterialElementId</c> into separate
    /// <see cref="MaterialMeshGroup"/> entries. Issue #108 fix: this replaces
    /// the single-buffer <c>CollectMesh</c> so each material becomes its own
    /// <see cref="MeshData"/> with its own <c>DiffuseColor</c>.
    /// <para>
    /// Handles:
    /// <list type="bullet">
    /// <item><description><see cref="Solid"/>: each <c>Face</c> is triangulated
    /// via <c>Face.Triangulate(1.0)</c> and appended to the group for that
    /// face's <c>MaterialElementId</c>.</description></item>
    /// <item><description><see cref="GeometryInstance"/>: recurse into
    /// <c>GetInstanceGeometry()</c> which applies the instance transform,
    /// placing nested-family geometry at its correct position within the
    /// parent family document. This is the critical fix for nested
    /// <c>FamilyInstance</c> — the old <c>GetColorFromGeometry</c> did not
    /// descend into <c>GeometryInstance</c>, so nested families always got
    /// <c>FallbackColor</c>. Jeremy Tammik (The Building Coder
    /// a/0026_instance_materials.htm) confirms the recursion pattern.</description></item>
    /// <item><description><see cref="Mesh"/>: direct meshes (imported geometry,
    /// topography-style) have no per-face material — appended to the
    /// <c>InvalidElementId</c> fallback group.</description></item>
    /// </list>
    /// </para>
    /// </summary>
    private static void CollectMeshWithMaterials(
        GeometryElement geomElem,
        Dictionary<int, MaterialMeshGroup> groups,
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
                        skippedEmptySolid++;
                        SmartConLogger.Debug(
                            $"    Solid #{solidCount}: SurfaceArea={solid.SurfaceArea:F4} " +
                            $"Faces={solid.Faces?.Size ?? 0} Edges={solid.Edges?.Size ?? 0} " +
                            $"Volume={solid.Volume:F4} → skipped (empty/degenerate)");
                        break;
                    }
                    AddSolidWithMaterials(solid, groups);
                    solidCount++;
                    break;

                case GeometryInstance geomInst:
                    var instanceGeom = geomInst.GetInstanceGeometry();
                    if (instanceGeom is not null)
                    {
                        var t = geomInst.Transform;
                        SmartConLogger.Debug(
                            $"    GeometryInstance: Transform origin=({t.Origin.X:F3},{t.Origin.Y:F3},{t.Origin.Z:F3}) " +
                            $"BasisX=({t.BasisX.X:F2},{t.BasisX.Y:F2},{t.BasisX.Z:F2})");
                        CollectMeshWithMaterials(instanceGeom, groups, ct);
                        instanceCount++;
                    }
                    break;

                case Mesh directMesh:
                    AddDirectMeshToGroup(directMesh, groups);
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
            var totalVerts = groups.Values.Sum(g => g.Positions.Count / 3);
            var totalTris = groups.Values.Sum(g => g.Indices.Count / 3);
            SmartConLogger.Debug(
                $"    CollectMeshWithMaterials: {solidCount} solids, {instanceCount} geometry instances, " +
                $"{directMeshCount} direct meshes, {skippedEmptySolid} empty solids skipped, " +
                $"{unknownTypeCount} unknown types skipped → {groups.Count} material group(s), " +
                $"{totalVerts} verts, {totalTris} tris");
        }

        // Issue #99 fix: renormalise accumulated vertex normals per group so
        // the GLB consumer (HelixToolkit Phong shader) interprets them as
        // standard glTF normals. Per-group normalisation means smooth shading
        // within a material and hard edges between materials (vertices are not
        // shared between groups — each group has its own dedup dictionary).
        foreach (var group in groups.Values)
        {
            if (group.Positions.Count > 0)
            {
                NormalizeVertexNormals(group.Positions, group.Normals);
            }
        }
    }

    /// <summary>
    /// Triangulates each face of a solid via <c>Face.Triangulate(1.0)</c> and
    /// appends the triangles to the <see cref="MaterialMeshGroup"/> matching
    /// the face's <c>MaterialElementId</c>. Issue #108 fix: this replaces the
    /// single-buffer <c>AddSolid</c> so faces with different materials end up
    /// in separate groups (and thus separate <see cref="MeshData"/> entries).
    /// </summary>
    /// <remarks>
    /// <b>Tessellation choice (Issue #99):</b> <c>SolidUtils.TessellateSolidOrShell</c>
    /// with <c>LevelOfDetail=1.0</c> produces too few axial segments for small
    /// cylinders (10-20mm diameter) due to Revit's internal area-scaled
    /// tessellation heuristic. Per-face <c>Face.Triangulate(1.0)</c> gives
    /// consistent high-density tessellation regardless of solid size (1747 tris
    /// for the 16mm pipe bend vs 412 from <c>TessellateSolidOrShell</c>).
    /// Per-face triangulation also gives shared vertices within a material
    /// group that the dedup pass + <c>AccumulateTriangleNormal</c> +
    /// <c>NormalizeVertexNormals</c> pipeline turns into smooth (Gouraud-style)
    /// normals across face boundaries inside the same material.
    /// </remarks>
    private static void AddSolidWithMaterials(
        Solid solid, Dictionary<int, MaterialMeshGroup> groups)
    {
        if (solid.Faces is null || solid.Faces.Size == 0) return;

        var faceCount = solid.Faces.Size;
        var beforeTris = groups.Values.Sum(g => g.Indices.Count / 3);

        foreach (Face face in solid.Faces)
        {
            try
            {
                var mesh = face.Triangulate(1.0);
                if (mesh is null || mesh.NumTriangles == 0) continue;

                var materialId = face.MaterialElementId;
                var key = GetElementIdInt(materialId);
                if (!groups.TryGetValue(key, out var group))
                {
                    group = new MaterialMeshGroup(materialId, groups.Count);
                    groups[key] = group;
                }
                AddRevitMeshToGroup(mesh, group);
            }
            catch (Exception ex)
            {
                SmartConLogger.Debug($"    AddSolidWithMaterials: Face.Triangulate(1.0) failed: {ex.Message}");
            }
        }

        var producedTris = groups.Values.Sum(g => g.Indices.Count / 3) - beforeTris;
        SmartConLogger.Debug(
            $"    AddSolidWithMaterials(Face.Triangulate): {faceCount} face(s) → {producedTris} triangles");
    }

    /// <summary>
    /// Appends triangles from a Revit <see cref="Mesh"/> (from
    /// <c>Face.Triangulate</c>) into a single <see cref="MaterialMeshGroup"/>.
    /// Vertex normals are accumulated per-triangle (smooth shading within a
    /// face) and normalised in a final pass by
    /// <see cref="CollectMeshWithMaterials"/>.
    /// </summary>
    /// <remarks>
    /// <b>Per-face dedup (Issue #108 regression fix):</b> the dedup dictionary
    /// is created <b>fresh on every call</b> — one call corresponds to one
    /// Revit <c>Face</c>. Vertices on the seam between two faces (e.g. the
    /// rim of a cylinder cap meeting its side surface) are therefore
    /// <b>not</b> shared across faces even when they belong to the same
    /// material group. This preserves hard edges between faces (sharp cylinder
    /// rims) while keeping smooth (Gouraud) shading <i>within</i> a face.
    /// <para>
    /// The previous version of this method (during Issue #108 development)
    /// reused <c>group.Dedup</c> across all faces in a material group, which
    /// merged seam vertices and produced smooth shading across face
    /// boundaries — making cylinder rims look rounded and individual
    /// triangles visible (user-reported regression 2026-07-06). Restoring
    /// per-call dedup restores the pre-Issue-#108 visual quality while
    /// keeping the per-material grouping and color resolution fixes.
    /// </para>
    /// <para>
    /// <b>Vertex count note:</b> per-face dedup means a vertex on an N-face
    /// seam is stored N times (once per face) instead of once. This matches
    /// the pre-Issue-#108 behaviour and is the price of hard edges. Triangle
    /// count is unchanged.
    /// </para>
    /// </remarks>
    private static void AddRevitMeshToGroup(Mesh mesh, MaterialMeshGroup group)
    {
        var vertices = mesh.Vertices;
        var numTriangles = mesh.NumTriangles;
        if (vertices is null || vertices.Count == 0 || numTriangles == 0) return;

        var positions = group.Positions;
        var indices = group.Indices;
        var normals = group.Normals;
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
    /// Appends a direct <see cref="Mesh"/> (imported geometry, topography-style)
    /// to the <c>InvalidElementId</c> fallback group. Direct meshes carry no
    /// per-face material information, so they all land in the same group and
    /// receive <c>FallbackColor</c> via <see cref="GetColorForMaterialId"/>.
    /// </summary>
    private static void AddDirectMeshToGroup(
        Mesh mesh, Dictionary<int, MaterialMeshGroup> groups)
    {
        var key = GetElementIdInt(ElementId.InvalidElementId);
        if (!groups.TryGetValue(key, out var group))
        {
            group = new MaterialMeshGroup(ElementId.InvalidElementId, groups.Count);
            groups[key] = group;
        }
        AddRevitMeshToGroup(mesh, group);
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
    /// Resolves a <see cref="MaterialMeshGroup"/>'s <c>MaterialElementId</c>
    /// to a diffuse <see cref="Vector4"/> color. Issue #108 fix: this replaces
    /// <c>GetColorFromGeometry</c> which iterated the geometry and took the
    /// first face material (missing <c>GeometryInstance</c> for nested
    /// families). The new method receives the material id directly from
    /// <see cref="AddSolidWithMaterials"/> (which already traverses
    /// <c>GeometryInstance</c> via <see cref="CollectMeshWithMaterials"/>),
    /// so nested <c>FamilyInstance</c> faces now reach this resolver with
    /// their real material id instead of falling through to
    /// <c>FallbackColor</c>.
    /// </summary>
    /// <remarks>
    /// <b>Fallback chain</b> (Autodesk Revit API Developer Guide,
    /// "Element Material"): when <c>Face.MaterialElementId</c> is
    /// <c>InvalidElementId</c> the material is "By Category" — the API guide
    /// says: "If the material property is set to By Category in the UI, the
    /// ElementId for the material is ElementId.InvalidElementId and cannot be
    /// used to retrieve the Material object. Try retrieving the Material from
    /// Category." The chain below covers family documents where
    /// <c>element.Category</c> may be null:
    /// <list type="number">
    /// <item><description><paramref name="materialId"/> →
    /// <c>doc.GetElement(materialId) as Material</c> →
    /// <c>Material.Color</c>.</description></item>
    /// <item><description><c>element.Category.Material</c> (By Category for
    /// this element).</description></item>
    /// <item><description>For <c>FamilyInstance</c>:
    /// <c>inst.Symbol.Family.Category.Material</c> (nested family
    /// category).</description></item>
    /// <item><description><c>doc.OwnerFamily.Category.Material</c> (family
    /// document owner category).</description></item>
    /// <item><description><see cref="FallbackColor"/> (neutral gray
    /// 0.65).</description></item>
    /// </list>
    /// <para>
    /// <b>Appearance Asset Color</b> (Phase 2): <c>Material.Color</c> can be
    /// invalid for materials imported from Rhino/SAT (Autodesk forum
    /// 8948589). Reading the appearance asset's <c>generic_diffuse</c>
    /// property via <c>AppearanceAssetElement.GetRenderingAsset()</c> would
    /// recover the render color, but requires version-specific
    /// <c>AssetPropertyDoubleArray4d</c> handling. Out of scope for this fix
    /// — the vast majority of family materials carry a valid
    /// <c>Material.Color</c>.
    /// </para>
    /// </remarks>
    private static Vector4 GetColorForMaterialId(
        ElementId materialId, Document doc, Element element, string nodeName)
    {
        string? source = null;

        try
        {
            // 1. Face material (explicit material assignment on the face)
            if (materialId is not null && materialId != ElementId.InvalidElementId)
            {
                var material = doc.GetElement(materialId) as Material;
                if (material is not null &&
                    TryGetMaterialColor(material, out var r, out var g, out var b))
                {
                    var result = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
                    source = "face material '" + material.Name + "'";
                    SmartConLogger.Debug(
                        $"GetColorForMaterialId: '{nodeName}' → {source} → " +
                        $"RGB({r},{g},{b}) → {result}");
                    return result;
                }
            }

            // 2. element.Category.Material (By Category for this element)
            try
            {
                var elemCat = element.Category;
                if (elemCat?.Material is { } catMat &&
                    TryGetMaterialColor(catMat, out var r, out var g, out var b))
                {
                    var result = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
                    source = "element.Category '" + elemCat.Name + "' material '" + catMat.Name + "'";
                    SmartConLogger.Debug(
                        $"GetColorForMaterialId: '{nodeName}' → {source} → " +
                        $"RGB({r},{g},{b}) → {result}");
                    return result;
                }
            }
            catch { }

            // 3. FamilyInstance: inst.Symbol.Family.Category.Material
            //    (nested family category — the typical path for nested
            //    FamilyInstance elements whose own Category is null)
            if (element is FamilyInstance inst)
            {
                try
                {
                    var famCat = inst.Symbol?.Family?.Category;
                    if (famCat?.Material is { } famCatMat &&
                        TryGetMaterialColor(famCatMat, out var r, out var g, out var b))
                    {
                        var result = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
                        source = "inst.Symbol.Family.Category '" + famCat.Name + "' material '" + famCatMat.Name + "'";
                        SmartConLogger.Debug(
                            $"GetColorForMaterialId: '{nodeName}' → {source} → " +
                            $"RGB({r},{g},{b}) → {result}");
                        return result;
                    }
                }
                catch { }
            }

            // 4. doc.OwnerFamily.Category.Material (family document owner)
            try
            {
                var ownerFamily = doc.OwnerFamily;
                if (ownerFamily?.Category is { } familyCat)
                {
                    var catMat = familyCat.Material;
                    if (catMat is not null &&
                        TryGetMaterialColor(catMat, out var r, out var g, out var b))
                    {
                        var result = new Vector4(r / 255f, g / 255f, b / 255f, 1f);
                        source = "OwnerFamily.Category '" + familyCat.Name + "' material '" + catMat.Name + "'";
                        SmartConLogger.Debug(
                            $"GetColorForMaterialId: '{nodeName}' → {source} → " +
                            $"RGB({r},{g},{b}) → {result}");
                        return result;
                    }
                }
            }
            catch { }

            SmartConLogger.Debug(
                $"GetColorForMaterialId: '{nodeName}' → no face material, no category material → FallbackColor");
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn(
                $"GetColorForMaterialId: failed for '{nodeName}': {ex.Message} " +
                "[Action: using fallback gray color for this mesh]");
        }

        return FallbackColor;
    }

    /// <summary>
    /// Reads <c>Material.Color</c> defensively using the TryGet pattern.
    /// Returns <c>false</c> if the material is null, the color is null, or
    /// <c>Color.IsValid</c> is false (e.g. for materials imported from
    /// Rhino/SAT — Autodesk forum 8948589). On success, <paramref name="red"/>,
    /// <paramref name="green"/>, <paramref name="blue"/> are set to the
    /// material's 0-255 RGB values.
    /// </summary>
    private static bool TryGetMaterialColor(
        Material material, out byte red, out byte green, out byte blue)
    {
        red = 0; green = 0; blue = 0;
        try
        {
            var color = material.Color;
            if (color is not null && color.IsValid)
            {
                red = color.Red;
                green = color.Green;
                blue = color.Blue;
                return true;
            }
        }
        catch { }
        return false;
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
