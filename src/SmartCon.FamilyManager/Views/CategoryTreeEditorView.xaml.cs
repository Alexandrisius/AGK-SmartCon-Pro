using System.ComponentModel;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class CategoryTreeEditorView : DialogWindowBase
{
    public CategoryTreeEditorView(CategoryTreeEditorViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
