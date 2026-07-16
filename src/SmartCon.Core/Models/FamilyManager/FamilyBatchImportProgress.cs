namespace SmartCon.Core.Models.FamilyManager;

public sealed record FamilyBatchImportProgress(
    int CurrentIndex,
    int Total,
    string CurrentItemName,
    FamilyBatchImportPhase Phase,
    FamilyBatchImportRowState? ItemState,
    string? ItemError,
    int SuccessCount,
    int SkippedCount,
    int ErrorCount);
