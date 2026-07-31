using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Orchestrates system-type synchronization on top of
/// <see cref="ISystemTypeSyncService"/> (Issue #104): resolves the managed
/// mini-project file via <c>IFamilyFileResolver</c>, opens it once per call,
/// synchronizes the requested types and closes the document. Also provides
/// the marker fast-path check used by drag-and-drop / placement so an
/// up-to-date type starts placement without opening the mini-project.
/// </summary>
/// <remarks>
/// All methods must be called on the Revit main thread (I-01) — both from
/// <c>IExternalEventHandler.Execute</c> callbacks and from
/// <c>IDropHandler.Execute</c>. SQLite reads inside are marshalled via
/// <c>AsyncBridge.RunSync</c>, which is safe on the Revit main thread
/// (existing precedent: <c>SystemFamilyPlacementService</c>).
/// </remarks>
public interface ISystemTypeSyncOrchestrator
{
    /// <summary>
    /// Fast path for placement: <c>true</c> when the type
    /// (<paramref name="typeName"/> of family <paramref name="familyName"/>)
    /// exists in the project AND carries an ES marker whose catalog item,
    /// version label and Revit version all match the currently resolvable
    /// catalog file. Any mismatch, a missing marker or an unresolvable file
    /// returns <c>false</c> (sync required).
    /// </summary>
    bool IsProjectTypeCurrent(
        Document activeDoc,
        string catalogItemId,
        string typeName,
        int targetRevitVersion,
        string? familyName = null);

    /// <summary>
    /// Synchronize the given types of a system catalog item into the active
    /// project. The mini-project is opened once and reused for all types;
    /// each type is synchronized in its own transaction (one Undo step per
    /// type) and receives the ES version marker on success. A failing type
    /// does not abort the batch — its result carries the error.
    /// Types are addressed by full identity (family, name) — Issue #183.
    /// </summary>
    SystemFamilySyncResult SyncTypes(
        Document activeDoc,
        string catalogItemId,
        IReadOnlyList<SystemTypeRef> types,
        int targetRevitVersion);
}
