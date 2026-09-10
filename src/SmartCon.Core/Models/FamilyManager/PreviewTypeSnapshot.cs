namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Per-type 3D-preview INPUT snapshot (Issue #249, Phase 5): everything
/// the GLB writer would emit for one family type, in hash-friendly form —
/// the GLB-filtered solid forms (FHV12-strengthened metrics + resolved
/// RGBA + visibility flags) and the nested instance placements (symbol
/// identity + quantized transform). This is the input of the VIEW3D
/// preview hash: the hash mirrors the GLB pipeline's inputs (never its
/// tessellated bytes — tessellation vertex counts are unstable).
/// Extracted in the SAME per-type pass as the meshes (one geometry
/// read), with the exact same visibility filters the GLB writer applies
/// (<c>form.Visible</c>, <c>IS_VISIBLE_PARAM</c>, detail-level).
/// </summary>
/// <param name="TypeName">Family type name (display). NOT part of the
/// VIEW3D hash — the name is the asset row's key, so renaming a type
/// reuses the same pooled GLB file.</param>
/// <param name="Forms">Solid forms visible on this type, with
/// FHV12-strengthened metrics.</param>
/// <param name="NestedInstances">Visible nested placements on this
/// type.</param>
public sealed record PreviewTypeSnapshot(
    string TypeName,
    IReadOnlyList<FormMetrics> Forms,
    IReadOnlyList<NestedInstanceSnapshot> NestedInstances);
