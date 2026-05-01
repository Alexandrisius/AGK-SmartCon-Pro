namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Options for loading a family into a Revit project.
/// </summary>
/// <param name="OverwriteExisting">Whether to overwrite existing family.</param>
/// <param name="UpdateFamilyIfChanged">Whether to update if source is newer.</param>
/// <param name="PreferredName">Preferred display name for the loaded family. If null, keeps the original name from the RFA file.</param>
public sealed record FamilyLoadOptions(
    bool OverwriteExisting = false,
    bool UpdateFamilyIfChanged = false,
    string? PreferredName = null)
{
    /// <summary>Default options for MVP.</summary>
    public static FamilyLoadOptions Default { get; } = new();
}
