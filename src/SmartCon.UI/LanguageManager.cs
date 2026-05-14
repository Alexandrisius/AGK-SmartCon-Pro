using SmartCon.Core.Services;
using SmartCon.UI.Localization;

namespace SmartCon.UI;

public static class LanguageManager
{
    public static void Initialize()
    {
        LocalizationService.LoadSavedLanguage();
        TranslationSource.Instance.ChangeLanguage(LocalizationService.CurrentLanguage);
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public static void SwitchLanguage(Language lang)
    {
        LocalizationService.SetLanguage(lang);
    }

    public static string? GetString(string key)
    {
        return GetString(key, LocalizationService.CurrentLanguage);
    }

    public static string? GetString(string key, Language language)
    {
        return StringLocalization.GetString(key, language);
    }

    public static string Format(string key, params object[] args)
    {
        return StringLocalization.Format(key, LocalizationService.CurrentLanguage, args);
    }

    private static void OnLanguageChanged()
    {
        TranslationSource.Instance.ChangeLanguage(LocalizationService.CurrentLanguage);
        LocalizationService.SaveLanguage();
    }
}
