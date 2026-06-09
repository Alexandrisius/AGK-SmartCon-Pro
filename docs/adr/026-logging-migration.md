# ADR-026: Logging Migration Plan (Phase 0 + Phase 1)

**Status:** accepted
**Date:** 2026-06-09
**Branch:** feature/logging-phase-0

## Контекст

Аудит `SmartConLogger` (выполнен 2026-06-09) выявил 5 проблем разной
критичности. Из них одна — **блокер** для M-019-005 из ADR-025: текущая
реализация `BeginScope` не пробрасывает `OpId` в дочерние `Info`/`Debug`,
поэтому миграция 1001 call site сама по себе не улучшит observability.

Этот ADR фиксирует **новый план в две фазы**:

| Фаза | Содержание | Трудозатраты | Статус |
|------|------------|--------------|--------|
| Phase 0a (блокер) | Починить `LogScope` storage + реструктурировать файлы логов | 16-22 ч (2-3 дня) | **в этой ветке** |
| Phase 0b | Миграция 1001 call site на `BeginScope` | 35-50 ч (5-7 дней) | следующий этап |

Phase 0b — это, по сути, переименованный M-019-005 из ADR-025 с
поправкой на корректный storage.

## Phase 0a — что сделано в этой ветке

### Изменения в инфраструктуре

1. **Новый `LogScopeProvider`** — `AsyncLocal<ImmutableStack<LogScope>>`.
   `OpId` течёт через `await Task.Yield()`, `await Task.Run(...)` и
   thread-pool hops. Старая реализация не имела storage вообще —
   `OpId` появлялся только в START/END-строках.

2. **Новый `LogScope` в отдельном файле** — теперь это public class
   с `FormatPrefix()` для рендера `[OpId=… Op=… Key=Value …]`.

3. **`WriteMain` / `WriteFormula`** собирают цепочку prefixes из
   `LogScopeProvider.EnumerateFromRoot()` и подмешивают в каждую строку.

4. **`MinLevel` стал `volatile`** — защита от race condition при
   override из другого потока (Revit IExternalEvent → background await).

5. **`Measure(...)`** — добавлен `TimedScope`, который при Dispose
   пишет `=== END elapsed=…ms ===` и снимает scope со стека.

### Реструктуризация файлов логов

| Было | Стало | Обоснование |
|------|-------|-------------|
| `smartcon.log` (ротация 5MB / 3 bak) | `smartcon.log` (ротация 5MB / 3 bak) | без изменений |
| `lookup-diagnostic.log` | **удалён** | был лог для одного конкретного бага, инфа переехала в `smartcon.log` с тегом `[Lookup]` |
| `formula-diagnostic.log` (ротация 5MB / 3 bak) | `formula-diagnostic.log` (**append-only**, без ротации) | нужен как baseline для статистики формул PipeConnect |
| `freeze-diagnostic.log` | **удалён** | был лог для одного конкретного бага, инфа переехала в `smartcon.log` с тегом `[Freeze]` |

### Удалённые методы (без замены)

- `SmartConLogger.Lookup(string)` — 4 вызова в `EditFamilySession.cs`,
  заменены на `Debug("[Lookup] …")`
- `SmartConLogger.Freeze(string)` — 8 вызовов, заменены на
  `Error("[Freeze] …")` для ошибок и `Debug("[Freeze] …")` для трейсов
- `SmartConLogger.FreezeThreadPool(string)` — 2 вызова, заменены на
  `Debug("[Freeze] [ThreadPool] …")`
- `SmartConLogger.FreezeTimer(...)` — 0 вызовов, удалён без замены

### Сознательно подавленные analyzer-предупреждения (CA1863, CA1305)

Два правила Microsoft .NET-овых analyzers срабатывают на нашем коде,
но **намеренно подавлены** через `severity = suggestion` в `.editorconfig`
(подробное обоснование — в `.editorconfig:185-236` и
`docs/architecture/logging.md §"Analyzer severity policy"`).

**CA1863** "Use CompositeFormat" — pre-existing **~30+ sites** в
`PipeConnect` (около 0 в `Revit`/`ProjectManagement`/`FamilyManager`).
Срабатывает на каждый `SmartConLogger.Info($"... {x} ...")` где
есть interpolation. Причина подавления: это **Performance** rule,
15-30% профит только в high-frequency loops (1000+ format calls/sec).
У нас user-triggered actions с <100 format calls per click — профит
не наблюдаем. Microsoft dotnet/runtime сами держат это правило на
`suggestion` в `eng/CodeAnalysis.src.globalconfig`. Официальная
формулировка Microsoft: "It's safe to suppress diagnostics from this
rule if performance isn't a concern."

