using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Coordinates the end-to-end 3D geometry preview pipeline triggered from
/// <c>LocalFamilyImportService</c> hooks H1/H2/H3 (ADR-042):
/// <list type="number">
/// <item><description>Marshal the extraction through
/// <c>IFamilyManagerAwaitableEvent.RaiseAsync&lt;T&gt;</c> so the Revit API
/// open-close cycle runs on the UI thread (I-01).</description></item>
/// <item><description>Write the resulting <see cref="FamilyGeometryPreview"/>
/// to a temp GLB via <see cref="IGlbWriter"/>.</description></item>
/// <item><description>Delete any previous auto-extracted Model3D asset for
/// the same (catalogItemId, versionLabel) — supports ADR-040
/// OverwriteCurrent where the GLB is regenerated for the same version.</description></item>
/// <item><description>Register the new GLB through
/// <c>IFamilyAssetService.AddAssetAsync(Model3D, is_primary=0, description=auto-extracted-preview:...)</c>
/// so the managed-storage CopyInto succeeds and the existing
/// <c>Model3DAssets</c> binding in <c>FamilyPropertiesViewModel</c> picks
/// it up automatically.</description></item>
/// </list>
/// </summary>
/// <remarks>
/// Implementations MUST swallow all exceptions and log a Warn with an
/// <c>[Action: ...]</c> suggestion (skill smartcon-logging L9) — geometry
/// preview is a nice-to-have and MUST NOT break the import transaction that
/// already committed before the hook was reached.
/// </remarks>
public interface IFamilyGeometryPipeline
{
    /// <summary>
    /// Writes N GLBs (one per type) and registers them as auto-extracted
    /// Model3D assets. When <paramref name="geometryPerType"/> is non-null
    /// and non-empty, the pipeline uses pre-extracted geometry (H1 batch
    /// import — no additional <c>OpenDocumentFile</c>). When null, the
    /// pipeline extracts geometry itself by opening
    /// <paramref name="managedRfaPath"/> once (H2/H3 — exactly one open).
    /// </summary>
    /// <param name="geometryPerType">Pre-extracted per-type geometry from
    /// Prepare phase (H1). Pass <c>null</c> for H2/H3 to trigger
    /// extraction inside the pipeline.</param>
    /// <param name="managedRfaPath">Path to the managed .rfa. Used only
    /// when <paramref name="geometryPerType"/> is null (H2/H3 path).</param>
    /// <param name="catalogItemId">Catalog item id.</param>
    /// <param name="versionId">Version id.</param>
    /// <param name="versionLabel">Version label.</param>
    /// <param name="familyName">Family display name (without extension).</param>
    /// <param name="overwriteBaselineSectionHashes">#252: pre-overwrite
    /// section hashes of the SAME version, captured by
    /// <c>OverwriteCurrentAsync</c> before it rewrites the catalog row.
    /// When supplied and the new sections still match this baseline on the
    /// preview-relevant keys (DEF/GEOM/TYPES/NESTED*), the overwrite did
    /// not touch preview content — the pipeline keeps the version's
    /// existing pooled previews untouched and skips BOTH the stale-asset
    /// deletion and the extraction (a text-only overwrite costs zero Revit
    /// work). <c>null</c> on the H1/H2 (new-version) paths.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RunAsync(
        IReadOnlyList<FamilyGeometryPerType>? geometryPerType,
        string? managedRfaPath,
        string catalogItemId,
        string versionId,
        string versionLabel,
        string familyName,
        IReadOnlyDictionary<string, string>? overwriteBaselineSectionHashes = null,
        CancellationToken ct = default);
}
