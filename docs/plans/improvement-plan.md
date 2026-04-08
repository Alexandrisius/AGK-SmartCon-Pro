# План улучшения SmartCon

> Дата: 2026-04-08
> Статус: Черновик
> Цель: Подготовка кодовой базы к презентации Senior-команде

---

## Сводка анализа

| Метрика | Значение |
|---|---|
| Проектов в solution | 6 |
| Строк кода | ~10 500 |
| Unit-тестов | 208 |
| ADR | 9 |
| Инвариантов | 10 (I-01 .. I-10) |
| Phases completed | 0-4 (✅), 5-9 (roadmap) |

**Сильные стороны:**
- Clean Architecture: Core не зависит от RevitAPIUI
- Формализованные инварианты с кодами (I-01..I-10)
- 9 ADR с обоснованиями архитектурных решений
- C# 12 / .NET 8, nullable enabled, warnings as errors
- FormulaEngine полностью покрыт тестами (Tokenizer → Parser → AST → Evaluator → Solver)
- Snapshot/Memento для Undo через TransactionGroup

**Ключевые проблемы:**
- God Object ViewModel (2024 строки)
- Service Locator вместо DI в командном слое
- Core зависит от RevitAPI value-типов (ElementId, XYZ, Domain)
- Молчаливое проглатывание ошибок
- Отсутствие CI/CD, анализаторов, метрик покрытия

---

## Граф зависимостей между фазами

```
Фаза A (Блокировка ошибки)
  |
Фаза B (Реструктуризация ViewModel)
  |
  +--> Фаза C (DI-рефакторинг)
  |      |
  |      +--> Фаза E (Тестирование)
  |
  +--> Фаза D (Чистота Core)
         |
         +--> Фаза E (Тестирование)
                |
                Фаза F (Инфраструктура)
                  |
                  Фаза G (Производительность)
                    |
                    Фаза H (UI/UX)
```

---

## Фаза A — Устранение блокирующих проблем

**Цель:** Убрать silent errors, stub-методы, очевидные дефекты.
**Зависит от:** Ничего.
**Оценка:** 2 дня.

### A.1 Убрать молчаливое проглатывание исключений

**Проблема:** Критические ошибки скрываются, что затрудняет диагностику.

| Файл | Проблема | Решение |
|---|---|---|
| `SmartConLogger.cs` | `catch { }` при записи в файл | Логировать в `Debug.WriteLine` как fallback |
| `JsonFittingMappingRepository.cs` | При ошибке JSON → `return null` | Логировать ошибку + бэкап повреждённого файла |
| `PipeConnectEditorViewModel.cs` | `catch { /* best-effort */ }` в `RealignAfterSizing` | Логировать exception как Warning |
| `Evaluator.cs` | Неизвестная переменная → `0.0` | Логировать Warning через FormulaDiagnostic |
| `Evaluator.cs` | Деление на 0 → `PositiveInfinity` | Логировать Warning |

**Принцип:** Ни одно исключение не должно быть проглочено без следа. Минимум — запись в диагностический лог.

### A.2 Удалить пустой stub-метод

**Файл:** `FittingMapper.cs` — `LoadFromFile()` не реализован.

**Действие:** Удалить метод из класса и из интерфейса `IFittingMapper`. Если понадобится — добавить позже (YAGNI).

### A.3 Исправить небезопасную замену строк в формулах

**Файл:** `FormulaSolver.cs` — `NormalizeForSolveFor` использует `string.Replace(name, alias)`.

**Проблема:** `"ADSK_D"` может совпасть с частью `"ADSK_Diameter"`.

**Решение:** Заменить на `Regex.Replace(formula, $@"\b{Regex.Escape(name)}\b", alias)`.

### A.4 Убрать дублирующий ElementIdEqualityComparer

**Файл:** `ElementIdEqualityComparer` — публичный класс, используется только в `ConnectionGraph`.

**Действие:** Сделать `private sealed class` внутри `ConnectionGraph`.

### Критерии приёмки

- [ ] Все `catch { }` / `catch { /* best-effort */ }` заменены на логирующие catch
- [ ] `FittingMapper.LoadFromFile` удалён из класса и интерфейса
- [ ] `NormalizeForSolveFor` использует regex с границами слов
- [ ] `ElementIdEqualityComparer` — nested private class
- [ ] Все 208+ тестов проходят
- [ ] 0 warnings

---

## Фаза B — Реструктуризация God Object ViewModel

