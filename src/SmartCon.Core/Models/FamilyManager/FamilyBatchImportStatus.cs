namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Status of a file in the batch import dialog.
/// </summary>
public enum FamilyBatchImportStatus
{
    /// <summary>New family, not in catalog.</summary>
    New,

    /// <summary>Family exists in catalog with the same normalized name
    /// but the content hash differs (content changed).</summary>
    Existing,

    /// <summary>Family exists in catalog with the same normalized name
    /// AND the content hash matches a version (current or archived).
    /// Import is skipped by default to avoid duplicate versions.</summary>
    Duplicate,

    /// <summary>Error reading file or computing content hash.</summary>
    Error
}
