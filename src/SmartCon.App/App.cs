using Autodesk.Revit.UI;
using SmartCon.App.DI;
using SmartCon.App.Ribbon;

namespace SmartCon.App;

/// <summary>
/// Точка входа плагина SmartCon. Реализует IExternalApplication.
/// Регистрирует DI-контейнер и создаёт Ribbon UI при запуске Revit.
/// </summary>
public sealed class App : IExternalApplication
{
    public Result OnStartup(UIControlledApplication application)
    {
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
}
