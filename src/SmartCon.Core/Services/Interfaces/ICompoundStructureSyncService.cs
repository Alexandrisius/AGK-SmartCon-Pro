using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Replaces the compound structure (layer stack) of a host type (walls,
/// floors, roofs, ceilings) in the active project with the catalog reference
/// (Issue #104). Layer materials are synchronized by name through
/// <see cref="IMaterialSyncService"/>; placed instances of the type pick up
/// the new structure automatically (standard Revit type behavior).
/// </summary>
/// <remarks>
/// Must be called on the Revit main thread (I-01) and inside an open
/// transaction. When the reference structure is invalid for the target type
/// (Revit rejects <c>SetCompoundStructure</c>) the existing structure is
/// kept and the failure is reported as not-converged.
/// </remarks>
public interface ICompoundStructureSyncService
{
    /// <summary>
    /// Apply the reference <paramref name="structure"/> to
    /// <paramref name="target"/>. Returns the number of not-converged items
    /// (unresolvable layer materials, rejected structure).
    /// </summary>
    int SyncStructure(
        Document sourceDoc,
        Document activeDoc,
        ElementType target,
        CompoundStructureSnapshot structure);
}
