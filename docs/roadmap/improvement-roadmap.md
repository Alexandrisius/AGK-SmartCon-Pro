# SmartCon Improvement Roadmap

> **Внимание:** этот план касается ТОЛЬКО доработок, которые **улучшают** проект согласно
> best practices. Не делаем "работу ради работы" — каждый пункт должен иметь
> обоснование "зачем".

**Дата:** 2026-06-09 (обновлено после C1-C10)
**Аудитор:** AI-агент (Claude M3)
**Ветка:** `feature/logging-improvements` (HEAD: `c54c19d`)

---

## Executive Summary

Проект в **хорошем состоянии**:

| Метрика | Значение |
|---|---|
| C# файлов всего | ~2700 (без obj/) |
| Строк кода | ~96k (без obj/, без тестов) |
| Проектов | 8 (+ updater) |
| Async/await | 746 statements, 393 async Task methods, 2 async void (см. P1) |
| Build configs | 4/4 ✅ (R19/R21/R24/R25) |
| Tests | 1232/1232 ✅ |
| `.GetAwaiter().GetResult()` в production | **9 мест** ⚠️ (см. P1) |
| `lock` statements | 7 — есть где добавить |
| DI service registration | `ServiceRegistrar.cs` (centralized) |
| Skills для AI агентов | 3 + 1 новый `smartcon-logging` |

**Главные находки:**
1. **Deadlock risk в 9 production местах** (`.GetAwaiter().GetResult()` на UI thread) — **критично**, фикс 1-2 дня
2. **`async void` в 2 местах** — потенциальный exception swallowing
3. **DI контейнер не используется в ViewModels** — 393 async methods работают на статических сервисах
4. **Counter pattern** в hot loops внедрён в Phase 0b, но не везде где нужно
5. **Семантические ad-hoc проверки** в нескольких местах — нет `IArgumentGuard` helper

---

## Phase 5 — Async/Threading safety (КРИТИЧНО, 1-2 дня)

**Обоснование:** `.GetAwaiter().GetResult()` на UI thread = deadlock. Документировано в
skill `revit-api-best-practice/async-threading-patterns.md`. ЭТО блокер для production-grade.

### Задача P1.1: Заменить 9 production мест `.GetAwaiter().GetResult()`

**Файлы:**

```powershell
src\SmartCon.App\App.cs                                                              # 2 sites
src\SmartCon.PipeConnect\ViewModels\AboutViewModel.cs                                # 1 site
src\SmartCon.Revit\FamilyManager\SystemFamilyPlacementService.cs                      # 1 site
src\SmartCon.Revit\FamilyManager\FamilyPlacementDropHandler.cs                        # 3 sites
src\SmartCon.Revit\FamilyManager\RevitFamilyPlacementService.cs                        # 2 sites
```

**Паттерн фикса:**

```csharp
// BEFORE (deadlock risk in IExternalEvent callback)
var resolved = _fileResolver.ResolveForLoadAsync(catalogItemId, version, ct).GetAwaiter().GetResult();

// AFTER
var resolved = Task.Run(() =>
    _fileResolver.ResolveForLoadAsync(catalogItemId, version, ct)
).GetAwaiter().GetResult();
```

Или, если метод уже async:

```csharp
// BEFORE
public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
{
    var result = cleanupService.CleanupAfterImportAsync().GetAwaiter().GetResult();
    ...
}

// AFTER
public async Task<Result> ExecuteAsync(ExternalCommandData commandData, ...)
{
    var result = await cleanupService.CleanupAfterImportAsync();
    ...
}
```

**Критерии приёмки:**
- 0 `GetAwaiter().GetResult()` в production (без `Tests\\`)
- 0 deadlock в ручном тесте (EditFamily в 10 раз подряд)
- 4/4 build configs + 1232/1232 tests

**Трудозатраты:** 0.5-1 день (однотипная правка, 9 мест)

### Задача P1.2: Удалить 2 `async void` в `FamilyBatchImportViewModel`

**Файл:** `src\SmartCon.FamilyManager\ViewModels\FamilyBatchImportViewModel.cs`

**Методы:**
- `OnRowPickCategoryRequested(FamilyBatchImportRow row)` — event handler
- `OnRowNameChanged(FamilyBatchImportRow row)` — event handler

**Проблема:** `async void` в event handlers — exception теряется.