**CA1305** "Specify IFormatProvider" — pre-existing **4 sites** в
`ProjectManagement/ViewModels/ExportNameDialogViewModel.cs`
(строки 114, 134, 144, 149). Срабатывает на
`sb.AppendLine($"...")` где есть overload с `IFormatProvider`.
Причина подавления: это **Globalization** rule, применимая только
к culture-sensitive числам. У нас — preview file names, культура
не играет роли. `dotnet/runtime` сами не поднимают это в warning
в своём globalconfig.

**Защита от случайного "фикса":** `EnforceCodeStyleInBuild=true` НЕ
эскалирует `suggestion` в `warning`, поэтому билд остаётся зелёным.
Если будущий агент решит "починить" эти правила, повысив severity до
`warning` — он сразу получит 30+ ошибок и потратит время на
рефакторинг ради нулевого наблюдаемого профита. Документация
явно говорит: "DO NOT raise to warning without (1) refactoring all
sites, (2) benchmarking, (3) updating this ADR".

Проверка что правила действительно срабатывают (без коммита):
временно изменить `.editorconfig` строку `CA1863.severity` (или
`CA1305.severity`) с `suggestion` на `warning` → `dotnet build`
выдаст точный список всех suppressed sites.

### Тесты

`SmartConLoggerScopeTests.cs` дополнен с 5 до 12 тестов:

| Тест | Что проверяет |
|------|---------------|
| `BeginScope_PropagatesAcrossAwait` | AsyncLocal flow через `await Task.Yield()` |
| `BeginScope_PropagatesAcrossThreadPoolHop` | AsyncLocal flow через `await Task.Run(...)` (регрессия для ThreadStatic) |
| `BeginScope_FormatPrefix_IncludesOpAndProperties` | Формат префикса |
| `BeginScope_OpId_IsUniquePerScope` | Уникальность `OpId` |
| `BeginScope_Nested_OuterStaysActiveAfterInnerDispose` | Корректность `PopOnDispose` |

## Phase 0b — что дальше (M-019-005b)

После мержа Phase 0a в develop:

1. Roslyn-анализатор, детектирующий
   `SmartConLogger.Info($"[{prefix}]…")` (старый ручной паттерн).
2. Auto-fix для простых случаев (batch, ~80 файлов с ≤5 вызовов).
3. Manual review для moderate (~30 файлов, 6-20 вызовов) и
   complex (~10 файлов, >20 вызовов).
4. Замена ручных `Stopwatch` на `Measure`/`BeginScope`.
5. Hot-loop: `Info` в циклах → `Debug` (Release выключает) +
   source-gen `[LoggerMessage]` с `SkipEnabledChecks = true` для
   zero-alloc Debug-вызовов.

### Phase 0b — quick wins (выполнено в этой ветке)

Самые шумные/наименее рискованные миграции сделаны в рамках той же
ветки, чтобы быстро проверить end-to-end scope-flow:

| Где | Что сделано |
|-----|-------------|
| `ActiveFamilyFilePreparer.PrepareActiveFamilyAsync` | `Measure` обёртка, убран ручной `=== Start ===` |
| `ActiveImportCleanupService.CleanupImpl` | `Measure` обёртка, `=== END:` → `=== summary:` (elapsed даёт `Measure`) |
| `FamilyManagerMainViewModel.FamilyEdit.ImportActiveFileAsync` | `Measure` обёртка, убраны ручные `[ImportActiveFile] === START/END ===` |
| `LoadableFamilyScanner.GetUniqueFamilies` | `Measure` обёртка, ручной `Stopwatch` удалён, `Info` → `Debug` |
| `FittingFamilyRepository.GetEligibleFittingFamilies` | 2× `Measure` (Phase1/Phase2), русский `Info` → `Debug` (исторический диагностический) |
| `ShareProjectCommand.Execute` | `Measure` обёртка (ручной `Stopwatch` оставлен — используется в success-message) |
| `ChainOperationHandler` | **53× `Info` → `Debug`** (все `[Chain+]` — диагностика Chain, в Release выключается) |
| `PipeConnectEditorViewModel.Init` | `Measure` обёртка, убран `[Init] START` |
| `PipeConnectEditorViewModel.Connect` | `Measure` обёртка, убран `[Connect] START` |
| `SmartConLoggerScopeTests` | +3 end-to-end теста: `InfoInsideScope_AppliesPrefixToMainLog`, `Measure_RendersElapsedFooterOnDispose`, `InfoInsideScope_AfterAwait_StillCarriesOpId` |

