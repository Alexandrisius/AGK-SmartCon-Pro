# Правило зависимостей (Dependency Rule)

> Загружать: **ВСЕГДА**. Это фундаментальное архитектурное правило.

## Диаграмма

```
                    SmartCon.Core
              (RevitAPI.dll compile-time only)
                 /    |    |    \     \        \
                /     |    |     \     \        \
          Revit    UI   App  PipeConnect  ProjectManagement  Tests
```

**Стрелка «зависит от» направлена вверх.** Все проекты зависят от Core. Core не знает ни о ком.

## Матрица зависимостей

| Проект | Core | Revit | UI | App | PipeConnect | ProjectManagement | FamilyManager |
|---|---|---|---|---|---|---|---|
| **Core** | — | — | — | — | — | — | — |
| **Revit** | да | — | — | — | — | — | — |
| **UI** | да | — | — | — | — | — | — |
| **App** | да | да | да | — | да | да | да |
| **PipeConnect** | да | — | да | — | — | — | — |
| **ProjectManagement** | да | — | да | — | — | — | — |
| **FamilyManager** | да | — | да | — | — | — | — |
| **Tests** | да | — | — | — | да | да | да |
| **IntegrationTests** | да | да | — | — | — | — | да* |

\* IntegrationTests → FamilyManager: только для оркестрационных тестов
internal stale-сервисов в реальном Revit (2026-08); WPF-типы модуля запрещены.

## Карта проектов

