using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Common;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.FamilyManager.Events;
using SmartCon.FamilyManager.Selectors;
using SmartCon.FamilyManager.Services;
using SmartCon.FamilyManager.Services.LocalCatalog;
using SmartCon.FamilyManager.Services.Stale;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    private async Task InitializeAsync()
    {
        DumpLoadedAssembliesBeforeTruncate();
        SmartConLogger.TruncateMainLog();
        _sessionStart = DateTime.Now;

        await _databaseManager.InitializeAsync().ConfigureAwait(true);
        RecomputeActiveBaseMatch();
        RefreshConnections();
        if (!HasActiveDatabase)
        {
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StatusNoDatabase) ?? "No database connected";
            return;
        }
        // The ExternalEvent round-trip wires up the Revit context; the
        // update-state check below depends on DetectRevitVersion, so it
        // must run after it, not fire-and-forget in parallel.
        await RefreshTreeViaExternalEventAsync().ConfigureAwait(true);
        // Issue #126: detect stale (v1) content hashes — shows the red
        // badge + "Update database" command; never pops a dialog.
        await RefreshDatabaseUpdateStateAsync().ConfigureAwait(true);
    }

    private static void DumpLoadedAssembliesBeforeTruncate()
    {
        try
        {
            var appDir = Path.GetDirectoryName(typeof(FamilyManagerMainViewModel).Assembly.Location);
            var asmLogPath = Path.Combine(appDir ?? ".", "assembly-load.log");
            var sb = new StringBuilder();
            sb.AppendLine("[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "] === PRE-TRUNCATE DUMP: all currently-loaded HelixToolkit/SharpDX/Assimp assemblies ===");
            foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            {
                var n = a.GetName().Name ?? "";
                if (n.Contains("HelixToolkit") || n.Contains("SharpDX") || n.Contains("Assimp") ||
                    n.Contains("SmartCon"))
                    sb.AppendLine($"  {n} v{a.GetName().Version} from={a.Location}");
            }
            sb.AppendLine(new string('=', 80));
            File.AppendAllText(asmLogPath, sb.ToString());
        }
        catch { }
    }

    private static void FireAndForget(Task task, string operationName)
    {
        Guard.ThrowIfNull(task);
        _ = task.ContinueWith(
            t => SmartConLogger.Error($"FamilyManager '{operationName}' failed: {t.Exception?.GetBaseException()}"),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Fire-and-forget helper for inline async lambdas. Schedules the factory on the
    /// thread pool so the calling Revit UI thread is never blocked, and routes any
    /// exception through the operation-scoped log without leaking as
    /// <c>AppDomain.UnhandledException</c>. Prefer
    /// <see cref="FireAndForget(Task, string)"/> when the task is already constructed.
    /// </summary>
    private static void FireAndForget(Func<Task> taskFactory, string operationName)
    {
        Guard.ThrowIfNull(taskFactory);
        _ = Task.Run(async () =>
        {
            try
            {
                await taskFactory().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SmartConLogger.Error($"FireAndForget '{operationName}' failed: {ex.GetBaseException()}");
            }
        });
    }

    private async Task RefreshAccessAndLoadTreeAsync()
    {
        DetectRevitVersion();
        SmartConLogger.LogSessionStart($"FamilyManager (Revit {CurrentRevitVersion})");

        if (!HasActiveDatabase)
        {
            _compatibility.Reset();
            IsDatabaseNewerThanPlugin = false;
            IsEditorRole = false;
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StatusNoDatabase) ?? "No database connected";
            TreeNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            CanImport = false;
            CanEdit = false;
            CanManageUsers = false;
            return;
        }

        // ADR-058 (#173): refresh the compat gate BEFORE the role resolution —
        // ApplyWriteAccess ANDs IsDatabaseNewerThanPlugin into write access.
        await _compatibility.RefreshAsync();
        IsDatabaseNewerThanPlugin = _compatibility.IsDatabaseNewerThanPlugin;

        _accessControl.InvalidateCache();

        try
        {
            await _accessControl.RefreshCurrentUserAsync();
            UpdateAccessProperties();
        }
        catch (DbAccessDeniedException ex)
        {
            CanImport = false;
            CanEdit = false;
            CanManageUsers = false;
            IsEditorRole = false;
            _dialogService.ShowError(
                LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied",
                string.Format(LanguageManager.GetString(StringLocalization.Keys.FM_AccessDeniedMessage) ?? "The owner of \"{0}\" has restricted your access.", ex.DbName));
            TreeNodes = new ObservableCollection<CatalogTreeNodeViewModel>();
            StatusMessage = LanguageManager.GetString(StringLocalization.Keys.FM_AccessDenied) ?? "Access Denied";
            return;
        }

        await LoadTreeAsync();
    }

    private void UpdateAccessProperties()
    {
        // Write capabilities come pre-gated from the service: CanImport/
        // CanEdit/CanManageUsers already AND the plugin-compat gate (ADR-058,
        // central enforcement point in DbAccessControlService).
        CanImport = _accessControl.CanImport;
        CanEdit = _accessControl.CanEdit;
        CanManageUsers = _accessControl.CanManageUsers;
        IsEditorRole = _accessControl.IsEditorRole;
    }
}
