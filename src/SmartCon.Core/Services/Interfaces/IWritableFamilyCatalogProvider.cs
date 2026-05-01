using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IWritableFamilyCatalogProvider
{
    Task<FamilyImportResult> ImportAsync(FamilyImportRequest request, CancellationToken ct = default);
    Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default);
    Task<FamilyCatalogItem> UpdateItemAsync(string id, string? name, string? description, string? categoryId, IReadOnlyList<string>? tags, ContentStatus? status, string? manufacturer = null, CancellationToken ct = default);
    Task<bool> DeleteItemAsync(string id, CancellationToken ct = default);
}
