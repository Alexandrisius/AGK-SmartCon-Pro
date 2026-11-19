using System.Windows;
using System.Windows.Input;

namespace SmartCon.UI.Behaviors;

public static class FileDropBehavior
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command",
            typeof(ICommand),
            typeof(FileDropBehavior),
            new PropertyMetadata(null, OnCommandChanged));

    public static void SetCommand(DependencyObject element, ICommand? value) =>
        element.SetValue(CommandProperty, value);

    public static ICommand? GetCommand(DependencyObject element) =>
        (ICommand?)element.GetValue(CommandProperty);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;

        element.AllowDrop = true;
        element.DragOver -= OnDragOver;
        element.Drop -= OnDrop;

        if (e.NewValue is ICommand)
        {
            element.DragOver += OnDragOver;
            element.Drop += OnDrop;
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
            return;

        var command = GetCommand((DependencyObject)sender);
        if (command is not null && command.CanExecute(paths))
            command.Execute(paths);

        e.Handled = true;
    }
}