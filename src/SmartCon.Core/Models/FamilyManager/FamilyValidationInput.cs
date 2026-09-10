namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Normalized validation input for one family: all types with their
/// parameter values, source-agnostic (import snapshot or catalog
/// extraction). Produced by SnapshotValidationMapper /
/// ExtractedValuesValidationMapper.
/// </summary>
public sealed record FamilyValidationInput(
    IReadOnlyList<FamilyTypeValidationData> Types);
