using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;
using SmartCon.UI.Converters;

namespace SmartCon.FamilyManager.Views;

public sealed partial class FamilyPropertiesView : DialogWindowBase
{
    public FamilyPropertiesView(FamilyPropertiesViewModel viewModel)
    {
        Resources.MergedDictionaries.Add(new SingletonResources());
        Resources["BoolToVisibility"] = new BoolToVisibilityConverter();
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
        BindCloseRequest(viewModel);
    }
}
