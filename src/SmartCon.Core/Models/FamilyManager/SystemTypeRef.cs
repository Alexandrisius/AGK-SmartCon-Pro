namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Issue #183: reference to a system type by its full identity —
/// (family, name). A bare type name is ambiguous: "Стандарт" exists in
/// both "Conduit with Fittings" and "Conduit without Fittings".
/// </summary>
/// <param name="Name">Type name.</param>
/// <param name="FamilyName">Revit system family of the type, or
/// <c>null</c> for legacy callers (first name match wins — the pre-#183
/// behaviour).</param>
/// <param name="FamilyKey">Locale-invariant family identity (Issue #190,
/// ADR-064); preferred over <paramref name="FamilyName"/> for matching
/// when present.</param>
public sealed record SystemTypeRef(
    string Name,
    string? FamilyName = null,
    string? FamilyKey = null);
