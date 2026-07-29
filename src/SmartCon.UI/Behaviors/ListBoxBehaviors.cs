using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SmartCon.UI.Behaviors;

public static class ListBoxBehaviors
{
    #region RightClickSelect

    public static readonly DependencyProperty RightClickSelectProperty =
        DependencyProperty.RegisterAttached(
            "RightClickSelect",
            typeof(bool),
            typeof(ListBoxBehaviors),
            new PropertyMetadata(false, OnRightClickSelectChanged));

    public static bool GetRightClickSelect(DependencyObject obj) => (bool)obj.GetValue(RightClickSelectProperty);

    public static void SetRightClickSelect(DependencyObject obj, bool value) => obj.SetValue(RightClickSelectProperty, value);

    private static void OnRightClickSelectChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox listBox) return;

        listBox.PreviewMouseRightButtonDown -= OnListBoxPreviewMouseRightButtonDown;

        if ((bool)e.NewValue)
        {
            listBox.PreviewMouseRightButtonDown += OnListBoxPreviewMouseRightButtonDown;
        }
    }

    private static void OnListBoxPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox) return;

        var hit = VisualTreeHelper.HitTest(listBox, e.GetPosition(listBox));
        if (hit?.VisualHit is not DependencyObject dep) return;

        var item = FindAncestor<ListBoxItem>(dep);
        if (item is null) return;

        item.IsSelected = true;
    }

    private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    #endregion
}
