using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IProjectFamilyStagingService
{
    Task<FamilyBatchImportItem?> StageSystemAsync(FamilyBatchImportItem item, CancellationToken ct);

    Task<FamilyBatchImportItem?> StageLoadableAsync(FamilyBatchImportItem item, CancellationToken ct);

    Task CloseAllPreparedDocumentsAsync(CancellationToken ct);
}
