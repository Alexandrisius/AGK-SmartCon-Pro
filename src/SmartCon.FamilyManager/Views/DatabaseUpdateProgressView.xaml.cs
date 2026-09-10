using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public partial class DatabaseUpdateProgressView : DialogWindowBase
{
    public DatabaseUpdateProgressView(DatabaseUpdateProgressViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
