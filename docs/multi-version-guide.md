# Multi-Version Development Guide

> **Загружать:** при создании нового функционала, новой команды, нового сервиса — всегда.
> **Цель:** любой код, написанный по этому стандарту, работает на Revit 2021–2025 без дублирования.

---

## Общая схема

```
┌────────────────────────────────────────────────────────────────────────┐
│                       4 shipping-артефакта сборки                     │
├────────────────┬──────────────────┬────────────────┬─────────────────┤
│      R19       │       R21        │      R24       │      R25        │
│     net48      │      net48       │     net48      │   net8.0-win    │
│ Revit 2019-20  │ Revit 2021-2023  │   Revit 2024   │   Revit 2025    │
│ RevitAPI 2020  │  RevitAPI 2021   │ RevitAPI 2024  │ RevitAPI 2025   │
│ ElementId(int) │  ElementId(int)  │ ElementId(long)│ ElementId(long) │
└────────────────┴──────────────────┴────────────────┴─────────────────┘

Один исходный код → 4 shipping-бинарника. Дублирование НЕ НУЖНО.
```

---

## Правило 1: Бизнес-логика — один раз

**Весь код пишется один раз** в `SmartCon.Core` или `SmartCon.PipeConnect`.

Версионность изолирована в **трёх** местах (см. ниже). Если вы пишете `#if` за пределами
этих мест — вы, скорее всего, делаете что-то не так.

---

## Правило 2: Где допустим `#if`

### Место 1: `SmartCon.Core/Compatibility/` — платформенные polyfills

```csharp
// NetFrameworkCompat.cs — то, чего нет в .NET Framework 4.8
#if NETFRAMEWORK
public static void ThrowIfNull(object o, string name) { if (o is null) throw new ... }
#endif

// ElementIdCompat.cs — разница ElementId(int) vs ElementId(long)
#if REVIT2024_OR_GREATER
    public static long GetValue(this ElementId id) => id.Value;
#else
    public static long GetValue(this ElementId id) => id.IntegerValue;
#endif

// SimplePriorityQueue.cs — PriorityQueue нет в net48
#if !NET8_0
public class SimplePriorityQueue<T> { ... }
#endif
```

### Место 2: `SmartCon.Revit/` — версия-специфичный Revit API

```csharp
// ConnectorExtensions.cs — API изменился между версиями
#if NETFRAMEWORK
    var origin = connector.Origin;       // XYZ (Revit 2021-2023)
#else
    var origin = connector.Origin;       // XYZ (Revit 2024+) — может отличаться сигнатура
#endif
```

### Место 3: `SmartCon.App/App.cs` — точка входа

```csharp
#if NETFRAMEWORK
    ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
#endif
```

### Больше НИГДЕ

Запрещено `#if` в:
- ViewModels (`SmartCon.PipeConnect/ViewModels/`)
- Сервисах модулей (`SmartCon.PipeConnect/Services/`)
- Моделях (`SmartCon.Core/Models/`)
- XAML-файлах

---

## Правило 3: Revit API — через интерфейсы

Новая функция, которой нужен Revit API:

```
1. Интерфейс → SmartCon.Core/Services/Interfaces/INewService.cs
2. Реализация → SmartCon.Revit/NewService.cs  (здесь допустим #if)
3. Регистрация → SmartCon.App/DI/ServiceRegistrar.cs
4. Использование → SmartCon.PipeConnect/ (через DI, без #if)
```

```csharp
// Core/Services/Interfaces/INewService.cs
public interface INewService
{
    bool DoSomething(ElementId elementId);
}

// Revit/NewService.cs
public class NewService : INewService
{
    public bool DoSomething(ElementId elementId)
    {
        // Здесь можно использовать Revit API и #if
#if REVIT2024_OR_GREATER
        return doc.GetElement(elementId) is not null;
#else
        return doc.GetElement(elementId) != null;
#endif
    }
}

// PipeConnect/Services/SomeHandler.cs
public class SomeHandler
{
    private readonly INewService _newService; // inject

    public void Handle()
    {
        _newService.DoSomething(someId); // чисто, без #if
    }
}
```

---

## Правило 4: WPF — полностью унифицирован

### Один набор окон для всех версий

```
Views/AboutView.xaml        → Revit 2021, 2022, 2023, 2024, 2025
Views/PipeConnectEditorView.xaml → та же XAML
ViewModels/...              → та же ViewModel
```

Никаких `#if` в XAML. Никаких разных окон для разных версий.

### ResourceDictionary — программно

