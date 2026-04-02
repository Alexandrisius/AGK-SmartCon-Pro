using System.Windows;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Views;

public partial class PipeConnectEditorView : Window
{
    public PipeConnectEditorView(PipeConnectEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += Close;
        Loaded += (_, _) => viewModel.OnWindowLoaded();
        Closing += (_, e) =>
        {
            if (viewModel.IsSessionActive && !viewModel.IsBusy && !viewModel.IsClosing)
                viewModel.CancelCommand.Execute(null);
        };
    }
}
