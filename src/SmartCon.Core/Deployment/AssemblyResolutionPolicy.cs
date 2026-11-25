namespace SmartCon.Core.Deployment;

/// <summary>Источник, из которого следует отдать identity-critical сборку при AssemblyResolve.</summary>
public enum AssemblyResolutionSource
{
    None = 0,
    Host = 1,
    Loaded = 2,
    Plugin = 3,
}

/// <summary>
/// Политика разрешения identity-critical инфраструктуры BCL на net48.
///
/// Типы Span/Memory входят в публичные сигнатуры сторонних сборок
/// (SQLitePCLRaw.core, Microsoft.Data.Sqlite, System.Text.Json). Поэтому в процессе
/// допустима ровно ОДНА физическая копия System.Memory: если JIT сравнит сигнатуру
/// вызывающего кода и метода по разным копиям, ReadOnlySpan&lt;T&gt; окажется разными
/// типами и вызов упадёт с MissingMethodException (dotnet/roslyn#66104,
/// ericsink/SQLitePCL.raw#462).
///
/// Хост (папка Revit.exe) поставляет собственную System.Memory (Revit 2023 — 4.0.1.1),
/// и ссылки с точной assembly-версией биндятся к ней молча, минуя AssemblyResolve.
/// Значит все остальные запросы обязаны разрешаться в эту же копию — приоритет у хоста,
/// иначе наш более новый System.Memory из папки плагина раздваивает тип.
///
/// Вторая половина защиты — не давать сборкам разные версии ссылок: Microsoft.Data.Sqlite
/// на net48 поставляется loose (вне ILRepack), потому что слияние унифицировало его
/// AssemblyRef System.Memory до версии соседних merged-пакетов (Issue #527).
/// </summary>
public static class AssemblyResolutionPolicy
{
    private static readonly HashSet<string> s_typeIdentityCriticalNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "System.Memory",
        };

    /// <summary>Сборка, для которой две копии в процессе недопустимы.</summary>
    public static bool IsTypeIdentityCritical(string? simpleName) =>
        simpleName is { Length: > 0 } && s_typeIdentityCriticalNames.Contains(simpleName);

    /// <summary>
    /// Приоритет копий для identity-critical сборки: хост → уже загруженная → папка плагина.
    /// Копия хоста первая намеренно: её же получают exact-version ссылки других сборок,
    /// поэтому только она гарантирует единую идентичность типа.
    /// </summary>
    public static AssemblyResolutionSource ChooseIdentityCriticalSource(
        bool hostCopyExists,
        bool loadedCopyExists,
        bool pluginCopyExists) =>
        hostCopyExists ? AssemblyResolutionSource.Host
        : loadedCopyExists ? AssemblyResolutionSource.Loaded
        : pluginCopyExists ? AssemblyResolutionSource.Plugin
        : AssemblyResolutionSource.None;
}
