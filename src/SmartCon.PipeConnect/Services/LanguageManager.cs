using System.Windows;
using SmartCon.Core.Services;

namespace SmartCon.PipeConnect.Services;

public static class LanguageManager
{
    private static readonly string RuPackUri = "pack://application:,,,/SmartCon.PipeConnect;component/Resources/Strings.ru.xaml";
    private static readonly string EnPackUri = "pack://application:,,,/SmartCon.PipeConnect;component/Resources/Strings.en.xaml";

    private static ResourceDictionary? _currentDict;

    public static void Initialize()
    {
        LocalizationService.LoadSavedLanguage();
        ApplyLanguage(LocalizationService.CurrentLanguage);
        LocalizationService.LanguageChanged += OnLanguageChanged;
    }

    public static void SwitchLanguage(Language lang)
    {
        LocalizationService.SetLanguage(lang);
    }

    private static void OnLanguageChanged()
    {
        ApplyLanguage(LocalizationService.CurrentLanguage);
        LocalizationService.SaveLanguage();
    }

    private static void ApplyLanguage(Language lang)
    {
        var app = Application.Current;
        if (app is null) return;

        if (_currentDict is not null)
        {
            app.Resources.MergedDictionaries.Remove(_currentDict);
        }

        var uri = lang == Language.EN ? EnPackUri : RuPackUri;
        _currentDict = new ResourceDictionary { Source = new Uri(uri) };
        app.Resources.MergedDictionaries.Add(_currentDict);
    }
}
