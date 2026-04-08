using System.IO;
using System.Reflection;
using Autodesk.Revit.UI;
using SmartCon.App.DI;
using SmartCon.App.Ribbon;

namespace SmartCon.App;

public sealed class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
        try
        {
            ServiceLocator.Initialize(application);
            RibbonBuilder.CreateRibbon(application);

            return Result.Succeeded;
        }
        catch (Exception ex)
        {
            TaskDialog.Show("SmartCon — Ошибка", $"Не удалось загрузить SmartCon:\n{ex.Message}");
            return Result.Failed;
        }
    }

    public Result OnShutdown(UIControlledApplication application)
    {
        ServiceLocator.Dispose();
        return Result.Succeeded;
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