**Цель:** Разделить `PipeConnectEditorViewModel` (2024 строки) на автономные компоненты.
**Зависит от:** Фаза A.
**Оценка:** 3 дня.

### B.1 Текущая структура (проблема)

```
PipeConnectEditorViewModel.cs (2024 строки)
├── 20+ ObservableProperty
├── 15+ RelayCommand
├── Логика поворота
├── Логика подбора размеров
├── Логика цепочки элементов
├── Оркестрация сервисов (14+ GetService<>)
├── Управление TransactionGroup
└── Вспомогательные методы
```

### B.2 Целевая структура

```
PipeConnect/
├── ViewModels/
│   ├── PipeConnectEditorViewModel.cs     (~400 строк) — UI-состояние, делегирование
│   └── ...существующие VM без изменений
├── Controllers/
│   ├── RotationController.cs             (~200 строк) — поворот, снэп углов
│   ├── SizingController.cs               (~300 строк) — подбор размеров, LookupTable
│   ├── ChainController.cs                (~250 строк) — глубина цепочки, snapshot/restore
│   └── FittingController.cs              (~200 строк) — вставка/замена фитингов
├── Orchestrators/
│   └── PipeConnectOrchestrator.cs        (~350 строк) — workflow S1→S7, управление сессией
└── Commands/
    └── PipeConnectCommand.cs             (~200 строк) — точка входа IExternalCommand
```

### B.3 Ответственность каждого компонента

**PipeConnectOrchestrator** — координация workflow:
- Методы `Init()`, `Commit()`, `Cancel()`
- Управление `PipeConnectionSession` и `TransactionGroup`
- Вызов сервисов для S1→S7 шагов
- Создание `PipeConnectSessionContext`

**RotationController** — логика поворота:
- `ApplyRotation(double angleDegrees)`
- `ApplySnapAngle(double stepDegrees)`
- Взаимодействие с `ITransformService`

**SizingController** — подбор размеров:
- `GetAvailableSizes()` → `List<SizeOption>`
- `ApplySize(SizeOption option)`
- Взаимодействие с `IParameterResolver`, `IDynamicSizeResolver`

**ChainController** — управление цепочкой:
- `IncrementChainDepth()` / `DecrementChainDepth()`
- Snapshot/restore через `NetworkSnapshotStore`
- Взаимодействие с `IElementChainIterator`, `INetworkMover`

**FittingController** — работа с фитингами:
- `GetAvailableFittings()` → `List<FittingCardItem>`
- `ApplyFitting(FittingCardItem fitting)`
- Взаимодействие с `IFittingMapper`, `IFittingInsertService`

**PipeConnectEditorViewModel** — тонкий слой:
- `[ObservableProperty]` для UI-состояния
- `[RelayCommand]` делегируют в controllers
- PropertyChanged-уведомления
- Никакой бизнес-логики

### B.4 Принципы рефакторинга

1. **Сохранить все публичные API** ViewModel (команды, свойства) — XAML не меняется
2. **Controllers не знают о UI** — нет `ObservableProperty`, нет `RelayCommand`
3. **Orchestrator получает сервисы через конструктор** (подготовка к Фазе C)
4. **Каждый класс < 400 строк**

### Критерии приёмки

- [ ] `PipeConnectEditorViewModel` ≤ 400 строк
- [ ] 4 controller-класса + 1 orchestrator созданы
- [ ] Все публичные команды и свойства ViewModel сохранены
- [ ] XAML-биндинги работают без изменений
- [ ] Все 208+ тестов проходят
- [ ] Manual smoke-test: полный workflow S1→S7

---

## Фаза C — Устранение Service Locator

**Цель:** Заменить `ServiceHost.GetService<T>()` на конструкторную инъекцию.
**Зависит от:** Фаза B.
**Оценка:** 2 дня.

### C.1 Текущая проблема

```csharp
// PipeConnectCommand.cs — 14+ скрытых зависимостей
var contextWriter = ServiceHost.GetService<IRevitContextWriter>();
var revitContext  = ServiceHost.GetService<IRevitContext>();
var txService     = ServiceHost.GetService<ITransactionService>();
// ... ещё 11 вызовов
```

Невозможно понять зависимости класса без чтения тела методов. Невозможно протестировать без инициализации статического ServiceHost.

### C.2 Целевая архитектура

**Для PipeConnectCommand** (создаётся Revit, не DI):

