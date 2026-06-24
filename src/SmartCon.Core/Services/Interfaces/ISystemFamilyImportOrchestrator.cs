using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Result of preparing a single system-family category for import. One
/// per non-empty <see cref="CategoryAnalysis"/> or per user-picked group.
/// </summary>
public sealed record SystemFamilyPendingImport(
    string CategoryName,
    IReadOnlyList<SelectedSystemType> Types,
    string ManagedRvtPath);

/// <summary>
/// Catalog-side orchestration of a staged system-family import:
/// takes the list of <see cref="FamilyBatchImportItem"/> produced by the
/// VM (one per staged .rvt), runs the batch importer, syncs the type
/// descriptors into the local catalog, and returns the
/// <see cref="SystemFamilyExtractionTask"/> list that the
/// <see cref="ISystemFamilyAttributeExtractor"/> needs to process.
///
/// Type descriptors travel with each <see cref="FamilyBatchImportItem.SourceTypes"/>
/// — the VM populates that field when staging system families, so the
/// orchestrator no longer needs a separate types-by-path dictionary.
/// Replaces the legacy <c>ISystemFamilyImportService.ImportBatchItemsAsync</c>
/// — the staging half of that interface moved into
/// <see cref="ISystemFamilyIsolationProjectService"/>, so this contract
/// owns the catalog-sync concern only.
/// </summary>
public interface ISystemFamilyImportOrchestrator
{
    Task<SystemFamilyImportResult> ImportBatchItemsAsync(
        IReadOnlyList<FamilyBatchImportItem> items);
}
