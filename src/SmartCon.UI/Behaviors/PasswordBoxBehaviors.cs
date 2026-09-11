using System.Windows;
using System.Windows.Controls;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// MVVM binding for <see cref="PasswordBox"/> (I-10: no code-behind). The
/// control exposes no bindable password property by design — this attached
/// behavior mirrors <c>PasswordBox.Password</c> into a VM string property
/// two-way. The plain-text string lives in the VM only for the duration of
/// the login request; it is never logged and never persisted.
/// </summary>
public static class PasswordBoxBehaviors
{
    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(string),
            typeof(PasswordBoxBehaviors),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.Journal,
                OnBindPasswordChanged));

    public static string GetBindPassword(DependencyObject obj)
        => (string)obj.GetValue(BindPasswordProperty);

    public static void SetBindPassword(DependencyObject obj, string value)
        => obj.SetValue(BindPasswordProperty, value);

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating",
            typeof(bool),
            typeof(PasswordBoxBehaviors),
            new PropertyMetadata(false));

    private static void OnBindPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;

        box.PasswordChanged -= OnPasswordChanged;
        if (!Equals(e.NewValue, box.Password))
        {
            var isUpdating = (bool)box.GetValue(IsUpdatingProperty);
            box.SetValue(IsUpdatingProperty, true);
            try
            {
                box.Password = (string?)e.NewValue ?? string.Empty;
            }
            finally
            {
                box.SetValue(IsUpdatingProperty, isUpdating);
            }
        }
        box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;
        if ((bool)box.GetValue(IsUpdatingProperty)) return;

        box.SetValue(IsUpdatingProperty, true);
        try
        {
            SetBindPassword(box, box.Password);
        }
        finally
        {
            box.SetValue(IsUpdatingProperty, false);
        }
    }
}
