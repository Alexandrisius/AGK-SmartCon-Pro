using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Revit-bound boundary of the database actualization engine (ADR-054):
/// opens ONE managed family file on the Revit main thread, extracts the
/// snapshot (optionally with per-type geometry), and closes the document.
/// The implementation lives in SmartCon.Revit and marshals through
/// <see cref="IFamilyManagerAwaitableEvent"/>; the caller
/// (<see cref="ICatalogActualizationService"/>) stays pure C# and
/// unit-testable with a fake extractor.
/// </summary>
public interface IFamilyMigrationExtractor
{
    /// <summary>
    /// Open <paramref name="absolutePath"/> (<c>.rfa</c>), extract a
    /// <see cref="FamilySnapshot"/>, and close the document without
    /// saving. Never throws across the boundary — failures are reported
    /// in the result so the migration continues with the next file.
    /// </summary>
    Task<FamilyMigrationExtractResult> ExtractLoadableAsync(
        string absolutePath,
        CancellationToken ct = default);

    /// <summary>
    /// Same open→extract→close session as <see cref="ExtractLoadableAsync"/>,
    /// additionally extracting per-type geometry (database actualization,
    /// ADR-054) so the file is opened exactly once. A geometry failure does
    /// NOT fail the whole result — the snapshot stays valid and
    /// <see cref="FamilyMigrationExtractResult.Geometry"/> is <c>null</c>.
    /// </summary>
    Task<FamilyMigrationExtractResult> ExtractLoadableWithGeometryAsync(
        string absolutePath,
        CancellationToken ct = default);
}
