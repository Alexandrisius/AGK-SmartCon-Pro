using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Tests.TestDoubles;

public sealed class FakeFileFamilyStagingService : IFileFamilyStagingService
{
    public int StageCallCount { get; private set; }
    public int CloseAllCallCount { get; private set; }

    public Task<FamilyBatchImportItem> StageAsync(FamilyBatchImportItem item, CancellationToken ct)
    {
        StageCallCount++;
        return Task.FromResult(item);
    }

    public Task CloseAllPreparedDocumentsAsync(CancellationToken ct)
    {
        CloseAllCallCount++;
        return Task.CompletedTask;
    }
}
