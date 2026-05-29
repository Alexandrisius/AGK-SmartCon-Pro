namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Options for loading a family into a Revit project.
/// </summary>
/// <param name="OverwriteExisting">
/// Whether to overwrite existing family.
/// NOTE: This flag is declared for API completeness but is NOT currently used by RevitFamilyLoadService.
/// The service always delegates the overwrite decision to Revit's IFamilyLoadOptions callback.
/// </param>
/// <param name="UpdateFamilyIfChanged">
/// Whether to update if source is newer.
/// NOTE: This flag is declared for API completeness but is NOT currently used by RevitFamilyLoadService.
/// The service always attempts to load and lets Revit determine if an update is needed via VersionGuid.
/// </param>
/// <param name="PreferredName">Preferred display name for the loaded family. If null, keeps the original name from the RFA file.</param>
public sealed record FamilyLoadOptions(
    bool OverwriteExisting = false,
    bool UpdateFamilyIfChanged = false,
    string? PreferredName = null,
    bool OverwriteParameterValues = true)
{
    /// <summary>Default options for MVP.</summary>
    public static FamilyLoadOptions Default { get; } = new();
}
