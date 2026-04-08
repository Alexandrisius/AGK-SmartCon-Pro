using System.Text.Json;
using SmartCon.Core.Models;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.Core.Services.Implementation;

public sealed class JsonUpdateSettingsRepository : IUpdateSettingsRepository
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public string SettingsFilePath => _filePath;

    public JsonUpdateSettingsRepository()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var dir = Path.Combine(appData, "SmartCon");
        Directory.CreateDirectory(dir);
        _filePath = Path.Combine(dir, "update-settings.json");
    }

    public UpdateSettings Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
                return UpdateSettings.Default;

            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize<UpdateSettings>(json, s_jsonOptions)
                   ?? UpdateSettings.Default;
        }
    }

    public void Save(UpdateSettings settings)
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
