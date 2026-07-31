namespace SmartCon.Core.Models.FamilyManager;

/// <param name="FamilyName">Revit system family of the type (Issue #183):
/// the identity of a system type is (FamilyName, Name) — e.g. "Conduit
/// with Fittings" vs "Conduit without Fittings". <c>null</c> for legacy
/// rows (pre-V26 schema) and loadable types (single family per item —
/// the family is implied by the catalog item itself).</param>
public sealed record FamilyTypeDescriptor(
    string Id,
    string CatalogItemId,
    string Name,
    int SortOrder,
    string? VersionId = null,
    string? FileId = null,
    string? ExtractionRunId = null,
    string? UniqueId = null,
    string? FamilyName = null);
