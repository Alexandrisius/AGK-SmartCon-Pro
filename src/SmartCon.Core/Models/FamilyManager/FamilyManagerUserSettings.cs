namespace SmartCon.Core.Models.FamilyManager;

/// <summary>
/// Per-machine FamilyManager user preferences (not per-database).
/// Stored as JSON under %APPDATA%\SmartCon\FamilyManager\user-settings.json.
/// </summary>
public sealed record FamilyManagerUserSettings(string? SharedParametersFilePath)
{
    public static FamilyManagerUserSettings Default => new((string?)null);
}