> Бывший `solution-structure.md` (удалён 2026-09, #260): по-файловые деревья
> гнили и вводили в заблуждение — ориентируйся по таблице и реальной файловой
> системе; детали модуля — в его README (`docs/<module>/README.md`).

| Проект | Назначение | Зависит от |
|---|---|---|
| **SmartCon.Core** | Чистый C#: модели, интерфейсы, алгоритмы, FormulaSolver. Запрет: вызовы Revit API, `using System.Windows` | — |
| **SmartCon.Revit** | Реализации интерфейсов Core через Revit API (единственный проект с прямой зависимостью от `Autodesk.Revit.DB`) | Core |
| **SmartCon.UI** | Общая WPF-библиотека: тема, стили, конвертеры, локализация (`TranslationSource`/`LocExtension`), behaviors | Core |
| **SmartCon.App** | Точка входа: `IExternalApplication`, Ribbon, DI (`ServiceRegistrar`), ExternalEvents, диагностика загрузки | Core, Revit, UI, все модули |
| **SmartCon.PipeConnect** | Модуль PipeConnect: Commands / ViewModels / Views / Services | Core, UI |
| **SmartCon.ProjectManagement** | Модуль Share Project (ISO 19650): та же структура папок | Core, UI |
| **SmartCon.FamilyManager** | Модуль FamilyManager (dockable panel, SQLite-каталог): Commands / Events / ViewModels / Views / Behaviors / Services (`LocalCatalog/`, `Stale/`, `Actualization/`, `Routing/`, `Geometry/`, `Import/`, `Migrations/`, `Validation/`) | Core, UI |
| **SmartCon.Dependencies** | net48-only ILRepack-хост сторонних зависимостей (ADR-051), листовой | — (только NuGet) |
| **SmartCon.Tests** | Unit + ViewModel тесты (xUnit + Moq), пиннут на net8.0-windows | Core, модули |
| **SmartCon.IntegrationTests** | TUnit-тесты внутри реального Revit (net48 / net8 / net10) | Core, Revit, FamilyManager (оркестрационные) |
| **SmartCon.Updater** | Standalone .NET 8 updater: применяет pending update при закрытии Revit | — |

## Конвенции файлов и папок

- Модули (PipeConnect / ProjectManagement / FamilyManager) держат структуру
  `Commands/ ViewModels/ Views/ Services/`; SmartCon.Revit — по зонам
  ответственности (`Transactions/`, `Selection/`, `Storage/`, `FamilyManager/`, …).
- Интерфейсы и модели — в SmartCon.Core; реализации Revit API — в SmartCon.Revit.
- ViewModel-базовые классы и команды — CommunityToolkit.Mvvm
  (`ObservableObject`, `[ObservableProperty]`, `[RelayCommand]`).
- Размер `.cs`-файлов: **цель ≤500 строк, жёсткий лимит 600** (#260). Больше —
  partial-разбивка `Class.Topic.cs` по зонам ответственности; поля с
  инициализаторами, primary constructor и базовые типы — в ядровом файле;
  `#if`-блоки переносятся только целиком. Исключения — data-словари локализации
  и неделимые single-методы (см. #260).

### SmartCon.IntegrationTests (особая роль)

`SmartCon.IntegrationTests` — единственный проект, которому **разрешена** ссылка
на `SmartCon.Revit`: его назначение — тестировать границу SmartCon ↔ Revit API
внутри реального процесса Revit (Nice3point.TUnit.Revit, см.
`.agents/skills/smartcon-testing/references/integration-testing.md`).
Ограничения: без UI/App/модулей-ViewModels; типы RevitAPIUI в тестах
запрещены (тест-хост без UI-сессии — FileLoadException дестабилизирует сессию).
**Исключение (2026-08):** разрешена ссылка и на `SmartCon.FamilyManager` —
оркестрационные тесты stale-update (`StaleUpdaterOrchestrationTests`,
`StaleCheckContentFallbackTests`) прогоняют настоящие internal-сервисы модуля
(`StaleFamilyUpdater`, `StaleDetector`) внутри реального Revit; WPF-типы
модуля по-прежнему запрещены (тот же краш-хост риск).

### SmartCon.Dependencies (net48 only, ADR-051)

`SmartCon.Dependencies` — **листовой** хост для ILRepack-merge сторонних NuGet-зависимостей.
Правила:

- На него ссылаются (`ProjectReference`) все проекты **только** при `'$(TargetFramework)' == 'net48'`
  (Core, Revit, UI, App, PipeConnect, ProjectManagement, FamilyManager). На net8.0-windows
  ссылок нет — там те же пакеты подключаются напрямую, а изоляцию даёт ALC.
- Он сам **не зависит** ни от одного проекта solution (только NuGet).
- Запрещено добавлять в него ссылки на проекты SmartCon — иначе их типы тоже уедут в merge.
- Транзитивные пути сшитых пакетов (HelixToolkit → CommunityToolkit.*, Roslyn → CodePages и т.д.)
  обрезаны централизованно в `src/Directory.Build.targets` (`ExcludeAssets`), иначе CS0433.

## Жёсткие запреты

1. **Core -> Revit:** `SmartCon.Core` НЕ ссылается на `SmartCon.Revit`. Core ссылается на `RevitAPI.dll` как compile-time reference (CopyLocal=false) только для типов-carriers (`ElementId`, `XYZ`, и т.д.). Core **не вызывает** методы Revit API. См. I-09.
2. **Core -> UI:** `SmartCon.Core` НЕ ссылается на `SmartCon.UI`. Core не содержит `using System.Windows`.
3. **PipeConnect -> Revit:** `SmartCon.PipeConnect` НЕ ссылается на `SmartCon.Revit` напрямую. Вся работа с Revit API — через интерфейсы Core, реализованные в Revit. PipeConnect ссылается на RevitAPI.dll / RevitAPIUI.dll (CopyLocal=false) для `IExternalCommand` и `[Transaction]`.
4. **UI -> Revit:** `SmartCon.UI` НЕ ссылается на `SmartCon.Revit`.
5. **ProjectManagement -> Revit:** `SmartCon.ProjectManagement` НЕ ссылается на `SmartCon.Revit` напрямую. Вся работа с Revit API — через интерфейсы Core. ProjectManagement ссылается на RevitAPI.dll / RevitAPIUI.dll (CopyLocal=false) для `IExternalCommand` и `[Transaction]`.
6. **FamilyManager -> Revit:** `SmartCon.FamilyManager` НЕ ссылается на `SmartCon.Revit` напрямую. Вся работа с Revit API — через интерфейсы Core. FamilyManager ссылается на RevitAPI.dll / RevitAPIUI.dll (CopyLocal=false) для `IExternalCommand` и `[Transaction]`.

## Как это работает

- **Интерфейсы** объявлены в `SmartCon.Core/Services/Interfaces/`
- **Реализации** живут в `SmartCon.Revit/`
- **DI-контейнер** в `SmartCon.App/DI/ServiceRegistrar.cs` связывает интерфейсы с реализациями
- **PipeConnect** получает зависимости через конструктор (Constructor Injection)

```csharp
// SmartCon.PipeConnect получает интерфейсы, не зная о Revit
public class PipeConnectEditorViewModel
{
    public PipeConnectEditorViewModel(
        ITransactionService transactionService,    // реализация в Revit
        IElementSelectionService selectionService,  // реализация в Revit
        IFittingMapper fittingMapper,              // реализация в Core
        IRevitContext revitContext)                 // реализация в Revit
    { ... }
}
```

## Проверка при коммите

Перед каждым коммитом убедиться:
- `SmartCon.Core/*.cs` не содержит **вызовов** Revit API (I-09). `using Autodesk.Revit.DB` допускается для типов-carriers.
- `SmartCon.Core/*.cs` не содержит `using System.Windows`
- `.csproj` файлы не содержат запрещённых ProjectReference
