namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Why a family group could not be extracted (ADR-054). Tasks decide per
/// kind how to mark their own criterion (e.g. the hash task writes the
/// terminal markers -2/-1; attribute/GLB tasks stay pending and retry on
/// the next run).
/// </summary>
public enum ActualizationFailureKind
{
    /// <summary>Managed file not found on disk.</summary>
    MissingFile,

    /// <summary>File exists but open/extract failed (corrupt, Revit error).</summary>
    ExtractionFailed,
}
