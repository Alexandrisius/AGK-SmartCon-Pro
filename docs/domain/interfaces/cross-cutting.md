---
module: cross-cutting-interfaces
---
# Cross-cutting абстракции и утилиты

> Загружать: при работе с тестируемостью, DI, общей инфраструктурой.
> Источник истины: `src/SmartCon.Core/Services/Interfaces/`, `Compatibility/`, `Data/`, `Logging/`, `Json/`.

## IClock

Абстракция `DateTimeOffset.UtcNow` для тестируемости кода, зависящего от текущего времени.

**Файл:** `SmartCon.Core/Services/Interfaces/IClock.cs`
**Реализация:** `SmartCon.Core/Services/Interfaces/IClock.cs::SystemClock`

```csharp
public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
```

**DI:** `services.AddSingleton<IClock, SystemClock>();`

---

## IIdGenerator

Абстракция `Guid.NewGuid()` для детерминированной генерации ID в тестах.

**Файл:** `SmartCon.Core/Services/Interfaces/IIdGenerator.cs`
**Реализация:** `GuidIdGenerator`

```csharp
public interface IIdGenerator
{
    string NewId();
    string NewId(string format);
}
```

**DI:** `services.AddSingleton<IIdGenerator, GuidIdGenerator>();`

---

## IDispatcher

Абстракция `Application.Current?.Dispatcher` (WPF) для тестируемости UI-thread переключений.

**Файл:** `SmartCon.Core/Services/Interfaces/IDispatcher.cs`
**Реализация:** `SmartCon.FamilyManager/UI/WpfDispatcher.cs` (net48-safe: Guard + manual AwaitWithCancellation)

```csharp
public interface IDispatcher
{
    bool CheckAccess();
    void Invoke(Action action);
    Task InvokeAsync(Action action, CancellationToken ct = default);
}
```

**DI:** `services.AddSingleton<IDispatcher, WpfDispatcher>();`

---

## ISmartConLogger

Контракт логирования. Реализация — `SmartConLogger` (singleton). Использует scope-based structured logging.

**Файл:** `SmartCon.Core/Logging/ISmartConLogger.cs`

```csharp
public interface ISmartConLogger
{
    void Debug(string message, params (string Key, object? Value)[] properties);
    void Info(string message, params (string Key, object? Value)[] properties);
    void Warn(string message, string action, params (string Key, object? Value)[] properties);
    void Error(string message, Exception? ex = null, params (string Key, object? Value)[] properties);
    void Fatal(string message, Exception? ex = null, params (string Key, object? Value)[] properties);
    IDisposable BeginScope(string category, params (string Key, object? Value)[] properties);
    IDisposable Measure(string operation, params (string Key, object? Value)[] properties);
}
```

**Правила (ADR-026):**
1. `FilePath` / `FileName` в scope = `Path.GetFileName()`, не full path (L8).
2. `Warn(...)` всегда заканчивается `[Action: ...]` — оператору нужен следующий шаг (L9).
3. Не оборачивай `BeginScope` методы, которые живут > 1 сек с тяжёлой inner работой — 2 МБ логов за один прогон (C15).

Подробности: см. навык `smartcon-logging`.

---

## ILocalCatalogMigrator

DIP для `LocalCatalogMigrator` (ранее конкретный класс инжектился напрямую).

**Файл:** `SmartCon.Core/Services/Interfaces/ILocalCatalogMigrator.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogMigrator.cs` (public sealed)

```csharp
public interface ILocalCatalogMigrator
{
    Task MigrateAsync(CancellationToken ct = default);
}
```

**DI:** `services.AddSingleton<ILocalCatalogMigrator, LocalCatalogMigrator>();`

---

## Cross-cutting Utilities (Phase 5, 7)

### SqliteConnectionExtensions

Extension-методы для `Microsoft.Data.Sqlite.SqliteConnection` (Core/Data/SqliteConnectionExtensions.cs):
- `ExecuteAsync(sql, params?, ct)` → `int` (rows affected)
- `ExecuteScalarAsync<T>(sql, params?, ct)` → `T?` (null-safe)
- `QueryAsync<T>(sql, map, params?, ct)` → `IReadOnlyList<T>`
- `QuerySingleOrDefaultAsync<T>(sql, map, params?, ct)` → `T?`

**net48 compat:** использует `using` (sync) для `SqliteCommand`/`SqliteDataReader`, не `await using` (CS8417).
**CA1510:** использует `Guard.ThrowIfNull` для null-check.
**Миграция 30+ существующих call-ов отложена.**

**SmartConLogger Scopes (Phase 7):**

```csharp
using IDisposable scope = SmartConLogger.BeginScope(
    "OperationName",
    ("UserId", "u123"),
    ("Action", "Import"));

// ... do work ...

scope.Dispose(); // logs === END elapsed=Xms ===
```

```csharp
using IDisposable timer = SmartConLogger.Measure("MyOp");
// ... do work ...
// logs Completed in Xms to freeze-diagnostic
```

Оба Dispose-twice-safe.

**Guard (Phase 3):**

```csharp
public static class Guard
{
    public static void ThrowIfNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? paramName = null);
}
```

Polyfill для `ArgumentNullException.ThrowIfNull` (нет в net48). Удовлетворяет CA1510.

### JsonOptions (Phase 3)

```csharp
public static class JsonOptions
{
    public static JsonSerializerOptions Default { get; }       // strict
    public static JsonSerializerOptions WriteIndented { get; } // pretty
    public static JsonSerializerOptions RelaxedWriteIndented { get; } // JavaScriptEncoder.UnsafeRelaxedJsonEscaping (кириллица)
}
```
