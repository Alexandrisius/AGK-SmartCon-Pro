using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
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
            var contextWriter = ServiceHost.GetService<IRevitContextWriter>();
            contextWriter.SetContext(uiapp);

            var revitContext = (IRevitContext)contextWriter;
            var originalDoc = revitContext.GetDocument();

            // Шаг 1.3: Проверить что файл сохранён
            if (string.IsNullOrWhiteSpace(originalDoc.PathName))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Share Project", "File must be saved first.");
                return Result.Failed;
            }

            // Шаг 1.1: Загрузить настройки
            var settingsRepo = ServiceHost.GetService<IShareProjectSettingsRepository>();
            var settings = settingsRepo.Load(originalDoc);

            SmartConLogger.Info($"[PM] Settings loaded. ShareFolder='{settings.ShareFolderPath}', Blocks={settings.FileNameTemplate.Blocks.Count}, Mappings={settings.FileNameTemplate.StatusMappings.Count}");

            // Шаг 1.2: Если настройки пустые
            if (string.IsNullOrWhiteSpace(settings.ShareFolderPath) || settings.FileNameTemplate.Blocks.Count == 0)
            {
                SmartConLogger.Warn("[PM] Settings incomplete — showing configure dialog.");
                Autodesk.Revit.UI.TaskDialog.Show("Share Project",
                    LocalizationService.GetString("PM_Result_NoSettings"));
                return Result.Failed;
            }

            // Шаг 1.4: Парсинг имени файла
            var parser = ServiceHost.GetService<IFileNameParser>();
            var (isValid, error) = parser.Validate(originalDoc.Title, settings.FileNameTemplate);
            if (!isValid)
            {
                SmartConLogger.Warn($"[PM] Validation failed for '{originalDoc.Title}': {error}");
                Autodesk.Revit.UI.TaskDialog.Show("Share Project", error);
                return Result.Failed;
            }

            SmartConLogger.Info($"[PM] Validation passed for '{originalDoc.Title}'.");

            // Шаг 6: Вычисление пути Shared
            var sharedFileName = parser.TransformStatus(originalDoc.Title, settings.FileNameTemplate);
            var extension = System.IO.Path.GetExtension(originalDoc.PathName);
            if (!string.IsNullOrEmpty(extension) && !System.IO.Path.HasExtension(sharedFileName))
                sharedFileName += extension;

            if (string.IsNullOrWhiteSpace(sharedFileName))
            {
                Autodesk.Revit.UI.TaskDialog.Show("Share Project", "Failed to transform file name.");
                return Result.Failed;
            }

            var sharedFilePath = System.IO.Path.Combine(settings.ShareFolderPath, sharedFileName);

            // Шаг 6.3: Создать папку если нет
            if (!System.IO.Directory.Exists(settings.ShareFolderPath))
                System.IO.Directory.CreateDirectory(settings.ShareFolderPath);

            var isWorkshared = originalDoc.IsWorkshared;
            var originalPathName = originalDoc.PathName;

            ShowProgress(commandData.Application.MainWindowHandle);

            Document? detachedDoc = null;
            Document? tempDoc = null;
            string? tempPath = null;

            try
            {
                if (isWorkshared)
                {
                    // Шаг 2: Синхронизация локального файла
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

                    // Шаг 3: Создание временного проекта
                    ReportProgress(LocalizationService.GetString("PM_Step_TempProject"), 15);

                    tempDoc = uiapp.Application.NewProjectDocument(UnitSystem.Metric);
                    tempPath = System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        $"SmartCon_temp_{Guid.NewGuid():N}.rvt");
                    tempDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
                    uiapp.OpenAndActivateDocument(tempPath);

                    // Шаг 4: Detach from central
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
                    // Не-workshared: упрощённый путь без detach
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

                // Шаг 5: Очистка модели
                ReportProgress(LocalizationService.GetString("PM_Step_Purge"), 40);

                var purgeService = ServiceHost.GetService<IModelPurgeService>();
                var deletedCount = purgeService.Purge(detachedDoc, settings.PurgeOptions, settings.KeepViewNames);
                SmartConLogger.Info($"[PM] Purge completed. Deleted {deletedCount} elements.");

                // Шаг 7: Сохранение в Shared
                ReportProgress(LocalizationService.GetString("PM_Step_Save"), 65);

                var modelPathOut = ModelPathUtils.ConvertUserVisiblePathToModelPath(sharedFilePath);
                var saveOpts = new SaveAsOptions { OverwriteExistingFile = true };

                if (detachedDoc.IsWorkshared)
                {
                    saveOpts.SetWorksharingOptions(new WorksharingSaveAsOptions { SaveAsCentral = true });
                }

                detachedDoc.SaveAs(modelPathOut, saveOpts);
                SmartConLogger.Info($"[PM] Saved to: {sharedFilePath}");

                // Шаг 8: Завершение
                ReportProgress(LocalizationService.GetString("PM_Step_Finish"), 80);

                // 8.1: Сначала открыть локальный файл (переключить активный документ)
                //      Нельзя закрывать активный документ — сначала переключаемся
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

                // 8.2: Теперь закрыть detach-копию (больше не активна)
                detachedDoc.Close(false);
                detachedDoc = null;

                // 8.3: Закрыть временный проект из UI (не in-memory tempDoc!)
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
                // Также закрыть in-memory tempDoc если ещё жив
                try
                {
                    if (tempDoc is not null && tempDoc.IsValidObject)
                        tempDoc.Close(false);
                }
                catch { }
                tempDoc = null;

                // 8.4: Удалить временный файл
                if (tempPath is not null && System.IO.File.Exists(tempPath))
                {
                    try { System.IO.File.Delete(tempPath); } catch { }
                    tempPath = null;
                }

                // 8.5: SyncWithoutRelinquishing (локальный файл)
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

                sw.Stop();

                // 8.6: Показать TaskDialog с результатом
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

                // Откат: закрыть всё что открыто
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

                // Попытка переоткрыть оригинальный файл
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

                Autodesk.Revit.UI.TaskDialog.Show("Share Project", $"Share failed:\n{ex.Message}");
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

    /// <summary>
    /// SyncWithoutRelinquishing — как в документации share-algorithm.md.
    /// ICentralLockedCallback.ShouldWaitForLockAvailability возвращает true.
    /// </summary>
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

        System.Windows.Application.Current?.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
        {
            _progressVm.StatusText = statusText;
            _progressVm.ProgressValue = progressValue;
        }));
    }

    private void CloseProgress()
    {
        if (_progressView is null) return;

        System.Windows.Application.Current?.Dispatcher.Invoke(DispatcherPriority.Background, new Action(() =>
        {
            _progressView.Close();
            _progressView = null;
            _progressVm = null;
        }));
    }

    /// <summary>
    /// Implementation of ICentralLockedCallback — returns true to wait for lock availability.
    /// </summary>
    private sealed class CentralLockCallback : ICentralLockedCallback
    {
        public bool ShouldWaitForLockAvailability()
        {
            return true;
        }
    }
}
