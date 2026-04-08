using System.Windows;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Views;

public partial class FittingCtcSetupView : Window
{
    public FittingCtcSetupView(FittingCtcSetupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += Close;
    }
}