```csharp
public sealed class PipeConnectCommand : IExternalCommand
{
    public static IServiceProvider? ServiceProvider { get; set; }

    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var orchestrator = new PipeConnectOrchestrator(
            ServiceProvider.GetRequiredService<IRevitContext>(),
            ServiceProvider.GetRequiredService<ITransactionService>(),
            // ...
        );
        // или
        var orchestrator = ActivatorUtilities.CreateInstance<PipeConnectOrchestrator>(ServiceProvider);
    }
}
```

`ServiceProvider` устанавливается один раз в `App.OnStartup` через `ServiceLocator`.

**Для PipeConnectOrchestrator** (создаётся через DI или ActivatorUtilities):

```csharp
public sealed class PipeConnectOrchestrator
{
    private readonly IRevitContext _revitContext;
    private readonly ITransactionService _txService;
    private readonly IConnectorService _connectorService;
    // ... все зависимости видимы в конструкторе

    public PipeConnectOrchestrator(
        IRevitContext revitContext,
        ITransactionService txService,
        IConnectorService connectorService,
        // ...
    )
}
```

### C.3 Этапы

1. После Фазы B — Orchestrator и Controllers уже получают зависимости через конструктор
2. Заменить `ServiceHost.GetService<T>()` в `PipeConnectCommand` на создание Orchestrator через `ActivatorUtilities.CreateInstance`
3. Заменить `ServiceHost.GetService<T>()` в `PipeConnectEditorViewModel` на передачу через параметр `Init()`
4. `ServiceHost` оставить только как bridge для `RibbonBuilder` (где нет DI scope)
5. Добавить `IServiceProvider` свойство в `PipeConnectCommand` для установки из `ServiceLocator`

### C.4 Статус ServiceHost после рефакторинга

| Потребитель | До | После |
|---|---|---|
| `PipeConnectCommand` | 14× `ServiceHost.GetService<T>()` | 1× `ActivatorUtilities.CreateInstance` |
| `PipeConnectEditorViewModel` | `ServiceHost.GetService<T>()` | Параметры `Init()` |
| `RibbonBuilder` | Не использует | Не использует |
| `App.OnStartup` | `ServiceHost.Initialize(resolver)` | `ServiceHost.Initialize(resolver)` — bridge |

### Критерии приёмки

- [ ] Ноль вызовов `ServiceHost.GetService<T>()` в `PipeConnectCommand`
- [ ] Ноль вызовов `ServiceHost.GetService<T>()` в ViewModel
- [ ] Все зависимости Orchestrator/Controllers видимы в конструкторе
- [ ] `ServiceHost` используется только как bridge для Ribbon
- [ ] Все 208+ тестов проходят

---

## Фаза D — Чистота Core (убрать зависимость от RevitAPI)

**Цель:** `SmartCon.Core` не ссылается на `RevitAPI.dll`.
**Зависит от:** Фаза B (модели используются в Controllers).
**Оценка:** 3 дня.

### D.1 Текущие зависимости Core от RevitAPI

| Revit-тип | Где используется в Core | Замена |
|---|---|---|
| `ElementId` | `ConnectorProxy.OwnerElementId`, `ConnectionEdge`, `ConnectionGraph`, `ConnectionRecord`, `ElementSnapshot`, `PipeConnectSessionContext` | `long` |
| `XYZ` | `ConnectorProxy.Origin`, `.BasisZ`, `.BasisX` (как свойства) | Уже есть `Vec3` — сделать основными |
| `Domain` | `ConnectorProxy.Domain` | `enum MepDomain { Undefined, Piping, Electrical, HVAC }` |
| `BuiltInParameter` | `ParameterDependency.BuiltIn` | `string BuiltInName` (или `int BuiltInEnumValue`) |
| `ForgeTypeId` | Не используется напрямую | — |

### D.2 План миграции

**Шаг 1:** Создать `MepDomain` enum в `SmartCon.Core/Models/`:
```csharp
public enum MepDomain { Undefined, Piping, Electrical, HVAC }
```

**Шаг 2:** Обновить `ConnectorProxy`:
```csharp
public sealed record ConnectorProxy
{
    public long OwnerElementId { get; init; }           // было ElementId
    public int ConnectorIndex { get; init; }
    public required Vec3 Origin { get; init; }          // было XYZ, уже есть Vec3-вариант
    public required Vec3 BasisZ { get; init; }
    public required Vec3 BasisX { get; init; }
    public double Radius { get; init; }
    public MepDomain Domain { get; init; }              // было Domain
    public ConnectionTypeCode ConnectionTypeCode { get; init; }
    public bool IsFree { get; init; }
}
```

