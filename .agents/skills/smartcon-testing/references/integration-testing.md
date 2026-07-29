# Integration Testing — SmartCon.IntegrationTests (Nice3point.TUnit.Revit)

> **Статус: ВНЕДРЕНО (2026-07-29).** Проект `src/SmartCon.IntegrationTests` —
> интеграционные тесты, выполняемые **внутри реального процесса Revit**.
> Это решает главное ограничение юнит-тестов: настоящие `Document`, `Element`,
> `Connector`, `Transaction`, `FilteredElementCollector` — всё работает.

## Как это работает

- Фреймворк: **TUnit** (Microsoft.Testing.Platform, source-generated) +
  пакет **Nice3point.TUnit.Revit** (`$(RevitVersion).*`, per-Revit-год, как RevitAPI).
- При запуске инжектор (`Nice3point.Revit.Injector` + PolyHook2) стартует
  установленный Revit нужного года и маршалит каждый тест на его единственный
  API-поток через `RevitThreadExecutor` (assembly-level в `TestsConfiguration.cs`).
- Тест наследует `RevitApiTest` → свойство `Application` (DB-уровень).
- Хуки `[Before(Test)]`/`[After(Test)]`, трогающие Revit, несут `[HookExecutor<RevitThreadExecutor>]`.

## Запуск

```bash
# Revit 2025 (net8.0-windows)
dotnet run --project src/SmartCon.IntegrationTests/SmartCon.IntegrationTests.csproj -c Debug.R25 --framework net8.0-windows

# Revit 2021 (net48)
dotnet run --project src/SmartCon.IntegrationTests/SmartCon.IntegrationTests.csproj -c Debug.R21 --framework net48
```

- `--framework` **обязателен** для `dotnet run` (иначе «проект для нескольких платформ»).
- Требуется установленный лицензионный Revit соответствующего года.
- Прогон запускает реальный процесс Revit (~30–60 сек на сессию).
- `dotnet test -c Debug.R25` тоже работает (TestingPlatformDotnetTestSupport=true).

## Жёсткие правила (нарушение = инфраструктурные сбои)

