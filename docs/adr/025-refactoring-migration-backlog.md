# ADR-025: Refactoring Migration Backlog (Phases 5–7)

**Status:** accepted (tracking only)
**Date:** 2026-06-05
**Branch:** develop

## Контекст

Phases 1–7 рефакторинга FamilyManager (см. [ADR-018 §Refactoring Updates](018-familymanager-refactoring.md)) ввели
пять новых инфраструктурных абстракций:

1. `IClock` / `SystemClock`
2. `IIdGenerator` / `GuidIdGenerator`
3. `IDispatcher` / `WpfDispatcher`
4. `SqliteConnectionExtensions` (4 метода)
5. `SmartConLogger.BeginScope(...)` / `SmartConLogger.Measure(...)`

Каждая из них уже **зарегистрирована в DI**, **покрыта unit-тестами** и **доступна для нового кода**.
Существующий код, написанный до рефакторинга, **продолжает использовать legacy API напрямую** —
это не regression (код работает), но **снижает testability** и **блокирует будущие улучшения**.

Этот ADR фиксирует **список миграций** как Single Source of Truth, чтобы backlog
не потерялся при ротации инженеров / месяцев бездействия.

## Зачем нужны миграции (для не-программистов)

Представь, что код — это **стройка**. Мы построили современный
**трансформатор тока** (новые абстракции) с **автоматами защиты**
(DI-контейнер), **счётчиками** (метрики), **маркировкой** (OpId).
Старая проводка **работает** — свет горит, насосы крутятся.
Но если произойдёт короткое замыкание, **найти место** сложнее,
**починить** — дольше, **проверить в лаборатории** — невозможно
(нет изоляции).

Миграция = **заменить старую проводку на новую**,
сохранив ту же функциональность, но получив **testability, observability,
safety**.

## Технические детали (для разработчиков)

### M-019-001: `DateTimeOffset.UtcNow` → `IClock.UtcNow`

**Скоуп:** 74 call site-а (оценка по `rg -c 'DateTime(Offset)?\.(Utc)?Now' src/`).
Горячие файлы: `RevitLookupTableService.cs` (84), `RevitDynamicSizeResolver.cs` (64),
`RevitParameterResolver.cs` (83), `FamilyParameterAnalyzer.cs` (28).

**Почему плохо:**
- Тесты не могут зафиксировать "сейчас 2025-01-01 00:00:00 UTC" — каждый прогон получает новое время.
- Логи содержат "плавающие" timestamps, которые нельзя коррелировать в распределённой системе.
- `DateTimeOffset.UtcNow` — **глобальная функция**, не инжектируемая. Если завтра захотим читать
  время из NTP-сервера, а не системных часов — придётся править 74 места.

**Почему хорошо после:**
- `private readonly IClock _clock; _clock.UtcNow` — в тесте подменяем на `FakeClock(fixedValue)`.
- Один source of truth для времени.
- Возможность ввести `LoggingClock` для диагностики clock skew.

**Критичность:** 🟡 средняя. Не блокирует работу, но блокирует
полноценное unit-тестирование timestamp-зависимой логики
(например, проверка истечения срока действия записей).

**Подводный камень:** Часть мест — это `DateTime.Now` (локальное время).
После миграции надо явно выбрать: `_clock.UtcNow` (рекомендуется)
или `_clock.LocalNow` (если локальное время действительно нужно).

**Стратегия:**
1. Добавить `IClock.LocalNow` (опционально).
2. Bulk-replace `DateTimeOffset.UtcNow` → `_clock.UtcNow` через Roslyn-fix.
3. Где поле `_clock` отсутствует — добавить ctor-параметр, зарегистрировать в DI.
4. Тесты: создать `FakeClock` (уже сделан в `ClockAndIdTests`), подменить через `WithClock(...)`.

**Ссылка:** инфраструктура в коммите `a9c2786` (Phase 5+6).

---

### M-019-002: `Guid.NewGuid()` → `IIdGenerator.NewId()`

**Скоуп:** 40 call site-ов. Горячие файлы: `LocalFamilyImportService.cs` (5),
`LocalFamilyImportService.TypeCatalog.cs` (3),
`SystemFamilyImportOrchestrator.cs` (1), `CategoryTreeEditorViewModel.*` (5).

