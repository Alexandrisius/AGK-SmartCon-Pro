using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Revit-bound boundary of the hash-recalculation migration (Issue #126):
/// opens ONE managed family file on the Revit main thread, extracts the
/// snapshot, and closes the document. The implementation lives in
/// SmartCon.Revit and marshals through
/// <see cref="IFamilyManagerAwaitableEvent"/>; the caller
/// (<see cref="ICatalogHashRecalculationService"/>) stays pure C# and
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
}
