using System.Windows;
using SmartCon.Core.Services;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public partial class FamilyBatchImportView : DialogWindowBase
{
    public FamilyBatchImportView(FamilyBatchImportViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        BindCloseRequest(viewModel);

        ColFileName.Header = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_FileName) ?? "File Name";
        ColRevitVersion.Header = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_RevitVersion) ?? "Revit Version";
        ColSize.Header = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Size) ?? "Size";
        ColStatus.Header = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Status) ?? "Status";
        ColCategory.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Category) ?? "Category";
        ColAction.Header = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Action) ?? "Action";
    }
}
