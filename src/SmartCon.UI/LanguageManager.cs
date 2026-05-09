using System.Windows;
using SmartCon.Core.Services;

namespace SmartCon.UI;

/// <summary>
/// Manages the active application language and provides the current string resources.
/// </summary>
public static class LanguageManager
{
    private static ResourceDictionary? _currentDict;

    /// <summary>
    /// Initializes the language manager. Must be called once at application startup,
    /// before any UI is shown.
    /// </summary>
    public static void Initialize()
    {
        if (Application.Current is null)
        {
            try { _ = new Application(); }
            catch { /* Application already exists in another domain */ }
        }

        LocalizationService.LoadSavedLanguage();
        ApplyLanguage(LocalizationService.CurrentLanguage);
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    /// <summary>
    /// Switches the application language.
    /// </summary>
    public static void SwitchLanguage(Language lang)
    {
        LocalizationService.SetLanguage(lang);
    }

    /// <summary>
    /// Returns the current resource dictionary containing all localized strings.
    /// </summary>
    public static ResourceDictionary? GetCurrentStrings()
    {
        if (_currentDict is null)
            ApplyLanguage(LocalizationService.CurrentLanguage);
        return _currentDict;
    }

    /// <summary>
    /// Returns the localized string for the given key, or null if not found.
    /// </summary>
    public static string? GetString(string key)
    {
        return GetCurrentStrings()?[key] as string;
    }

    private static void OnLanguageChanged()
    {
        ApplyLanguage(LocalizationService.CurrentLanguage);
        LocalizationService.SaveLanguage();
    }

    private static void ApplyLanguage(Language lang)
    {
        _currentDict = StringLocalization.BuildResourceDictionary(lang);

        var app = Application.Current;
        if (app is not null && _currentDict is not null)
        {
            var old = app.Resources.MergedDictionaries.FirstOrDefault(d =>
                d.Contains(StringLocalization.Keys.Btn_Cancel));
            if (old is not null)
                app.Resources.MergedDictionaries.Remove(old);

            app.Resources.MergedDictionaries.Add(_currentDict);
        }
    }
}
