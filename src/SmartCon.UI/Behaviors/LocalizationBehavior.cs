using System.Windows;
using SmartCon.Core.Services;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Automatically refreshes merged resource dictionaries on a FrameworkElement
/// when the application language changes.
/// </summary>
public static class LocalizationBehavior
{
    private static readonly List<WeakReference<FrameworkElement>> _tracked = [];
    private static bool _subscribed;

    public static readonly DependencyProperty AutoRefreshProperty =
        DependencyProperty.RegisterAttached(
            "AutoRefresh",
            typeof(bool),
            typeof(LocalizationBehavior),
            new PropertyMetadata(false, OnAutoRefreshChanged));

    public static bool GetAutoRefresh(DependencyObject obj)
        => (bool)obj.GetValue(AutoRefreshProperty);

    public static void SetAutoRefresh(DependencyObject obj, bool value)
        => obj.SetValue(AutoRefreshProperty, value);

    private static void OnAutoRefreshChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        EnsureSubscribed();

        if ((bool)e.NewValue)
        {
            RefreshDictionary(element);
            _tracked.RemoveAll(wr => !wr.TryGetTarget(out _));
            _tracked.Add(new WeakReference<FrameworkElement>(element));
        }
        else
        {
            _tracked.RemoveAll(wr => wr.TryGetTarget(out var t) && t == element);
        }
    }

    private static void EnsureSubscribed()
    {
        if (_subscribed) return;
        LocalizationService.LanguageChanged += OnLanguageChanged;
        _subscribed = true;
    }

    private static void OnLanguageChanged()
    {
        for (int i = _tracked.Count - 1; i >= 0; i--)
        {
            if (!_tracked[i].TryGetTarget(out var element))
            {
                _tracked.RemoveAt(i);
                continue;
            }

            if (element.Dispatcher.CheckAccess())
                RefreshDictionary(element);
            else
                element.Dispatcher.BeginInvoke(() => RefreshDictionary(element));
        }
    }

    private static void RefreshDictionary(FrameworkElement element)
    {
        var existing = element.Resources.MergedDictionaries.FirstOrDefault(d =>
            d.Contains(StringLocalization.Keys.Btn_Cancel));
        if (existing is not null)
            element.Resources.MergedDictionaries.Remove(existing);

        var dict = LanguageManager.GetCurrentStrings();
        if (dict is not null)
            element.Resources.MergedDictionaries.Add(dict);
    }
}