**End-to-end контракт теперь проверяется:** тест
`InfoInsideScope_AppliesPrefixToMainLog` читает `smartcon.log` и
проверяет что строка `e2e probe line` действительно содержит
`[OpId=…]` и `Op=E2ETest`. Это та проверка, которой не хватало
в Phase 0a (там были только in-memory smoke-тесты).

**Что НЕ сделано в Phase 0b** (перенесено в Phase 1):
- ~900 из ~1000 call-sites остались как `Info($"[Category] …")` —
  это скоуп M-019-005b, требует ручной ревизии по модели ACAT.
- Hot-loop в `RevitLookupTableService` (84) / `RevitParameterResolver` (83) /
  `RevitDynamicSizeResolver` (64) — не трогали, чтобы не раздувать PR.
- Source-gen `[LoggerMessage]` — Phase 1 (после миграции на `ILogger<T>`).

## Что НЕ входит в scope

- ❌ Sanitization PII (принято не делать — локальный single-user инструмент)
- ❌ Structured JSON output (только при явном запросе)
- ❌ Подключение `Microsoft.Extensions.Logging` в Revit (см. Phase 2 ниже)

## Phase 1 — что сделано в коммитах 5afc419…7c50c41

### Цель

Заменить ручные `[Category]` префиксы в message-строке на
structured `BeginScope`, чтобы `OpId`/`Method`/`DynId`/etc попадали
в prefix и были queryable.

### Метрики (финальные)

| Метрика | До | После |
|---|---:|---:|
| Call-sites с ручным `[Cat]` префиксом | 460 | **0** |
| Файлов с `BeginScope` | 15 | **81** |
| BeginScope uses | 15 | **115+** |
| Файлов с call-sites | 82 | 82 (все покрыты) |
| 4/4 build configs (R19/R21/R24/R25) | ✅ | ✅ 0 warnings |
| Tests | 1227 | 1232 |

### Зафиксированные дефекты

- **D1 (Op=Op= дубль)**: `LogScope.FormatPrefix()` всегда рендерит
  `Op={Operation}`, а `SmartConLogger.Measure()` добавлял `("Op", op)`
  в Properties → дабл-печать. Фикс: `Measure` убрал `Op` property;
  `FormatPrefix` skip-ит property с key=`Op` если value равно Operation.
- **D2 (LogScopeExtensions)**: helper не использовался ни одним
  call-site → удалён полностью.

### Подход

Manual refactor, bulk-replacement префиксов (с защитой от Unicode
Chain+/Chain−), scope на ключевых методах. Roslyn analyzers
отвергнуты (слишком сложная инфраструктура, 2ч без результата).

### Известные false-positives в grep

- `[{attemptName}]` — параметр, не категория
- `[DIAG {label}]` — параметр
- `[Restore SystemClassification]` — два слова
- `SmartConLogger.Formula($"[ParseSizeLookup] ...")` — formula-diagnostic.log, отдельная система

## Phase 2 — что сделано в коммите 4f0e907

### Цель

Подготовить инфраструктуру для будущей DI-интеграции и unit-тестов
без изменения call-sites.

### Изменения

- `ISmartConLogger` (новый): `Info/Debug/Warn/Error(string)` — 1:1
  статическим методам. BeginScope/Measure **не входят** в интерфейс —
  scope state живёт в `LogScopeProvider` (AsyncLocal), а не в writer.
- `SmartConLoggerAdapter` (новый): singleton-реализация, делегирующая
  в статический `SmartConLogger`. Кейс: DI контейнер резолвит
  `ISmartConLogger`, но write-path остаётся прежним.

### Что НЕ сделано в Phase 2 (обоснование)

- **Microsoft.Extensions.Logging adapter**: подключение M.E.L. в
  Revit-плагин проблематично — конфликт версий с PnIDModeler
  (Autodesk forum 9270358). Если пользователь явно запросит
  M.E.L.-совместимость, она добавляется отдельным optional-пакетом
  `SmartCon.Core.MicrosoftLoggingAdapter` без зависимости на основной
  `SmartCon.App`.
- **`[LoggerMessage]` source-generator**: работает только с методами
  расширения `ILogger`, не со static facade. Принципиально
  несовместим с текущей архитектурой.

## Связанные документы

