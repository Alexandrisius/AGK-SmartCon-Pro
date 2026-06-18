using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// CRUD operations for the <c>SmartCon.FamilyVersion.v1</c> ExtensibleStorage marker
/// (ADR-030). The marker is stored on the <see cref="Family"/> element for loadable
/// families in the active project, and on the <c>Family.OwnerFamily</c> for an opened
/// <c>.rfa</c> file. All write methods are synchronous because they must be called
/// from the Revit main thread (I-01) and run inside an <see cref="ITransactionService"/>
/// transaction (I-03). Read methods are also synchronous for the same reason.
/// </summary>
public interface IFamilyVersionStore
{
    /// <summary>
    /// Read the <see cref="FamilyVersion"/> from a Family already loaded into the project.
    /// Returns <c>null</c> if the family is missing the ES entity (pre-Phase 24 load,
    /// or corrupted entity).
    /// </summary>
    FamilyVersion? ReadFromLoadedFamily(Document doc, ElementId familyId);

    /// <summary>
    /// Read the <see cref="FamilyVersion"/> from an <c>.rfa</c> file on disk.
    /// Opens the file in Revit transiently. Returns <c>null</c> on read failure.
    /// </summary>
    Task<FamilyVersion?> ReadFromRfaFileAsync(string rfaFilePath, CancellationToken ct);

    /// <summary>
    /// Write the <see cref="FamilyVersion"/> to a Family in the active project document.
    /// Wrapped in a transaction by <see cref="ITransactionService"/> (I-03).
    /// </summary>
    void WriteToLoadedFamily(Document doc, ElementId familyId, FamilyVersion version);

    /// <summary>
    /// Write the <see cref="FamilyVersion"/> to an <c>.rfa</c> file on disk.
    /// Opens the family document transiently, writes the entity, saves, and closes.
    /// Uses <c>new Transaction(familyDoc, ...)</c> directly (I-03b exception for family documents).
    /// </summary>
    Task WriteToRfaFileAsync(string rfaFilePath, FamilyVersion version, CancellationToken ct);

    /// <summary>
    /// Batch read for many Family IDs. Returns a dictionary keyed by <see cref="ElementId"/>
    /// with <c>null</c> entries for families that have no ES entity or that fail to read.
    /// Used by <c>StaleDetector.CheckCategoryAsync</c> for performance.
    /// </summary>
    IReadOnlyDictionary<ElementId, FamilyVersion?> ReadManyFromDocument(
        Document doc, IEnumerable<ElementId> familyIds);
}
