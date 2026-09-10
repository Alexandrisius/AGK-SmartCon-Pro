namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Canonical identity key of a system type row — "TOKEN|NAME"
/// (upper-invariant), where TOKEN is the locale-invariant
/// <c>family_key</c> when present, otherwise the localized
/// <c>family_name</c>, otherwise empty (pre-V26 legacy rows).
/// Issue #190 (ADR-064) / #191: single definition shared by the stale
/// detector, the type repository's UPSERT result map, the extraction
/// pipeline and the tree — so every layer builds the SAME key for the
/// SAME descriptor.
/// </summary>
public static class SystemTypeIdentityKey
{
    public static string Build(string? familyKey, string? familyName, string typeName)
        => $"{(familyKey ?? familyName ?? string.Empty).ToUpperInvariant()}|{typeName.ToUpperInvariant()}";
}
