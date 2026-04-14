# SmartCon — Master Refactoring Plan v2

Полная дорожная карта рефакторинга для доведения кодовой базы до open-source презентационного качества с максимальной защитой от регрессий.

---

## Ключевые решения

**Revit API в ViewModel — это нормально.** Nice3point (автор RevitTemplates):
> *"The API calls themselves, transactions, collectors, can be in the VM, this is normal, but if the VM grows and contains code responsible not only for Binding to View, it is better to transfer it to separate classes"*

Поэтому: **не создаём абстракции ради абстракций**. Цель — разбить крупные файлы на логические классы-помощники с единой ответственностью. Revit API вызовы из ViewModel допустимы, но логика должна быть в отдельных классах.

---

## Текущее состояние — аудит

### Все файлы >250 строк (кандидаты на декомпозицию)

| Файл | Строк | Проблема |
|---|---|---|
| `PipeConnectEditorViewModel.cs` | **3 746** | God Object: 97 методов, 12 зависимостей, 6+ ответственностей |
| `RevitLookupTableService.cs` | **1 062** | Монолит: EditFamily boilerplate + CSV парсинг + формулы |
| `RevitDynamicSizeResolver.cs` | **805** | Дублирует paramSnapshot/formulaByName с LookupTableService |
| `RevitParameterResolver.cs` | **518** | Содержит raw `SubTransaction`, TODO I-03 |
| `PipeConnectCommand.cs` | **419** | Монолит: S1-S6 + анализ + создание VM |
| `RevitFamilyConnectorService.cs` | **363** | Приемлемо, но стоит проверить |
| `FormulaSolver.cs` | **283** | ОК (сложный алгоритм) |
| `Parser.cs` | **309** | ОК (парсер) |
| `FamilyParameterAnalyzer.cs` | **252** | Приемлемо |

### Найденные дублирования

| Дублированный код | Где встречается | Раз |
|---|---|---|
| **Best-size-match алгоритм** (`minTotalDelta + weightedDelta`) | `LoadDynamicSizes`, `RefreshAutoSelectSize`, `FindBestOptionForRadius` | **3** |
| **Insert fitting + align + move dynamic** | `InsertFittingSilent`, `InsertFittingSilentNoDynamicAdjust`, `InsertReducerSilent`, `InsertReducer` | **4** |
| **Reassign CTC** (show dialog + update store + reorient) | `ReassignFittingCtc`, `ReassignReducerCtc` | **2** |
| **GuessCtc** (allDefined check + spatial sort + store.Set) | `GuessCtcForFitting`, `GuessCtcForReducer` | **2** |
| **paramSnapshot + formulaByName pre-cache** | `RevitLookupTableService.GetAllSizeRows`, `RevitDynamicSizeResolver.ExtractSizesFromFamily` | **2** |
| **EditFamily → FamilySizeTableManager boilerplate** | `RevitLookupTableService` (3 methods), `RevitDynamicSizeResolver` | **4** |
| **Position correction** (refresh → compute offset → move → regen) | ViewModel: 12+ мест | **12+** |
| **Magic number `304.8`** (ft→mm conversion) | 12 файлов, **134 вхождения** | — |
| **ConnectorManager pattern** (`elem switch { FI => ..., MEPCurve => ... }`) | ViewModel, chain helpers, WarmDeps | **4+** |

### Прочие проблемы

- **578 вызовов SmartConLogger** — слишком много, многие дублируют информацию
- **23 пустых catch-блока** — проглоченные исключения
- **12/32 публичных типов** без XML-документации
- **Нет .editorconfig** — нет стандарта кодирования
- **Нет корневого README.md** — нечего показать при открытии репозитория
- **5 TODO** в коде
- **Все комментарии и логи на русском** — нужен перевод на EN

---

## Стратегия защиты от регрессий

> **Приоритет #1 — не сломать рабочий код.**

