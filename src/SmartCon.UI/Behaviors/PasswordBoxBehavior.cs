using System.Windows;
using System.Windows.Controls;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Two-way binding for <see cref="PasswordBox.Password"/>: WPF deliberately
/// keeps the password out of the dependency-property system, so an attached
/// property bridges it to the view model without logic in code-behind.
/// </summary>
public static class PasswordBoxBehavior
{
    public static readonly DependencyProperty BoundPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BoundPassword",
            typeof(string),
            typeof(PasswordBoxBehavior),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnBoundPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached(
            "IsUpdating",
            typeof(bool),
            typeof(PasswordBoxBehavior));

    public static string GetBoundPassword(DependencyObject obj)
        => (string)obj.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject obj, string value)
        => obj.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
            return;

        box.PasswordChanged -= OnPasswordChanged;

        if (!(bool)box.GetValue(IsUpdatingProperty))
            box.Password = e.NewValue as string ?? string.Empty;

        box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box)
            return;

        box.SetValue(IsUpdatingProperty, true);
        try
        {
            SetBoundPassword(box, box.Password);
        }
        finally
        {
            box.SetValue(IsUpdatingProperty, false);
        }
    }
}