using System.Windows;
using SmartCon.FamilyManager.ViewModels.ProjectBase;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public partial class AllowedValuesView : DialogWindowBase
{
    public AllowedValuesView(AllowedValuesViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
