using System.Windows;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Views;

public partial class FamilySelectorView : Window
{
    public FamilySelectorView(FamilySelectorViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        viewModel.RequestClose += () => Close();
    }
}
