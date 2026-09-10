using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class MissingRecordsCleanupView : DialogWindowBase
{
    public MissingRecordsCleanupView(MissingRecordsCleanupViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);

        ColItem.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ColItem);
        ColVersion.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ColVersion);
        ColRevit.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ColRevit);
        ColFile.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ColFile);
        ColReason.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Cleanup_ColReason);
    }
}
