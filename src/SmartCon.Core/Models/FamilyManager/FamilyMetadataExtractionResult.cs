namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Result of metadata extraction from a family file.
/// MVP: populated with file-level metadata only (name, size, hash, timestamps).
/// Post-MVP: populated with deep extraction (types, parameters, category, preview).
/// </summary>
/// <param name="FileName">File name.</param>
/// <param name="FileSizeBytes">File size.</param>
/// <param name="Sha256">Content hash.</param>
/// <param name="LastWriteTimeUtc">Last write time.</param>
/// <param name="CategoryName">Detected Revit category (Post-MVP).</param>
/// <param name="RevitMajorVersion">Detected Revit version (Post-MVP).</param>
/// <param name="Types">Extracted type descriptors (Post-MVP).</param>
/// <param name="Parameters">Extracted parameter descriptors (Post-MVP).</param>
public sealed record FamilyMetadataExtractionResult(
    string FileName,
    long FileSizeBytes,
    string Sha256,
    DateTimeOffset? LastWriteTimeUtc,
    string? CategoryName,
    int? RevitMajorVersion,
    IReadOnlyList<FamilyTypeDescriptor>? Types,
    IReadOnlyList<FamilyParameterDescriptor>? Parameters);
