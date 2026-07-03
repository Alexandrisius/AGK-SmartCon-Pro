namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Action to take for a family during batch import.
/// </summary>
public enum FamilyBatchImportAction
{
    /// <summary>Create a new version (vN+1) and update current_version_label.</summary>
    IncrementVersion,

    /// <summary>Replace the file in the current version without changing current_version_label.</summary>
    OverwriteCurrent,

    /// <summary>Skip this file.</summary>
    Skip,

    /// <summary>
    /// Switch the active version pointer to the existing duplicate version found
    /// by content-hash. Does NOT save the incoming file — only updates
    /// <c>catalog_items.current_version_label</c> to point at the matched version.
    /// Available only for <see cref="FamilyBatchImportStatus.Duplicate"/>.
    /// </summary>
    MakeActive
}
