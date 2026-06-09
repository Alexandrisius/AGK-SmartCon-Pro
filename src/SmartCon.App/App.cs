using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using SmartCon.App.DI;
using SmartCon.App.Ribbon;
using SmartCon.Core.Logging;
using SmartCon.Core.Services;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Core.Threading;
using SmartCon.FamilyManager;
using SmartCon.UI;

namespace SmartCon.App;

/// <summary>
/// SmartCon Revit plugin entry point. Registers the Ribbon panel, DI container,
/// and handles self-update on startup.
/// </summary>
public sealed class App : IExternalApplication
{
    private static readonly string s_smartConDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "SmartCon");

    public Result OnStartup(UIControlledApplication application)
    {
#if NETFRAMEWORK
        System.Net.ServicePointManager.SecurityProtocol |=
            System.Net.SecurityProtocolType.Tls12 |
            System.Net.SecurityProtocolType.Tls13;
#endif
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        try
        {
            ApplyUpdaterSelfUpdate();
            CleanupStalePendingUpdate();
            ServiceLocator.Initialize(application);
            LanguageManager.Initialize();

            // Sweep stale temp folders left over from a previous Revit session
            // (e.g. Revit crashed mid-import). Runs once at startup; log
            // lines use the same [ActiveCleanup] prefix as the regular
            // per-import cleanup so the user can trace both in the log.
            try
            {
                var cleanupService = ServiceHost.GetService<IActiveImportCleanupService>();
                AsyncBridge.RunSync(() => cleanupService.CleanupAfterImportAsync());
            }
            catch (Exception ex)
            {
                SmartConLogger.Warn($"App.OnStartup.StartupTempSweep: failed: {ex.GetType().Name}: {ex.Message}");
            }

            var fmProvider = ServiceHost.GetService<FamilyManagerPaneProvider>();
            var fmPaneId = FamilyManagerPaneIds.FamilyManagerPane;
            application.RegisterDockablePane(fmPaneId, "Family Manager", fmProvider);

            RibbonBuilder.CreateRibbon(application);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("SmartCon - Error", $"Failed to load SmartCon:\n{ex.Message}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        TryLaunchUpdater();
        // Use the same cleanup service as per-import cleanup so that
        // BOTH staging roots (FMLoad and SystemFamilyLoadFromProject)
        // are removed on shutdown. The previous inline implementation
        // only handled FMLoad, leaking SystemFamilyLoadFromProject/*.
        try
        {
            var cleanupService = ServiceHost.GetService<IActiveImportCleanupService>();
            AsyncBridge.RunSync(() => cleanupService.CleanupAfterImportAsync());
        }
        catch (Exception ex)
        {
            SmartConLogger.Warn($"App.OnShutdown.TempCleanup: failed: {ex.GetType().Name}: {ex.Message}");
        }
        ServiceLocator.Dispose();
        return Result.Succeeded;
    }

    private static void ApplyUpdaterSelfUpdate()
    {
        try
        {
            var pendingDir = Path.Combine(s_smartConDir, "updater-pending");
            if (!Directory.Exists(pendingDir)) return;

            foreach (var file in Directory.GetFiles(pendingDir))
            {
                var dest = Path.Combine(s_smartConDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }

            Directory.Delete(pendingDir, true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"App.ApplyUpdaterSelfUpdate: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CleanupStalePendingUpdate()
    {
        try
        {
            var markerPath = Path.Combine(s_smartConDir, "update-pending.json");
            if (!File.Exists(markerPath)) return;

            var stagingDir = Path.Combine(s_smartConDir, "staging", "extracted");
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, true); } catch { /* Intentional: cleanup */ }
            }

            var stagingRoot = Path.Combine(s_smartConDir, "staging");
            if (Directory.Exists(stagingRoot))
            {
                try { Directory.Delete(stagingRoot, true); } catch { /* Intentional: cleanup */ }
            }

            File.Delete(markerPath);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"App.CleanupStalePendingUpdate: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryLaunchUpdater()
    {
        try
        {
            var markerPath = Path.Combine(s_smartConDir, "update-pending.json");
            if (!File.Exists(markerPath)) return;

            var updaterPath = Path.Combine(s_smartConDir, "SmartCon.Updater.exe");
            if (!File.Exists(updaterPath))
            {
                var appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                if (appDir is not null)
                {
                    var fallbackPath = Path.Combine(appDir, "SmartCon.Updater.exe");
                    if (File.Exists(fallbackPath))
                        updaterPath = fallbackPath;
                    else return;
                }
                else return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                UseShellExecute = false,
                CreateNoWindow = false
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"App.TryLaunchUpdater: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        var name = new AssemblyName(args.Name).Name;
        var loaded = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == name);
        if (loaded != null) return loaded;
        var pluginDir = Path.GetDirectoryName(typeof(App).Assembly.Location);
        if (pluginDir is null) return null;
        var path = Path.Combine(pluginDir, name + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }
}
