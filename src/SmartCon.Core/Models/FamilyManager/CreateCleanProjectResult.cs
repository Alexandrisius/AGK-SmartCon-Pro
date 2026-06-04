namespace SmartCon.Core.Models.FamilyManager;

public sealed record CreateCleanProjectResult(
    bool Success,
    string? FilePath,
    string? Error,
    int CopiedElementsCount,
    string? CategoryName = null);
