using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Discovers dependency families of a just-extracted snapshot at Phase-1
/// prepare time (ADR-066, EPIC #207). E1 covers routing dependencies:
/// fitting families referenced by the RoutingPreferenceManager rules of
/// system MEPCurve types (pipes / ducts / conduits / cable trays).
/// The collector resolves each rule's part to the LIVE Family object and
/// returns its identity (<see cref="FamilyDependencyDescriptor.FamilyUniqueId"/>)
/// — downstream stages must key on that identity, never on the display name
/// (I-05, #183).
/// </summary>
public interface IFamilyDependencyCollector
{
    /// <summary>
    /// Collects routing-dependency descriptors from a system family
    /// snapshot. Must be called on the Revit API thread (touches
    /// <paramref name="document"/>). Families that cannot be re-imported
    /// (<c>Family.IsEditable == false</c>, in-place families) are skipped
    /// with a Warn log. Duplicates (several rules/types referencing the
    /// same family) collapse to one descriptor per family; the FIRST
    /// part name wins.
    /// </summary>
    /// <param name="document">Source document the snapshot was extracted from (opaque).</param>
    /// <param name="snapshot">System snapshot whose routing rules are scanned.</param>
    /// <returns>Unique dependency descriptors, empty when the category has no routing rules.</returns>
    IReadOnlyList<FamilyDependencyDescriptor> CollectRoutingDependencies(
        Document document,
        SystemFamilySnapshot snapshot);

    /// <summary>
    /// Collects shared-nested dependency descriptors from a LOADABLE family
    /// document (E2, #209). Must be called on the Revit API thread. The scan
    /// covers every nesting level at once: all shared nested families are
    /// visible flat in the top parent's family document (probe-verified,
    /// <c>SharedNestedCollectorTests.P1</c>), so no recursion or cycle guard
    /// is needed. Families that cannot be re-opened
    /// (<c>Family.IsEditable == false</c>) or have an empty name are skipped
    /// with a Warn log. The returned <see cref="FamilyDependencyDescriptor.FamilyUniqueId"/>
    /// is valid in <paramref name="familyDocument"/> only.
    /// </summary>
    /// <param name="familyDocument">Open family document of the parent (opaque).</param>
    /// <returns>Unique shared-nested descriptors, empty when none.</returns>
    IReadOnlyList<FamilyDependencyDescriptor> CollectSharedNestedDependencies(
        Document familyDocument);
}