1. **НИКАКИХ инициализаторов полей Revit-типов** (`private Document _doc = null!;`,
   `static readonly XYZ P = new(...)`) — TUnit 1.33+ выполняет инициализаторы
   полей ДО инъекции Revit; преждевременная загрузка RevitAPI ломает инжектор:
   `InvalidOperationException: Attempted to write protected memory` в
   `Injector.InjectApplication()` (Nice3point/RevitUnit#78). Паттерн:
   nullable-поля + `private Document Doc => _document!;`, значения — в `[Before(Test)]`,
   Revit-значения — ленивые свойства (`private static XYZ Joint => new(10, 0, 0);`).
2. **`Document.Regenerate()` — только внутри открытой транзакции** (в т.ч. в seed-хуках).
3. **Каждый открытый документ закрывается** в `[After(Test)]` — `Close(false)`.
4. **Шаблон зависит от машины** — `NewProjectDocument` использует дефолтный шаблон
   из настроек Revit. Отсутствующие типы (PipeType/WallType/ViewFamilyType) →
   `Skip.Test(...)`, не падение (см. `ModelSeed.Has*`).
5. **RevitAPIUI = немедленный краш сессии.** Тест-хост не имеет UI-сессии:
   первое обращение к любому типу RevitAPIUI (`ISelectionFilter`, `UIApplication`,
   `UIDocument`, TaskDialog) даёт `FileLoadException: Could not load file or
   assembly 'RevitAPIUI' (0x8007045A)` — и, что хуже, **дестабилизирует процесс**:
   последующие тесты падают с нативным AccessViolationException в случайных местах
   (`Transaction.Commit`, `NewProjectDocument`). Проверено 2026-07-29:
   `FreeConnectorFilter` (реализует `ISelectionFilter`) — удалён из сьюта.
   **Правило:** SUT, трогающий RevitAPIUI, в этом хосте НЕТЕСТИРУЕМ — рефакторить
   логику на DB-уровень (как `FreeConnectorFilter.AllowElement` — сама проверка
   тестируема, но класс грузит RevitAPIUI при JIT) или оставить ручной тест.
6. **R19/R20 не поддерживаются** — пакет публикуется только для Revit 2021+.
   В sln у проекта для `*.R19` конфигураций нет `Build.0` (исключён из сборки),
   а csproj выдаёт явную ошибку при прямой сборке.
7. **Храни `ElementId`, не `Element`** между транзакциями (I-05 действует и здесь).
8. **Не используй типы merge-списка ADR-051 напрямую** (`System.Text.Json`,
   `CommunityToolkit.Mvvm`, `Microsoft.Extensions.DependencyInjection`) в коде
   IntegrationTests на net48: в output лежат и loose `System.Text.Json.dll` 9.0.0,
   и `SmartCon.Dependencies.dll` с публичными сшитыми типами — прямое обращение
   даст CS0433. Тестируемый код (`SmartCon.Core`/`SmartCon.Revit`) это не касается.
9. **`[assembly: NotInParallel]` обязателен** (уже в `TestsConfiguration.cs`).
   TUnit по умолчанию параллелит тесты, но весь сьют живёт в ОДНОМ процессе Revit:
   async-тесты чередуются на его потоке, и гонка «commit ExtensibleStorage одного
   документа параллельно с операциями над другими» давала нативный AVE
   (воспроизведено 2026-07-29). Параллелизм тут и бессмысленен: Revit API
   однопоточен (I-01), ускорения всё равно нет.

## Структура проекта

```
src/SmartCon.IntegrationTests/
├── SmartCon.IntegrationTests.csproj   # Exe, MTP, TFM из конфигурации (R21→net48, R25→net8)
├── TestsConfiguration.cs              # TestExecutor<RevitThreadExecutor> + NotInParallel
├── Support/
│   ├── StubRevitContext.cs            # IRevitContext поверх тестового Document
│   ├── ModelSeed.cs                   # сидирование: уровни/стены/трубы/виды/ведомости + skip-гарды
│   ├── SampleFiles.cs                 # путь к Samples установленного Revit (по Application.VersionNumber)
│   ├── PipeModelFixture.cs            # базовая фикстура: 3 коллинеарные трубы + ConnectPipesAt
│   └── ProjectViewsFixture.cs         # базовая фикстура: виды/шаблон/ведомость для Share-тестов
├── RevitEnvironmentTests.cs           # canary: инъекция + NewProjectDocument
├── TransactionServiceTests.cs         # RevitTransactionService (I-03): commit/rollback/rethrow
├── PipeConnect/                       # модуль PipeConnect
│   ├── ConnectorWrapperTests.cs       # ToProxy: геометрия, IsFree, DescribeConnector
│   ├── TransformServiceTests.cs       # MoveElement + zero-offset guard
│   ├── ConnectorServiceTests.cs       # Connect/Disconnect roundtrip, порядок (#163)
│   ├── ElementChainIteratorTests.cs   # BFS-граф сети труб, stopAt, chain ends
│   ├── FittingMappingRepositoryTests.cs # ExtensibleStorage roundtrip (ADR-012, I-13)
│   ├── ParameterResolverTests.cs      # RBS_PIPE_DIAMETER_PARAM резолв/запись
│   └── FamilyConnectorServiceTests.cs # CTC-запись + полный цикл через ToProxy (ADR-002)
├── FamilyManager/                     # модуль FamilyManager
│   ├── FamilyVersionStoreTests.cs     # ES-маркер SmartCon_FamilyVersion_v1 (ADR-030)
│   ├── FamilyLoadServiceTests.cs      # LoadFamilyAsync: Loaded → Current
│   ├── FamilyDataExtractionServiceTests.cs # извлечение типов из .rfa + graceful failure
│   └── FamilySnapshotExtractorTests.cs  # FHV3: детерминизм content hash (ADR-056)
└── ProjectManagement/                 # модуль Share Project
    ├── ModelPurgeServiceTests.cs      # Purge: keepViewNames, флаги категорий (#176)
    ├── ViewRepositoryTests.cs         # без шаблонов, сортировка по имени
    └── ShareProjectSettingsRepositoryTests.cs # ES roundtrip настроек
```

## Gotchas, найденные на практике (2026-07-29)

| Симптом | Причина | Решение |
|---|---|---|
| AVE в `Transaction.Commit`/`NewProjectDocument` в полном прогоне, изолированно зелёно | TUnit параллелит тесты в одном процессе Revit | `[assembly: NotInParallel]` (правило 9) |
| `FileLoadException: RevitAPIUI (0x8007045A)` + AVE у последующих тестов | SUT трогает RevitAPIUI (`ISelectionFilter`) | Такой SUT нетестируем в хосте (правило 5) |
| `View.IsTemplate = true` не компилируется | Сеттер read-only в Revit 2025+ | `viewPlan.CreateViewTemplate()` |
| В шаблоне нет Title Block (ViewSheet не посеять) | Дефолтный шаблон урезан | `ModelSeed.FindTitleBlockFamilyFile` — fallback в `C:\ProgramData\Autodesk\RVT {ver}\Libraries` + `doc.LoadFamily` |
| `string.Contains(s, StringComparison)` — ошибка на net48 | API появилось в .NET Core 2.1 | `string.Equals(a, b, StringComparison)` — кросс-TFM безопасно (CA2249 на net8 против IndexOf) |
| `FamilyLoadResult.IsSuccess` не компилируется | Свойство называется `Success` | Проверять точные имена моделей по исходникам |
| Запуск подмножества тестов | — | `dotnet run ... -- --treenode-filter "/*/*ClassName*/*/*"` |

## Доказанная ценность

Сьют поймал первый production-баг в день внедрения: **#176** — флаги
`PurgeSheets=false`/`PurgeSchedules=false` не сохраняли листы и ведомости
(`ViewSheet`/`ViewSchedule` наследуются от `View` и подметались общим свипом
pass 2 в `RevitModelPurgeService`). Юнит-тесты это не видели: нужен реальный
коллектор по реальной иерархии классов Revit.

## Что покрывать интеграционными тестами (decision flow)

```
SUT чистая логика (нет Revit-типов)?        → юнит-тест (SmartCon.Tests, быстро)
SUT обёрнут в интерфейс без Revit-типов?    → юнит-тест с Moq/фейком
SUT трогает Document/Element/Connector/
  Transaction/FilteredElementCollector?     → IntegrationTests (этот проект)
WPF UI / диалоги / picking?                 → ручной тест + валидация логов (см. ниже)
```

Интеграционные тесты — для **границы** SmartCon ↔ Revit API (`SmartCon.Revit`):
`RevitTransactionService`, `ConnectorWrapper`, `RevitTransformService`,
`ElementChainIterator`, `RevitParameterResolver`, seed-and-verify сценарии PipeConnect.

## MSBuild-интеграция (как устроено)

- Версия пакета: `VersionOverride="$(RevitVersion).*"` в `src/Directory.Build.targets`
  (зеркало паттерна RevitAPI; fallback 2021.*/2025.*). `PackageVersion` в
  `Directory.Packages.props` НЕ добавлять.
- net48: `EnableTUnitPolyfills=false` — неявный polyfill TUnit багован на .NET
  Framework (thomhurst/TUnit#3731); `ModuleInitializerAttribute` даёт PolySharp.
- net48: TUnit.Core требует `System.Text.Json >= 9.0.0`, production-пин — 8.0.6
  (ADR-051). Проект исключён из обрезки merge-списка в `Directory.Build.targets`
  и пинит STJ/Encodings.Web 9.0.0 через `VersionOverride` в csproj.
- CI (`.github/workflows/build.yml`): compile-only шаг (на runner нет Revit).
  Прогон интеграционных тестов — локальный, перед фиксацией границы Revit-слоя.

## Production log validation (остаётся для UI/E2E)

Интеграционные тесты НЕ покрывают WPF UI, picking, диалоги. Для них —
прежний паттерн: ручной тест в Revit + валидация `%APPDATA%\AGK\SmartCon\smartcon.log`:

1. Структурное логирование `SmartConLogger.BeginScope(...)` в SUT.
2. Ручной прогон сценария в Revit 2024/2025.
3. Проверка лога: целостные цепочки `OpId`, `[DBG]`-переходы состояний,
   `[WRN]` с `[Action: ...]`, отсутствие `[ERR]`.

Пример (2026-06-19): `docs/testing/stale-detection-coverage-gaps.md`.

## Отклонённые альтернативы

| Framework | Почему не выбран |
|---|---|
| ricaun.RevitTest (NUnit) | Жизнеспособен (2019–2025, CI/Design Automation), но Nice3point-стек уже используется в проекте (Revit.Api, Toolkit), единый вендор = меньше трения. Может закрыть R19 при необходимости в будущем. |
| RevitXunit.TestAdapter | Только 2025+ (net8.0) — не закрывает net48-версии R21–R24. |
| Speckle xUnitRevit | Legacy (2021–2024), ручная установка, без CI. |
| DynamoDS RevitTestFramework | Заброшен (6+ лет). |
