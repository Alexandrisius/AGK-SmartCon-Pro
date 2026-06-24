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
            CleanupLegacyStageFolder();
            ServiceLocator.Initialize(application);
            LanguageManager.Initialize();

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

    /// <summary>
    /// One-shot cleanup of the legacy <c>files/_stage/</c> folder used by
    /// SmartCon &lt; v2.0.0 for transient family staging. The folder has been
    /// removed from the runtime flow (see ADR-035) but may still exist on
    /// disk for users upgrading from a prior version.
    /// </summary>
    private static void CleanupLegacyStageFolder()
    {
        try
        {
            var fmDir = Path.Combine(s_smartConDir, "FamilyManager");
            if (!Directory.Exists(fmDir)) return;

            foreach (var catalogDir in Directory.GetDirectories(fmDir))
            {
                var stageDir = Path.Combine(catalogDir, "files", "_stage");
                if (!Directory.Exists(stageDir)) continue;

                try
                {
                    Directory.Delete(stageDir, recursive: true);
                    SmartConLogger.Info($"Removed legacy staging folder: {stageDir}");
                }
                catch (Exception ex)
                {
                    SmartConLogger.Debug(
                        $"App.CleanupLegacyStageFolder: failed to remove '{stageDir}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"App.CleanupLegacyStageFolder: {ex.GetType().Name}: {ex.Message}");
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