1. **Каждый шаг = один коммит** с зелёными тестами
2. **Механические рефакторинги первыми** (Extract Method → Extract Class) — не меняют логику
3. **IDE-рефакторинг** (Move, Rename, Extract) вместо ручного переписывания
4. **Тесты ДО изменения** — если для куска логики нет теста, пишем его сначала
5. **Только Core-логика тестируется** (unit tests). Revit-слой не тестируется (нет runtime)
6. **Smoke-test после каждой фазы** — ручная проверка в Revit: straight connect, fitting, reducer, chain
7. **Дублирование удаляем поэтапно**: сначала создаём единый метод, потом по одному заменяем callsites

---

## Phase 0 — Safety Net

- [ ] **0.1** `dotnet test` → зафиксировать baseline (525+ pass)
- [ ] **0.2** Git branch `refactor/master-plan`
- [ ] **0.3** Добавить `.editorconfig` (C# conventions, naming, indentation)
- [ ] **0.4** `Directory.Build.props`: `<EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>`
- [ ] **0.5** Исправить новые warnings от анализаторов (trivial fixes / suppressions)
- [ ] **0.6** `dotnet test` → все тесты зелёные

---

## Phase 1 — Ликвидация дублирования (вся кодовая база)

**Цель:** Создать единственные источники правды для повторяющихся паттернов. Это ПРЕДШЕСТВУЕТ декомпозиции — после дедупликации файлы станут меньше естественным образом.

### 1.1 — Константы единиц и допусков
Создать `SmartCon.Core/Constants.cs`:
```csharp
public static class Units {
    public const double FeetToMm = 304.8;
    public const double MmToFeet = 1.0 / 304.8;
}
public static class Tolerance {
    public const double RadiusFt = 1e-5;
    public const double PositionFt = 1e-4;  // ~0.03mm
    public const double AngleDeg = 1.0;
}
```
Заменить все 134 вхождения `304.8` и magic numbers.

### 1.2 — `BestSizeMatcher` (дедупликация алгоритма подбора размеров)
Создать `SmartCon.Core/Math/BestSizeMatcher.cs`:
- Единый `FindClosest(IReadOnlyList<FamilySizeOption> candidates, double targetRadius, int targetConnIdx, IReadOnlyDictionary<int, double> currentRadii)` → `FamilySizeOption?`
- Заменить 3 copy-paste из ViewModel
- **Написать тесты ДО рефакторинга** (из существующего поведения)

### 1.3 — `PositionCorrector` (дедупликация refresh→offset→move→regen)
Создать утилитный метод в `SmartCon.PipeConnect/Services/PositionCorrector.cs`:
```csharp
static void CorrectPosition(Document doc, IConnectorService connSvc, 
    ITransformService transformSvc, ElementId elemId, int connIdx, Vec3 targetOrigin);
```
Заменить 12+ мест с одинаковым паттерном.

### 1.4 — Унификация Insert Fitting/Reducer
`InsertFittingSilent` и `InsertFittingSilentNoDynamicAdjust` отличаются одним параметром `adjustDynamicToFit`. Объединить в один метод с параметром. То же для `InsertReducerSilent` vs `InsertReducer` — вынести общую логику.

### 1.5 — Унификация GuessCtc
`GuessCtcForFitting` и `GuessCtcForReducer` почти идентичны (отличие: `CtcGuesser.GuessAdapterCtc` vs `CtcGuesser.GuessReducerCtc`). Объединить в `GuessCtcForElement(ElementId, bool isReducer)`.

### 1.6 — Унификация ReassignCtc
`ReassignFittingCtc` и `ReassignReducerCtc` — 80% общего кода. Вынести `ReassignElementCtc(ElementId elemId, ...)`.

### 1.7 — `EditFamilySession` (дедупликация EditFamily boilerplate)
В `RevitLookupTableService` и `RevitDynamicSizeResolver` повторяется:
```
if (doc.IsModifiable) return; 
var family = instance.Symbol?.Family;
familyDoc = doc.EditFamily(family);
try { ... } finally { familyDoc?.Close(false); }
```
Создать helper: `EditFamilySession.Run<T>(doc, instance, Func<Document, T>)` в `SmartCon.Revit/Extensions/`.

### 1.8 — `ParamSnapshotBuilder` (дедупликация pre-cache)
`paramSnapshot` + `formulaByName` строятся одинаково в `RevitLookupTableService` и `RevitDynamicSizeResolver`. Вынести в `FamilyParameterSnapshot.Build(FamilyManager)`.

**Тесты:** После каждого шага → `dotnet test`. Для Core-логики (1.2) — новые unit tests.

---

## Phase 2 — Декомпозиция `PipeConnectEditorViewModel` (3 746 → ~500 строк)

> После Phase 1 файл уже значительно уменьшится. Здесь выносим оставшиеся группы ответственности.

### 2.1 — Extract `ChainOperationHandler` (≈660 строк)
Методы: `IncrementChainDepth`, `DecrementChainDepth`, `ConnectAllChain`, `WarmDepsForLevel`, `CaptureSnapshot`, `FindEdgeToParent`, `IsInCurrentChain`, `UpdateChainUI`

Куда: `SmartCon.PipeConnect/Services/ChainOperationHandler.cs`

Это самый большой непрерывный блок. Зависимости: `_connSvc`, `_paramResolver`, `_transformSvc`, `_fittingInsertSvc`, `_groupSession`, `_snapshotStore`, `_chainGraph`.
Передаём через конструктор или как параметры метода.

### 2.2 — Extract `FittingCtcManager` (≈500 строк)
Методы: `EnsureFittingCtcForInsert`, `EnsureReducerCtcForInsert`, `IsFittingCtcDefined`, `BuildConnectorItems`, `ApplyFittingCtcToFamily`, `BuildSpatialCtcMap`, `SetDrivingFamilyParameter`, `GetConnectorParamName`, `FlushVirtualCtcToFamilies`, `FindFamilySymbol`, `FamilyLoadOptions`

Куда: `SmartCon.PipeConnect/Services/FittingCtcManager.cs`

Этот блок содержит `EditFamily`, `FilteredElementCollector`, `new Transaction` — это **нормально** для Revit-плагина. Выносим ради читаемости, не ради "чистоты слоёв".

### 2.3 — Extract `ConnectExecutor` (≈250 строк)
Методы: `ValidateAndFixBeforeConnect`, `Connect` (основная логика ConnectTo)

Куда: `SmartCon.PipeConnect/Services/ConnectExecutor.cs`

### 2.4 — Extract `PipeConnectDiagnostics` (≈50 строк)
Метод: `LogConnectorState`

Куда: `SmartCon.PipeConnect/Services/PipeConnectDiagnostics.cs`

### 2.5 — Финальная чистка ViewModel
После 2.1–2.4 ViewModel содержит:
- `[ObservableProperty]` поля (UI state)
- `[RelayCommand]` обёртки (1–5 строк каждая, делегация)
- `Init()` — вызывает handler-ы
- `OnSelected*Changed` — partial methods
- Конструктор

**Целевой размер: ~400–500 строк.**

**Тесты:** `dotnet test` после каждого шага.

---

## Phase 3 — Декомпозиция `PipeConnectCommand` (419 → ~150 строк)

- [ ] **3.1** Extract `PipeConnectSessionBuilder` — логика S1–S5 (анализ, подбор фитингов, создание `PipeConnectSessionContext`)
- [ ] **3.2** Extract `ParameterResolutionPlan` analysis в отдельный метод/класс
- [ ] **3.3** Command = select → build session → create VM → show dialog

---

## Phase 4 — Декомпозиция Revit-сервисов

### 4.1 — `RevitLookupTableService` (1 062 → ~500 строк)
После 1.7 (EditFamilySession) и 1.8 (ParamSnapshotBuilder):
- Extract `LookupTableCsvParser` — чистая CSV-логика → `SmartCon.Core/Math/LookupTableCsvParser.cs`
- Extract `LookupColumnResolver` — `FindColumnIndex`, `FindQueryParamsForTable` → `SmartCon.Revit/Parameters/LookupColumnResolver.cs`

### 4.2 — `RevitDynamicSizeResolver` (805 → ~400 строк)
После 1.7, 1.8:
- Extract `FamilySymbolSizeExtractor` — перебор FamilySymbol → `SmartCon.Revit/Parameters/FamilySymbolSizeExtractor.cs`
- `IntersectRadiusSets` уже хорошая утилита — возможно перенести в Core

### 4.3 — `RevitParameterResolver` (518 → ~400 строк)
- Мигрировать `SubTransaction` → `ITransactionService` (I-03)
- Убрать TODO

---

## Phase 5 — Exception Handling

- [ ] **5.1** Аудит всех 23 catch-блоков
- [ ] **5.2** Классификация:
  - **Допустимо пустые** (7): `File.Delete`, `SmartConLogger`, `Directory.Delete` — добавить `// Intentional: ...` комментарий
  - **Скрывают баги** (8): `catch { return []; }`, `catch { }` в ViewModel — добавить `SmartConLogger.Warn` + тип исключения
  - **Нужен рефакторинг** (8): `catch { /* best-effort */ }` — заменить на `catch (Exception ex) { Log.Debug(ex, ...); }`

---

## Phase 6 — Логирование (578 → ~200 Info+ вызовов)

- [ ] **6.1** Перевести все сообщения логов RU → EN
- [ ] **6.2** Перевести все code comments RU → EN (UI strings остаются на RU)
- [ ] **6.3** Классифицировать уровни: `Debug` (verbose трейс), `Info` (ключевые точки), `Warn`, `Error`
- [ ] **6.4** Удалить/понизить ~60% verbose логов (в основном из ViewModel и Revit-сервисов)
- [ ] **6.5** Стандартизировать формат: `[ClassName.Method] key=value, key2=value2`

---

## Phase 7 — XML-документация

- [ ] **7.1** `<summary>` для всех 12 недокументированных public types
- [ ] **7.2** `<summary>` + `<param>` для всех методов в `SmartCon.Core/Services/Interfaces/`
- [ ] **7.3** `<summary>` для всех моделей в `SmartCon.Core/Models/`
- [ ] **7.4** Перевести существующие XML docs RU → EN
- [ ] **7.5** `<GenerateDocumentationFile>true</GenerateDocumentationFile>` для Core

---

## Phase 8 — Тесты для новой Core-логики

> Revit-слой не тестируется. Только Core и чистые helper-классы.

- [ ] **8.1** Тесты для `BestSizeMatcher` (из Phase 1.2)
- [ ] **8.2** Тесты для `PositionCorrector` если есть чистая логика
- [ ] **8.3** Тесты для `LookupTableCsvParser` (из Phase 4.1)
- [ ] **8.4** Тесты для `Constants` (unit conversion round-trips)
- [ ] **8.5** Организация: `Tests/Core/`, `Tests/PipeConnect/`

---

## Phase 9 — Open Source Presentation

### 9.1 — Root `README.md`
- Описание проекта (что, зачем, для кого)
- Скриншот/GIF PipeConnect в действии
- Архитектурная диаграмма (Mermaid)
- Getting started (clone, prerequisites, build, deploy to Revit)
- Структура проекта
- License

### 9.2 — `CONTRIBUTING.md`
- Code style, branch naming, test requirements

### 9.3 — `LICENSE` (MIT / Apache-2.0 — на выбор)

### 9.4 — `CHANGELOG.md`

### 9.5 — Чистка `docs/`
- Удалить устаревшие планы
- Обновить `docs/README.md`
- Перевести dev docs на EN

---

## Phase 10 — Code Style Polish

- [ ] **10.1** `.editorconfig` enforcement — финальный прогон
- [ ] **10.2** `sealed` на всех не-наследуемых классах
- [ ] **10.3** Primary constructors (C# 12) где уместно
- [ ] **10.4** `file`-scoped types для internal helper-ов
- [ ] **10.5** Collection expressions (`[]`) единообразно
- [ ] **10.6** Удалить unused usings

---

## Phase 11 — Финальная верификация

- [ ] **11.1** `dotnet test` — все тесты зелёные
- [ ] **11.2** Grep: `SmartCon.Core` не вызывает Revit API (I-09)
- [ ] **11.3** Dependency matrix соответствует `docs/architecture/dependency-rule.md`
- [ ] **11.4** Обновить `docs/domain/models.md` и `docs/domain/interfaces.md`
- [ ] **11.5** ADR для ключевых решений декомпозиции
- [ ] **11.6** Ручной smoke-test в Revit: straight connect, fitting, reducer, chain ±, cycle connector, change size

---

## Порядок выполнения и оценки

| Фаза | Риск | Оценка | Зависимости |
|---|---|---|---|
| **0 Safety Net** | Низкий | 1–2ч | — |
| **1 Дедупликация** | **Средний** — меняет сигнатуры | 6–8ч | Phase 0 |
| **2 ViewModel** | **Высокий** — самое крупное изменение | 8–10ч | Phase 1 |
| **3 Command** | Средний | 2ч | Phase 2 |
| **4 Revit services** | Средний | 4–5ч | Phase 1 |
| **5 Exceptions** | Низкий | 1–2ч | Phase 2 |
| **6 Logging** | Низкий | 3–4ч | Phase 2 |
| **7 XML Docs** | Низкий | 2–3ч | Phase 2 |
| **8 Tests** | Низкий | 3–4ч | Phase 1, 4 |
| **9 Presentation** | Низкий | 2–3ч | Любое время |
| **10 Style** | Низкий | 2ч | Phase 2 |
| **11 Verification** | Низкий | 1–2ч | Все фазы |

**Total estimate: ~35–45 часов** pair-programming сессий.

### Рекомендуемые батчи

1. **Batch A (фундамент):** Phase 0 → 1 → 8 (tests for new core logic)
2. **Batch B (декомпозиция):** Phase 2 → 3 → 4
3. **Batch C (полировка):** Phase 5 → 6 → 7
4. **Batch D (presentation):** Phase 9 → 10 → 11

---

## Файлы: до и после

| Файл | Сейчас | Цель | Действие |
|---|---|---|---|
| `PipeConnectEditorViewModel.cs` | 3 746 | ~500 | Разбить на 5+ классов |
| `RevitLookupTableService.cs` | 1 062 | ~500 | Вынести CSV parser + column resolver |
| `RevitDynamicSizeResolver.cs` | 805 | ~400 | Вынести symbol extractor |
| `RevitParameterResolver.cs` | 518 | ~400 | Мигрировать transactions |
| `PipeConnectCommand.cs` | 419 | ~150 | Вынести session builder |

## Новые файлы

| Файл | Проект | Назначение |
|---|---|---|
| `Constants.cs` | Core | `Units.FeetToMm`, `Tolerance.*` |
| `BestSizeMatcher.cs` | Core/Math | Единый алгоритм подбора размера |
| `PositionCorrector.cs` | PipeConnect/Services | Refresh → offset → move → regen |
| `ChainOperationHandler.cs` | PipeConnect/Services | Chain depth ± логика |
| `FittingCtcManager.cs` | PipeConnect/Services | CTC management |
| `ConnectExecutor.cs` | PipeConnect/Services | Validation + ConnectTo |
| `PipeConnectDiagnostics.cs` | PipeConnect/Services | Диагностическое логирование |
| `PipeConnectSessionBuilder.cs` | PipeConnect/Services | S1–S5 из Command |
| `EditFamilySession.cs` | Revit/Extensions | EditFamily boilerplate helper |
| `FamilyParameterSnapshot.cs` | Revit/Parameters | paramSnapshot + formulaByName |
| `LookupTableCsvParser.cs` | Core/Math | Чистая CSV-логика |
| `LookupColumnResolver.cs` | Revit/Parameters | FindColumnIndex logic |
| `FamilySymbolSizeExtractor.cs` | Revit/Parameters | Symbol iteration |
| `.editorconfig` | Root | Code style rules |
| `README.md` | Root | Презентация проекта |
| `CONTRIBUTING.md` | Root | Гайд контрибутора |
| `CHANGELOG.md` | Root | История версий |
