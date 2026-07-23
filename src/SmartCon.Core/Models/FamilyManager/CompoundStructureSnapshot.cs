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
public sealed record CompoundStructureSnapshot(
    int ExteriorShellLayerCount,
    int InteriorShellLayerCount,
    IReadOnlyList<CompoundLayerSnapshot> Layers);

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
public sealed record CompoundLayerSnapshot(
    int Function,
    double Width,
    string? MaterialName,
    bool IsVariable);
