using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class ConnectDatabaseView : DialogWindowBase
{
    public ConnectDatabaseView(ConnectDatabaseViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
