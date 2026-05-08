using System.Windows;
using System.Windows.Controls;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Sets keyboard focus to the target element when the attached element is loaded.
/// </summary>
public static class FocusBehavior
{
    public static readonly DependencyProperty FocusOnLoadProperty =
        DependencyProperty.RegisterAttached(
            "FocusOnLoad",
            typeof(bool),
            typeof(FocusBehavior),
            new PropertyMetadata(false, OnFocusOnLoadChanged));

    public static bool GetFocusOnLoad(DependencyObject obj)
        => (bool)obj.GetValue(FocusOnLoadProperty);

    public static void SetFocusOnLoad(DependencyObject obj, bool value)
        => obj.SetValue(FocusOnLoadProperty, value);

    private static void OnFocusOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;

        element.Loaded -= OnLoaded;

        if ((bool)e.NewValue)
        {
            element.Loaded += OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is UIElement element)
        {
            element.Focus();
        }
    }
}