**Паттерн фикса:**

```csharp
// BEFORE
public event Action<FamilyBatchImportRow>? RowPickCategoryRequested;
private async void OnRowPickCategoryRequested(FamilyBatchImportRow row) { ... }

// AFTER
public event Func<FamilyBatchImportRow, Task>? RowPickCategoryRequested;
private async Task OnRowPickCategoryRequestedAsync(FamilyBatchImportRow row) { ... }
```

Event signature `Func<T, Task>` — async-friendly. Caller обязан `await`.

**Трудозатраты:** 0.5 дня (проверить все подписки на эти event'ы)

---

## Phase 6 — Counter pattern в hot loops (1-2 дня)

**Обоснование:** `Debug` логи в hot loops (BFS по connector'ам, 100k+ iterations) **без** counter
pattern = allocation pressure. Phase 0b внедрил pattern в `RevitLookupTableService.GetNearestAvailableRadius`
но НЕ во всех hot-loop файлах.

**Hot loop candidates (Debug > 100 calls per operation):**

```powershell
src\SmartCon.Revit\Family\FittingFamilyRepository.cs                  # EditFamily loop
src\SmartCon.Revit\Family\RevitFamilyConnectorService.cs             # ConnectorElement search
src\SmartCon.PipeConnect\Services\ChainOperationHandler.cs            # BFS levels
src\SmartCon.PipeConnect\Services\ConnectExecutor.cs                 # Validate branches
```

**Паттерн:**

```csharp
// BEFORE — 100k Info calls, allocation per call
foreach (var c in allConns)
    SmartConLogger.Debug($"connector: {c.ConnectorIndex} R={c.Radius}");

// AFTER — counter + summary
var logCounter = 0;
foreach (var c in allConns)
{
    if (++logCounter % 1000 == 0)
        SmartConLogger.Debug($"processed {logCounter}/{allConns.Count} connectors");
    // ... heavy work
}
SmartConLogger.Info($"finished processing {allConns.Count} connectors (sample debug every 1000)");
```

**Критерии приёмки:**
- `Info`/`Debug` в hot loops либо (a) в counter block, либо (b) внутри `if (level >= Debug)` guard
- 0 случайных `Debug($"...{complex interpolation}...")` в `for`/`foreach` body без counter

**Трудозатраты:** 1-2 дня (audit + правка)

---

## Phase 7 — DI-готовность ViewModels (3-5 дней)

**Обоснование:** 393 async Task methods, **0** DI-инжекций в ViewModels. Все ViewModel'ы
резолвят сервисы через статические фасады или Service Locator pattern.
Это затрудняет unit-тестирование (исключение — 2 теста, которые через `ISmartConLogger`).

**Что делать:**

1. **P7.1**: Ввести `IConnectorService`/`IParameterResolver`/`IFamilyManagerAwaitableEvent` interfaces
   уже есть — нужно проверить, что **ВСЕ** ViewModel'ы принимают их через конструктор, а не
   резолвят через статические фасады.
2. **P7.2**: `ServiceRegistrar` уже содержит DI регистрации — проверить, что все они
   используются.
3. **P7.3**: Заменить `await Task.Run(() => _service.GetXAsync())` в **ViewModel'ах** на
   `await _service.GetXAsync()` напрямую (DI services уже возвращают `Task`).

**TBD:** Перед началом фазы — провести 1-часовой audit чтобы понять объём.

**Трудозатраты:** 3-5 дней (audit + постепенная миграция)

---

## Phase 8 — Тестовое покрытие (5-10 дней)

**Обоснование:** 101 файл тестов, **14512 строк**. Это **13%** от production кода — здоровый baseline.
НО: проверка качества тестов — каждый ли тест изолирован? использует ли FakeItEasy/NSubstitute?
есть ли integration-тесты с реальным Revit?

**Что делать:**

1. **P8.1**: Проверить что **все** тесты используют Arrange-Act-Assert pattern
2. **P8.2**: Проверить что нет тестов которые требуют Revit host (integration tests должны быть в отдельной сборке)
3. **P8.3**: Проверить покрытие `RevitLookupTableService` — самый сложный файл (77 sites)
4. **P8.4**: Проверить `PipeConnectSizeHandler` — содержит math formulas без тестов
5. **P8.5**: Smoke test в CI (уже есть в `docs/testing/smoke-test-checklist.md`)

**Трудозатраты:** 5-10 дней (audit + новые тесты)

---

## Phase 9 — Performance: hot paths (по запросу)

**Обоснование:** `ConnectExecutor.ValidateAndFixBeforeConnect` — 26 Debug-сообщений в одной транзакции.
В production hot paths (например, batch import 1000 families) это может дать ощутимый
overhead.

**Что делать:**

1. **P9.1**: BenchmarkDotNet профиль для `SmartConLogger.Info` — измерить baseline
2. **P9.2**: Если профилирование покажет >5% CPU на logging — применить `LogLevel.IsEnabled`
   guard pattern (с защитой от evaluation cost для параметров)
3. **P9.3**: `Channel<LogEntry>` background drain — для случая когда logging блокирует UI thread
   (вероятно не критично для Revit add-in'а, но всё равно полезно)

**TBD:** Запускать ТОЛЬКО если user сообщит о проблемах с производительностью.

**Трудозатраты:** 2-3 дня (когда понадобится)

---

## Phase 10 — Документация и ADR (1-2 дня)

**Обоснование:** Phase 1/2 оставили отличную документацию (ADR-026, final-validation-report,
skill `smartcon-logging`). Нужно дополнить смежные разделы.

**Что делать:**

1. **P10.1**: Обновить `docs/architecture/dependency-rule.md` — добавить ссылку на
   `smartcon-logging` skill
2. **P10.2**: Создать `docs/architecture/async-threading.md` — консолидировать
   правила из skill `revit-api-best-practice` + специфику проекта
3. **P10.3**: Создать `docs/architecture/dependency-injection.md` — описать DI
   стратегию (если Phase 7 будет реализована)
4. **P10.4**: Обновить `docs/adr/025-refactoring-migration-backlog.md` — отметить
   `M-019-005` как **выполнено** (Phase 1)

**Трудозатраты:** 1-2 дня

---

## Что НЕ делаем (out of scope)

- ❌ Roslyn analyzers (отвергнуто пользователем в Phase 0)
- ❌ Microsoft.Extensions.Logging адаптер (Phase 2 отказался)
- ❌ Background drain `Channel<LogEntry>` (Phase 3, по запросу)
- ❌ JSON sink (Phase 3, по запросу)
- ❌ Полный рефакторинг `FamilyManager` (1771 файлов, 43k строк) — слишком большой scope,
  нужен отдельный ADR
- ❌ Переписывание на F# / Rust / etc. — текущий стек (C# 12, .NET 8, WPF) соответствует задачам

---

## Приоритеты

| Приоритет | Фаза | Трудозатраты | Статус |
|---|---|---:|---|
| 🔴 P0 | Phase 5 — Async/threading safety | 1-2 дня | ✅ **DONE** (C5+C6+C7: AsyncBridge + 8 мест RunSync + 2 async void) |
| 🟡 P1 | Phase 6 — Counter pattern | 1-2 дня | ✅ **DONE** (C2+C3: HotLoopCounter helper + audit — не нужен в существующем коде) |
| 🟡 P1 | Phase 10 — Docs | 1-2 дня | ✅ **DONE** (C10: ADR-025/026/roadmap/AGENTS.md обновлены) |
| 🟢 P2 | Phase 7 — DI readiness | 3-5 дней | 🟡 **PARTIAL** (C8: ADR-028 audit; Tier 1+2 в `feature/di-readiness`) |
| 🟢 P2 | Phase 8 — Test coverage | 5-10 дней | 🟡 **PARTIAL** (C9: baseline report 38.51%; Tier 1+2 в `feature/test-coverage-baseline`) |
| ⚪ P3 | Phase 9 — Perf | 2-3 дня | ⚪ NOT STARTED (по запросу) |

## Обновлено 2026-06-09

Phase 5/6/10 **выполнены** в `feature/logging-improvements` (9 follow-up коммитов
после Phase 1+2). Phase 7/8 — **partial**: audit + baseline report сделаны;
полная реализация в **отдельных ветках** для удобства review.

Подробный план доработок (C1-C11 + Tier 1+2):
[`docs/roadmap/logging-final-plan.md`](logging-final-plan.md)

**Рекомендуемый следующий шаг:** Phase 5 (Async/threading safety) — критично, делать первым.
