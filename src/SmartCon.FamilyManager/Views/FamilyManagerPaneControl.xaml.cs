using System.Windows;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class FamilyManagerPaneControl : System.Windows.Controls.UserControl
{
    public FamilyManagerPaneControl(FamilyManagerMainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        var window = Window.GetWindow(this);
        if (window is not null)
            LanguageManager.EnsureWindowResources(window);
    }
}
