namespace SmartCon.Core.Services;

/// <summary>
/// Global access point to the DI container.
/// Initialized in SmartCon.App at OnStartup.
/// Used by commands (IExternalCommand) to obtain services.
/// </summary>
public static class ServiceHost
{
    private static Func<Type, object>? _resolver;

    /// <summary>
    /// Initialize the resolver. Called once from App.OnStartup().
    /// </summary>
    public static void Initialize(Func<Type, object> resolver)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
    }

    /// <summary>
    /// Resolve a service by type.
    /// </summary>
    public static T GetService<T>() where T : notnull
    {
        if (_resolver is null)
        {
            throw new InvalidOperationException(
                "ServiceHost not initialized. Call Initialize() from App.OnStartup().");
        }

        return (T)_resolver(typeof(T));
    }

    public static void Reset()
    {
        _resolver = null;
    }
}
