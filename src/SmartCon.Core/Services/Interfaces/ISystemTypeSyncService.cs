using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Synchronizes one system type in the active project with its reference
/// data read from an already open source mini-project (Issue #104).
/// The synchronization never copies elements between documents: the target
/// type is either found by (name, category) and overwritten, or created by
/// duplicating an existing prototype type of the same category and then
/// overwritten. All writes — creation, parameters, ES version marker — happen
/// in a single transaction (one Undo step per type).
/// </summary>
/// <remarks>
/// Must be called on the Revit main thread (I-01). The caller owns the
/// lifetime of <c>sourceDoc</c> (open/close) so batch synchronizations reuse
/// one opened mini-project for all its types.
/// </remarks>
public interface ISystemTypeSyncService
{
    /// <summary>
    /// Synchronize <paramref name="typeName"/> from <paramref name="sourceDoc"/>
    /// into <paramref name="activeDoc"/> and stamp the ES version marker.
    /// </summary>
    /// <param name="sourceDoc">Open catalog mini-project (.rvt) containing the
    /// reference type.</param>
    /// <param name="activeDoc">Target project document.</param>
    /// <param name="typeName">Type name to synchronize (case-insensitive
    /// match).</param>
    /// <param name="catalogItemId">Catalog item id written into the ES
    /// marker.</param>
    /// <param name="versionLabel">Catalog version label written into the ES
    /// marker (from the resolved catalog file).</param>
    /// <param name="sourceRevitVersion">Current Revit major version, written
    /// into the ES marker for RevitVersionMismatch detection.</param>
    /// <param name="familyName">Issue #183: system family of the type — the
    /// reference lookup and the target match are restricted to this family
    /// ("Conduit without Fittings" never touches "Conduit with Fittings").
    /// <c>null</c> keeps the legacy first-name-match behaviour.</param>
    /// <param name="familyKey">Issue #190 (ADR-064): locale-invariant family
    /// key — preferred over <paramref name="familyName"/> for both the
    /// reference lookup and the target match when present.</param>
    /// <param name="categoryOrdinal">Category of the catalog item, resolved
    /// by the caller from the catalog. Scopes the reference lookup to the
    /// category — without it a name match can bind an unrelated
    /// ElementType of another category that happens to share the name and
    /// the (usually degenerate) family key, e.g. the electrical
    /// settings-graph types DistributionSysType/VoltageType named
    /// "По умолчанию" vs the real WireType (manual test 2026-08-04, round 3).
    /// <c>null</c> keeps the unscoped legacy behaviour.</param>
    SystemTypeSyncResult SyncTypeFromSource(
        Document sourceDoc,
        Document activeDoc,
        string typeName,
        string catalogItemId,
        string versionLabel,
        int sourceRevitVersion,
        string? familyName = null,
        string? familyKey = null,
        int? categoryOrdinal = null);

    /// <summary>
    /// Manual mini-project staging (ADR-072): clones <paramref name="typeName"/>
    /// from <paramref name="sourceDoc"/> into the freshly created
    /// <paramref name="stagingDoc"/> WITHOUT CopyElements — duplicate a
    /// same-family template prototype, write the type parameters, sync the
    /// segment and write a SLIM routing (segment rules + no-part rules
    /// only — the staging project carries no fittings, so the material-
    /// duplication class of #254 cannot be born). No fitting loads, no ES
    /// version marker, no catalog-DB routing substitution.
    /// <see cref="SystemTypeSyncStatus.FamilyNotFound"/> signals the caller
    /// to fall back to CopyElements for this type (prototype-less system
    /// family — ADR-072 §2.7 п.6).
    /// </summary>
    /// <remarks>Must be called on the Revit main thread (I-01).</remarks>
    SystemTypeSyncResult StageTypeFromSource(
        Document sourceDoc,
        Document stagingDoc,
        string typeName,
        int? categoryOrdinal = null,
        string? familyName = null,
        string? familyKey = null);
}
