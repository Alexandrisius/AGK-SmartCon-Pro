using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SmartCon.Core.Logging;
using SmartCon.Core.Models;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.ProjectManagement.ViewModels;
using SmartCon.ProjectManagement.Views;
#if NET8_0_OR_GREATER
using CommandBase = Nice3point.Revit.Toolkit.External.ExternalCommand;
#else
using CommandBase = Autodesk.Revit.UI.IExternalCommand;
#endif

namespace SmartCon.ProjectManagement.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class ShareProjectCommand : CommandBase
{
    private ShareProgressViewModel? _progressVm;
    private ShareProgressView? _progressView;

#if NET8_0_OR_GREATER
    public override void Execute()
    {
        Result = ExecuteCore(Application, out var message);
        ErrorMessage = message;
    }
#else
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        return ExecuteCore(commandData.Application, out message);
    }
#endif

    private Result ExecuteCore(UIApplication uiApp, out string message)
    {
        message = string.Empty;
        using var _ = SmartConLogger.BeginScope("ShareProject",
            ("Method", "Execute"));
        using var measure = SmartConLogger.Measure("ShareProject.Execute");

        try
        {
            SmartConLogger.Info("ShareProjectCommand started.");

            var uiapp = uiApp;
            CommandHelper.InitializeContext(uiapp);
            var originalDoc = CommandHelper.GetDocument();

            if (string.IsNullOrWhiteSpace(originalDoc.PathName))
            {
                Autodesk.Revit.UI.TaskDialog.Show(
                    LocalizationService.GetString("PM_Title_ShareDialog"),
                    LocalizationService.GetString("PM_Msg_MustBeSaved"));
                return Result.Failed;
            }

            var settingsRepo = ServiceHost.GetService<IShareProjectSettingsRepository>();
            var settings = settingsRepo.Load(originalDoc);

            SmartConLogger.Info($"Settings loaded. ShareFolder='{settings.ShareFolderPath}', Blocks={settings.FileNameTemplate.Blocks.Count}, ExportMappings={settings.FileNameTemplate.ExportMappings.Count}");

            if (string.IsNullOrWhiteSpace(settings.ShareFolderPath) || settings.FileNameTemplate.Blocks.Count == 0)
            {
                SmartConLogger.Warn("Settings incomplete — showing configure dialog. " +
                    "[Action: задайте папку Shared и блоки шаблона имени в настройках Share и повторите команду]");
                Autodesk.Revit.UI.TaskDialog.Show(LocalizationService.GetString("PM_Title_ShareDialog"),
                    LocalizationService.GetString("PM_Result_NoSettings"));
                return Result.Failed;
            }

            var currentFileName = System.IO.Path.GetFileName(originalDoc.PathName);
            var parser = ServiceHost.GetService<IFileNameParser>();
            var exportValidation = parser.ValidateExportMappings(currentFileName, settings.FileNameTemplate, settings.FieldLibrary);

            var hasAnyError = !exportValidation.IsValid;

            string sharedFileName;

            if (hasAnyError)
            {
                var combinedSummary = exportValidation.Summary;

                SmartConLogger.Warn($"Export validation failed for '{currentFileName}': {combinedSummary} " +
                    "[Action: будет показан диалог коррекции имени экспорта или применён сохранённый override]");

                var existingOverride = settingsRepo.LoadExportNameOverride(originalDoc);
                if (existingOverride is not null)
                {
                    SmartConLogger.Info("Using saved ExportNameOverride.");
                    var values = existingOverride.FieldValues;
                    var orderedBlocks = settings.FileNameTemplate.Blocks.OrderBy(b => b.Index).ToList();
                    var sb = new StringBuilder();
                    for (int i = 0; i < orderedBlocks.Count; i++)
                    {
                        var block = orderedBlocks[i];
                        sb.Append(values.TryGetValue(block.Field, out var v) ? v : string.Empty);
                        if (i < orderedBlocks.Count - 1)
                        {
                            var delimiter = block.ParseRule?.Delimiter;
                            sb.Append(!string.IsNullOrEmpty(delimiter) ? delimiter : "-");
                        }
                    }
                    var ext = System.IO.Path.GetExtension(originalDoc.PathName);
                    sharedFileName = sb.ToString();
                    if (!string.IsNullOrEmpty(ext) && !System.IO.Path.HasExtension(sharedFileName))
                        sharedFileName += ext;
                }
                else
                {
                    var exportDetails = exportValidation.Blocks
                        .Where(b => !b.IsValid)
                        .Select(b => $"  \u2022 {b.Field}: '{b.Value}' (mapped) \u2014 {b.Error}");

                    var details = string.Join("\n", exportDetails);

                    var dialogVm = new ViewModels.ExportNameDialogViewModel(
                        currentFileName,
                        $"{LocalizationService.GetString("PM_Result_InvalidName")}\n\n{combinedSummary}\n{details}",
                        settings.FileNameTemplate.Blocks,
                        settings.FieldLibrary,
                        settings.FileNameTemplate.ExportMappings);

                    var dialogView = new Views.ExportNameDialog(dialogVm)
                    {
                        Owner = GetMainWindow(uiApp)
                    };
                    dialogView.ShowDialog();

                    if (dialogView.CustomDialogResult != true)
                    {
                        SmartConLogger.Info("User cancelled ExportNameDialog.");
                        return Result.Cancelled;
                    }

                    var fieldValues = dialogVm.GetFieldValues();
                    settingsRepo.SaveExportNameOverride(originalDoc, new Core.Models.ExportNameOverride { FieldValues = fieldValues });

                    var ext = System.IO.Path.GetExtension(originalDoc.PathName);
                    sharedFileName = dialogVm.PreviewFileName;
                    if (!string.IsNullOrEmpty(ext) && !System.IO.Path.HasExtension(sharedFileName))
                        sharedFileName += ext;

                    SmartConLogger.Info($"ExportNameOverride saved. Custom name: {sharedFileName}");
                }
            }
            else
            {
                SmartConLogger.Info($"Validation passed for '{currentFileName}'.");
                sharedFileName = parser.TransformForExport(currentFileName, settings.FileNameTemplate, settings.FieldLibrary) ?? string.Empty;
                var extension = System.IO.Path.GetExtension(originalDoc.PathName);
                if (!string.IsNullOrEmpty(extension) && !System.IO.Path.HasExtension(sharedFileName))
                    sharedFileName += extension;
            }

            if (string.IsNullOrWhiteSpace(sharedFileName))
            {
                Autodesk.Revit.UI.TaskDialog.Show(
                    LocalizationService.GetString("PM_Title_ShareDialog"),
                    LocalizationService.GetString("PM_Msg_TransformFailed"));
                return Result.Failed;
            }

            var sharedFilePath = System.IO.Path.Combine(settings.ShareFolderPath, sharedFileName);

            if (!System.IO.Directory.Exists(settings.ShareFolderPath))
                System.IO.Directory.CreateDirectory(settings.ShareFolderPath);

            var isWorkshared = originalDoc.IsWorkshared;
            var originalPathName = originalDoc.PathName;

            ShowProgress(uiApp.MainWindowHandle);

            EventHandler<Autodesk.Revit.DB.Events.FailuresProcessingEventArgs>? failureHandler = null;
            failureHandler = (sender, args) =>
            {
                var fa = args.GetFailuresAccessor();
                var failures = fa.GetFailureMessages();
                foreach (var f in failures)
                {
                    if (f.GetSeverity() == FailureSeverity.Warning)
                        fa.DeleteWarning(f);
                }
            };
            uiapp.Application.FailuresProcessing += failureHandler;

            EventHandler<DialogBoxShowingEventArgs>? dialogHandler = null;
            dialogHandler = (sender, args) =>
            {
                SmartConLogger.Info($"DialogBoxShowing: Id='{args.DialogId}'");

                if (args is TaskDialogShowingEventArgs taskArgs)
                {
                    var msg = taskArgs.Message ?? string.Empty;
                    var dlgId = taskArgs.DialogId ?? string.Empty;

                    bool isMissingLinks =
#if NETFRAMEWORK
                        dlgId.ToLowerInvariant().Contains("missinglink")
                        || dlgId.ToLowerInvariant().Contains("unresolved")
                        || msg.ToLowerInvariant().Contains("could not find");
#else
                        dlgId.Contains("MissingLink", StringComparison.OrdinalIgnoreCase)
                        || dlgId.Contains("Unresolved", StringComparison.OrdinalIgnoreCase)
                        || msg.Contains("could not find", StringComparison.OrdinalIgnoreCase);
#endif

                    if (isMissingLinks)
                    {
                        SmartConLogger.Info("Suppressing missing links dialog → Ignore (1002)");
                        taskArgs.OverrideResult(1002);
                        return;
                    }
                }

                SmartConLogger.Info("Suppressing unknown dialog → Cancel");
                try { args.OverrideResult((int)Autodesk.Revit.UI.TaskDialogResult.Cancel); }
                catch (Exception ex) { SmartConLogger.Warn($"OverrideResult failed: {ex.Message} [Action: диалог Revit останется показанным пользователю — продолжите вручную]"); }
            };
            uiapp.DialogBoxShowing += dialogHandler;

            Document? detachedDoc = null;
            Document? tempDoc = null;
            string? tempPath = null;

            try
            {
                if (isWorkshared)
                {
                    ReportProgress(LocalizationService.GetString("PM_Step_Sync"), 5);

                    if (settings.SyncBeforeShare)
                    {
                        try
                        {
                            SyncWithoutRelinquishing(originalDoc);
                        }
                        catch (Exception syncEx)
                        {
                            SmartConLogger.Warn($"Sync failed: {syncEx.Message} [Action: пользователю показан диалог — можно продолжить share без синхронизации или синхронизировать модель вручную]");

                            using var td = new Autodesk.Revit.UI.TaskDialog(LocalizationService.GetString("PM_Title_ShareDialog"));
                            td.MainInstruction = LocalizationService.Format("PM_Msg_SyncFailed", syncEx.Message);
                            td.MainContent = LocalizationService.GetString("PM_Msg_ContinueWithoutSync");
                            td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;

                            if (td.Show() != TaskDialogResult.Yes)
                            {
                                ReportProgress(LocalizationService.GetString("PM_Step_Cancelled"), 0);
                                CloseProgress();
                                return Result.Cancelled;
                            }
                        }
                    }

                    ReportProgress(LocalizationService.GetString("PM_Step_TempProject"), 15);

                    tempDoc = uiapp.Application.NewProjectDocument(UnitSystem.Metric);
                    tempPath = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        $"SmartCon_temp_{Guid.NewGuid():N}.rvt");
                    tempDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
                    uiapp.OpenAndActivateDocument(tempPath);

                    ReportProgress(LocalizationService.GetString("PM_Step_Detach"), 25);

                    var centralPath = originalDoc.GetWorksharingCentralModelPath();
                    originalDoc.Close(false);

                    var openOpts = new OpenOptions
                    {
                        DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets,
                        Audit = true,
                        AllowOpeningLocalByWrongUser = true
                    };

                    var uiDoc = uiapp.OpenAndActivateDocument(centralPath, openOpts, false);
                    detachedDoc = uiDoc.Document;

                    SmartConLogger.Info("Detached from central successfully.");
                }
                else
                {
                    ReportProgress(LocalizationService.GetString("PM_Step_TempProject"), 15);

                    // Save the original document BEFORE closing it. The share
                    // flow closes the user's document and reopens it from disk
                    // — without this save every unsaved user change would be
                    // silently discarded (and missing from the shared output
                    // too). The workshared branch does the equivalent via
                    // SynchronizeWithCentral with SaveLocalAfter = true.
                    // On failure we abort BEFORE closing anything, so the
                    // user keeps the open document with all changes.
                    try
                    {
                        originalDoc.Save();
                        SmartConLogger.Info("Saved original non-workshared document before share.");
                    }
                    catch (Exception saveEx)
                    {
                        SmartConLogger.Error($"Failed to save original document before share: {saveEx.Message}");
                        uiapp.Application.FailuresProcessing -= failureHandler;
                        uiapp.DialogBoxShowing -= dialogHandler;
                        ReportProgress(LocalizationService.GetString("PM_Step_Failed"), 0);
                        CloseProgress();
                        Autodesk.Revit.UI.TaskDialog.Show(LocalizationService.GetString("PM_Title_ShareDialog"),
                            LocalizationService.Format("PM_Msg_SaveFailed", saveEx.Message));
                        return Result.Failed;
                    }

                    tempDoc = uiapp.Application.NewProjectDocument(UnitSystem.Metric);
                    tempPath = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        $"SmartCon_temp_{Guid.NewGuid():N}.rvt");
                    tempDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
                    uiapp.OpenAndActivateDocument(tempPath);

                    ReportProgress(LocalizationService.GetString("PM_Step_Detach"), 25);

                    var sourcePath = originalDoc.PathName;
                    originalDoc.Close(false);

                    var uiDoc = uiapp.OpenAndActivateDocument(sourcePath);
                    detachedDoc = uiDoc.Document;

                    SmartConLogger.Info("Opened non-workshared file for processing.");
                }

                ReportProgress(LocalizationService.GetString("PM_Step_Purge"), 40);

                var purgeService = ServiceHost.GetService<IModelPurgeService>();
                var deletedCount = purgeService.Purge(detachedDoc, settings.PurgeOptions, settings.KeepViewNames);
                SmartConLogger.Info($"Purge completed. Deleted {deletedCount} elements.");

                ReportProgress(LocalizationService.GetString("PM_Step_Save"), 65);

                var modelPathOut = ModelPathUtils.ConvertUserVisiblePathToModelPath(sharedFilePath);
                var saveOpts = new SaveAsOptions { OverwriteExistingFile = true };

                if (detachedDoc.IsWorkshared)
                {
                    saveOpts.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                }

                detachedDoc.SaveAs(modelPathOut, saveOpts);
                SmartConLogger.Info($"Saved to: {sharedFilePath}");

                ReportProgress(LocalizationService.GetString("PM_Step_Finish"), 80);

                if (isWorkshared)
                {
                    var localModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(originalPathName);
                    var reopenOpts = new OpenOptions
                    {
                        DetachFromCentralOption = DetachFromCentralOption.DoNotDetach,
                        AllowOpeningLocalByWrongUser = true
                    };
                    uiapp.OpenAndActivateDocument(localModelPath, reopenOpts, false);
                }
                else
                {
                    uiapp.OpenAndActivateDocument(originalPathName);
                }

                SmartConLogger.Info("Reopened original local file.");

                detachedDoc.Close(false);
                detachedDoc = null;

                Document? tempDocFromDisk = null;
                foreach (Document d in uiapp.Application.Documents)
                {
                    if (d.PathName == tempPath)
                    {
                        tempDocFromDisk = d;
                        break;
                    }
                }
                if (tempDocFromDisk is not null)
                {
                    tempDocFromDisk.Close(false);
                }
                try
                {
                    if (tempDoc is not null && tempDoc.IsValidObject)
                        tempDoc.Close(false);
                }
                catch (Exception ex) { SmartConLogger.Debug($"tempDoc.Close after success failed (ignored): {ex.Message}"); }
                tempDoc = null;

                if (tempPath is not null && System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); }
                    catch (Exception ex) { SmartConLogger.Debug($"temp file delete failed (ignored): {ex.Message}"); }
                    tempPath = null;
                }

                if (isWorkshared)
                {
                    try
                    {
                        var reopenedDoc = uiapp.ActiveUIDocument.Document;
                        SyncWithoutRelinquishing(reopenedDoc);
                        SmartConLogger.Info("Post-reopen sync completed.");
                    }
                    catch (Exception postSyncEx)
                    {
                        SmartConLogger.Warn($"Post-reopen sync failed: {postSyncEx.Message} [Action: синхронизируйте модель с центральным файлом вручную — share-файл уже создан]");
                    }
                }

                ReportProgress(LocalizationService.GetString("PM_Step_Done"), 100);
                CloseProgress();

                uiapp.Application.FailuresProcessing -= failureHandler;
                uiapp.DialogBoxShowing -= dialogHandler;

                var elapsedSec = measure.GetElapsedMilliseconds() / 1000.0;

                SmartConLogger.Info($"Share succeeded: {sharedFilePath} ({elapsedSec:F1}s, {deletedCount} deleted)");

                var resultVm = new ShareResultViewModel(sharedFilePath, deletedCount, elapsedSec);
                var presenter = ServiceHost.GetService<IDialogPresenter>();
                presenter.ShowDialog(resultVm);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                SmartConLogger.Error($"Share algorithm failed: {ex.Message}");

                try
                {
                    if (detachedDoc is not null && detachedDoc.IsValidObject)
                        detachedDoc.Close(false);
                }
                catch (Exception closeEx) { SmartConLogger.Debug($"detachedDoc.Close in error path failed (ignored): {closeEx.Message}"); }

                try
                {
                    if (tempDoc is not null && tempDoc.IsValidObject)
                        tempDoc.Close(false);
                }
                catch (Exception closeEx) { SmartConLogger.Debug($"tempDoc.Close in error path failed (ignored): {closeEx.Message}"); }

                if (tempPath is not null && System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); }
                    catch (Exception delEx) { SmartConLogger.Debug($"temp file delete in error path failed (ignored): {delEx.Message}"); }
                }

                try
                {
                    if (System.IO.File.Exists(originalPathName))
                    {
                        if (isWorkshared)
                        {
                            var localModelPath = ModelPathUtils.ConvertUserVisiblePathToModelPath(originalPathName);
                            var reopenOpts = new OpenOptions { AllowOpeningLocalByWrongUser = true };
                            uiapp.OpenAndActivateDocument(localModelPath, reopenOpts, false);
                        }
                        else
                        {
                            uiapp.OpenAndActivateDocument(originalPathName);
                        }
                    }
                }
                catch (Exception reopenEx)
                {
                    SmartConLogger.Error($"Failed to reopen original file: {reopenEx.Message}");
                }

                ReportProgress(LocalizationService.GetString("PM_Step_Failed"), 0);
                CloseProgress();

                uiapp.Application.FailuresProcessing -= failureHandler;
                uiapp.DialogBoxShowing -= dialogHandler;

                Autodesk.Revit.UI.TaskDialog.Show(
                    LocalizationService.GetString("PM_Title_ShareDialog"),
                    LocalizationService.Format("PM_Msg_ExportFailed", ex.Message));
                return Result.Failed;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"ShareProjectCommand exception: {ex}");
            message = ex.Message;
            return Result.Failed;
        }
    }

    private static void SyncWithoutRelinquishing(Document doc)
    {
        var transOpts = new TransactWithCentralOptions();
        transOpts.SetLockCallback(new CentralLockCallback());

        var syncOpts = new SynchronizeWithCentralOptions();
        syncOpts.SetRelinquishOptions(new RelinquishOptions(false));
        syncOpts.SaveLocalAfter = true;
        syncOpts.Comment = "SmartCon - ShareProject";

        doc.SynchronizeWithCentral(transOpts, syncOpts);
    }

    private void ShowProgress(IntPtr ownerHandle)
    {
        _progressVm = new ShareProgressViewModel();
        _progressView = new ShareProgressView(_progressVm);
        new WindowInteropHelper(_progressView).Owner = ownerHandle;
        _progressView.Show();
    }

    private void ReportProgress(string statusText, int progressValue)
    {
        if (_progressVm is null) return;

        _progressView?.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
        {
            _progressVm.StatusText = statusText;
            _progressVm.ProgressValue = progressValue;
        }));
    }

    private void CloseProgress()
    {
        if (_progressView is null) return;

        _progressView.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
        {
            _progressView.Close();
            _progressView = null;
            _progressVm = null;
        }));
    }

    private sealed class CentralLockCallback : ICentralLockedCallback
    {
        public bool ShouldWaitForLockAvailability()
        {
            return true;
        }
    }

    private static System.Windows.Window? GetMainWindow(UIApplication uiapp)
    {
        var handle = uiapp.MainWindowHandle;
        return System.Windows.Application.Current?.Windows.OfType<System.Windows.Window>()
            .FirstOrDefault(w => new System.Windows.Interop.WindowInteropHelper(w).Handle == handle);
    }
}

