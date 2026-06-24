namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of preparing a single system-family category for import. One
/// per non-empty <see cref="CategoryAnalysis"/> or per user-picked group.
/// </summary>
public sealed record SystemFamilyPendingImport(
    string CategoryName,
    IReadOnlyList<FamilySourceTypeInfo> Types,
    string ManagedRvtPath);
