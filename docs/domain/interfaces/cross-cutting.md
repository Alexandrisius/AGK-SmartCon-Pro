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

**v2.0.0 (ADR-036, M-019-003 DONE):** `FamilyManagerMainViewModel` теперь инжектирует `IDispatcher` через `FamilyManagerServices` (62→63 props) и использует `_dispatcher.InvokeAsync(...)` вместо захваченного в ctor `Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher`. Это:
- Устраняет известный баг WPF/Revit в net48 (Application.Current == null).
- Делает VM unit-тестируемым через `Mock<IDispatcher>` или `new WpfDispatcher(null)`.
- Следует правилу ADR-031 #1: **Любое обновление UI внутри FireAndForget должно маршалиться через dispatcher явно**.

**Out of scope:** `CategoryPickerViewModel`, `CategoryTreeEditorViewModel`, `RevitWindowFocusService`, `ShareProjectCommand` — ещё используют legacy-паттерн. Их миграция — отдельный PR.

**Пример использования (после M-019-003):**

```csharp
// ctor
_dispatcher = services.Dispatcher;

// в FireAndForget (ADR-031 правило #1)
FireAndForget(async () =>
{
    await _dataImportService.SaveExtractionResultAsync(...);

    // ✅ Marshal back to UI thread explicitly.
    // IDispatcher.InvokeAsync принимает Action, а LoadTreeAsync возвращает Task.
    // Оборачиваем в fire-and-forget лямбду через discard (`_ = ...`), чтобы
    // Action оставался sync — иначе компилятор генерирует async void lambda,
    // что нарушает I-13. Этот же паттерн использован в
    // FamilyManagerMainViewModel.FamilyEdit.cs:1034 (Bug #2 fix, ADR-036).
    await _dispatcher.InvokeAsync(() => { _ = LoadTreeAsync(); });
}, nameof(MyMethod));

// синхронный UI update с CheckAccess
private void OnStatusChanged(string message)
{
    if (_dispatcher.CheckAccess())
        StatusMessage = message;
    else
        _ = _dispatcher.InvokeAsync(() => StatusMessage = message);
}
```

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

---

## ITypeCatalogValueApplier (Phase 25 / ADR-032)

Парсит сырое строковое значение из Type Catalog (`.txt`) в типизированное значение, соответствующее RevitAPI `StorageType`. Pure C# — без зависимости от RevitAPI в Core, что позволяет unit-тестировать в test bin (RevitAPI имеет `ExcludeAssets=runtime` — см. skill `smartcon-testing` §"What cannot be mocked").

**Файл:** `ITypeCatalogValueApplier.cs`
**Реализация:** `SmartCon.Core/Services/Implementation/TypeCatalogValueApplier.cs`

```csharp
public sealed record TypeCatalogValueApplyResult(
    TypeCatalogValueApplyStatus Status,
    object? Value,         // string | double | int | long (ElementId raw id)
    string? Error);

public enum TypeCatalogValueApplyStatus
{
    Success,
    InvalidFormat,
    UnsupportedStorageType,
}

/// <summary>
/// Integer codes matching Revit API <c>StorageType</c> enum exactly. The underlying
/// integer values are STABLE across all Revit versions (2019 through 2026+):
/// <c>None=0, Integer=1, Double=2, String=3, ElementId=4</c>. Confirmed via
/// <see href="https://www.revitapidocs.com/2025/3dbebcb8-792b-a3dd-fe63-faaa05704f3c.htm"/>.
/// </summary>
public enum StorageTypeCode
{
    StgNone = 0,
    StgInt = 1,        // StorageType.Integer
    StgNumber = 2,     // StorageType.Double
    StgText = 3,       // StorageType.String
    StgElementId = 4,
}

public interface ITypeCatalogValueApplier
{
    TypeCatalogValueApplyResult Apply(string? rawValue, StorageTypeCode storageType);
}
```

**Стратегия парсинга:**
- `StgText` — value возвращается как есть
- `StgInt` — `int.TryParse` с `InvariantCulture`
- `StgNumber` — `InvariantCulture` → fallback на `CurrentCulture` (для русской локали с запятой)
- `StgElementId` — `long.TryParse` с `InvariantCulture` (raw id, оборачивается в `new ElementId(id)` на стороне вызывающего кода)
- Прочее — `UnsupportedStorageType`

**Имена членов `StorageTypeCode` используют префикс `Stg`** (storage) чтобы избежать CA1720 (имя члена совпадает с именем типа — `Integer`/`String`/`Double` запрещены).

**Хронология:** значения `StorageType` enum были **стабильны с Revit 2015 по 2026** (None=0, Integer=1, Double=2, String=3, ElementId=4). В `revitapidocs.com/2025` Autodesk явно указал underlying values для всех членов enum. Маппинг выполняется через `(int)param.StorageType` → `StorageTypeCode` напрямую, без runtime switch по версии. Подтверждено через exa search и revitapidocs.com.
