using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Threading;

namespace SmartCon.Core.Services.Interfaces;

public interface IFamilyBatchImportExecutor
{
    /// <param name="externalParentItemIds">
    /// E2 (#209, UC-2): parent rows imported OUTSIDE this executor (the
    /// active-family bespoke path) — original dialog file path → catalog
    /// item id. Merged into the link-write parent map so dependency links
    /// land on those parents too. <c>null</c> for self-contained batches.
    /// </param>
    Task<FamilyBatchImportExecutionResult> ExecuteAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        string? categoryId,
        IProgress<FamilyBatchImportProgress>? progress,
        PauseGate? pauseGate,
        CancellationToken ct,
        IReadOnlyDictionary<string, string>? externalParentItemIds = null);
}
