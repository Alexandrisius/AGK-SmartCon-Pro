using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.LocalCatalog;

/// <summary>
/// v2.0.0: SQLite-backed implementation of
/// <see cref="IFamilyImportPrecomputer"/>. Resolves the display name
/// against <see cref="LocalCatalogDatabase"/> through
/// <see cref="IFamilyCatalogProvider"/> and asks
/// <see cref="IFamilyImportService"/> for the canonical managed path
/// (single source of truth for the on-disk layout).
/// </summary>
/// <remarks>
/// Construction pulls in only the catalog provider and the import
/// service — no DB connection, no file I/O. The actual DB call is
/// <see cref="IFamilyCatalogProvider.FindByNormalizedNameAsync"/>; the
/// path math goes through
/// <see cref="IFamilyImportService.ComputeManagedFilePath"/>, so the
/// triple this class produces is guaranteed to land at the same path
/// the staging helpers and <c>ImportFileAsync</c> will write to.
/// <para>
/// This service is the ONLY place that combines
/// <c>FindByNormalizedNameAsync</c> + <c>GetNextVersionLabelAsync</c> +
/// <c>ComputeManagedFilePath</c> for the purpose of pre-computing an
/// import triple. The dialog rename handler goes through here, and so
/// does the initial <c>BuildSystemFamilyBatchRowVirtualAsync</c> /
/// <c>BuildLoadableFamilyBatchRowVirtualAsync</c> build (so the two
/// stay in lock-step on the next-version math).
/// </para>
/// <para>
/// <b>Pure compute, no side effects.</b> This service MUST NOT touch
/// the file system. The directory is created at the point in the
/// pipeline where it is certain the file will be written:
/// <list type="bullet">
///   <item><c>ProcessFamilyImportAsync</c> — immediately before
///         <c>Document.SaveAs</c>, AFTER the user has confirmed the
///         batch dialog.</item>
///   <item><c>ImportFileAsync</c> via <c>ComputeManagedRfaPath</c> —
///         for the UC-1 direct-import path (no dialog).</item>
///   <item><c>StageSystemFamiliesFromMetadataAsync</c> /
///         <c>StageLoadableFamiliesFromMetadataAsync</c> — for the
///         UC-3/UC-4 staged paths.</item>
/// </list>
/// Earlier revisions of this service created the directory eagerly,
/// but that produced empty <c>vN+1/</c> folders whenever the user
/// cancelled the dialog or the downstream import failed — the next
/// import would then read <c>GetNextVersionLabelAsync</c> = <c>vN+2</c>
/// and leave another empty directory behind. A pure precomputer is the
/// fix: the directory exists only when the importer actually writes
/// the file.
/// </para>
/// </remarks>
internal sealed class LocalFamilyImportPrecomputer : IFamilyImportPrecomputer
{
    private readonly IFamilyCatalogProvider _catalogProvider;
    private readonly IFamilyImportService _importService;

    public LocalFamilyImportPrecomputer(
        IFamilyCatalogProvider catalogProvider,
        IFamilyImportService importService)
    {
        _catalogProvider = catalogProvider ?? throw new ArgumentNullException(nameof(catalogProvider));
        _importService = importService ?? throw new ArgumentNullException(nameof(importService));
    }

    /// <inheritdoc />
    public async Task<PrecomputedImportTriple?> BuildPrecomputedTripleAsync(
        string displayName,
        string extension,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return null;
        }

        var normalizedName = FamilyNameNormalizer.Normalize(displayName);
        var existing = await _catalogProvider
            .FindByNormalizedNameAsync(normalizedName, ct)
            .ConfigureAwait(false);

        string catalogItemId;
        string versionLabel;

        if (existing is not null)
        {
            // Re-import: keep the existing id and advance the version. The
            // import service's ComputeManagedFilePath is the single source
            // of truth for "what's the next version label for an existing
            // item", so the dialog pre-build, the dialog rename, and the
            // import service all agree on the value.
            catalogItemId = existing.Id;
            versionLabel = await _importService
                .GetNextVersionLabelAsync(existing.Id, ct)
                .ConfigureAwait(false);
        }
        else
        {
            // New item: allocate a fresh GUID + "v1". The "N" format
            // strips dashes so the catalog_items.id column matches the
            // other writers in the pipeline (no surprises with INSERT).
            catalogItemId = Guid.NewGuid().ToString("N");
            versionLabel = "v1";
        }

        // The file-name component of the path has to be sanitised the
        // same way the staging helpers and the import service sanitise
        // it, otherwise a name like 'foo/bar' would either produce a
        // mangled relative path or fail on Windows. The shared helper is
        // the single source of truth for that sanitisation.
        var fileName = SafeFileName.SanitizeFileName(displayName);
        if (string.IsNullOrEmpty(fileName)) fileName = "Family";

        var managedPath = _importService.ComputeManagedFilePath(
            catalogItemId,
            versionLabel,
            fileName,
            extension);

        if (managedPath is null)
        {
            // The import service returns null when the active database
            // has no root configured. The caller must surface this as
            // a user error rather than fabricating a fallback (v2.0.0
            // has no temp folders, per ADR-035).
            SmartConLogger.Warn(
                $"LocalFamilyImportPrecomputer.BuildPrecomputedTripleAsync: " +
                $"ComputeManagedFilePath returned null for displayName='{displayName}' " +
                $"[Action: ensure the active catalog database is selected and has a writable root]");
            return null;
        }

        return new PrecomputedImportTriple(catalogItemId, versionLabel, managedPath);
    }
}