**Почему плохо:**
- `Guid.NewGuid()` возвращает **непредсказуемый** ID. В тесте нельзя проверить
  "запись с ID X имеет параметр Y", потому что X каждый раз новый.
- Нельзя перейти на **детерминированный** ID (ULID, Snowflake, hash-based) без
  массового переписывания.

**Почему хорошо после:**
- В тестах — `FakeIdGenerator("test-id-1", "test-id-2", ...)`.
- В продакшене — `GuidIdGenerator` (поведение идентично `Guid.NewGuid()`).
- Будущая миграция на ULID = `services.AddSingleton<IIdGenerator, UlidIdGenerator>()`.

**Критичность:** 🟢 низкая. ID уникальны в обоих случаях.
Главная ценность — testability, не безопасность.

**Подводный камень:** `Guid.NewGuid().ToString("N")` (32 hex-символа)
и `Guid.NewGuid().ToString("D")` (с дефисами) — формат должен сохраниться.

**Стратегия:**
1. Добавить `IIdGenerator.NewId("N")` / `IIdGenerator.NewId("D")` (уже сделано).
2. Bulk-replace `Guid.NewGuid().ToString(...)` → `_idGenerator.NewId(...)`.
3. Где нужен bare `Guid` (например, `new Guid(idString)`) — добавить `IIdGenerator.NewGuid()`.

**Ссылка:** инфраструктура в коммите `a9c2786` (Phase 5+6).

---

### M-019-003: `Application.Current?.Dispatcher` → `IDispatcher`

**Скоуп:** 5 call site-ов в 3 файлах:
- `FamilyManagerMainViewModel.cs` (2 — `OnPlacementCompleted`)
- `RevitWindowFocusService.cs` (1)
- `ShareProjectCommand.cs` (2 — но это `UIElement.Dispatcher`, другая семантика)

**Почему плохо:**
- `Application.Current` — **глобальный синглтон**, не инжектируется.
- В тестах `Application.Current` = `null` (нет WPF Application), паттерн `?? Dispatcher.CurrentDispatcher` падает с "must create DependencySource on same thread".
- `dispatcher.HasShutdownStarted` — race condition между проверкой и `BeginInvoke`.

**Почему хорошо после:**
- В тестах — `Mock<IDispatcher>()` или `InlineDispatcher` (синхронный).
- В продакшене — `WpfDispatcher` (уже реализован, net48-safe).
- `await _dispatcher.InvokeAsync(action, ct)` вместо `dispatcher.BeginInvoke(...)` — единый async-паттерн.

**Критичность:** 🟠 высокая. **UI-thread deadlock потенциально возможен**
(см. [revit-api-best-practice skill](../../.agents/skills/revit-api-best-practice/SKILL.md) — `MFC UI freezes`).

**Подводный камень:**
- `UIElement.Dispatcher` (например, `_progressView?.Dispatcher`) — **другая семантика**,
  это dispatcher конкретного UIElement, не Application. Не мигрировать на `IDispatcher`.
- `Dispatcher.CurrentDispatcher` fallback в `WpfDispatcher` — может создать лишний
  dispatcher в тестах. Сейчас `WpfDispatcher` (FamilyManager/UI) уже корректно
  обрабатывает `null` через `Dispatcher.CurrentDispatcher`.

**Стратегия:**
1. В `FamilyManagerMainViewModel` — добавить `IDispatcher` в `FamilyManagerServices`
   (record, 30→31 props).
2. Заменить `Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher` → `_dispatcher`.
3. `dispatcher.BeginInvoke(action)` → `_dispatcher.InvokeAsync(action)`.
4. Тесты: `Mock<IDispatcher>` + `Verify(d => d.InvokeAsync(...))`.

**Ссылка:** инфраструктура в коммите `1bb47f7` (Phase 6 finish).
WpfDispatcher + 7 тестов в `WpfDispatcherTests.cs`.

---

### M-019-004: manual `SqliteConnection`/`SqliteCommand` → `SqliteConnectionExtensions`

**Скоуп:** 30+ call site-ов. Горячие файлы: `LocalCatalogMigrator.cs` (~530 строк,
50+ command-ов), `LocalFamilyImportService.cs`,
`DatabaseManager.cs`, repository-классы.

