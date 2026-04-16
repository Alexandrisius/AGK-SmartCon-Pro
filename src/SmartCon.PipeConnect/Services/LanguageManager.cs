using System.Windows;
using System.Windows.Controls;
using SmartCon.Core.Services;

namespace SmartCon.PipeConnect.Services;

public static class LanguageManager
{
    private static readonly List<WeakReference<Window>> _registeredWindows = [];
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

    public static ResourceDictionary? GetCurrentStrings()
    {
        if (_currentDict is null)
            ApplyLanguage(LocalizationService.CurrentLanguage);
        return _currentDict;
    }

    public static string? GetString(string key)
    {
        return GetCurrentStrings()?[key] as string;
    }

    public static void EnsureWindowResources(Window window)
    {
        var dict = GetCurrentStrings();
        if (dict is null) return;

        var existing = window.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.Contains(StringLocalization.Keys.Btn_Cancel));
        if (existing is not null)
            window.Resources.MergedDictionaries.Remove(existing);

        window.Resources.MergedDictionaries.Add(dict);

        window.Loaded += (_, _) => ApplyDataGridHeaders(window);

        _registeredWindows.RemoveAll(wr => !wr.TryGetTarget(out _));
        _registeredWindows.Add(new WeakReference<Window>(window));
    }

    private static void OnLanguageChanged()
    {
        ApplyLanguage(LocalizationService.CurrentLanguage);
        RefreshAllWindows();
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

    private static void RefreshAllWindows()
    {
        for (int i = _registeredWindows.Count - 1; i >= 0; i--)
        {
            if (!_registeredWindows[i].TryGetTarget(out var window))
            {
                _registeredWindows.RemoveAt(i);
                continue;
            }

            var existing = window.Resources.MergedDictionaries.FirstOrDefault(d =>
                d.Contains(StringLocalization.Keys.Btn_Cancel));
            if (existing is not null)
                window.Resources.MergedDictionaries.Remove(existing);

            if (_currentDict is not null)
                window.Resources.MergedDictionaries.Add(_currentDict);

            ApplyDataGridHeaders(window);
        }
    }

    private static void ApplyDataGridHeaders(DependencyObject parent)
    {
        switch (parent)
        {
            case DataGrid dataGrid:
                ApplyColumnHeaders(dataGrid);
                break;
            case TabControl tabControl:
                foreach (var item in tabControl.Items)
                {
                    if (tabControl.ItemContainerGenerator.ContainerFromItem(item) is TabItem tabItem)
                        ApplyDataGridHeaders(tabItem);
                }
                break;
        }

        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            ApplyDataGridHeaders(System.Windows.Media.VisualTreeHelper.GetChild(parent, i));
    }

    private static void ApplyColumnHeaders(DataGrid dataGrid)
    {
        foreach (var column in dataGrid.Columns)
        {
            if (column.Header is string headerKey)
            {
                var value = GetString(headerKey);
                if (value is not null)
                    column.Header = value;
            }
        }
    }
}
