namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Status of a file in the batch import dialog.
/// </summary>
public enum FamilyBatchImportStatus
{
    /// <summary>New family, not in catalog.</summary>
    New,

    /// <summary>Family exists in catalog with a different SHA256.</summary>
    Existing,

    /// <summary>Exact SHA256 match, skip automatically.</summary>
    Duplicate,

    /// <summary>Error reading file.</summary>
    Error
}
