namespace SmartCon.Core.Models.FamilyManager;

public sealed record FamilyBatchImportExecutionResult(
    int SuccessCount,
    int SkippedCount,
    int ErrorCount,
    bool WasStopped);