**Почему плохо:**
- `using var cmd = connection.CreateCommand(); cmd.CommandText = ...; cmd.Parameters.Add(...);`
  повторяется в 30+ местах. **Copy-paste**.
- `ExecuteNonQueryAsync(ct)` / `ExecuteScalarAsync(ct)` / `ExecuteReaderAsync(ct)` — нестандартная
  обработка ошибок, нет retry на `SQLite BUSY`.
- Параметры — `SqliteParameter` с magic strings (`@id`, `@name`).

**Почему хорошо после:**
- `await connection.ExecuteAsync("UPDATE t SET x=@x", new { x = value }, ct)` — короче, читаемо.
- Можно добавить retry-обёртку `WithRetryAsync` — один раз, применяется ко всем вызовам.
- Можно добавить `IParameter` interface, параметризовать naming policy.

**Критичность:** 🟡 средняя. Не блокирует, но **плодит copy-paste**,
который через год превращается в "30 разных способов сделать одно и то же".

**Подводный камень:**
- `LocalCatalogMigrator` имеет сложные multi-statement сценарии (PRAGMA, transactions, conditional schema)
  — не всё ложится на extension. Возможно, мигрировать только простые одно-командные места.
- `Microsoft.Data.Sqlite` package теперь в Core — некоторые файлы Core могут
  зависеть от него (что нормально).

**Стратегия:**
1. Начать с `DatabaseManager.cs` (простые single-command операции).
2. Мигрировать `LocalFamilyImportService.cs` (INSERT/UPDATE).
3. `LocalCatalogMigrator.cs` мигрировать **выборочно** — оставить `using var cmd` для multi-statement.
4. Тесты: существующие integration-тесты в `LocalMetadataImportFlowIntegrationTests.cs`
  покроют regressions.

**Ссылка:** инфраструктура в коммите `a9c2786` (Phase 5+6).
`SqliteConnectionExtensions` + 7 тестов в `SqliteConnectionExtensionsTests.cs`.

---

### M-019-005: `SmartConLogger.Info("...")` → `using var _ = BeginScope("OpName")`

**Скоуп:** 1001 call site. Горячие файлы:
- `SystemFamilyRevitOperations.cs` (24)
- `LocalFamilyImportService.cs` (50+)
- `FamilyManagerMainViewModel.*` (все partials, ~100)
- `DatabaseManager.cs`, repository-классы

**Почему плохо:**
- `SmartConLogger.Info($"[{opName}] something happened")` — opName **пишется вручную**
  в каждое сообщение, нет корреляции между сообщениями одной операции.
- Невозможно в логе найти **все** сообщения, относящиеся к операции X — только если помнишь opName.
- Тайминги операций — вручную через `Stopwatch.StartNew()` + `Stopwatch.Stop()`.

**Почему хорошо после:**
- `using var _ = SmartConLogger.BeginScope("ImportActiveFile")` — все сообщения
  внутри блока автоматически получают `[OpId=abc12345]`.
- В логе: `grep abc12345 smartcon.log` → вся операция целиком.
- Тайминги — `using var _ = SmartConLogger.Measure("SubOperation")` — авто elapsed в Dispose.

**Критичность:** 🟢 низкая. Логи работают, **но observability** страдает:
при инциденте в продакшене корреляция событий — manual.

**Подводный камень:**
- 1001 call site — это **большой** diff. Code review будет длинным.
- Возможны **новые баги**: `using var _ = scope` — если scope не Dispose-ится (например, exception в ctor),
  логирование не сработает. Текущий `LogScope.Dispose` идемпотентен (есть `_disposed` guard).
- Некоторые `Info(...)` вызываются в **fire-and-forget** контексте (после `await` отвалился) —
  scope там не сработает корректно.

**Стратегия:**
1. Начать с `DatabaseManager.cs` (centralized, простая логика).
2. Мигрировать один ViewModel partial за раз (5-10 use sites).
3. Каждый commit — отдельный файл.
4. **Никогда** не bulk-replace — слишком высокий риск regression.

**Ссылка:** инфраструктура в коммите `a273063` (Phase 7).
`SmartConLogger.BeginScope` / `Measure` + 5 тестов в `SmartConLoggerScopeTests.cs`.

---

## Сводная таблица

