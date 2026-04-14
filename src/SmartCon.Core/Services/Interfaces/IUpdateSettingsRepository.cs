using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

/// <summary>
/// Persistence for auto-update settings (GitHub owner/repo, channel, check interval).
/// </summary>
public interface IUpdateSettingsRepository
{
    UpdateSettings Load();
    void Save(UpdateSettings settings);
    string SettingsFilePath { get; }
}
