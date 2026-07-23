using System.Text.Json;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

/// <summary>
/// JSON-backed per-machine FamilyManager user settings.
/// Default location: %APPDATA%\SmartCon\FamilyManager\user-settings.json.
/// </summary>
public sealed class JsonFamilyManagerUserSettingsRepository : IFamilyManagerUserSettingsRepository
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public JsonFamilyManagerUserSettingsRepository(string? filePath = null)
    {
        if (filePath is not null)
        {
            _filePath = filePath;
        }
        else
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var dir = Path.Combine(appData, "SmartCon", "FamilyManager");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "user-settings.json");
        }
    }

    public string SettingsFilePath => _filePath;

    public FamilyManagerUserSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
                return FamilyManagerUserSettings.Default;

            try
            {
                var json = File.ReadAllText(_filePath);
                return JsonSerializer.Deserialize<FamilyManagerUserSettings>(json, s_jsonOptions)
                       ?? FamilyManagerUserSettings.Default;
            }
            catch (JsonException)
            {
                return FamilyManagerUserSettings.Default;
            }
        }
    }

    public void Save(FamilyManagerUserSettings settings)
    {
        lock (_lock)
        {
            var dir = Path.GetDirectoryName(_filePath)!;
            Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(settings, s_jsonOptions);
            File.WriteAllText(_filePath, json);
        }
    }
}
