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
