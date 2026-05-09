using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class AttributeLibraryView : DialogWindowBase
{
    public AttributeLibraryView(AttributeLibraryViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);

        Loaded += (_, _) => SetColumnHeaders();
    }

    private void SetColumnHeaders()
    {
        ColName.Header = LanguageManager.GetString(StringLocalization.Keys.FM_AL_Name);
        ColGroup.Header = LanguageManager.GetString(StringLocalization.Keys.FM_AL_Group);
        ColActive.Header = LanguageManager.GetString(StringLocalization.Keys.FM_AL_Active);
    }
}
