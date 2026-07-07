using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// CRUD operations for the <c>SmartCon_FamilyVersion_v1</c> ExtensibleStorage marker
/// (ADR-030). The marker is stored on the <see cref="Family"/> element for loadable
/// families in the active project. All methods are synchronous because they must be
/// called from the Revit main thread (I-01) and run inside an
/// <see cref="ITransactionService"/> transaction (I-03) for writes.
/// </summary>
public interface IFamilyVersionStore
{
    /// <summary>
    /// Read the <see cref="FamilyVersion"/> from a Family already loaded into the project.
    /// Returns <c>null</c> if:
    /// <list type="bullet">
    ///   <item><description><paramref name="doc"/> or <paramref name="familyId"/> is null.</description></item>
    ///   <item><description><paramref name="familyId"/> is <c>ElementId.InvalidElementId</c>.</description></item>
    ///   <item><description>The element does not exist or is not a <see cref="Family"/>.</description></item>
    ///   <item><description>The family has no ES entity (pre-Phase 24 load).</description></item>
    ///   <item><description>The entity is corrupted (unreadable fields) — a <c>Warn</c>
    ///     is logged; the family is treated as having no marker.</description></item>
    /// </list>
    /// </summary>
    FamilyVersion? ReadFromLoadedFamily(Document doc, ElementId familyId);

    /// <summary>
    /// Write the <see cref="FamilyVersion"/> to a Family in the active project document.
    /// Wrapped in a transaction by <see cref="ITransactionService"/> (I-03).
    /// Throws <see cref="ArgumentException"/> if <paramref name="familyId"/> is
    /// <c>ElementId.InvalidElementId</c>. Silently returns if the family element
    /// does not exist (the transaction commits an empty change).
    /// </summary>
    void WriteToLoadedFamily(Document doc, ElementId familyId, FamilyVersion version);

    /// <summary>
    /// Batch read for many Family IDs. Returns a dictionary keyed by <see cref="ElementId"/>
    /// with <c>null</c> entries for families that have no ES entity, that fail to read,
    /// or that have an <c>InvalidElementId</c>. Used by
    /// <c>StaleDetector.CheckCategoryAsync</c> for performance.
    /// <para>
///     The implementation iterates the input in order and emits a single
///     progress log on the happy path when more than 32 IDs were requested,
///     so a 10 000-family batch does not stay silent in <c>smartcon.log</c>.
///     Individual exceptions are caught, logged via <c>HotLoopCounter</c>
///     sampling, and do not abort the rest of the batch.
/// </para>
/// </summary>
    IReadOnlyDictionary<ElementId, FamilyVersion?> ReadManyFromDocument(
        Document doc, IEnumerable<ElementId> familyIds);
}