- [ADR-025 §M-019-005](025-refactoring-migration-backlog.md) — оригинальный план миграции (выполнен Phase 0a/0b + Phase 1)
- [docs/architecture/logging.md](../architecture/logging.md) — обновлённая документация
- [docs/logging/migration-inventory.md](../logging/migration-inventory.md) — таблица call-sites до/после
- [docs/logging/final-validation-report.md](../logging/final-validation-report.md) — Phase 1+2 final metrics
- [docs/logging/hot-loop-counter-audit.md](../logging/hot-loop-counter-audit.md) — Phase C3 audit
- [docs/testing/coverage-baseline-2026-06.md](../testing/coverage-baseline-2026-06.md) — Phase C9 baseline
- [docs/adr/028-di-readiness.md](028-di-readiness.md) — Phase C8 DI audit
- [docs/roadmap/logging-final-plan.md](../roadmap/logging-final-plan.md) — финальный план
- [Stephen Cleary — Implicit Async Context](https://blog.stephencleary.com/2013/04/implicit-async-context-asynclocal.html) — обоснование AsyncLocal vs ThreadStatic
- [Microsoft — High-performance logging](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/high-performance-logging) — best practice для hot-path логирования
- [Andrew Lock — Source-generated logging](https://andrewlock.net/exploring-dotnet-6-part-8-improving-logging-performance-with-source-generators/) — обоснование отказа от [LoggerMessage]

## Phase L — Finalization (2026-06-09, feature/logging-improvements)

**Цель:** доделать улучшения логирования + safety/observability правки,
чтобы **закрыть ветку** `feature/logging-improvements` со status "ready to merge".

### Изменения (C1-C9)

| # | Commit | Scope |
|---|---|---|
| **C1** | `c084e58` | feat(logging): MeasureScope API — elapsed до Dispose |
| **C2** | `bdbf991`+`2c99a9b` | feat(logging): HotLoopCounter helper + skill reference |
| **C3** | `cda9f91` | docs(logging): hot-loop counter pattern audit report (не нужен) |
| **C4** | `89f1725` | refactor(logging): ShareProjectCommand Stopwatch → MeasureScope |
| **C5** | `10c53c9` | feat(threading): AsyncBridge.RunSync helper |
| **C6** | `64e8659` | fix(threading): 8 production .GetAwaiter().GetResult() → RunSync |
| **C7** | `0ffe775` | fix(threading): 2 async void в FamilyBatchImportViewModel |
| **C8** | `901b282` | chore(di): ADR-028 DI readiness audit |
| **C9** | `c54c19d` | chore(testing): coverage baseline report |

### Финальные метрики

| Метрика | До | После | Δ |
|---|---:|---:|---:|
| BeginScope uses | 184 | 184+ | +0 (без изменений) |
| Measure uses | 6 | 7+ | +1 (C1: MeasureScope API) |
| HotLoopCounter helper | 0 | 1 struct + 8 tests | NEW |
| AsyncBridge helper | 0 | 1 class + 5 tests | NEW |
| Production .GetAwaiter().GetResult() | 8 | 0 | -8 |
| Production async void | 2 | 0 | -2 |
| Production Stopwatch (non-logger) | 1 | 0 | -1 |
| Tests | 1245 | 1245 | +0 (было 1232 до C1, +13 C1+C2+C5+C7) |
| Build configs зелёные | 4/4 | 4/4 | = |
| 0 warnings | ✅ | ✅ | = |
| Skill `smartcon-logging` | 3 файла | 4 файла (+ counter-pattern.md) | +1 |
| ADR-028 (DI readiness) | — | 175 lines | NEW |
| Coverage baseline report | — | 171 lines | NEW |
| Hot-loop counter audit | — | 80 lines | NEW |

### Что осталось для следующих веток

| # | Scope | Ветка | Трудозатраты |
|---|---|---|---:|
| DI readiness — Tier 1 | 15 `ServiceHost.GetService<>` → ctor-inj | `feature/di-readiness` | 1-2 дня |
| DI readiness — Tier 2 | static → instance services | `feature/di-readiness` | 3-5 дней |
| Test coverage — Tier 1 | 10-49% → 70% (24 файла) | `feature/test-coverage-baseline` | 3-5 дней |
| Test coverage — Tier 2 | UI host mocking (ViewModels) | `feature/test-coverage-baseline` | 5-10 дней |
| Integration tests | реальный Revit host | `feature/test-coverage-baseline` | 5-10 дней |

### Out of scope (навсегда)

- ❌ Roslyn analyzers (отвергнуто в Phase 0)
- ❌ Microsoft.Extensions.Logging adapter (отвергнуто в Phase 2)
- ❌ JSON sink
- ❌ Background drain `Channel<LogEntry>` (Phase 3)
- ❌ BenchmarkDotNet profiling (по запросу)
- [Autodesk forum 9270358 — M.E.L. conflict](https://forums.autodesk.com/t5/revit-api-forum/revit-api-s-integration-with-xbim-geometry-microsoft-extensions/td-p/9270358) — обоснование отказа от M.E.L. adapter
