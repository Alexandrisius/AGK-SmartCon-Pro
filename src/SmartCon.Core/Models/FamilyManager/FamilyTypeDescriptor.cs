namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Descriptor for a family type (Post-MVP: populated by deep extraction).
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="VersionId">Parent version ID.</param>
/// <param name="Name">Type name.</param>
/// <param name="SortOrder">Display sort order.</param>
public sealed record FamilyTypeDescriptor(
    string Id,
    string VersionId,
    string Name,
    int SortOrder);
