using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;

namespace SmartCon.UI.Controls;

public static class HyperlinkCommand
{
    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.RegisterAttached(
            "Command", typeof(ICommand), typeof(HyperlinkCommand),
            new PropertyMetadata(null, OnCommandChanged));

    public static ICommand GetCommand(DependencyObject obj) => (ICommand)obj.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject obj, ICommand value) => obj.SetValue(CommandProperty, value);

    private static void OnCommandChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        if (obj is not System.Windows.Documents.Hyperlink link) return;
        link.RequestNavigate -= OnRequestNavigate;
        if (e.NewValue is not null)
            link.RequestNavigate += OnRequestNavigate;
    }

    private static void OnRequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        var link = (System.Windows.Documents.Hyperlink)sender;
        var command = GetCommand(link);
        if (command?.CanExecute(e.Uri.ToString()) == true)
            command.Execute(e.Uri.ToString());
        e.Handled = true;
    }
}
