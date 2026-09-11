using System.Windows;
using System.Windows.Controls;

namespace SmartCon.UI.Behaviors;

/// <summary>
/// MVVM binding for <see cref="PasswordBox"/> (I-10: no code-behind). The
/// control exposes no bindable password property by design — this attached
/// behavior mirrors <c>PasswordBox.Password</c> into a VM string property
/// two-way. Input pushes via <see cref="SetCurrentValue"/>: a plain
/// <c>SetValue</c> from the handler would REPLACE the BindingExpression
/// (local value precedence) and silently kill the binding — the exact bug
/// behind the permanently gray login button. The plain-text string lives in
/// the VM only for the duration of the login request; it is never logged
/// and never persisted.
/// </summary>
public static class PasswordBoxBehaviors
{
    // ВАЖНО: default = null, а НЕ "". Callback вызывается только при ИЗМЕНЕНИИ
    // эффективного значения: с default "" активация биндинга (source == "")
    // не меняет значение → callback не зовётся → подписка на PasswordChanged
    // не ставится → ввод никогда не доезжает до VM (серая кнопка логина).
    public static readonly DependencyProperty BindPasswordProperty =
        DependencyProperty.RegisterAttached(
            "BindPassword",
            typeof(string),
            typeof(PasswordBoxBehaviors),
            new FrameworkPropertyMetadata(
                null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault | FrameworkPropertyMetadataOptions.Journal,
                OnBindPasswordChanged));

    public static string GetBindPassword(DependencyObject obj)
        => (string?)obj.GetValue(BindPasswordProperty) ?? string.Empty;

    public static void SetBindPassword(DependencyObject obj, string value)
        => obj.SetValue(BindPasswordProperty, value);

    private static void OnBindPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;

        box.PasswordChanged -= OnPasswordChanged;
        var newValue = (string?)e.NewValue ?? string.Empty;
        if (!Equals(newValue, box.Password))
        {
            // source → target (VM очистила пароль и т.п.)
            box.PasswordChanged -= OnPasswordChanged;
            try
            {
                box.Password = newValue;
            }
            finally
            {
                box.PasswordChanged += OnPasswordChanged;
            }
        }
        box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object? sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox box) return;

        // НЕ SetValue: он заменит BindingExpression локальным значением и
        // убьёт биндинг. SetCurrentValue меняет только эффективное значение —
        // expression остаётся владельцем и (UST=PropertyChanged) пушит в source.
        box.SetCurrentValue(BindPasswordProperty, box.Password);
    }
}
