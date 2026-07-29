using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public sealed partial class ValidationRulesEditorView : DialogWindowBase
{
    public ValidationRulesEditorView(ValidationRulesEditorViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);

        ColOperator.Header = LanguageManager.GetString(StringLocalization.Keys.FM_RulesEditor_ColOperator);
        ColValue.Header = LanguageManager.GetString(StringLocalization.Keys.FM_RulesEditor_ColValue);
        ColEnabled.Header = LanguageManager.GetString(StringLocalization.Keys.FM_RulesEditor_ColEnabled);
    }
}
