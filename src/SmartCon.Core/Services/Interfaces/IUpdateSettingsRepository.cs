using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Persistence for auto-update settings (GitHub owner/repo, channel, check interval).
/// </summary>
public interface IUpdateSettingsRepository
{
    /// <summary>Load update settings from disk (creates default if missing).</summary>
    UpdateSettings Load();

    /// <summary>Persist update settings to disk.</summary>
    void Save(UpdateSettings settings);

    /// <summary>Full path to the settings JSON file.</summary>
    string SettingsFilePath { get; }
}
