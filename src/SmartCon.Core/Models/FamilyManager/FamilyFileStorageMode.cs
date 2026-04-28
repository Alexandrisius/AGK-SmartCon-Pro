namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// How the family file is stored.
/// </summary>
public enum FamilyFileStorageMode
{
    /// <summary>File is referenced at its original path.</summary>
    Linked = 0,
    /// <summary>File is copied to managed cache.</summary>
    Cached = 1,
    /// <summary>File is not available (deleted or moved).</summary>
    Missing = 2
}
