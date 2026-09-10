using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Core.Compatibility;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Util;

namespace SmartCon.Revit.FamilyManager;

public sealed partial class RevitFamilyGeometryExtractor
{
    internal static FamilyMeshExtractionResult ExtractMeshesFromFamilyDoc(
        Document familyDoc,
        CancellationToken ct)
    {
        var result = new List<MeshData>();
        // #249 (Phase 5): per-type preview INPUT snapshot — collected for
        // the elements that PASS the GLB visibility filters, in the same
        // traversal (the VIEW3D hash mirrors the emitted GLB content).
        var previewForms = new List<FormMetrics>();
        var previewNestedInstances = new List<NestedInstanceSnapshot>();

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

        // Verbose per-element diagnostics only for small families. For families
        // with hundreds of nested instances (rebar hosts etc.) per-element lines
        // produce tens of thousands of log rows per family (see #237) —
        // aggregate counters + the summary below carry the same information.
        var scanVerbose = solidForms.Count + nestedInstances.Count <= ScanVerboseElementThreshold;

        var totalProcessed = 0;
        var totalSkipped = 0;
        var totalEmpty = 0;
        var totalSkippedNotVisible = 0;

        // #249 (Phase 5): content-pure node names — NO ElementIds, NO
        // family/type names in the GLB bytes (CAS normalization). The
        // ordinal is per name-prefix within ONE extraction pass —
        // deterministic for identical document content, while
        // "Extrusion_12345" changed on every SaveAs/reload and made
        // byte-level preview dedup impossible.
        var nodeNameOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        string NextNodeName(string prefix)
        {
            nodeNameOrdinals.TryGetValue(prefix, out var ordinal);
            nodeNameOrdinals[prefix] = ordinal + 1;
            return prefix + "_" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        foreach (var form in solidForms)
        {
            ct.ThrowIfCancellationRequested();
            var formType = form.GetType().Name;
            var nodeName = NextNodeName(formType);

            bool isVisible;
            try { isVisible = form.Visible; }
            catch { isVisible = true; }
            var isVisibleParam = GetIsVisibleParam(form);

            if (scanVerbose)
                SmartConLogger.Debug(
                    $"  Scanning {nodeName}: IsSolid={form.IsSolid}, Visible={isVisible}, " +
                    $"IS_VISIBLE_PARAM={FormatNullableInt(isVisibleParam)}, " +
                    $"Category={form.Category?.Name ?? "<null>"}");

            if (!isVisible)
            {
                totalSkippedNotVisible++;
                if (scanVerbose)
                    SmartConLogger.Info($"  · {nodeName}: skipped (Visible=false)");
                continue;
            }

            if (isVisibleParam == 0)
            {
                totalSkippedNotVisible++;
                if (scanVerbose)
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
                totalSkippedNotVisible++;
                if (scanVerbose)
                    SmartConLogger.Info(
                        $"  · {nodeName}: skipped (not visible at Fine — {detailSkipReason})");
                continue;
            }

            try
            {
                var meshes = ExtractMeshesFromElement(form, options, nodeName, scanVerbose);
                if (meshes.Count > 0)
                {
                    result.AddRange(meshes);
                    totalProcessed += meshes.Count;
                    // #249 (Phase 5): the form produced GLB content — its
                    // FHV12-strengthened metrics join the preview input
                    // snapshot (same visibility filters by construction).
                    try
                    {
                        previewForms.Add(
                            RevitFamilySnapshotExtractor.ExtractFormMetrics(form, options, familyDoc));
                    }
                    catch (Exception metricEx)
                    {
                        SmartConLogger.Debug($"  · {nodeName}: preview metrics failed: {metricEx.Message}");
                    }
                    if (scanVerbose)
                    {
                        var verts = meshes.Sum(m => m.VertexCount);
                        var tris = meshes.Sum(m => m.TriangleCount);
                        SmartConLogger.Debug($"  ✔ {nodeName}: {meshes.Count} mesh(es), {verts} verts, {tris} tris");
                    }
                }
                else
                {
                    totalEmpty++;
                    if (scanVerbose)
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
            var nodeName = NextNodeName($"Nested[{symbolName}]");

            var instVisibleParam = GetIsVisibleParam(inst);
            if (scanVerbose)
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
                totalSkippedNotVisible++;
                if (scanVerbose)
                    SmartConLogger.Info(
                        $"  · {nodeName}: skipped (IS_VISIBLE_PARAM=0)");
                continue;
            }

            if (!IsShownAtDetailLevel(inst, ViewDetailLevel.Fine, out var detailSkipReason))
            {
                totalSkippedNotVisible++;
                if (scanVerbose)
                    SmartConLogger.Info(
                        $"  · {nodeName}: skipped (not visible at Fine — {detailSkipReason})");
                continue;
            }

            try
            {
                var meshes = ExtractMeshesFromElement(inst, options, nodeName, scanVerbose);
                if (meshes.Count > 0)
                {
                    result.AddRange(meshes);
                    totalProcessed += meshes.Count;
                    // #249 (Phase 5): the placement joins the preview input
                    // snapshot (symbol identity + transform + visibility).
                    try
                    {
                        var nestedFamilyName = inst.Symbol?.Family?.Name;
                        if (!string.IsNullOrEmpty(nestedFamilyName))
                        {
                            var transform = inst.GetTransform();
                            previewNestedInstances.Add(new NestedInstanceSnapshot(
                                nestedFamilyName!,
                                inst.Symbol?.Name ?? string.Empty,
                                transform.Origin.X, transform.Origin.Y, transform.Origin.Z,
                                transform.BasisX.X, transform.BasisX.Y, transform.BasisX.Z,
                                transform.BasisY.X, transform.BasisY.Y, transform.BasisY.Z,
                                transform.BasisZ.X, transform.BasisZ.Y, transform.BasisZ.Z,
                                instVisibleParam,
                                // #250: the nested child's own content
                                // fingerprint — a geometry/material edit
                                // inside the child must re-key the pool
                                // even when the placement is untouched.
                                RevitFamilySnapshotExtractor.ComputeNestedContentMetrics(familyDoc, inst, options)));
                        }
                    }
                    catch (Exception nestedEx)
                    {
                        SmartConLogger.Debug($"  · {nodeName}: preview placement failed: {nestedEx.Message}");
                    }
                    if (scanVerbose)
                    {
                        var verts = meshes.Sum(m => m.VertexCount);
                        var tris = meshes.Sum(m => m.TriangleCount);
                        SmartConLogger.Debug($"  ✔ {nodeName}: {meshes.Count} mesh(es), {verts} verts, {tris} tris");
                    }
                }
                else
                {
                    totalEmpty++;
                    if (scanVerbose)
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
            $"{totalEmpty} empty, {totalSkipped} failed, " +
            $"{totalSkippedNotVisible} skipped (not visible)");

        if (result.Count == 0)
        {
            SmartConLogger.Warn(
                $"No meshes extracted from '{familyDoc.Title}': " +
                $"solidForms={solidForms.Count}, nestedInstances={nestedInstances.Count}, " +
                $"totalProcessed={totalProcessed}, totalEmpty={totalEmpty}, totalSkipped={totalSkipped} " +
                "[Action: verify family has visible 3D solids at Fine detail level]");
        }

        return new FamilyMeshExtractionResult(result, previewForms, previewNestedInstances);
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
        Element element, Options options, string nodeName, bool verbose)
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
            if (verbose)
                SmartConLogger.Debug($"  '{nodeName}': get_Geometry returned null");
            return new List<MeshData>();
        }

        var groups = new Dictionary<int, MaterialMeshGroup>();
        try
        {
            CollectMeshWithMaterials(geomElem, groups, verbose, stats: null, ct: default);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug(
                $"  Geometry traversal failed for '{nodeName}': {ex.Message}");
            return new List<MeshData>();
        }

        if (groups.Count == 0)
        {
            if (verbose)
                SmartConLogger.Debug(
                    $"  '{nodeName}': traversal produced no material groups");
            return new List<MeshData>();
        }

        var result = new List<MeshData>(groups.Count);
        foreach (var group in groups.Values)
        {
            if (group.Positions.Count < 3 || group.Indices.Count < 3)
            {
                if (verbose)
                    SmartConLogger.Debug(
                        $"  '{nodeName}' material #{group.MaterialIndex}: no triangles " +
                        $"(positions={group.Positions.Count}, indices={group.Indices.Count})");
                continue;
            }

            var color = GetColorForMaterialId(
                group.MaterialId, element.Document, element, nodeName, verbose);

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
            if (verbose)
                SmartConLogger.Debug(
                    $"  '{nodeName}': all material groups empty after filtering");
        }
        else if (verbose)
        {
            SmartConLogger.Debug(
                $"  '{nodeName}': {result.Count} material group(s) → " +
                $"{result.Sum(m => m.TriangleCount)} total triangles");
        }

        return result;
    }
}
