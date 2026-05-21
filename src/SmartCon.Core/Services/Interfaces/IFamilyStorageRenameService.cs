namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Renames physical family files in managed storage and updates database records
/// when a family's display name is changed.
/// </summary>
public interface IFamilyStorageRenameService
{
    /// <summary>
    /// Renames all .rfa files for the current version of the specified catalog item
    /// to match the new name, preserving the .rfa extension.
    /// Only the current version (per current_version_label) is affected;
    /// historical versions remain untouched.
    /// </summary>
    Task RenameFamilyFilesAsync(string catalogItemId, string newName, CancellationToken ct = default);
}
