using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Persists per-machine FamilyManager user preferences (JSON file).
/// </summary>
public interface IFamilyManagerUserSettingsRepository
{
    FamilyManagerUserSettings Load();
    void Save(FamilyManagerUserSettings settings);
}
