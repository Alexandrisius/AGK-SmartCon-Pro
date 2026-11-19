using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Redirects mouse-wheel scrolling from an inner scrollable control
/// (e.g. FlowDocumentScrollViewer) to the nearest outer ScrollViewer,
/// so the chat transcript keeps scrolling when the cursor is over content.
/// </summary>
public static class MouseWheelRedirectBehavior
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled",
        typeof(bool),
        typeof(MouseWheelRedirectBehavior),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) =>
        element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element) =>
        (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        element.PreviewMouseWheel -= OnPreviewMouseWheel;
        if (e.NewValue is true)
            element.PreviewMouseWheel += OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject source)
            return;

        var scrollViewer = FindAncestor<ScrollViewer>(source);
        if (scrollViewer is null)
            return;

        var delta = e.Delta / 120.0 * 48.0;
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - delta);
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject current)
        where T : DependencyObject
    {
        var parent = VisualTreeHelper.GetParent(current);
        while (parent is not null)
        {
            if (parent is T typed)
                return typed;

            parent = VisualTreeHelper.GetParent(parent);
        }

        return null;
    }
}