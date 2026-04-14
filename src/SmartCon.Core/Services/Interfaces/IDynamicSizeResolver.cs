using Autodesk.Revit.DB;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Provides available DN sizes for a dynamic family.
/// Implementation: SmartCon.Revit/Parameters/RevitDynamicSizeResolver.cs
/// Call OUTSIDE transaction (EditFamily requires doc.IsModifiable == false).
/// </summary>
public interface IDynamicSizeResolver
{
    /// <summary>
    /// Returns all available radii for the specified connector of an element.
    /// Tries LookupTable first; if none, iterates all FamilySymbol.
    /// constraints — restrictions by other DN columns (for multi-query fittings).
    /// If constraints is not null, dropdown shows only table rows
    /// where other column values match the restrictions.
    /// </summary>
    IReadOnlyList<SizeOption> GetAvailableSizes(Document doc, ElementId elementId,
        int connectorIndex,
        IReadOnlyList<LookupColumnConstraint>? constraints = null);

    /// <summary>
    /// Returns all available type sizes of a FamilyInstance with radii of ALL connectors.
    /// Each list item is a full family configuration (e.g. DN 65x50x65).
    /// DisplayName is formatted as "DN {target} x DN {other1} [x ...]".
    /// Call OUTSIDE transaction (EditFamily).
    /// </summary>
    IReadOnlyList<FamilySizeOption> GetAvailableFamilySizes(Document doc, ElementId elementId,
        int targetConnectorIndex);
}
