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
    /// Runs extract → write GLB → register asset for the given managed
    /// <c>.rfa</c>. Safe to invoke from any thread — internally marshals
    /// Revit API calls to the UI thread via
    /// <c>IFamilyManagerAwaitableEvent</c>.
    /// </summary>
    /// <param name="managedRfaPath">Absolute path to the managed
    /// <c>.rfa</c> that was just created/overwritten by the import (H1
    /// = Insert, H2 = Increment, H3 = OverwriteCurrent).</param>
    /// <param name="catalogItemId">Catalog item id (the just-committed version's parent).</param>
    /// <param name="versionId">Version id of the just-committed version row
    /// (informational — the asset is keyed by version_label, not version_id).</param>
    /// <param name="versionLabel">Version label of the just-committed version.</param>
    /// <param name="familyName">Family display name (without extension) —
    /// becomes family_assets.file_name stem.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RunAsync(
        string managedRfaPath,
        string catalogItemId,
        string versionId,
        string versionLabel,
        string familyName,
        CancellationToken ct = default);
}
