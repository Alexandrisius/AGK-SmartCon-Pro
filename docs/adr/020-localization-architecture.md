# ADR-020: Единая архитектура локализации через LocalizationBehavior

**Статус:** accepted
**Дата:** 2026-05-09

## Контекст

Локализация в SmartCon работает через `DynamicResource` привязки к словарю ресурсов. При смене языка словарь обновляется, и WPF должен перечитывать все `DynamicResource`.

**Проблемы, которые привели к этому ADR:**

1. **net48 vs net8 различия**: В net48 `Application.Current` может быть null (Revit plugin = class library, нет WPF `App`). В net8 `Application.Current` создаётся автоматически. `DynamicResource` fallback chain работает по-разному.

2. **Живое обновление UI при смене языка**: В net8 `DynamicResource` подписывается на "mentor" (ближайший `FrameworkElement` вверх по дереву), а не на `Application.Current.Resources`. Простое обновление `Application.Current.Resources.MergedDictionaries` не вызывает автоматического перечитывания у элементов, которые получили словарь через `MergedDictionaries` своего UserControl/Window.

3. **Разные паттерны для dockable panel и диалогов**: Изначально dockable panel использовал `LocalizationBehavior.AutoRefresh` (attached behavior), а диалоги использовали `LanguageManager.EnsureWindowResources(this)` (code-behind вызов). Это создавало два разных механизма для одной задачи.

## Решение

### 1. Единый механизм: LocalizationBehavior.AutoRefresh

Все View (dockable panel UserControls и dialog Window) используют **один** attached behavior:

```xml
<Window xmlns:behaviors="clr-namespace:SmartCon.UI.Behaviors;assembly=SmartCon.UI"
        behaviors:LocalizationBehavior.AutoRefresh="True"
        ...>
```

`LocalizationBehavior`:
- Подписывается на `LocalizationService.LanguageChanged` один раз (статически)
- Ведёт `List<WeakReference<FrameworkElement>>` отслеживаемых элементов
- При смене языка: заменяет `MergedDictionaries` у элемента + вызывает `element.UpdateLayout()`
- `WeakReference` предотвращает утечки памяти

### 2. Application.Current — single source of truth

`LanguageManager.Initialize()` создаёт `Application.Current` вручную, если он null (net48 паттерн для Revit plugins):

```csharp
if (Application.Current is null)
{
    try { _ = new Application(); }
    catch { /* already exists in another domain */ }
}
```

Все `DynamicResource` bindings fallback на `Application.Current.Resources`. Нет необходимости дублировать словарь в каждый `ContextMenu`, `Popup` или `UserControl`.

### 3. LanguageManager — только глобальное состояние

`LanguageManager` отвечает только за:
- Инициализацию языка при старте
- `SwitchLanguage()` — публичный API
- `GetString()` / `GetCurrentStrings()` — чтение ресурсов
- Обновление `Application.Current.Resources.MergedDictionaries`

**Удалены** (устаревшие методы):
- `EnsureWindowResources(Window)` — заменён на `LocalizationBehavior`
- `RefreshAllWindows()` — логика перенесена в `LocalizationBehavior`
- `_registeredWindows` — больше не нужен

### 4. MVVM: ViewModel реагирует на LanguageChanged

Для узлов дерева, которые содержат локализованные строки (например, "Без категории"), ViewModel подписывается на `LocalizationService.LanguageChanged` и обновляет свойства напрямую:

```csharp
// FamilyManagerMainViewModel — singleton, живёт весь lifetime приложения
LocalizationService.LanguageChanged += OnLanguageChanged;

private void OnLanguageChanged()
{
    var newLabel = LanguageManager.GetString(StringLocalization.Keys.FM_NoCategory) ?? "No category";
    if (_noCategoryNode is not null)
    {
        _noCategoryNode.DisplayName = newLabel;
        _noCategoryNode.FullPath = newLabel;
    }
}
```

Важно: VM хранит прямую ссылку на узел (`_noCategoryNode`), а не ищет его по дереву при каждом изменении языка.

## Последствия

**Плюсы:**
- Единый паттерн для всех View — никакого code-behind для локализации
- Никаких утечек памяти (WeakReference в LocalizationBehavior)
- Чистое разделение: `LanguageManager` = глобальное состояние, `LocalizationBehavior` = UI-адаптация
- Работает одинаково в net48 и net8

**Минусы:**
- Для live-обновления VM-данных нужна подписка на `LanguageChanged` в VM (только для динамических строк, не для DB-данных)
- `UpdateLayout()` после смены словаря — необходим в net8 из-за изменений в WPF 8

## Альтернативы

1. **EnsureWindowResources для каждого Window в code-behind**: работает, но нарушает MVVM (I-10) и создаёт два разных паттерна.
2. **Дублирование словаря в каждый ContextMenu/Popup**: избыточно, `Application.Current.Resources` уже содержит словарь.
3. **Ручной вызов UpdateLayout() во всех VM**: слишком много работы, `LocalizationBehavior` делает это централизованно.

## Связанные инварианты

- **I-10**: MVVM строго. `.xaml.cs` содержит только `DataContext = viewModel`. Локализация теперь полностью в XAML.
- **I-01**: Revit API из WPF — только через `IExternalEventHandler`. `LanguageManager` не вызывает Revit API.

## Связанные файлы

- `src/SmartCon.UI/LanguageManager.cs`
- `src/SmartCon.UI/Behaviors/LocalizationBehavior.cs`
- `src/SmartCon.UI/StringLocalization.cs`
- `src/SmartCon.Core/Services/LocalizationService.cs`
