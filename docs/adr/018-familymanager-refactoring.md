# ADR-018: FamilyManager Refactoring — DI Patterns, Async Safety, and Performance

## Статус

accepted

## Контекст

После завершения Phase 17 (Attribute Extraction Foundation) кодовая база FamilyManager накопила технический долг:

- ViewModel напрямую создавали другие ViewModel через `new`, обходя DI-контейнер
- `async Task` + `_ = discard` внутри `ExternalEvent.Raise()` вызывали deadlock на UI thread
- `GetBindingCountsAsync` делал N+1 запросов к SQLite
- `Split(new[] { ':' }, 2)` создавал лишний аллокацию массива
- `MessageBox` результат возвращался как `bool?`, что не различало Cancel от No
- `DynamicResource` в `DataGridColumn.Header` ломал заголовки на net48 (Revit 2021–2024)

## Решения

### 1. IFamilyManagerViewModelFactory — DI-защита для ViewModel

**Проблема:** `CategoryTreeEditorViewModel` создавал `AttributeLibraryViewModel` через `new AttributeLibraryViewModel(...)`, передавая 6 зависимостей вручную. Это нарушало ISP и делало тестирование невозможным.

**Решение:** Расширить `IFamilyManagerViewModelFactory` методами:

```csharp
public interface IFamilyManagerViewModelFactory
{
    // ... существующие методы ...
    CategoryTreeEditorViewModel CreateCategoryTreeEditorViewModel();
    AttributeLibraryViewModel CreateAttributeLibraryViewModel();
    CategoryPickerViewModel CreateCategoryPickerViewModel(bool allowClear = true);
}
```

**Преимущества:**
- Единая точка создания всех ViewModel
- Легко мокать в тестах
- Нет ручного перечисления 6+ зависимостей

### 2. DialogResult enum — явная семантика Yes/No/Cancel

**Проблема:** `bool?` для `ShowYesNoCancel` не различал Cancel (null) и No (false). В `ConfirmUnsavedChanges` приходилось делать хаки.

**Решение:** Перенести `DialogResult` enum в `SmartCon.Core`:

```csharp
public enum DialogResult { None, OK, Cancel, Yes, No }
```

Обновить `IFamilyManagerDialogService`:
```csharp
DialogResult ShowYesNoCancel(string title, string message);
```

**Преимущества:**
- Явная семантика: Yes, No, Cancel — три разных состояния
- Core не зависит от WPF (`MessageBoxResult` в WPF-слое)

### 3. Batch GetBindingCountsAsync — устранение N+1

**Проблема:** `AttributeLibraryViewModel` вызывал `GetBindingCountAsync(id)` в цикле для каждого атрибута → N+1 запросов к SQLite.

**Решение:** Добавить batch-метод в `ICategoryAttributeBindingService`:

```csharp
Task<IReadOnlyDictionary<string, int>> GetBindingCountsAsync(
    IEnumerable<string> attributeIds, CancellationToken ct = default);
```

Реализация — один SQL-запрос с `GROUP BY`.

**Результат:** O(N) → O(1) запросов.

### 4. async void FireAndForget — предотвращение deadlock

**Проблема:** `async Task FireAndForgetAsync` + `_ =` внутри `ExternalEvent.Raise()` создавал Task, который захватывал `SynchronizationContext.Current` (Revit UI thread). Continuation постился обратно в UI thread, но UI thread был занят → deadlock.

**Решение:** Использовать `async void` для true fire-and-forget внутри ExternalEvent:

```csharp
private static async void FireAndForget(Func<Task> f)
{
    try { await f(); }
    catch (Exception ex) { SmartConLogger.Warn($"FireAndForget: {ex.Message}"); }
}

_externalEvent.Raise(() => { FireAndForget(() => SaveTypesAsync(id)); });
```

**Почему это работает:** `async void` не возвращает Task → нет top-level объекта, который ждёт завершения через SynchronizationContext. Отдельные `await` внутри всё ещё захватывают контекст для UI-обновлений, но это нормально — они просто постятся в очередь.

**Источники:**
- Stephen Toub, "ConfigureAwait FAQ", .NET Blog, 2019
- Stephen Cleary, "Async/Await Best Practices", MSDN Magazine, 2013