**Шаг 3:** Обновить все модели (`ConnectionEdge`, `ConnectionRecord`, `ConnectionGraph`, `ElementSnapshot`, `PipeConnectSessionContext`) — заменить `ElementId` на `long`.

**Шаг 4:** Обновить `ConnectorWrapper` в SmartCon.Revit — конвертация `ElementId.Value` → `long`, `Domain` → `MepDomain`.

**Шаг 5:** Убрать `<Reference Include="RevitAPI">` из `SmartCon.Core.csproj`.

**Шаг 6:** Обновить все `using Autodesk.Revit.DB;` в Core — оставить только в файлах, где это необходимо (если останутся). После полной миграции — убрать.

### D.3 Обновление тестов

- Тесты, создающие `ConnectorProxy` — заменить `new ElementId(n)` на `n` (long)
- Тесты с `Domain` — заменить на `MepDomain.Piping`
- Тесты с `XYZ` — заменить на `Vec3`

### Критерии приёмки

- [ ] `SmartCon.Core.csproj` не содержит `<Reference Include="RevitAPI">`
- [ ] Ни один `.cs` файл в Core не содержит `using Autodesk.Revit.DB;`
- [ ] Все модели используют `long`, `Vec3`, `MepDomain` вместо Revit-типов
- [ ] `ConnectorWrapper` в Revit-слое корректно конвертирует типы
- [ ] Все 208+ тестов проходят
- [ ] Core компилируется без RevitAPI.dll в GAC

---

## Фаза E — Усиление тестирования

**Цель:** Повысить покрытие и качество тестов.
**Зависит от:** Фазы B, C, D.
**Оценка:** 3 дня.

### E.1 Покрытие Controllers и Orchestrator

После Фазы B каждый Controller и Orchestrator можно тестировать изолированно:

| Компонент | Зависимости (mock) | Ключевые тесты |
|---|---|---|
| `PipeConnectOrchestrator` | `ITransactionService`, `IConnectorService`, `IElementSelectionService` | Init/Commit/Cancel workflow, RollBack при ошибке |
| `RotationController` | `ITransformService`, `ITransactionService` | ApplyRotation, ApplySnapAngle, граничные углы |
| `SizingController` | `IParameterResolver`, `IDynamicSizeResolver` | GetAvailableSizes, ApplySize, fallback |
| `ChainController` | `IElementChainIterator`, `INetworkMover`, `NetworkSnapshotStore` | Increment/Decrement depth, snapshot/restore |
| `FittingController` | `IFittingMapper`, `IFittingInsertService` | GetFittings, ApplyFitting, direct connect |

### E.2 Метрики покрытия

**Действие:** Добавить в `SmartCon.Tests.csproj`:
```xml
<PackageReference Include="coverlet.collector" Version="6.0.*" />
```

Запуск: `dotnet test --collect:"XPlat Code Coverage"`

**Целевые показатели:**

| Проект | Цель | Примечание |
|---|---|---|
| `SmartCon.Core` | ≥ 85% | FormulaEngine + Math + Models + Services |
| `SmartCon.PipeConnect` (Controllers/Orchestrator) | ≥ 70% | После Фазы B |
| `SmartCon.Revit` | — | Невозможно тестировать без Revit runtime |

### E.3 Параметризация тестов

Конвертировать однотипные тесты в `[Theory]`:

```
Tokenizer: 15+[Fact] → 3[Theory] с [InlineData]
Evaluator: 20+[Fact] → 5[Theory] с [InlineData]
Solver:    10+[Fact] → 3[Theory] с [InlineData]
```

### E.4 Revit-слой — стратегия

> Revit API невозможно тестировать вне Revit runtime.
> Это архитектурное ограничение платформы, а не дефект проекта.

**Подход:**
- Максимально тонкий Revit-слой (адаптеры) — вся логика в Core
- Revit-реализации содержат только вызовы API + конвертацию типов
- Интеграционное тестирование — ручное или через Revit Test Framework при наличии

**Что можно протестировать в Revit-слое без runtime:**
- Extension-методы конвертации `XYZ ↔ Vec3` — через mock/wrapper
- `ConnectorWrapper.ToProxy()` — через mock `Connector`
- Логика `FreeConnectorFilter.AllowElement` — через mock `Element`

### Критерии приёмки

- [ ] Unit-тесты на все 4 Controller + Orchestrator (≥ 50 новых тестов)
- [ ] Coverlet настроен, отчёт генерируется
- [ ] Core coverage ≥ 85%
- [ ] PipeConnect coverage ≥ 70%
- [ ] ≥ 20 тестов конвертировано в `[Theory]`

