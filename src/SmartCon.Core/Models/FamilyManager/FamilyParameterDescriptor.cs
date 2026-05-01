namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Descriptor for a family parameter (Post-MVP: populated by deep extraction).
/// </summary>
/// <param name="Id">Unique identifier.</param>
/// <param name="VersionId">Parent version ID.</param>
/// <param name="TypeId">Associated type ID (null for shared parameters).</param>
/// <param name="Name">Parameter name.</param>
/// <param name="StorageType">Storage type string (e.g. "String", "Double").</param>
/// <param name="ValueText">Default value as text.</param>
/// <param name="IsInstance">True if instance parameter.</param>
/// <param name="IsReadonly">True if read-only parameter.</param>
/// <param name="ForgeTypeId">ForgeTypeId string representation.</param>
public sealed record FamilyParameterDescriptor(
    string Id,
    string VersionId,
    string? TypeId,
    string Name,
    string? StorageType,
    string? ValueText,
    bool? IsInstance,
    bool? IsReadonly,
    string? ForgeTypeId);
