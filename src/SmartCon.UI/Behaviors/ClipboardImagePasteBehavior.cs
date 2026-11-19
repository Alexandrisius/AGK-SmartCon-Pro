using System.Windows;
using System.Windows.Input;

namespace SmartCon.UI.Behaviors;

public static class ClipboardImagePasteBehavior
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command",
            typeof(ICommand),
            typeof(ClipboardImagePasteBehavior),
            new PropertyMetadata(null, OnCommandChanged));

    public static void SetCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(CommandProperty, value);

    public static ICommand? GetCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(CommandProperty);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        element.PreviewKeyDown -= OnPreviewKeyDown;
        if (e.NewValue is ICommand)
            element.PreviewKeyDown += OnPreviewKeyDown;
    }

    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.V || (Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;

        try
        {
            if (!Clipboard.ContainsImage())
                return;
        }
        catch (Exception)
        {
            return;
        }

        var command = GetCommand((DependencyObject)sender);
        if (command is null || !command.CanExecute(null))
            return;

        command.Execute(null);
        e.Handled = true;
    }
}