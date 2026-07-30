namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Snapshot of a system type's <c>CompoundStructure</c> (ADR-056,
/// Issue #159) — the layer stack of Basic walls, floors, roofs and
/// ceilings. Not a parameter: changing a layer material/thickness leaves
/// every type parameter untouched, so without this section the content
/// hash misses real edits. <c>null</c> on the type means "no compound
/// structure" (stacked/curtain walls, non-host categories) — a
/// deterministic canonical state.
/// </summary>
/// <param name="ExteriorShellLayerCount">Number of shell layers outside
/// the core boundary (<c>GetNumberOfShellLayers(Exterior)</c>).</param>
/// <param name="InteriorShellLayerCount">Number of shell layers inside
/// the core boundary (<c>GetNumberOfShellLayers(Interior)</c>).</param>
/// <param name="Layers">Layers in Revit order (exterior → interior for
/// walls, top → bottom for floors/roofs/ceilings). Order is content —
/// do not sort.</param>
/// <param name="StructuralMaterialIndex">Index of the layer whose material
/// is the structural material (the "Материал несущих конструкций" per-layer
/// checkbox), <c>-1</c> when none. Sync-only field (Issue #104): NOT part of
/// the FHV3 canonical string — the hash format is frozen, so the content
/// hasher must not read this member.</param>
/// <param name="EndCap"><c>EndCapCondition</c> enum ordinal ("Огибание в
/// торцах стен" / end wrapping). Sync-only, not hashed (FHV3 frozen).</param>
/// <param name="OpeningWrapping"><c>OpeningWrappingCondition</c> enum
/// ordinal ("Огибание в местах вставки элементов"). Sync-only, not hashed
/// (FHV3 frozen).</param>
public sealed record CompoundStructureSnapshot(
    int ExteriorShellLayerCount,
    int InteriorShellLayerCount,
    IReadOnlyList<CompoundLayerSnapshot> Layers,
    int StructuralMaterialIndex = -1,
    int EndCap = -1,
    int OpeningWrapping = -1);

/// <summary>
/// One layer of a <see cref="CompoundStructureSnapshot"/>.
/// </summary>
/// <param name="Function"><c>MaterialFunctionAssignment</c> enum ordinal
/// (Structure, Finish1, Insulation, …).</param>
/// <param name="Width">Layer width in internal units (feet).</param>
/// <param name="MaterialName">Resolved material name, or <c>null</c>
/// when the layer has no material assigned ("By Category"). Names are
/// document content (not UI-localized), so they survive cross-locale
/// extraction.</param>
/// <param name="IsVariable"><c>true</c> when this is the variable
/// thickness layer (<c>CompoundStructure.VariableLayerIndex</c>).</param>
/// <param name="LayerCapFlag">Per-layer end-cap flag
/// (<c>CompoundStructureLayer.LayerCapFlag</c>). Sync-only (Issue #104), NOT
/// part of the FHV3 canonical string.</param>
/// <param name="ParticipatesInWrapping"><c>true</c> when this shell layer
/// participates in wrapping at inserts/openings
/// (<c>CompoundStructure.ParticipatesInWrapping</c>); meaningful only for
/// shell layers, <c>false</c> for core layers. Sync-only, not hashed
/// (FHV3 frozen).</param>
public sealed record CompoundLayerSnapshot(
    int Function,
    double Width,
    string? MaterialName,
    bool IsVariable,
    bool LayerCapFlag = false,
    bool ParticipatesInWrapping = false);
