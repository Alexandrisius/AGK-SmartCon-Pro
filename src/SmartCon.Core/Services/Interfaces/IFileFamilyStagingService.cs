using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IFileFamilyStagingService
{
    Task<FamilyBatchImportItem> StageAsync(FamilyBatchImportItem item, CancellationToken ct);

    Task CloseAllPreparedDocumentsAsync(CancellationToken ct);
}
