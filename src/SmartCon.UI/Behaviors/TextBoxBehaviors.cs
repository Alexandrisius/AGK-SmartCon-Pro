using System.Windows;
using System.Windows.Controls;

namespace SmartCon.UI.Behaviors;

public static class TextBoxBehaviors
{
    public static readonly DependencyProperty PlaceholderProperty =
        DependencyProperty.RegisterAttached(
            "Placeholder",
            typeof(UIElement),
            typeof(TextBoxBehaviors),
            new PropertyMetadata(null, OnPlaceholderChanged));

    public static UIElement GetPlaceholder(DependencyObject obj)
        => (UIElement)obj.GetValue(PlaceholderProperty);

    public static void SetPlaceholder(DependencyObject obj, UIElement value)
        => obj.SetValue(PlaceholderProperty, value);

    private static void OnPlaceholderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox) return;

        if (e.OldValue is not null)
            textBox.TextChanged -= OnTextChanged;

        if (e.NewValue is not null)
        {
            UpdatePlaceholder(textBox, (UIElement)e.NewValue);
            textBox.TextChanged += OnTextChanged;
        }
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        var placeholder = GetPlaceholder(textBox);
        if (placeholder is not null)
            UpdatePlaceholder(textBox, placeholder);
    }

    private static void UpdatePlaceholder(TextBox textBox, UIElement placeholder)
    {
        placeholder.Visibility = string.IsNullOrEmpty(textBox.Text)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
