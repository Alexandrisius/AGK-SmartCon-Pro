using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface ISystemFamilyImportService
{
    IReadOnlyList<SystemFamilyPendingImport> PickAndPrepare();

    Task<SystemFamilyImportResult> ImportBatchItemsAsync(IReadOnlyList<FamilyBatchImportItem> items);
}

public sealed record SystemFamilyPendingImport(
    string CategoryName,
    IReadOnlyList<SelectedSystemType> Types,
    string TempRvtPath);
