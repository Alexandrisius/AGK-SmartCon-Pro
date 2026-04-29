using System.Windows;
using System.Windows.Controls;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;

namespace SmartCon.FamilyManager.Views;

public partial class FamilyManagerPaneControl : System.Windows.Controls.UserControl
{
    public FamilyManagerPaneControl(FamilyManagerMainViewModel viewModel)
    {
        InitializeComponent();

        var dict = LanguageManager.GetCurrentStrings();
        if (dict is not null)
        {
            Resources.MergedDictionaries.Add(dict);
        }

        DataContext = viewModel;
        SetColumnHeaders();

        SearchBox.TextChanged += (s, e) =>
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void SetColumnHeaders()
    {
        ColName.Header = LanguageManager.GetString(StringLocalization.Keys.FM_ColName);
        ColStatus.Header = LanguageManager.GetString(StringLocalization.Keys.FM_ColStatus);
        ColVersion.Header = LanguageManager.GetString(StringLocalization.Keys.FM_ColVersion);
        ColUpdated.Header = LanguageManager.GetString(StringLocalization.Keys.FM_ColUpdated);
    }
}
