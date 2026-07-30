using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// CRUD operations for the <c>SmartCon_FamilyVersion_v1</c> ExtensibleStorage
/// marker stored on system types (<c>ElementType</c>) in the active project
/// (Issue #104). Same schema and payload as <see cref="IFamilyVersionStore"/>;
/// the only difference is the carrier element — a system type instead of a
/// <c>Family</c> element. All methods are synchronous and must be called from
/// the Revit main thread (I-01); writes run inside an
/// <see cref="ITransactionService"/> transaction (I-03).
/// </summary>
public interface ISystemTypeVersionStore
{
    /// <summary>
    /// Read the <see cref="FamilyVersion"/> marker from a system type.
    /// Returns <c>null</c> when the element is missing, is not an
    /// <c>ElementType</c>, has no entity, or the entity is corrupted
    /// (corruption is logged at <c>Warn</c> and treated as "no marker").
    /// </summary>
    FamilyVersion? ReadFromType(Document doc, ElementId typeId);

    /// <summary>
    /// Write the <see cref="FamilyVersion"/> marker to a system type.
    /// Wrapped in its own transaction — do NOT call from inside an open
    /// transaction; synchronizers that already hold a transaction write the
    /// entity inline instead.
    /// </summary>
    void WriteToType(Document doc, ElementId typeId, FamilyVersion version);

    /// <summary>
    /// Batch read for stale detection. Entries with a missing/corrupt entity
    /// or an invalid id map to <c>null</c>.
    /// </summary>
    IReadOnlyDictionary<ElementId, FamilyVersion?> ReadManyFromTypes(
        Document doc, IEnumerable<ElementId> typeIds);
}
