using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ILoadableFamilyImportOrchestrator
{
    Task<LoadableFamilyImportResult> ImportAndPersistTypesAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        int targetRevitVersion,
        string? categoryId = null,
        CancellationToken ct = default);
}
