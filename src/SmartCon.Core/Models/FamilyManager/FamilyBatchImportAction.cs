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
    Skip
}