---

## Фаза F — Инфраструктура

**Цель:** CI/CD, анализаторы, версионирование.
**Зависит от:** Фазы A, B.
**Оценка:** 2 дня.

### F.1 Анализаторы кода

Добавить в `Directory.Build.props`:
```xml
<PackageReference Include="Microsoft.CodeAnalysis.NetAnalyzers" Version="9.0.*" PrivateAssets="all" />
<PackageReference Include="Roslynator.Analyzers" Version="4.12.*" PrivateAssets="all" />
```

Настроить `.editorconfig`:
```ini
dotnet_diagnostic.CA1031.severity = warning  # Не catch Exception без re-throw
dotnet_diagnostic.CA1062.severity = warning  # Проверка аргументов
dotnet_diagnostic.CA2000.severity = warning  # Dispose объектов
dotnet_roslynator.RCS1090.severity = warning # Call ToList() перед iterating
```

### F.2 CI/CD (GitHub Actions)

Создать `.github/workflows/build.yml`:
```yaml
name: Build & Test
on: [push, pull_request]
jobs:
  build:
    runs-on: windows-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 8.0.x
      - run: dotnet build src/SmartCon.sln -c Debug --verbosity minimal
      - run: dotnet test src/SmartCon.Tests -c Debug --no-build
```

### F.3 Версионирование сборки

Обновить `Directory.Build.props`:
```xml
<VersionPrefix>1.0.0</VersionPrefix>
<VersionSuffix>$(VersionSuffix)</VersionSuffix>
```

В `.addin`:
```xml
<Name>SmartCon $(Version)</Name>
```

### F.4 Гибкая конфигурация Revit API path

Заменить в `Directory.Build.props`:
```xml
<RevitApiPath Condition="'$(RevitApiPath)' == ''">C:\Program Files\Autodesk\Revit 2025</RevitApiPath>
```

Это позволяет переопределить путь через environment variable или command line:
```
dotnet build -p:RevitApiPath="C:\Program Files\Autodesk\Revit 2026"
```

### Критерии приёмки

- [ ] NetAnalyzers + Roslynator подключены, 0 новых warnings
- [ ] GitHub Actions pipeline: build + test green
- [ ] Версия сборки задаётся в `Directory.Build.props`
- [ ] `RevitApiPath` переопределяем через `-p:`

---

## Фаза G — Производительность и устойчивость

**Цель:** Оптимизация горячих путей, thread-safety, кеширование.
**Зависит от:** Фазы B, C.
**Оценка:** 2 дня.

### G.1 Thread-safety ExternalEvent

**Проблема:** `volatile Action<UIApplication>? _pendingAction` — при быстрых вызовах Raise() action может быть перезаписан до выполнения.

**Решение:** Использовать `Interlocked.Exchange`:
```csharp
public void Raise(ExternalEvent externalEvent, Action<UIApplication> action)
{
    Interlocked.Exchange(ref _pendingAction, action);
    externalEvent.Raise();
}

public void Execute(UIApplication app)
{
    var action = Interlocked.Exchange(ref _pendingAction, null);
    // ...
}
```

### G.2 CancellationToken для BisectionSolver

Добавить параметр `CancellationToken cancellationToken = default`:
- Проверка на каждой итерации
- Защита от зависания при экстремальных формулах

### G.3 Кеширование FormulaSolver

Одинаковые формулы с одинаковыми переменными пересчитываются при каждом вызове.

**Решение:** LRU-кеш внутри `FormulaSolver`:
```csharp
private readonly LruCache<(string Formula, string VariablesKey), double> _cache = new(capacity: 64);
```

Формулы Revit детерминированы — кеширование безопасно. Кеш сбрасывается при смене документа.

### G.4 Минимизация doc.Regenerate()

**Проблема:** `doc.Regenerate()` вызывается после каждого Transform в `PipeConnectEditorViewModel`.

**Решение:** Группировать трансформации. Вызывать Regenerate только перед чтением геометрии (например, перед `RefreshConnector`). Добавить комментарий с обоснованием каждого Regenerate.

### Критерии приёмки

- [ ] `Interlocked.Exchange` в обоих ExternalEvent handler'ах
- [ ] `CancellationToken` в `BisectionSolver`
- [ ] LRU-кеш в `FormulaSolver` (опционально, с disable-флагом)
- [ ] Количество `doc.Regenerate()` минимизировано
- [ ] Все 208+ тестов проходят

