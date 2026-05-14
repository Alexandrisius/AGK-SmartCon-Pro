using System.ComponentModel;
using System.Runtime.CompilerServices;
using SmartCon.Core.Services;

namespace SmartCon.UI.Localization;

public sealed class TranslationSource : INotifyPropertyChanged
{
    public static TranslationSource Instance { get; } = new();

    private Language _currentLanguage = Language.RU;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrEmpty(key))
                return string.Empty;

            var result = StringLocalization.GetString(key, _currentLanguage);
            return result ?? $"[{key}]";
        }
    }

    public Language CurrentLanguage => _currentLanguage;

    public void ChangeLanguage(Language language)
    {
        if (_currentLanguage == language) return;
        _currentLanguage = language;
        OnLanguageChanged();
    }

    private void OnLanguageChanged()
    {
        foreach (var key in StringLocalization.GetAllKeys())
            OnPropertyChanged($"Item[{key}]");

        OnPropertyChanged(string.Empty);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
