using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class SharedParameterPickerView : DialogWindowBase
{
    public SharedParameterPickerView(SharedParameterPickerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);

        ColName.Header = LanguageManager.GetString(StringLocalization.Keys.FM_SP_ColName);
        ColDescription.Header = LanguageManager.GetString(StringLocalization.Keys.FM_SP_ColDescription);
    }
}
