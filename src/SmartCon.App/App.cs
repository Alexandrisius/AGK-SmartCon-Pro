using System.Diagnostics;
using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using SmartCon.App.DI;
using SmartCon.App.Ribbon;
using SmartCon.Core.Logging;
using SmartCon.PipeConnect.Services;

namespace SmartCon.App;

/// <summary>
/// SmartCon Revit plugin entry point. Registers the Ribbon panel, DI container,
/// and handles self-update on startup.
/// </summary>
public sealed class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        try
        {
            ApplyUpdaterSelfUpdate();
            CleanupStalePendingUpdate();
            ServiceLocator.Initialize(application);
            RibbonBuilder.CreateRibbon(application);
            LanguageManager.Initialize();

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
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var pendingDir = Path.Combine(appData, "SmartCon", "updater-pending");
            if (!Directory.Exists(pendingDir)) return;

            var appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (appDir is null) return;

            foreach (var file in Directory.GetFiles(pendingDir))
            {
                var dest = Path.Combine(appDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }

            Directory.Delete(pendingDir, true);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"[App.ApplyUpdaterSelfUpdate] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void CleanupStalePendingUpdate()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var markerPath = Path.Combine(appData, "SmartCon", "update-pending.json");
            if (!File.Exists(markerPath)) return;

            var stagingDir = Path.Combine(appData, "SmartCon", "staging", "extracted");
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, true); } catch { /* Intentional: cleanup */ }
            }

            var stagingRoot = Path.Combine(appData, "SmartCon", "staging");
            if (Directory.Exists(stagingRoot))
            {
                try { Directory.Delete(stagingRoot, true); } catch { /* Intentional: cleanup */ }
            }

            File.Delete(markerPath);
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"[App.CleanupStalePendingUpdate] {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void TryLaunchUpdater()
    {
        try
        {
            var appDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (appDir is null) return;

            var markerPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SmartCon", "update-pending.json");

            if (!File.Exists(markerPath)) return;

            var updaterPath = Path.Combine(appDir, "SmartCon.Updater.exe");
            if (!File.Exists(updaterPath)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = updaterPath,
                UseShellExecute = false,
                CreateNoWindow = false
            });
        }
        catch (Exception ex)
        {
            SmartConLogger.Debug($"[App.TryLaunchUpdater] {ex.GetType().Name}: {ex.Message}");
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
