using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Interfaces;

public interface IUpdateSettingsRepository
{
    UpdateSettings Load();
    void Save(UpdateSettings settings);
    string SettingsFilePath { get; }
}
