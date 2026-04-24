using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using SmartCon.Core.Logging;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.ProjectManagement.ViewModels;
using SmartCon.ProjectManagement.Views;

namespace SmartCon.ProjectManagement.Commands;

[Transaction(TransactionMode.Manual)]
public sealed class ShareProjectCommand : IExternalCommand
{
    private ShareProgressViewModel? _progressVm;
    private ShareProgressView? _progressView;

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            SmartConLogger.Info("[PM] ShareProjectCommand started.");

            var uiapp = commandData.Application;
            CommandHelper.InitializeContext(uiapp);
            var originalDoc = CommandHelper.GetDocument();

            if (string.IsNullOrWhiteSpace(originalDoc.PathName))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Share Project", "File must be saved first.");
                return Result.Failed;
            }

            var settingsRepo = ServiceHost.GetService<IShareProjectSettingsRepository>();
            var settings = settingsRepo.Load(originalDoc);

            SmartConLogger.Info($"[PM] Settings loaded. ShareFolder='{settings.ShareFolderPath}', Blocks={settings.FileNameTemplate.Blocks.Count}, ExportMappings={settings.FileNameTemplate.ExportMappings.Count}");

            if (string.IsNullOrWhiteSpace(settings.ShareFolderPath) || settings.FileNameTemplate.Blocks.Count == 0)
            {
                SmartConLogger.Warn("[PM] Settings incomplete — showing configure dialog.");
                Autodesk.Revit.UI.TaskDialog.Show("Share Project",
                    LocalizationService.GetString("PM_Result_NoSettings"));
                return Result.Failed;
            }

            var parser = ServiceHost.GetService<IFileNameParser>();
            var validation = parser.ValidateDetailed(originalDoc.Title, settings.FileNameTemplate, settings.FieldLibrary);

            string sharedFileName;

            if (!validation.IsValid)
            {
                SmartConLogger.Warn($"[PM] Validation failed for '{originalDoc.Title}': {validation.Summary}");

                var existingOverride = settingsRepo.LoadExportNameOverride(originalDoc);
                if (existingOverride is not null)
                {
                    SmartConLogger.Info("[PM] Using saved ExportNameOverride.");
                    var values = existingOverride.FieldValues;
                    var orderedValues = settings.FileNameTemplate.Blocks
                        .OrderBy(b => b.Index)
                        .Select(b => values.TryGetValue(b.Field, out var v) ? v : string.Empty);
                    var ext = System.IO.Path.GetExtension(originalDoc.PathName);
                    sharedFileName = string.Join("-", orderedValues);
                    if (!string.IsNullOrEmpty(ext) && !System.IO.Path.HasExtension(sharedFileName))
                        sharedFileName += ext;
                }
                else
                {
                    var details = string.Join("\n", validation.Blocks
                        .Where(b => !b.IsValid)
                        .Select(b => $"  \u2022 {b.Field}: '{b.Value}' \u2014 {b.Error}"));

                    var dialogVm = new ViewModels.ExportNameDialogViewModel(
                        originalDoc.Title,
                        $"{LocalizationService.GetString("PM_Result_InvalidName")}\n\n{validation.Summary}\n{details}",
                        settings.FileNameTemplate.Blocks,
                        settings.FieldLibrary,
                        settings.FileNameTemplate.ExportMappings);

                    var dialogView = new Views.ExportNameDialog(dialogVm)
                    {
                        Owner = GetMainWindow(commandData.Application)
                    };
                    dialogView.ShowDialog();

                    if (dialogView.CustomDialogResult != true)
                    {
                        SmartConLogger.Info("[PM] User cancelled ExportNameDialog.");
                        return Result.Cancelled;
                    }

                    var fieldValues = dialogVm.GetFieldValues();
                    settingsRepo.SaveExportNameOverride(originalDoc, new Core.Models.ExportNameOverride { FieldValues = fieldValues });

                    var ext = System.IO.Path.GetExtension(originalDoc.PathName);
                    sharedFileName = dialogVm.PreviewFileName;
                    if (!string.IsNullOrEmpty(ext) && !System.IO.Path.HasExtension(sharedFileName))
                        sharedFileName += ext;

                    SmartConLogger.Info($"[PM] ExportNameOverride saved. Custom name: {sharedFileName}");
                }
            }
            else
            {
                SmartConLogger.Info($"[PM] Validation passed for '{originalDoc.Title}'.");
                sharedFileName = parser.TransformForExport(originalDoc.Title, settings.FileNameTemplate, settings.FieldLibrary) ?? string.Empty;
                var extension = System.IO.Path.GetExtension(originalDoc.PathName);
                if (!string.IsNullOrEmpty(extension) && !System.IO.Path.HasExtension(sharedFileName))
                    sharedFileName += extension;
            }

            if (string.IsNullOrWhiteSpace(sharedFileName))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Share Project", "Failed to transform file name.");
                return Result.Failed;
            }

            var sharedFilePath = System.IO.Path.Combine(settings.ShareFolderPath, sharedFileName);

            if (!System.IO.Directory.Exists(settings.ShareFolderPath))
                System.IO.Directory.CreateDirectory(settings.ShareFolderPath);

            var isWorkshared = originalDoc.IsWorkshared;
            var originalPathName = originalDoc.PathName;

            ShowProgress(commandData.Application.MainWindowHandle);

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
                SmartConLogger.Info($"[PM] DialogBoxShowing: Id='{args.DialogId}'");

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
                        SmartConLogger.Info("[PM] Suppressing missing links dialog → Ignore (1002)");
                        taskArgs.OverrideResult(1002);
                        return;
                    }
                }

                SmartConLogger.Info("[PM] Suppressing unknown dialog → Cancel");
                try { args.OverrideResult((int)Autodesk.Revit.UI.TaskDialogResult.Cancel); }
                catch (Exception ex) { SmartConLogger.Warn($"[PM] OverrideResult failed: {ex.Message}"); }
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
                            SmartConLogger.Warn($"[PM] Sync failed: {syncEx.Message}");

                            using var td = new Autodesk.Revit.UI.TaskDialog("Share Project");
                            td.MainInstruction = $"Synchronization failed:\n{syncEx.Message}";
                            td.MainContent = "Continue without synchronization?";
                            td.CommonButtons = TaskDialogCommonButtons.Yes | TaskDialogCommonButtons.No;

                            if (td.Show() != TaskDialogResult.Yes)
                            {
                                ReportProgress("Cancelled", 0);
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

                    SmartConLogger.Info("[PM] Detached from central successfully.");
                }
                else
                {
                    ReportProgress(LocalizationService.GetString("PM_Step_TempProject"), 15);

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

                    SmartConLogger.Info("[PM] Opened non-workshared file for processing.");
                }

                ReportProgress(LocalizationService.GetString("PM_Step_Purge"), 40);

                var purgeService = ServiceHost.GetService<IModelPurgeService>();
                var deletedCount = purgeService.Purge(detachedDoc, settings.PurgeOptions, settings.KeepViewNames);
                SmartConLogger.Info($"[PM] Purge completed. Deleted {deletedCount} elements.");

                ReportProgress(LocalizationService.GetString("PM_Step_Save"), 65);

                var modelPathOut = ModelPathUtils.ConvertUserVisiblePathToModelPath(sharedFilePath);
                var saveOpts = new SaveAsOptions { OverwriteExistingFile = true };

                if (detachedDoc.IsWorkshared)
                {
                    saveOpts.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                }

                detachedDoc.SaveAs(modelPathOut, saveOpts);
                SmartConLogger.Info($"[PM] Saved to: {sharedFilePath}");

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

                SmartConLogger.Info("[PM] Reopened original local file.");

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
                catch { }
                tempDoc = null;

                if (tempPath is not null && System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); } catch { }
                    tempPath = null;
                }

                if (isWorkshared)
                {
                    try
                    {
                        var reopenedDoc = uiapp.ActiveUIDocument.Document;
                        SyncWithoutRelinquishing(reopenedDoc);
                        SmartConLogger.Info("[PM] Post-reopen sync completed.");
                    }
                    catch (Exception postSyncEx)
                    {
                        SmartConLogger.Warn($"[PM] Post-reopen sync failed: {postSyncEx.Message}");
                    }
                }

                ReportProgress("Done", 100);
                CloseProgress();

                uiapp.Application.FailuresProcessing -= failureHandler;
                uiapp.DialogBoxShowing -= dialogHandler;

                sw.Stop();

                var successMsg =
                    $"Project shared successfully.\n\n" +
                    $"Path: {sharedFilePath}\n" +
                    $"Elements deleted: {deletedCount}\n" +
                    $"Time: {sw.Elapsed.TotalSeconds:F1}s";

                SmartConLogger.Info($"[PM] Share succeeded: {sharedFilePath} ({sw.Elapsed.TotalSeconds:F1}s, {deletedCount} deleted)");
                Autodesk.Revit.UI.TaskDialog.Show("Share Project", successMsg);
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                SmartConLogger.Error($"[PM] Share algorithm failed: {ex.Message}");

                try
                {
                    if (detachedDoc is not null && detachedDoc.IsValidObject)
                        detachedDoc.Close(false);
                }
                catch { }

                try
                {
                    if (tempDoc is not null && tempDoc.IsValidObject)
                        tempDoc.Close(false);
                }
                catch { }

                if (tempPath is not null && System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); } catch { }
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
                    SmartConLogger.Error($"[PM] Failed to reopen original file: {reopenEx.Message}");
                }

                ReportProgress("Failed", 0);
                CloseProgress();

                uiapp.Application.FailuresProcessing -= failureHandler;
                uiapp.DialogBoxShowing -= dialogHandler;

                Autodesk.Revit.UI.TaskDialog.Show("Share Project", $"Export failed:\n{ex.Message}");
                return Result.Failed;
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Error($"[PM] ShareProjectCommand exception: {ex}");
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
