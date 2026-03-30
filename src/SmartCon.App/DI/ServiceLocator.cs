using Autodesk.Revit.UI;
using Microsoft.Extensions.DependencyInjection;
using SmartCon.Core.Services;

namespace SmartCon.App.DI;

/// <summary>
/// IoC-контейнер приложения (MEDI).
/// Инициализируется один раз при OnStartup, Dispose при OnShutdown.
/// Публикует резолвер в ServiceHost (Core) для доступа из команд.
/// </summary>
public static class ServiceLocator
{
    private static ServiceProvider? _serviceProvider;

    /// <summary>
    /// Создаёт и конфигурирует DI-контейнер.
    /// </summary>
    public static void Initialize(UIControlledApplication app)
    {
        var services = new ServiceCollection();
        ServiceRegistrar.Register(services, app);
        _serviceProvider = services.BuildServiceProvider();

        ServiceHost.Initialize(type => _serviceProvider.GetRequiredService(type));
    }

    public static void Dispose()
    {
        ServiceHost.Reset();
        _serviceProvider?.Dispose();
        _serviceProvider = null;
    }
}
