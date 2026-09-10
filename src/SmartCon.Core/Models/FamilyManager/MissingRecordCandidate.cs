namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Why a catalog version is a cleanup candidate for the
/// "Очистить недоступные записи" database tool (Issue #133).
/// </summary>
public enum MissingRecordReason
{
    /// <summary>
    /// Marked by a hash actualization run as
    /// <see cref="FamilyContentHashFormat.RecalculationMissing"/> (-2).
    /// </summary>
    MarkedMissing = 0,

    /// <summary>
    /// The managed file is physically absent on disk — found by an explicit
    /// on-disk scan requested from the cleanup dialog (expensive on network
    /// drives, never runs automatically).
    /// </summary>
    FileMissing = 1
}

/// <summary>
/// One catalog version proposed by the "Очистить недоступные записи"
/// database tool (Issue #133): either marked RecalculationMissing (-2) by a
/// hash actualization run, or discovered by an explicit on-disk scan.
/// Purging is done through
/// <see cref="SmartCon.Core.Services.Interfaces.ICatalogActualizationService.PurgeMissingAsync"/>.
/// </summary>
/// <param name="CatalogItemId">Catalog item that owns the version.</param>
/// <param name="ItemName">Display name of the catalog item.</param>
/// <param name="VersionLabel">Version label whose file is missing.</param>
/// <param name="FileName">Expected file name (basename).</param>
/// <param name="RelativePath">Path of the expected file relative to the database root.</param>
/// <param name="RevitVersion">Highest Revit major version stored for this version label.</param>
/// <param name="Reason">How the candidate was detected.</param>
public sealed record MissingRecordCandidate(
    string CatalogItemId,
    string ItemName,
    string VersionLabel,
    string FileName,
    string RelativePath,
    int RevitVersion,
    MissingRecordReason Reason);

/// <summary>
/// Progress of the missing-record disk scan (Issue #133) — mirrors
/// <c>DatabaseMigrationProgress</c> of the actualization dialog: "Checking
/// X of Y — file.rfa". <see cref="Found"/> is non-null when the checked
/// version turned out to be missing — the dialog adds the row immediately,
/// so an interrupted scan keeps its partial results.
/// </summary>
/// <param name="Current">1-based index of the group being checked.</param>
/// <param name="Total">Total groups to check.</param>
/// <param name="CurrentFileName">File currently being checked.</param>
/// <param name="Found">Candidate discovered by THIS step, or null.</param>
public sealed record MissingRecordScanProgress(
    int Current,
    int Total,
    string CurrentFileName,
    MissingRecordCandidate? Found = null);
