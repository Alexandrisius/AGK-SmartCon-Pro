using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// Two-way binding for <see cref="PasswordBox.Password"/>: WPF deliberately
/// keeps the password out of the dependency-property system, so an attached
/// property bridges it to the view model without logic in code-behind.
/// </summary>
/// <remarks>
/// <see cref="BindPasswordProperty"/> is the reliable subscription path: WPF does
/// not raise the <see cref="BoundPasswordProperty"/> property-changed callback
/// when the bound value equals the metadata default (an empty key field on a
/// fresh machine), so a subscription made only from that callback would never
/// fire and typing would never reach the view model. The callback still
/// resubscribes as a fallback for legacy bindings.
/// </remarks>
public static class PasswordBoxBehavior
{
    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(bool),
            typeof(PasswordBoxBehavior),
            new PropertyMetadata(false, OnBindPasswordChanged));

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

    public static bool GetBindPassword(DependencyObject obj)
        => (bool)obj.GetValue(BindPasswordProperty);

    public static void SetBindPassword(DependencyObject obj, bool value)
        => obj.SetValue(BindPasswordProperty, value);

    public static string GetBoundPassword(DependencyObject obj)
        => (string)obj.GetValue(BoundPasswordProperty);

    public static void SetBoundPassword(DependencyObject obj, string value)
        => obj.SetValue(BoundPasswordProperty, value);

    private static void OnBindPasswordChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
            return;

        box.PasswordChanged -= OnPasswordChanged;
        box.GotKeyboardFocus -= OnGotKeyboardFocus;
        if ((bool)e.NewValue)
        {
            box.PasswordChanged += OnPasswordChanged;
            box.GotKeyboardFocus += OnGotKeyboardFocus;
        }
    }

    /// <summary>
    /// The field is pre-filled with the stored key (shown as dots); selecting it
    /// on focus makes typing replace the key instead of appending to it.
    /// </summary>
    private static void OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is PasswordBox box)
            box.SelectAll();
    }

    private static void OnBoundPasswordChanged(
        DependencyObject d,
        DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box)
            return;

        // Belt and braces for bindings that do not set BindPassword: the callback
        // only runs when the value really changed, so typing cannot be lost any
        // more than it was before this fix. Resubscription is idempotent.
        box.PasswordChanged -= OnPasswordChanged;
        box.PasswordChanged += OnPasswordChanged;

        if ((bool)box.GetValue(IsUpdatingProperty))
            return;

        var value = e.NewValue as string ?? string.Empty;
        if (!string.Equals(box.Password, value, StringComparison.Ordinal))
            box.Password = value;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box)
            return;

        if (string.Equals(GetBoundPassword(box), box.Password, StringComparison.Ordinal))
            return;

        box.SetValue(IsUpdatingProperty, true);
        try
        {
            // SetCurrentValue keeps the binding expression alive; SetValue would
            // replace it, and the next source update would silently vanish.
            box.SetCurrentValue(BoundPasswordProperty, box.Password);
        }
        finally
        {
            box.SetValue(IsUpdatingProperty, false);
        }
    }
}