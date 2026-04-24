using System.Text.Json;
using System.Text.Json.Serialization;
using SmartCon.Core.Models;

namespace SmartCon.Core.Services.Storage;

public static class ShareSettingsJsonSerializer
{
    public const int CurrentVersion = 2;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize(ShareProjectSettings settings)
    {
#if NETFRAMEWORK
        if (settings is null) throw new ArgumentNullException(nameof(settings));
#else
        ArgumentNullException.ThrowIfNull(settings);
#endif
        return JsonSerializer.Serialize(settings, Options);
    }

    public static ShareProjectSettings Deserialize(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return ShareProjectSettings.Empty;

        var migrated = Migrate(json!);

        return JsonSerializer.Deserialize<ShareProjectSettings>(migrated, Options)
               ?? ShareProjectSettings.Empty;
    }

    private static string Migrate(string json)
    {
        json = json.Replace("\"role\":", "\"field\":");

        if (json.Contains("\"statusMappings\""))
            json = json.Replace("\"statusMappings\"", "\"exportMappings\"");

        if (json.Contains("\"status_mappings\""))
            json = json.Replace("\"status_mappings\"", "\"exportMappings\"");

        return json;
    }

    public static ShareProjectSettings? TryReadFromFile(string path)
    {
#if NETFRAMEWORK
        if (path is null) throw new ArgumentNullException(nameof(path));
#else
        ArgumentNullException.ThrowIfNull(path);
#endif
        if (!File.Exists(path)) return null;

        try
        {
            var json = File.ReadAllText(path);
            return Deserialize(json);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }

    public static void WriteToFile(string path, ShareProjectSettings settings)
    {
#if NETFRAMEWORK
        if (path is null) throw new ArgumentNullException(nameof(path));
        if (settings is null) throw new ArgumentNullException(nameof(settings));
#else
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(settings);
#endif

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        File.WriteAllText(path, Serialize(settings));
    }
}
