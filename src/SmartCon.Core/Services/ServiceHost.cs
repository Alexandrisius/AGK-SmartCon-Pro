namespace SmartCon.Core.Services;

/// <summary>
/// Глобальная точка доступа к DI-контейнеру.
/// Инициализируется в SmartCon.App при OnStartup.
/// Используется командами (IExternalCommand) для получения сервисов.
/// </summary>
public static class ServiceHost
{
    private static Func<Type, object>? _resolver;

    /// <summary>
    /// Инициализация резолвера. Вызывается один раз из App.OnStartup().
    /// </summary>
    public static void Initialize(Func<Type, object> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// Разрешить сервис по типу.
    /// </summary>
    public static T GetService<T>() where T : notnull
    {
        if (_resolver is null)
        {
            throw new InvalidOperationException(
                "ServiceHost не инициализирован. Вызовите Initialize() из App.OnStartup().");
        }

        return (T)_resolver(typeof(T));
    }

    public static void Reset()
    {
        _resolver = null;
    }
}
