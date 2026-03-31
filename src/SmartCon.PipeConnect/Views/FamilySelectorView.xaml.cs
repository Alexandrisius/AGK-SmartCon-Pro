using System.Windows;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Views;

public partial class FamilySelectorView : Window
{
    public FamilySelectorView(FamilySelectorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += () => Close();
    }
}
