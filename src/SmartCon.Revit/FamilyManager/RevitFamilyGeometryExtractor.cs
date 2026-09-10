using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Autodesk.Revit.DB;
using SmartCon.Revit.Compatibility;
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
public sealed partial class RevitFamilyGeometryExtractor : IFamilyGeometryExtractor
{
    private readonly IRevitContext _revitContext;

    private static readonly Vector4 FallbackColor = new(0.65f, 0.65f, 0.65f, 1f);

    /// <summary>
    /// Max element count (solid forms + nested instances) for which per-element
    /// diagnostic lines are emitted. Above this threshold only aggregate
    /// counters and the final summary are logged — per-element lines from
    /// families with hundreds of nested instances produced tens of thousands
    /// of rows per family in smartcon.log.
    /// </summary>
    private const int ScanVerboseElementThreshold = 20;

    public RevitFamilyGeometryExtractor(IRevitContext revitContext)
    {
        _revitContext = revitContext ?? throw new ArgumentNullException(nameof(revitContext));
    }

    /// <summary>
    /// True when the family's root category never displays in 3D views
    /// (annotation symbols, tags, title blocks, detail components) — a 3D
    /// preview is meaningless for such families, while the per-type
    /// extraction is expensive (type switch + implicit regeneration;
    /// ~3 s/type on a 4-type title block with 13 nested annotation
    /// instances, stress test 2026-09-07).
    /// <see cref="CategoryType.Annotation"/> covers title blocks, generic
    /// annotations and tags; OST_DetailComponents is API-typed Model but is
    /// view-dependent 2D content ("visible only in those views", Autodesk
    /// help), so it is added explicitly.
    /// </summary>
    public static bool IsViewSpecificPreviewCategory(Category? category)
    {
        if (category is null) return false;
        if (category.CategoryType == CategoryType.Annotation) return true;
        // Category.BuiltInCategory was added in Revit 2023 — multi-version
        // access through CategoryCompat (cast fallback on R19-R22).
        return CategoryCompat.GetBuiltInCategory(category) == BuiltInCategory.OST_DetailComponents;
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

            var rootCategory = doc.OwnerFamily?.FamilyCategory;
            if (IsViewSpecificPreviewCategory(rootCategory))
            {
                SmartConLogger.Info(
                    $"3D preview not applicable: '{rfaFileName}' is a view-specific family " +
                    $"(category '{rootCategory!.Name}') — annotation/detail content never displays " +
                    "in 3D views; per-type geometry extraction skipped");
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

    /// <summary>
    /// Result of one family-document mesh pass: the GLB meshes AND the
    /// per-type preview INPUT snapshot (Issue #249, Phase 5) — the
    /// GLB-filtered forms/nested instances with FHV12-strengthened
    /// metrics, collected in the SAME traversal (one geometry read per
    /// element, identical visibility filters) so the VIEW3D hash always
    /// mirrors what the GLB writer emits.
    /// </summary>
    internal sealed record FamilyMeshExtractionResult(
        List<MeshData> Meshes,
        List<FormMetrics> PreviewForms,
        List<NestedInstanceSnapshot> PreviewNestedInstances);

}