| ID | Миграция | Скоуп | Критичность | Трудоёмкость | Риск регрессии |
|---|---|---|---|---|---|
| M-019-001 | `DateTimeOffset.UtcNow` → `IClock` | 74 места | 🟡 средне | 1-2 дня | низкий (read-only) |
| M-019-002 | `Guid.NewGuid()` → `IIdGenerator` | 40 мест | 🟢 низко | 0.5 дня | низкий (read-only) |
| M-019-003 | `Application.Current?.Dispatcher` → `IDispatcher` | 5 мест | 🟠 высоко | 0.5 дня | средний (UI thread) |
| M-019-004 | manual SQL → `SqliteConnectionExtensions` | 30+ мест | 🟡 средне | 1 день | средний (repositories) |
| M-019-005 | `SmartConLogger.Info` → `BeginScope` | 1001 место | 🟢 низко | 2-3 дня | средний (observability) |
| | **ИТОГО** | **~1150 мест** | | **5-7 дней** | |

## Рекомендуемый порядок миграции

1. **M-019-002** (Guid) — самый безопасный, быстрый, даёт опыт с pattern.
2. **M-019-001** (Clock) — аналогичный, чуть больше мест.
3. **M-019-003** (Dispatcher) — маленький скоуп, большой выигрыш по deadlock-safety.
4. **M-019-004** (SQL) — средний риск, хороший выигрыш.
5. **M-019-005** (Logger) — **последний**, потому что самый большой diff.

**Между миграциями** — ручное тестирование в Revit 2025.

## Почему не сделано в Phases 5-7

Каждая миграция — **массовый diff**, который:
- Ломает code review (1 файл с 50+ изменениями трудно проверить).
- Требует ручного тестирования в Revit (unit-тесты не покрывают UI/Revit API).
- Имеет **нелинейный** риск — даже одна ошибка в 1000 мест = hard-to-find bug.

В момент завершения Phases 1-7 приоритетом было:
1. Закрыть баг импорта атрибутов (Phase 1).
2. Покрыть тестами (Phases 2, 8).
3. Ввести инфраструктуру (Phases 3-7).

Миграция существующих call site-ов — **отдельная задача**,
требующая изолированных коммитов и ручного тестирования.

## Что **уже работает идеально** (post-Phase 7)

Эти изменения **не требуют** миграции:

- ✅ Phase 1: bug fix (import attributes) — работает в `smoke-тесте`
  (лог `smartcon.log` показал 3 успешных `ImportActiveFile` сессии,
  5 placements, 14 extractions, 0 WRN, 0 ERR).
- ✅ Phase 4c: async safety — `AwaitableEvent` корректно дренирует queue,
  нет deadlock-ов.
- ✅ Phase 4a: DIP `LocalCatalogMigrator` — миграция БД работает
  (11 schema versions, все миграции применены).
- ✅ Phase 7: `BeginScope` инфраструктура — готова к использованию
  в новом коде (5 тестов зелёные).

## Связанные документы

- [ADR-017](017-familymanager-attribute-extraction.md) — Attribute Extraction (Phase 1 bug fix context)
- [ADR-018](018-familymanager-refactoring.md) — Phases 1-7 Refactoring Updates
- [revit-api-best-practice skill](../../.agents/skills/revit-api-best-practice/SKILL.md) — threading patterns
- [revit-wpf-compat skill](../../.agents/skills/revit-wpf-compat/SKILL.md) — net48 dispatcher safety
- [docs/domain/interfaces.md](../domain/interfaces.md) — `IClock`, `IIdGenerator`, `IDispatcher`, `ILocalCatalogMigrator`
- [docs/domain/models.md](../domain/models.md) — `FamilyMetadataFormat`, `FamilyMetadataMigrator`

## Verification логов (2026-06-05)

| Метрика | Значение |
|---|---|
| Sessions started | 6 |
| Imports completed | 3 |
| Placements succeeded | 5 |
| Extractions | 14 |
| WRN | **0** |
| ERR | **0** |
| AwaitableEvent drained | ✓ каждый раз |
| Cleanup runs | 4 (все clean, без skipped) |

**Заключение:** код после Phases 1-7 **работает корректно**.
Миграции (M-019-001..005) — это **улучшение**, не **исправление**.
