using System.Windows;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Views;

public partial class MappingEditorView : Window
{
    public MappingEditorView(MappingEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
