using System.Text.Json;

namespace SmartCon.Core.Services;

public static partial class LocalizationService
{
    private static readonly Dictionary<string, string> Ru = new();
    private static readonly Dictionary<string, string> En = new();

    private static Dictionary<string, string> _current;

    public static Language CurrentLanguage { get; private set; } = Language.RU;

    public static event Action? LanguageChanged;

    static LocalizationService()
    {
        AddCommonKeys(Ru, En);
        AddPipeConnectKeys(Ru, En);
        AddProjectManagementKeys(Ru, En);
        AddFamilyManagerKeys(Ru, En);
        _current = Ru;
    }

    public static string GetString(string key)
        => _current.TryGetValue(key, out var value) ? value : key;

    public static string GetString(string key, Language language)
    {
        var dict = language == Language.EN ? En : Ru;
        return dict.TryGetValue(key, out var value) ? value : key;
    }

    public static bool ContainsKey(string key) => Ru.ContainsKey(key);

    public static IReadOnlyCollection<string> GetAllKeys() => Ru.Keys;

    public static string Format(string key, params object[] args)
    {
        var format = GetString(key);
        try { return string.Format(format, args); }
        catch { return format; }
    }

    public static string Format(string key, Language language, params object[] args)
    {
        var format = GetString(key, language);
        try { return string.Format(format, args); }
        catch { return format; }
    }

    public static void SetLanguage(Language lang)
    {
        if (CurrentLanguage == lang) return;
        CurrentLanguage = lang;
        _current = lang == Language.EN ? En : Ru;
        LanguageChanged?.Invoke();
    }

    public static void LoadSavedLanguage()
    {
        try
        {
            if (!File.Exists(SettingsPath)) return;
            var json = File.ReadAllText(SettingsPath);
            var doc = JsonDocument.Parse(json);
            var langStr = doc.RootElement.TryGetProperty("language", out var prop)
                ? prop.GetString() : "RU";
            var lang = langStr == "EN" ? Language.EN : Language.RU;
            CurrentLanguage = lang;
            _current = lang == Language.EN ? En : Ru;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[SmartConLogger] {ex.Message}");
            CurrentLanguage = Language.RU;
            _current = Ru;
        }
    }

    public static void SaveLanguage()
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            var json = "{\"language\":\"" + (CurrentLanguage == Language.EN ? "EN" : "RU") + "\"}";
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[SmartConLogger] {ex.Message}"); }
    }

    public static string Get(Language lang)
        => lang == Language.EN ? "EN" : "RU";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AGK", "SmartCon", "settings.json");
}
