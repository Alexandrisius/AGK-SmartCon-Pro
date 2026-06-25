namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Status of a file in the batch import dialog.
/// </summary>
public enum FamilyBatchImportStatus
{
    /// <summary>New family, not in catalog.</summary>
    New,

    /// <summary>Family exists in catalog with the same normalized name.</summary>
    Existing,

    /// <summary>Error reading file.</summary>
    Error
}
