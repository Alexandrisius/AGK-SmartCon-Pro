using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Threading;

namespace SmartCon.Core.Services.Interfaces;

public interface IFamilyBatchImportExecutor
{
    Task<FamilyBatchImportExecutionResult> ExecuteAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        string? categoryId,
        IProgress<FamilyBatchImportProgress>? progress,
        PauseGate? pauseGate,
        CancellationToken ct);
}
