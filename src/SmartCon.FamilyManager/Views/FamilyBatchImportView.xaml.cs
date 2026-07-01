using System.Windows;
using SmartCon.Core.Logging;
using SmartCon.Core.Services;
using SmartCon.FamilyManager.ViewModels;
using SmartCon.UI;
using SmartCon.UI.Controls;

namespace SmartCon.FamilyManager.Views;

public partial class FamilyBatchImportView : DialogWindowBase
{
    public FamilyBatchImportView(FamilyBatchImportViewModel viewModel)
    {
        SmartConLogger.Info($"FamilyBatchImportView.ctor: start (vm.Items.Count={viewModel.Items.Count})");

        try
        {
            SmartConLogger.Info("FamilyBatchImportView.ctor: calling InitializeComponent");
            InitializeComponent();
            SmartConLogger.Info("FamilyBatchImportView.ctor: InitializeComponent OK");
        }
        catch (Exception ex)
        {
            var current = ex;
            int depth = 0;
            while (current is not null && depth < 5)
            {
                SmartConLogger.Error(
                    $"FamilyBatchImportView.ctor FAILED [{depth}]: {current.GetType().Name}: {current.Message}");
                if (depth == 0 && ex.StackTrace is not null)
                    SmartConLogger.Error($"Stack: {ex.StackTrace}");
                current = current.InnerException;
                depth++;
            }
            throw;
        }

        DataContext = viewModel;
        BindCloseRequest(viewModel);

        ColFileName.Header = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_FileName);
        ColRevitVersion.Header = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_RevitVersion);
        ColTypeCount.Header = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_TypeCount);
        ColStatus.Header = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Status);
        ColCategory.Header = LanguageManager.GetString(StringLocalization.Keys.FM_Category);
        ColAction.Header = LanguageManager.GetString(StringLocalization.Keys.FM_BatchImport_Action);
        SmartConLogger.Info("FamilyBatchImportView.ctor: done");
    }
}
