using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// High-level import orchestration service.
/// Copies files into managed storage and registers them in the catalog.
/// </summary>
public interface IFamilyImportService
{
    /// <summary>Import a single family file into the published catalog.</summary>
    Task<FamilyImportResult> ImportFileAsync(FamilyImportRequest request, CancellationToken ct = default);

    /// <summary>Import all .rfa files from a folder with progress.</summary>
    Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default);
}
