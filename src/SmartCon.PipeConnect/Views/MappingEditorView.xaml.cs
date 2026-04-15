using System.Windows;
using SmartCon.PipeConnect.Services;
using SmartCon.PipeConnect.ViewModels;

namespace SmartCon.PipeConnect.Views;

public partial class MappingEditorView : Window
{
    public MappingEditorView(MappingEditorViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
    }
}
