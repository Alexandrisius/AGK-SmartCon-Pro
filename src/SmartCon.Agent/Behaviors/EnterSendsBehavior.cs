using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SmartCon.Agent.Behaviors;

public static class EnterSendsBehavior
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command",
        typeof(ICommand),
        typeof(EnterSendsBehavior),
        new PropertyMetadata(null, OnCommandChanged));

    public static void SetCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(CommandProperty, value);

    public static ICommand? GetCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(CommandProperty);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox textBox)
            return;

        textBox.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is ICommand)
            textBox.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;

        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            return;

        var command = GetCommand((DependencyObject)sender);
        if (command is null)
            return;

        e.Handled = true;
        if (command.CanExecute(null))
            command.Execute(null);
    }
}