`DynamicResource` в `DataGridColumn.Header` не работает на net48 без `Application.Current`
(Revit не создаёт WPF Application). Поэтому:

```csharp
// Code-behind: заголовки колонок задаются программно
ColCode.Header = LanguageManager.GetString(StringLocalization.Keys.Col_Code);
```

Обычные кнопки и текст — через `{DynamicResource Key}` в XAML + `LanguageManager.EnsureWindowResources(this)`
после `InitializeComponent()`.

### Стандартный шаблон View

```csharp
public partial class NewView : Window
{
    public NewView(NewViewModel viewModel)
    {
        InitializeComponent();
        LanguageManager.EnsureWindowResources(this);
        DataContext = viewModel;
    }
}
```

---

## Правило 5: Символы условной компиляции

Определены в `src/Directory.Build.targets`:

| Символ | Когда определён |
|---|---|
| `NETFRAMEWORK` | TFM = net48 (Revit 2021-2024) |
| `NET8_0` | TFM = net8.0-windows (Revit 2025) |
| `REVIT2021_OR_GREATER` | Всегда (минимальная версия) |
| `REVIT2022_OR_GREATER` | Revit 2022+ |
| `REVIT2023_OR_GREATER` | Revit 2023+ |
| `REVIT2024_OR_GREATER` | Revit 2024+ (ElementId long) |
| `REVIT2025_OR_GREATER` | Revit 2025 только (net8.0) |

**Использовать `OR_GREATER`**, а не конкретную версию:

```csharp
// ПРАВИЛЬНО:
#if REVIT2024_OR_GREATER

// НЕПРАВИЛЬНО:
#if REVIT2024
```

---

## Правило 6: ElementId — через ElementIdCompat

```csharp
// ПРАВИЛЬНО:
long value = elementId.GetValue();           // extension method
ElementId id = ElementIdCompat.Create(42);
int hash = elementId.GetStableHashCode();

// НЕПРАВИЛЬНО:
long value = elementId.Value;        // только Revit 2024+
int value = elementId.IntegerValue;   // только Revit 2021-2023
new ElementId(42);                    // неоднозначно (int vs long)
```

---

## Правило 7: Хранение данных — int64

`ElementId` сериализуется как `long` (int64):

```csharp
// Сериализация
long serialized = elementId.GetValue();

// Десериализация
ElementId restored = ElementIdCompat.Create(serialized);
```

На net48 (Revit 2021-2023) значение > `int.MaxValue` выбросит исключение — это ожидаемо,
такие данные не могут существовать в этих версиях.

---

## Правило 8: Сборка и деплой

### Для разработки (build-and-deploy.bat)

```
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25           → Revit 2025
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24           → Revit 2024
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21           → Revit 2021-2023
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19           → Revit 2019-2020
dotnet build src/SmartCon.Updater/SmartCon.Updater.csproj -c Debug -f net8.0 → Updater
```

### Для релиза (release.ps1)

Автоматически собирает 4 shipping-конфигурации (`R19 / R21 / R24 / R25`), прогоняет тесты на `Release.R25`, создаёт ZIP и Inno Setup EXE.

---

## Правило 9: Добавление новой команды — чеклист

При создании новой команды (например, `PipeDisconnect`):

1. **Интерфейс** → `Core/Services/Interfaces/` — если нужен Revit API
2. **Реализация** → `Revit/` — с `#if` если API отличается
3. **Модель** → `Core/Models/` — чистый C#, обнови `domain/models.md`
4. **ViewModel** → `PipeConnect/ViewModels/` — чистый C#, без `#if`
5. **View** → `PipeConnect/Views/` — XAML + code-behind по шаблону (Правило 4)
6. **Command** → `PipeConnect/Commands/` — `IExternalCommand`, вызывает ViewModel
7. **DI** → `App/DI/ServiceRegistrar.cs` — регистрация новых сервисов
8. **Тесты** → `Tests/` — xUnit + Moq

---

## Правило 10: Чего НЕ делать

| Запрет | Почему |
|---|---|
| Дублировать XAML для разных версий | WPF полностью унифицирован |
| Писать `#if` в ViewModel | Версионность изолирована в Revit-слое |
| Хранить `Element`/`Connector` между транзакциями | I-05 |
| Использовать `new Transaction()` напрямую | I-03 |
| Вызывать Revit API из WPF-потока | I-01 |
| Использовать XAML-файлы для локализации | Программное создание ResourceDictionary (net48 mscorlib vs net8 System.Runtime) |
| Использовать `DynamicResource` в `DataGridColumn.Header` | DataGridColumn не в visual tree, не работает без Application.Current |