### 5. StringComparer.Ordinal — культура-независимое сравнение

**Проблема:** `StringComparison.CurrentCultureIgnoreCase` в `RevitFamilyDataExtractionService.ReadFile` давал разные результаты на разных локалях Windows.

**Решение:** Заменить на `StringComparer.Ordinal` / `StringComparison.OrdinalIgnoreCase`.

### 6. Split(':', 2) — устранение лишней аллокации

**Проблема:** `Split(new[] { ':' }, 2)` аллоцирует массив char[] на каждый вызов.

**Решение:** `Split(':', 2)` — перегрузка для одиночного char не аллоцирует.

### 7. I-12 — программная установка DataGridColumn.Header

**Проблема:** `{DynamicResource}` в `DataGridColumn.Header` не резолвится на net48, потому что `DataGridColumn` не наследует `FrameworkElement`.

**Решение:** Задавать заголовки программно в code-behind:

```csharp
ColCode.Header = LanguageManager.GetString(StringLocalization.Keys.Col_Code);
```

## Последствия

- Все ViewModel FamilyManager используют `IFamilyManagerViewModelFactory`
- `async void` — единственный допустимый паттерн внутри `ExternalEvent.Raise()`
- Все строковые сравнения в Core — через `Ordinal`
- `Split(char, int)` используется вместо `Split(char[], int)`
- `DialogResult` используется вместо `bool?` для Yes/No/Cancel

## Связанные ADR

- [006](006-external-event-pattern.md) — ExternalEvent pattern
- [012](012-per-project-extensible-storage.md) — I-12 multi-version compatibility
- [017](017-familymanager-attribute-extraction.md) — Attribute Extraction Foundation

---

## Refactoring Updates (Phases 1–7, 2026-06)

После аудита проведена серия инкрементальных улучшений. Каждое закоммичено отдельно. R25: 1176 → 1224 теста.

### Phase 1 — Bug fix: import attributes + ITransactionService 3-arg

**Bug:** При импорте семейств сохранялись только категории, атрибуты и биндинги пропускались.

**Root cause:** `ExtractAttributesForImportedFamilies`/`ExtractTypesForImportedFamilies` не вызывали `LoadActive` через `ITransactionService.RunInTransaction(Document, ...)`. Только 2-arg overload, который использует active document, но для system families нужен explicit doc scope.

**Fix:** Добавлен `ITransactionService.RunInTransaction(Document document, string name, Action<Document> action)` overload (C4 fix). `WithNonNullCollections()` extension для `FamilyMetadataPackage` гарантирует непустые коллекции перед JSON-сериализацией (C1+C2+C3 fix).

### Phase 2 — Integration tests

Добавлено 9 тестов: 5 unit (`FamilyMetadataPackageExtensions`) + 4 integration (`LocalMetadataImportFlowIntegrationTests`, target fixture — отдельный `TempCatalogFixture` чтобы не путать source/target).

### Phase 3 — Centralize JSON + Format constants + Migrator

- `JsonOptions.Default` / `WriteIndented` / `RelaxedWriteIndented` — статические singleton-ы для всех JSON-операций.
- `FamilyMetadataFormat.Id` ("smartcon.family-metadata") + `CurrentVersion` (2) — единственный источник правды.
- `FamilyMetadataMigrator.Migrate(package)` — stub v1→v2, throws `NotSupportedException` для unknown format/version.
- `Guard.ThrowIfNull<T>` polyfill — net48 не имеет `ArgumentNullException.ThrowIfNull`, плюс CA1510 запрещает `throw new ArgumentNullException(...)`. polyfill в `SmartCon.Core/Common/`.
- Заменено 11 hard-coded `JsonSerializerOptions` instances + 5 в тестах.

### Phase 4a — DIP for LocalCatalogMigrator

- `ILocalCatalogMigrator` interface в Core.
- `LocalCatalogMigrator` → `public sealed`, implements interface.
- `LocalCatalogDatabase` → `public sealed` (CS0051 fix: public ctor принимал internal параметр).
- 6 ctor-ов обновлены на interface, DI registration переключена на `AddSingleton<ILocalCatalogMigrator, LocalCatalogMigrator>()`.