---

## Фаза H — UI/UX и полировка

**Цель:** Улучшения интерфейса, локализация, консолидация проектов.
**Зависит от:** Фазы B, E.
**Оценка:** 2 дня.

### H.1 Статический логгер → интерфейс

**Проблема:** `SmartConLogger` — static class, невозможно подменить в тестах.

**Решение:**
1. Создать `ISmartConLogger` в Core:
```csharp
public interface ISmartConLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    // ...
}
```
2. Реализация — обёртка над текущим файловым логом
3. Регистрация в DI как Singleton
4. Static facade `SmartConLogger` делегирует в resolved instance (backward compat)
5. Новые классы используют инъекцию `ISmartConLogger`

### H.2 P/Invoke GetCursorPos — fallback

**Файл:** `PipeConnectEditorView.xaml.cs`

**Проблема:** `GetCursorPos` не работает в RDP/VM.

**Решение:** Обернуть в try-catch:
```csharp
try
{
    GetCursorPos(out var point);
    // позиционирование рядом с курсором
}
catch
{
    WindowStartupLocation = WindowStartupLocation.CenterScreen;
}
```

### H.3 SmartCon.UI — определить судьбу

Проект содержит 1 конвертер. Два варианта:

**Вариант A — Расширить:**
- Общие стили (Material Design или Fluent)
- `ResourceDictionary` с общими шаблонами
- Базовый класс `DialogWindow`

**Вариант B — Объединить:**
- Перенести конвертер в `SmartCon.PipeConnect`
- Удалить проект `SmartCon.UI`
- Обновить references

Рекомендация: Вариант B, если не планируется второй WPF-модуль. Вариант A — если будут новые модули с UI.

### H.4 Локализация (опционально)

Вынести UI-строки в `.resx`:
- `Resources/Strings.ru-RU.resx`
- `Resources/Strings.en-US.resx`

Это даст:
- Возможность локализации
- Единое место для изменения текстов
- Проще искать строки по коду

### Критерии приёмки

- [ ] `ISmartConLogger` интерфейс + DI-регистрация
- [ ] Static facade backward-compatible
- [ ] GetCursorPos с fallback
- [ ] Решение по SmartCon.UI принято и реализовано
- [ ] (Опционально) .resx файлы созданы

---

## Итоговая таблица фаз

| Фаза | Название | Зависит от | Дней | Приоритет |
|---|---|---|---|---|
| **A** | Устранение блокирующих проблем | — | 2 | P0 |
| **B** | Реструктуризация ViewModel | A | 3 | P0 |
| **C** | Устранение Service Locator | B | 2 | P1 |
| **D** | Чистота Core (убрать RevitAPI) | B | 3 | P1 |
| **E** | Усиление тестирования | B, C, D | 3 | P1 |
| **F** | Инфраструктура (CI/CD, анализаторы) | A, B | 2 | P2 |
| **G** | Производительность и устойчивость | B, C | 2 | P2 |
| **H** | UI/UX и полировка | B, E | 2 | P3 |
| | **Итого** | | **19 дней** | |

---

## Риски и ограничения

| Риск | Вероятность | Влияние | Митигация |
|---|---|---|---|
| Рефакторинг ViewModel ломает XAML-биндинги | Средняя | Высокое | Manual smoke-test после каждого шага |
| Миграция ElementId→long ломает Revit-код | Низкая | Высокое | Поэтапная миграция +编译测试 |
| Controllers слишком сильно связаны с ViewModel | Средняя | Среднее | Чёткие интерфейсы, event-based коммуникация |
| Performance regression после рефакторинга | Низкая | Среднее | Замеры до/после для критичных операций |
| Revit API version compatibility (2025 vs 2026) | Низкая | Низкое | Параметризованный RevitApiPath |

---

## Рекомендации для презентации

1. **Начать с сильных сторон:** Clean Architecture, ADR, инварианты, 208 тестов
2. **Показать график фаз** — итеративный подход, нет "большого взрыва"
3. **Подчеркнуть:** Revit-слой без тестов — это осознанное ограничение платформы, компенсируется тонким адаптерным слоем
4. **Акцент на Фазе B** — это самый заметный дефект, его решение демонстрирует зрелость
5. **Фаза D** — показывает стратегическое мышление (подготовка к Revit 2026+)
6. **19 дней** — реалистичная оценка для всего плана, но можно остановиться после любой фазы