### Phase 4b — God Object fix: FamilyManagerServices record

`FamilyManagerMainViewModel` имел ctor с 30 параметрами. Решение: `public sealed record FamilyManagerServices(Param1, Param2, ..., Param30)` с 30 readonly properties. ctor: 30 → 1 param. Тело ctor сохранило 30 field assignments через `services.X`. 7 partial classes не затронуты.

### Phase 4c — Async all 13 sync-over-async sites

**Проблема:** 13 мест с `GetAwaiter().GetResult()` / `Task.Run().Result` / `RaiseAsync(_ => { ... })` (statement lambda) создавали UI-thread deadlocks и silent behavior changes.

**Решение:**
- Новый `IFamilyManagerAwaitableEvent.RaiseAsyncTask(Func<object, Task>, CT)` overload — отдельный name, **не** overload (C# statement lambda implicit convert в `Func<object, Task>` — silent behavior change).
- 6 sync-over-async в `LoadPlace.cs`, 1 в `Import.cs`, 1 в `FamilyEdit.cs` → `await`.
- `InitializeAsync` в ctor: `.GetAwaiter().GetResult()` → `async Task` + `FireAndForget` helper.
- **`FireAndForget` — static void, НЕ async void** (исправление к ADR-018 §4): статический метод + `Task.ContinueWith` с `TaskContinuationOptions.OnlyOnFaulted | ExecuteSynchronously` + `TaskCreationOptions.RunContinuationsAsynchronously` на TCS awaitableEvent. Никакого `async void` overhead.
- Test fakes: `UninitializedAwaitableEvent` / `InlineAwaitableEvent` обновлены.

**Sync-over-async count: 13 → 0.**

### Phase 5 — SqliteConnectionExtensions

- `Core/Data/SqliteConnectionExtensions.cs`: `ExecuteAsync` / `ExecuteScalarAsync<T>` / `QueryAsync<T>` / `QuerySingleOrDefaultAsync<T>`.
- `Microsoft.Data.Sqlite` package reference в Core.
- `using` (sync), не `await using` (net48 compatibility — CS8417 fix).
- Миграция 30+ существующих SQL call-ов отложена (риск регрессий > выгода; extensions доступны для нового кода).

### Phase 6 — IClock, IIdGenerator, IDispatcher

- `IClock.UtcNow` + `SystemClock` — для тестируемости `DateTimeOffset.UtcNow` (74 call-а в коде).
- `IIdGenerator.NewId(format?)` + `GuidIdGenerator` — для тестируемости `Guid.NewGuid()` (40 call-ов).
- `IDispatcher.CheckAccess/Invoke/InvokeAsync` (Core) + `WpfDispatcher` (FamilyManager/UI) — для тестируемости `Application.Current?.Dispatcher` (5 call-ов).
- net48-safe: `Guard.ThrowIfNull` + manual `AwaitWithCancellation` (нет `Task.WaitAsync` в net48).
- Миграция существующих call-ов отложена.

### Phase 7 — Structured logging

- `SmartConLogger.BeginScope(operation, params (key,value)[])` → `IDisposable`. Emits `[OpId=xxxxxxxx] === START/END operation elapsed=Xms ===` с key=value properties. Short opId (8 chars) для log grep-ability.
- `SmartConLogger.Measure(operation)` → `IDisposable`. Emits Start/Completed-in-Xms to freeze log.
- Оба Dispose-twice-safe (idempotent guard).
- Миграция 1001 существующих `SmartConLogger.*` call-ов отложена (out of scope; новый код использует scopes).

### Test counts

| Phase | Added | Total R25 |
|---|---|---|
| Baseline | — | 1176 |
| Phase 1 | (fix, no new tests) | 1176 |
| Phase 2 | +9 | 1185 |
| Phase 3 | +16 | 1201 |
| Phase 4a-c | (no new tests; updates) | 1201 |
| Phase 5 | +7 | 1208 |
| Phase 6 | +11 | 1219 |
| Phase 7 | +5 | 1224 |

R24, R21 — все три билда проходят.
