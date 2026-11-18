# Handoff — Agent: текущее состояние (2026-11-18)

## ⚡ Итог сессии «новые инструменты»: гейты (2026-11-19)

Каталог 27 → **33** (+6 тулов), паки `sheets`, `schedules`, `filters` populated.
Коммиты: `56e32a53` (read: листы/ведомости), `f2e71a2a` (фильтры/overrides + undo),
`26b53a68` (routing/decision бенчмарк, 170 фраз).

| Гейт | Результат |
|---|---|
| Unit `SmartCon.Tests -c Debug.R25` | **3739/3739** |
| Integration R25 (полный, replay) | **355 passed, 11 skipped, 0 failed** (366) — прогон до мержа `24fd4c69` |
| Integration net48 (Revit 2023) | **НЕ ЗАПУСКАЛСЯ** (владелец остановил долгие прогоны) |
| Integration R27 (net10) | **НЕ ЗАПУСКАЛСЯ** |
| Build R19 (net48, старейший API) | 0 ошибок (после мержа) |
| Build R25 | 0 ошибок (до мержа) |
| Build R27 (net10) | MC3074 `ContextDonut` — известная преходящая гонка WPF-разметки (BG1002-семейство), retry не выполнен по указанию владельца |
| 7 конфигураций sweep | **НЕ ЗАПУСКАЛСЯ** |
| Eval replay | **46/46**, schema 149/149, unauthorized 0, gate PASS |
| Routing rules (170 фраз) | top-1 95.9 %, top-5 100 %, top-14 100 %, p95 1.70 мс |
| Routing decisions live | top-1 100 % (20 фраз), p95 4 129 мс |
| Ручной UI-тест в Revit | **manual pending** |

Изменение после полного R25: `get_sheet_content` теперь отдаёт истинный
`placedScheduleCount` независимо от `includeSchedules` (одна строка,
покрыта `AgentSheetScheduleToolsTests`; точечный перепрогон не выполнялся в
этой сессии — владелец остановил прогоны).

## ⚡ Сессия «новые инструменты» — Фаза 3: routing/decision (2026-11-19)

- `real-queries.json` 120 → **170** фраз (r123–r172), гейты: rules top-1 95.9 %,
  top-5 100 %, top-14 100 %, false tool 0 %, p95 1.70 мс.
- Decisions live (pplx free через OpenRouter, 20-запросная выборка): top-1 100 %,
  top-5 100 %, p95 4 129 мс (сеть). `SMARTCON_DECIDER_ENABLED=0` в secrets —
  live-тест требует явного `SMARTCON_DECIDER_ENABLED=1` и `SMARTCON_DECIDER_LIVE=1`.
- Новый live-бенчмарк `RoutingLiveBenchmarkTests` (opt-in `SMARTCON_AGENT_LIVE=1`,
  вне CI): production-оркестратор, 33 скриптованных тула, 16 интентов (10 прямых +
  6 парафраз), два режима `find_tools`. Результат: expected tool в первом
  shortlist **16/16** в обоих режимах; задачи 16/16 (rules) и 15/16 (decisions);
  раундов до первого правильного вызова 3.81/3.07 (avg), всего раундов 7.62/6.25;
  `find_tools` и `load_tool_pack` — **0** на задачу.
- **Вердикт:** decision-reranker по умолчанию не включать. BM25 top-5 покрывает
  парафразы, `find_tools` не вызывается, decision не включается; на routing-уровне
  decision закрывает top-1 промахи (95.9 % → 100 % на выборке), но p95 ~4 с.
  Пересмотр — при top-5 <95 % или каталоге >200.

Не проверено: ручной UI-тест (manual pending).

## ⚡ Сессия «новые инструменты» — Фаза 2: фильтры и overrides (2026-11-19)

Каталог 31 → **33**: `create_view_filter`, `set_filter_overrides`; пак `filters`
больше не задел (15 populated + 5 reserved).

- `create_view_filter` — generic DSL: `categories` (OST_ или локализованное имя) +
  `conditions` (`field: family|type|level|room|system|parameter`, `op` eq/neq/
  contains/…/gt/ge/lt/le/hasvalue/novalue, `value`, `logic: and|or`) +
  `visibility: none|hide|show`. Фильтр переиспользуется по имени (idempotent),
  имя санитизируется от запрещённых Revit символов (с warning), фильтруемое
  множество параметров берётся из `ParameterFilterUtilities`, `level` для стен
  падает на `WALL_BASE_CONSTRAINT`; per-section отчёт и read-back по каждому виду.
- `set_filter_overrides` — цвет поверхностей/линий/разреза, штриховка, прозрачность,
  halftone, `clear`; delta-семантика (сохраняет текущие overrides), `color`
  ставит solid fill + цвет; неизвестный фильтр возвращает `knownFilters`,
  неизвестная штриховка — `knownPatterns`.
- Undo: новые ключи change item — `filter` (снимок фильтров вида old/new и полное
  восстановление) и `filter_created` (удаление созданного фильтра); поддержано в
  `undo_last` (partial `UndoLastTool.Filters.cs`).

Гейты фазы:

| Проверка | Результат |
|---|---|
| Unit `Agent.Services` + `Agent.Routing` | 288/288; routing 120 запросов: top-1 96.7 %, top-5 100 %, p95 1.64 мс |
| Integration R25 `AgentFilterToolsTests` | 13/13 (dry-run/apply/reuse, level-правило, ошибки, undo фильтра и overrides) |
| Eval replay (все 46 задач) | 46/46, schema 149/149, unauthorized 0, gate PASS |
| Новые eval-задачи | `dry_create_filter`, `dry_filter_overrides` — PASS |

Не проверено: полный unit/integration sweep и 7 конфигураций (финальный гейт),
ручной UI-тест — manual pending.

## ⚡ Сессия «новые инструменты» — Фаза 1: read breadth (2026-11-18)

Каталог 27 → **31**: добавлены `list_sheets`, `get_sheet_content`,
`list_schedules`, `get_schedule_data`. Паки `sheets` и `schedules` больше не
заделы (14 populated + 6 reserved, общее число паков 20 неизменно).

- `list_sheets` — номер/имя/размер мм/число видов и ведомостей/placeholder;
  `limit`+`offset`+`returned`+`nextOffset`+`truncated`; `fields`-проекция с
  `ignoredFields`/`availableFields`.
- `get_sheet_content` — видовые окна (координаты и размер в мм) и размещённые
  ведомости; поиск по `sheetId`/номеру/имени, неоднозначное имя → `ambiguous_sheet`
  с `candidates`; пагинация по видам.
- `list_schedules` — имя, категория (`OST_*` + локализованное имя), видимые поля,
  число строк, `isKeySchedule`/`isTemplate`/ревизионная, номера листов;
  `unknownCategories` не выбрасываются молча.
- `get_schedule_data` — колонки (`heading` + `parameterName` + `fieldType`) и
  страница строк; строка заголовков и пустая строка названия распознаются и
  пропускаются (`headerRowSkipped`); чтение через `TableView.GetCellText` с
  fallback на `TableSectionData`, значения — как в ведомости.

Гейты фазы:

| Проверка | Результат |
|---|---|
| Unit `Agent.Services` + `Agent.Routing` | 288/288 |
| Routing (110 реальных запросов) | top-1 96.4 %, top-5 100 %, p95 1.68 мс, false tool 0 |
| Integration R25 `AgentSheetScheduleToolsTests` | 9/9 (лист создаётся загрузкой Title Block из контент-библиотеки, если в шаблоне его нет) |
| Eval replay новых задач | 4/4 (`read_sheets`, `read_sheet_content`, `read_schedules`, `read_schedule_data`), schema 4/4 |

Не проверено: полный unit/integration sweep (финальный гейт), ручной UI-тест —
manual pending.

## ⚡ Фидбэк-тул удалён + стресс-тест №3 (2026-11-18, текущая сессия)

По решению владельца `report_tooling_feedback` удалён полностью: из системного
промпта, карточек, паков, каталога, ядра, DI, eval-фикстур и живого dogfood-теста.
Таблица `agent_feedback` осталась только как исторический архив (схема v1
неизменна); обратная связь теперь запрашивается текстом в чате.

Исправления из стресс-теста №3:

- **`discard_change_set` + `dryRun`**: раньше `dryRun` молча выбрасывался и
  change set реально закрывался. Теперь `dryRun=true` (по умолчанию) — preview
  `{dryRun:true, wouldDiscard:true, changeSetId, items}`, ничего не закрывается;
  `dryRun=false` требует подтверждения и только тогда закрывает. `SupportsDryRun=true`.
- **`<progress>` теперь содержит контекст**: `round`, `elapsed_minutes`,
  `context_tokens`, `context_limit` — модель видит приближение к порогу сжатия.
- **`list_categories`**: `limit`/`offset`/`returned`/`nextOffset`, `truncated`
  честный (было: limit игнорировался).
- **`copy_view_settings`**: per-section отчёт `sections[{section,status,reason}]`
  (copied/skipped/failed) и `detail` с причинами skip — потерянный crop больше
  не выглядит как успех.
- **`query_elements`**: ключи `parameters` локализованы (`Definition.Name`),
  совпадают с `get_element_details`/`set_parameters`; English label — fallback.

## ⚡ Фидбэк самого агента: где лежит и как читать (2026-11-18)

`report_tooling_feedback` пишет в `%APPDATA%\Roaming\SmartCon\Agent\agent.db`,
таблица `agent_feedback` (дубль — `smartcon.log`). Автоматического читателя нет:
`AgentFrictionAnalyzer` зарегистрирован в DI, но нигде не вызывается — таблица
была write-only. На 2026-11-18 в ней 22 записи (включая стресс-тесты №1/№2).

Правило для следующих сессий: **перед работой над инструментами и после каждого
стресс-теста читать `agent_feedback`** (SQL/DB browser), а не полагаться только
на текст, присланный в чат. Быстрый запрос:
`select created_at, area, rating, tool_name, issue, suggestion from agent_feedback order by created_at desc limit 50;`

## ⚡ Стресс-тест №2: исправления (2026-11-18, текущая сессия)

- **Критично — подмена тула.** Провайдер иногда присылает несколько tool-calls
  без `index`; аккумулятор сводил их в index 0: имя последнего + склеенные
  аргументы → исполнялся чужой инструмент со старым аргументом и `success:true`.
  Теперь вызовы ключуются по `id`, при его отсутствии аллоцируются по порядку,
  id-less дельта продолжает последний вызов (`LlmResponseAccumulator`).
- `plan_fidelity_mismatch` возвращает `steps[]` со всеми dry-run диффами
  (s1–s4 больше не теряются); `plan_dry_run_failed` — тоже.
- `readBackTool/ArgumentsReadBackJson/ReadBackExpectations` исключены из
  `planDigest`: их можно добавить после preview, не подтверждая план заново.
- `rename_views`/`rename_levels` при чистом no-op пишут «No changes: N target(s)
  already have the requested names (see skipped[])», а не «see failed[]».
- `query_elements`: пустой `parameters: {}` больше не выводится (карточка
  опускает поле, если значений нет).

## ⚡ Автономные ходы без лимитов (2026-11-18, текущая сессия)

Жёсткий лимит 8 раундов удалён полностью. Ход выполняется до одного из событий:
модель ответила без tool-calls, пользователь нажал Stop (CancellationToken),
`LoopGuard` заблокировал повторяющийся вызов (3 одинаковых → block, 5 → abort).
Никакого лимита по времени/шагам нет — как в других харнесах.
Модель видит `<progress round="N" elapsed_minutes="M" />` в `<turn_context>`.
Контекст не растёт безгранично: compaction ladder срабатывает перед каждым
раундом.

Замечания из ручного прогона закрыты:

- `load_tool_pack(packs:[...])` — несколько паков одним вызовом;
- `find_tools` с пустым query возвращает компактный listing (name/description/
  pack/risk), схема больше не требует query; `alreadyAvailable` учитывает
  loaded+pending;
- `get_element_details`: `maxParametersPerElement` (default 50, max 500),
  `parameterNameContains`, `excludeDerived` (read-only/formula), реальный
  `parametersTruncated`;
- `summarize_elements`: `topN` (default 20), `excludeInternal` (default true,
  выкидывает «(no category)»), `groupsTotal`/`groupsSkippedInternal`;
- `get_view_template_details`: `fields:["settings"]` отдаёт все настройки,
  `fields:["template"]` — всю карточку, поддержан `settings.<name>`.

## ⚡ H1 ЗАКРЫТ полностью (2026-11-18, текущая сессия)

### Коммиты

- `3ed6d229` — легаси v1 и rollout-флаги удалены, поведение одно (H1).
- `6b67f859` — **H1-14** store-and-reference: `tool_result_blobs`, `resultHandle`,
  `read_tool_result(handle, offset, limit, path)`, 30 дней, привязка к conversation.
- `f8cec03e` — **H1-15/H1-16/H1-17**: mask → 9-section checkpoint → hard shrink,
  protected tools, constraint pinning, `compactions` + restore после рестарта,
  circuit breaker (3), chunked summarizer (`SummaryMaxInputTokens`).

### Что теперь умеет харнес

- Каталог 28 тулов: core 9 + BM25 top-5 + `load_tool_pack`; мелкий каталог (≤8)
  отдаётся целиком. `find_tools` — только discovery.
- Обрезанные результаты не теряются: полный redacted JSON в blob, модель
  дочитывает страницами, в т.ч. после рестарта.
- Длинная сессия: старые tool-результаты маскируются digest'ом (handle
  сохраняется); выше порога — чекпойнт из 9 секций с pinned goal/constraints/
  change sets; после рестарта чекпойнт подставляется в промпт.
- Падение summarizer'а не ломает ход: fallback на маску/shrink, после 3 ошибок
  summary-ступень отключается на сессию.

### Гейты (заполняется финальным прогоном)

| Проверка | Результат |
|---|---|
| `build-all-configs.ps1` (R19–R27) | R19/R21/R25/R26/R27 — 0 ошибок; R23/R24 упали на net48-API (`Contains(StringComparison)`), после фикса пересобраны отдельно — 0 ошибок |
| Unit `SmartCon.Tests -c Debug.R25` | 3743/3743 |
| Routing eval (102 запроса) | top-1 95.1 %, top-5 100 %, p95 2.05 мс |
| 1000 карточек | top-1 100 %, top-5 100 %, p95 21.4 мс |
| Prompt с паками | 4 285 токенов (≤8k) |
| Compaction eval (10 кейсов H1-18) | 10/10 |
| `AgentCompactionStorageTests` (Revit R25) | 5/5 |
| Integration R25 (полный, replay) | **332 passed, 12 skipped, 0 failed** (344) |
| Eval replay 40 задач | PASS (тест `AgentEvalTests`, replay-режим) |
| Integration net48 / R27 | **не перезапускались в этом проходе** — владелец остановил долгие прогоны. net48 сьют не содержит Agent-тестов (исключены конфигурацией); R27 использует тот же net10 Agent-код, что собран в sweep |

### Осталось

- Ручной UI-тест в Revit (обязателен): длинная сессия → чекпойнт; трим >40k →
  `read_tool_result`; рестарт Revit → чекпойнт/WorkingState; loop guard/timeout.
- Деплой `build-and-deploy.bat` — только владелец, после ручного теста.

## ⚡ Легаси v1 удалено (2026-11-18, текущая сессия)

Решение владельца: продукт новый, пользователей и совместимости нет. Удалены
`ToolCatalogV2`, `ContextCompactionV2`, `WorkingStateEnabled`, `UseToolRouter` и все
ветки старого поведения: legacy `find_tools` (с расширением каталога), короткий
список RuleToolRouter как основной режим, выключенный WorkingState. Поведение
одно — H1. Env-выключателей больше нет; остались только параметры контекста
(`SMARTCON_AGENT_CONTEXT_TOKENS/_THRESHOLD`, `SMARTCON_AGENT_SUMMARY_*`).
Мелкий каталог (≤8 тулов) отдаётся целиком без retrieval.

## ⚡ H1 session — H1.0 + H1.1 закрыты (2026-11-18, текущая сессия)

### Где мы сейчас

- Ветка `develop`. Эпик H1: **H1.0 и H1.1 закрыты** (H1.2 — context compaction — следующий).
- Коммиты сессии: `8a2406cb` (docs: runbook H1), `cecac3b9` (test: P2-8 key names), `ea059c12` (**H1.0**), `d289da83` (H1.0 handoff), `8dd01da1` (**H1.1**).
- H1.1 включает: карточки 27 тулов, 20 паков, Router v2 + `find_tools`/`load_tool_pack`, tolerant repair, loop guard/timeout/watchdog, routing eval 200 запросов, 1000-карточный тест, cache stability.

### H1.0 — что сделано

- **H1-01**: ordered migrations (`AgentSchema`, v1→v2→v3a), backup `SqliteConnection.BackupDatabase` перед миграцией, транзакция на миграцию + `PRAGMA user_version`, self-heal текущей версии, immediate-transaction + перечитывание версии (гонка нескольких Revit). Новые таблицы `compactions`, `tool_result_blobs`, `working_states`; колонки `token_usage` (cached_tokens, cache_write_tokens, latency_ms, status, request_id, compaction_id, estimated_prompt_tokens), `messages.turn_id`, `tool_calls.repairs_json`. FTS5 smoke на net48 (`IntegrationTests/Storage/AgentSchemaStorageTests`).
- **H1-02**: оценка промпта system+messages+tools (`AgentTokenEstimator.EstimateRequest`), `AgentTokenCalibration` (EMA по `usage.prompt_tokens`), cached/cache-write из OpenRouter/OpenAI и DeepSeek форм, ровно одна запись `token_usage` за ход (ok/error/cancelled), latency/request_id.
- **H1-03**: `WorkingState` по §7.3 (+Document*), `AgentTurnContextBuilder` (`<turn_context>`, XML-escape, лимит 2000), `AgentWorkingStateUpdater` (goal/constraints/selection/change sets/touched ids/plan steps; dry-run не считается применённым), `SqliteAgentWorkingStateStore`, инъекция только в payload, восстановление после рестарта.
- **H1-05**: флаги `ToolCatalogV2`, `ContextCompactionV2`, `WorkingStateEnabled` — **включены по умолчанию** (решение владельца: старая логика не нужна); env `SMARTCON_AGENT_*` со значением `0`/`false` — аварийный выключатель.

### Гейты H1.0 (все зелёные)

| Проверка | Результат |
|---|---|
| `build-all-configs.ps1` (R19–R27) | 7/7, 0 warnings / 0 errors |
| Unit `SmartCon.Tests -c Debug.R25` | **3579/3579** |
| Integration R25 (replay) | **327 passed, 12 skipped, 0 failed** (339) |
| Integration net48 (Revit 2023) | **246 passed, 12 skipped, 0 failed** (258) |
| Integration R27 (net10) | **313 passed, 27 skipped, 0 failed** (340) |
| Eval replay 40 задач | **40/40 PASS**, routing 100 %, schema 144/144 |
| Валидатор `general` (H1.0) | **PASS** (2 HIGH + MEDIUM исправлены фикс-проходом) |

### Схема БД и флаги

- `user_version = 3` (v3a). При старте на v3 выполняется идемпотентный self-heal колонок.
- Флаги H1 (`AgentLlmSettings`) **включены по умолчанию** — это новая логика харнеса. `0`/`false` в env (`SMARTCON_AGENT_TOOL_CATALOG_V2`, `SMARTCON_AGENT_CONTEXT_COMPACTION_V2`, `SMARTCON_AGENT_WORKING_STATE`) — аварийный выключатель. Живая проверка нового дефолта: `AbstractTaskLiveTests` 1/1 (модель сама нашла инструменты через `find_tools` → `load_tool_pack`, 21 с).
- Расширения сверх runbook §7.5 (аддитивные): `working_states`, `token_usage.estimated_prompt_tokens`, `tool_calls.repairs_json`.

### H1.1 — что сделано и метрики

- **H1-06**: `AgentToolDefinition` расширен (Pack, Purpose, WhenToUse, WhenNotToUse, Examples, Synonyms, NegativeExamples, Idempotent, TimeoutMs, RequiredContext, Bulk-алиас); `AgentToolCards` — 27 intent-карточек (ADR-076, без 1:1 обёрток API); `ToolCardRouter` — BM25 по карточке с negative-примерным штрафом.
- **H1-07**: `AgentToolPacks` — 20 паков (11 заполненных + 9 задел), каждый тул ровно в одном паке; core = 9 инструментов ADR-075.
- **H1-08**: при `ToolCatalogV2=1` payload = core 9 + загруженные + BM25 top-5 (≤16); `find_tools` — только discovery (top-5, pack-фильтр, listing `fields=["name","description"]`), не расширяет payload; `load_tool_pack` пишет pending/loaded, применяет на границе раунда и логирует cache invalidation.
- **H1-09**: валидация аргументов перед выполнением + tolerant repair (aliases/coerce/scalar↔array/unknown fields), `_repairs[]` в tool-result и `tool_calls.repairs_json`, unknown-tool suggestions.
- **H1-10**: `LoopGuard` (2 одинаковые ошибки → warn, 3 одинаковых вызова → `loop_detected`, 5 → abort хода), per-tool timeout (cooperative cancel + grace), watchdog `AgentAwaitableEvent`.
- **H1-11/12/13** (детали — `docs/agent/00-strategy/eval.md`): rules — top-1 95 %, top-5 100 %, p95 1.28 мс; decisions live (20 запросов) — 95 %/100 %; 1000 карточек — top-5 100 %, p95 11 мс; prompt с паками 4 118 токенов (≤8k); static/tool-set hash стабильны. **Вердикт: vector-ветка (H6) пока не нужна** — BM25 top-14 100 %, видимый каталог 27 < 200.
- **H1-04**: live baseline 5 read + 5 write — 10/10, tool selection 100 %, schema 20/20, prompt 202 173 / completion 4 427 токенов.

### Гейты H1.1 (все зелёные)

| Проверка | Результат |
|---|---|
| `build-all-configs.ps1` | 7/7, 0 warnings / 0 errors |
| Unit `SmartCon.Tests -c Debug.R25` | **3714/3714** |
| Integration R25 (replay) | **327 passed, 12 skipped, 0 failed** (339) |
| Integration net48 (Revit 2023) | **246 passed, 12 skipped, 0 failed** (258) |
| Integration R27 (net10) | **313 passed, 27 skipped, 0 failed** (340) |
| Eval replay 40 задач | **40/40 PASS** |
| Routing eval 100 + 100 | top-1 95 %, top-5 100 %, p95 1.28 мс; 1000 карточек top-5 100 % |

### Что осталось (следующий эпик/сессия)

- **H1.2**: store-and-reference (`tool_result_blobs` + `read_tool_result`), masking, compaction v2 (checkpoint-summary, constraint pinning, 9 секций), compaction eval.
- **H1-14** (опционально, если останется время): store-and-reference без compaction v2.
- Ручной UI-тест в Revit — **manual pending**: флаг `ToolCatalogV2=1` (find/load/freeze), `WorkingStateEnabled=1` (turn_context/restart), timeout/loop guard, запись+undo.
- Валидатор `general` повторно не запускался по решению владельца (H1.0 — PASS, фиксы внесены).

### Риски / заметки

- Документация H1/H6/H7 и ADR-075/076 зафиксированы отдельным коммитом `bab714ca` (другая сессия); H1.1 реализован уже с учётом ADR-075 (core 9, listing mode, vector — H6).
- `%APPDATA%\SmartCon\Agent\agent.db` на dev-машине может быть v3 без новых колонок — self-heal лечит при старте.
- Ручной UI-тест в Revit **manual pending** (флаги OFF/ON, рестарт, смена conversation, timeout/loop guard).
- `AgentContextBudgetTests` (P2-8) исправлен отдельным коммитом `cecac3b9` — это baseline-фикс, не H1.0.
- H1.1 gate-валидатор `general` повторно не запускался (решение владельца «ревалидация не нужна»); H1.0 — PASS, все найденные HIGH/MEDIUM исправлены.

---

## ⚡ Передача продолжения (финал, 2026-11-18)

### Где мы сейчас

- **Порядок чтения после суммаризации:** этот блок → `git status` → `git log -1` → таблица фаз ниже → `docs/agent/00-strategy/initial-improvement-runbook.md`.
- **Актуальные документы:** `docs/agent/README.md` (индекс) → `00-strategy/harness-ideal-plan.md` (стратегия) → `00-strategy/harness-development-approach.md` (процесс) → `00-strategy/initial-improvement-runbook.md` (эпик H1).
- Ветка `develop`. Фазы P1/P2/A/B/C + A4 и финальный полиш завершены. Коммиты: `c64a9109` (plan validation, bypass modes, tool discovery), `5fe44e41` (UI polish + live-session feedback), запушены в `develop`.
- Валидатор `general` прогонялся 4 раза: 1–3 FAIL → исправлены; 4-й FAIL (локализация Approval UI, `Risks` не показывались в overlay, `AgentEvalRunner` >600 строк) → **всё исправлено**. 5-й прогон по решению владельца **не запускался**.
- **НЕ добавлять в коммит:** `opencode.json`, `server/`, `tools/allow-revit-network.ps1` (личное/не по задаче). Секреты — только в `%APPDATA%\AGK\SmartCon\Agent\secrets.env`, в репозиторий не попадают.

### Финальная верификация

| Проверка | Результат |
|---|---|
| Sweep 7 конфигураций (R19–R27) | 7/7, 0 ошибок |
| Полный unit (`SmartCon.Tests`, Debug.R25) | 3502/3502 |
| Integration `AgentPlanExecutionTests` (Revit 2025) | 6/6 |
| Agent integration R25 (полный сьют) | 77 passed, 2 skipped; live-eval упал на 1 стохастическом schema-invalid вызове, replay 40/40 PASS |
| Eval replay 40 задач (Revit 2025) | 40/40, gate PASS |
| Agent integration R27 | 78 passed, 2 skipped, 0 failed |
| net48 (Revit 2023) | 245 passed, 12 skipped, 0 failed (платформенный сьют) |
| A4 offline orchestrator eval | 5/5 |
| Live abstract (`AbstractTaskLiveTests`) | 1/1 (4 раунда, 2× find_tools с context) |
| Decision live (`DecisionsLiveTests`) | 3/3 (pplx free через OpenRouter) |
| Бенчмарк decision-моделей | 27 моделей, free pplx 100 %/100 %, sep 0.990 |

### Что исправлено после 4-го FAIL

- Локализация: 7 ключей `Agent_Approval_*` + `Agent_Confirm_Risks` + 3 `Agent_Risk_*` (ru/en); тест `AgentLocalizationTests` (11 кейсов).
- Confirmation overlay показывает риски (`ConfirmationRisks`/`HasConfirmationRisks`, DangerBrush); прямой `discard_change_set` теперь даёт риск «closing the change set removes the undo path».
- `AgentPlanExecutor`: plan-level auto-approve в `AutoApproveSession` больше не пропускает план с `discard_change_set` (defense-in-depth) + unit-тест.
- `AgentEvalRunner` разбит: `.cs` 535 + `.Toolset.cs` 99 + `.State.cs` 122; `Checks.cs` 522 — все ≤600.
- Оркестратор расширяет каталог после `find_tools` и при равном количестве, если состав отличается (`ExpandsCatalog`).
- Новый bypass-тест `set_view_visibility` (100 views × 3 categories > 200).
- `AgentQueryToolsTests.GetViewSettings_ReturnsSeededPlanSettings` обновлён под C4: `compact=false` для полного payload (R27-падение было устаревшим тестом, не регрессом).

### Полиш по фидбэку владельца и live-сессии (2026-11-18, коммит после c64a9109)

UI:
- Выбор режима подтверждения перенесён из шапки в композер (слева, рядом со скрепкой), как в харнесах; шапка освободилась под «+» и шестерёнку.
- В настройках появился выбор decision-модели (`Не использовать` / `pplx-decider-v1.3:free` / `pplx-decider-latest` / `jev-1.13.0`); выбор сразу применяется и пишется в `secrets.env`. Роутер и risk gate регистрируются всегда: при отключённой модели ведут себя как раньше.
- Поле «Прокси» удалено из настроек, модели и LLM-клиента (вместе с `SMARTCON_LLM_PROXY`).
- Вложения: картинка нормализуется; полностью нулевой alpha-канал клипборда трактуется как «альфы нет» (RGB сохраняется), смешанная прозрачность композитится на белый. Миниатюра, пузырь, полноэкранный просмотр и payload больше не белые; клик по миниатюре открывает просмотр, крестик — круглый, поверх белого, появляется по ховеру и не вылезает за угол.

Agent feedback (P0–P2):
- **P0-1**: digest плана больше не зависит от `dryRun` — ни от `step.DryRun`, ни от `dryRun` внутри `arguments`; preview и apply сходятся. Hint обновлён.
- **P0-2**: read-back понимает `byId[<elementId>].prop` (поиск карточки по id/viewId/elementId/levelId); в результате появились `expectationsChecked` и счётчики verified/mismatch по шагам — пустой `verified` больше не путается с «ожиданий не задавали».
- **P0-3**: `find_tools` ранжирует без принудительных core-read (они съедали весь лимит), возвращает write-инструменты, `risk`, `alreadyAvailable` и warning при write-интенте без write-результата.
- **P1-4**: `execute_plan` preview/apply отдаёт per-item `changed/skipped/failed` (id, name, before, after) из dry-run/apply.
- **P1-5**: `apply_view_template` dry-run отдаёт `delta`: scale before→after, hiddenCategoriesAdded, controlledParameters, overwritten, previousTemplate.
- **P1-6**: `list_views` возвращает `browserFolder`/`inBrowser`, фильтр `folders`, группировку `byFolder` (через `BrowserOrganization.GetCurrentBrowserOrganizationForViews`).
- **P2-7**: `report_tooling_feedback` всегда в shortlist; неизвестные поля возвращаются в `ignoredFields`.
- **P2-8**: `substitutedFields` в `list_views`/`get_view_settings` — видно, что `template` заменён на `templateName`.
- **P2-9**: default limit `list_views` = 200, `truncated` уже был.

Повторный live-фидбэк (4 замечания):
- `rename_views` и `apply_view_template` добавлены в always-on shortlist (`RuleToolRouter.CoreTools`), лимит shortlist поднят до 16 — штатные write-инструменты больше не требуют `find_tools` в начале задачи.
- `execute_plan` apply-отчёт берёт per-item diff из `ApplyResult` (не из dry-run), поэтому в применённом плане `items.dryRun=false`; hint при `expectationsChecked=0` больше не говорит «All expectations verified», а явно сообщает, что проверок не задавали.
- `list_views` summary/byFolder: сегменты `???` от `FolderItemInfo.Name` разрешаются через параметр (`LabelUtils.GetLabelFor` / имя элемента); для Revit 2026+ используется `GetGroupingParameterIdPath()` вместо устаревшего `ElementId`.
- `fields` возвращает ключи ровно с теми именами, которые запросили (`type` → `type`, не `viewType`); в схемах `list_views`/`get_view_settings`/`get_view_template_details` перечислены допустимые имена и алиасы.

### Что осталось (не блокеры финального коммита)

- 5-й прогон валидатора `general` не проводился — решение владельца.
- Ручная проверка UI в Revit: режим подтверждения в композере, выбор decision-модели, вложения (превью/ховер/просмотр), строка рисков в overlay, сброс режима при смене документа.
- D: `execute_plan` пока не входит в 40 eval-задач; live-запись plan-кейсов не делалась. `docs/agent/00-strategy/eval.md` содержит базовые метрики и заготовку «до/после».
- Новые Agent-модели/интерфейсы не перенесены в `docs/domain/**` — по правилам проекта docs правятся после ручной приёмки.
- Production `execute_plan`: single-use nonce/expiry остаются только в прямом API; в tool-пути digest-binding + внешнее подтверждение.
- L6 mismatch не помечает change set; `plan_id` отдельным полем audit не пишется (digest в `ArgsHash`).
- Не запускать `build-and-deploy.bat` без ручной приёмки владельца.

### Команды live-проверок (ключ из secrets.env, не печатать)

```bash
# Decision layer
SMARTCON_DECIDER_LIVE=1 SMARTCON_DECIDER_ENABLED=1 SMARTCON_DECIDER_TIMEOUT_MS=20000 \
  dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 --filter "FullyQualifiedName~DecisionsLiveTests"

# Абстрактная задача (реальная модель + find_tools)
SMARTCON_AGENT_LIVE=1 SMARTCON_DECIDER_ENABLED=1 SMARTCON_DECIDER_TIMEOUT_MS=20000 \
  dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 --filter "FullyQualifiedName~AbstractTaskLiveTests"
```

---

## Текущий статус (обновляется после каждой фазы)

Ветка `develop`. Прогон завершён; новый цикл — `docs/agent/00-strategy/initial-improvement-runbook.md`.

### Сделано и проверено

| Фаза | Статус | Проверка |
|---|---|---|
| 0 Baseline | ✅ | unit 3368/3368; Agent R25 65 pass / 2 skip, 0 fail |
| P1 hotfix C1–C4 + A1 | ✅ | C2: fallback на имя элемента для ElementId-параметров; C4: `fields`/`compact` у `get_view_settings`/`get_view_template_details`; C1 регресс-тест `list_levels.total == overview.levelCount`; C3 тест «Schedule-шаблон на FloorPlan → template_not_applicable»; A1 правило «имя не переносится шаблоном» в промпте и описаниях |
| P2 routing v1 | ✅ код + метрика | `AgentToolDefinition.Category/Tags/SupportsBulk`; `AgentToolCatalog` (Core); `RuleToolRouter` (BM25, core-read + cap 14, fallback на полный каталог); `find_tools` meta-tool теперь ходит через настроенный `IToolRouter` (JEV/pplx при включении) и принимает `context` — агент ищет инструменты по требованию, уже понимая задачу; оркестратор шлёт shortlist (`SMARTCON_AGENT_TOOL_ROUTER=0` отключает); decision-слой (OpenRouter Decisions, `perplexity/pplx-decider-v1.3:free`) — live-тесты `DecisionsLiveTests` 3/3 + бенчмарк 27 моделей (`docs/agent/00-strategy/decisions-benchmark.md`): free-модель на потолке (100 %/100 %, sep 0.990), JEV/GPT-6 Luna недоступны из региона |
| A Eval-харнес | ✅ | 40 задач (15 read / 15 dry-run / 10 apply+undo, включая 5 регресс-кейсов); запись fixtures live, replay без ключа/сети; отчёт JSON+MD в `%LOCALAPPDATA%\AGK\SmartCon\Agent\eval\<ts>`; порог-гейт; routing accuracy считается |
| A4 Offline orchestrator | ✅ | `SmartCon.Tests/Agent/Eval/AgentOfflineOrchestratorEvalTests` 5/5: multi-round tool loop + pairing, лимит 8 раундов, компакция (digest, последние 4 целы), find_tools→полный каталог, `execute_plan` preview→apply с `planDigest` |
| B Plan validation | ✅ | L0 (`AgentPlanValidator`: tool unknown, args/schema, duplicate, cycle, >20, destructive, nested plan), L1 preflight (`AgentWriteToolBase.PreflightCore` + `apply_view_template`), L3 (в `AgentPlanExecutor`: nonce/expiry/digest, ledger покрыт unit-тестами; production-путь `execute_plan` связывает preview→apply каноническим `planDigest`, привязанным к документу), L4 fidelity, L6 read-back; `AgentPlanExecutor` в Core (partial: `.cs` + `.Steps.cs` + `.Audit.cs`), `PreviewAsync`; meta-tool `execute_plan` (dryRun/apply, строгая схема аргументов, DI, промпт с `planDigest`, каталог, always-in-shortlist); unit `AgentPlanValidatorTests` + `AgentPlanExecutorTests` + `ExecutePlanToolTests`; integration `AgentPlanExecutionTests` 6/6 (preflight missing view, end-to-end 2-step plan→approve→apply→read-back→undo, discard-after-conflict→next undo, discard without approval, invalid changeSetId, already rolled-back set). L2 critic не включён (опционален по ранбуку) |
| C Bypass/надёжность | ✅ | `AgentApprovalMode` (ConfirmWrites/AutoApproveSession/ReadOnly) + UI: ComboBox в шапке, баннер с кнопкой «Выключить», сброс при смене документа (VM + orchestrator, unit-тесты); гейт в `AgentToolExecutor` (Destructive, консервативный affected: `configure_views` items×секции, `copy_view_settings` targets×секции, `remap_parameter_values` mapping×limit, `set_view_visibility` categories+worksets; nested-план суммируется, Destructive/allowDestructive/неизвестный шаг → confirm; `discard_change_set` всегда confirm; risk gate fail-closed — unit-тесты); `AgentRedactor` JSON-aware (секретные ключи любого типа, включая `x-api-key`/`openai_api_key`/numeric/object; JSON всегда валиден, кириллица не экранируется) + redaction tool_calls в оркестраторе; `AgentTextSanitizer.SanitizeJson`; `discard_change_set` (I-01: document-id через `context.Revit.RaiseAsync`, `invalid_change_set_id`, `change_set_not_applied`) + `MarkDiscardedAsync` (unit + integration) |
| Валидация B+C (`general`) | ✅ FAIL×4 → исправлено | 1-й FAIL — net48, I-01, L3, redaction, UI, shortlist, размер файла → исправлено. 2-й FAIL — JSON-redaction numeric/prefixed, обход лимита 200 через configure/copy/remap, digest не канонизирован/не привязан к документу, prompt без `planDigest`, нет schema-валидации плана, discard авто-одобрялся → исправлено. 3-й FAIL — redactor падал на обычной строке с `token:`/`Bearer`, вложенный discard обходил confirm, affected риск-плана = 0 → исправлено. 4-й FAIL — локализация `Agent_Approval_*`, Risks в overlay, `AgentEvalRunner` >600 → исправлено. 5-й прогон не запускался (решение владельца); финальная верификация: sweep 7/7, unit 3494/3494, integration 6/6, eval replay 40/40, R27 Agent 78/2skip/0fail |

### Известные ограничения после валидации B+C

- Production-путь `execute_plan` использует digest-binding + внешнее подтверждение на каждый вызов; single-use nonce/expiry остаются в `AgentPlanExecutor` для прямого API и покрыты unit-тестами, но в tool-пути не задействованы (осознанно, чтобы не плодить второй диалог).
- L6 mismatch не помечает change set (только `Warn` + audit `plan#apply=mismatch`); поля предупреждения в схеме набора нет.
- `plan_id` не пишется отдельным полем audit; след плана — 16-символьный digest в `ArgsHash`.
- `execute_plan` пока не входит в eval-задачи (B5-метрики плана соберутся после добавления кейсов в фазе D).
- Новые Agent-модели/интерфейсы не перенесены в `docs/domain/**` (по правилам проекта — долг; docs правятся после ручной приёмки).
- UI режимов подтверждения требует ручной проверки в Revit (ComboBox + баннер + сброс при смене документа).
- Decision-модели: бенчмарк — `docs/agent/00-strategy/decisions-benchmark.md`; JEV/GPT-6 Luna недоступны из региона (451/502), free pplx — на потолке точности на нашем наборе.

### Eval-база (зафиксирована)

- Живая запись: 40 fixtures в `src/SmartCon.IntegrationTests/Agent/Eval/Fixtures/` (модель `deepseek/deepseek-v4.1-flash-1017`, OpenRouter).
- Replay (без ключа): **40/40**, read 100 %, write dry-run 100 %, apply+undo 100 %, schema-valid 144/144, unauthorized 0, routing accuracy 97.5 %, gate PASS.
- Первый live-прогон был 34/40 (85 %); после уточнения ожиданий (configure changed=1 на вид, альтернативы `set_parameters`/`configure_views`, honest `no_active_view`, retry на пустой ответ) — 40/40.
- Отчёт последнего прогона: `%LOCALAPPDATA%\AGK\SmartCon\Agent\eval\` (последняя папка по времени).

### Важные решения этой сессии

- **Рабочий контекст — 500k токенов** (`AgentLlmSettings.ContextWindowTokens`), порог компакции 0.7 → старые tool-результаты сжимаются после ~350k (было 200k/0.6 = 120k). Переопределяется `SMARTCON_AGENT_CONTEXT_TOKENS` (50k..2M) и `SMARTCON_AGENT_CONTEXT_THRESHOLD` (0.3..0.95). DeepSeek стабильно работает на 500k+; system prompt и схемы инструментов в оценке пока не учитываются (на этом масштабе это <1%).
- **Decision-слой — OpenRouter Decisions API** (`POST https://openrouter.ai/api/alpha/decisions`), модель `perplexity/pplx-decider-v1.3:free`: проверено живьём (HTTP 200 без VPN, `choice`+`noul`, `cost: 0`). JEV/TypeSafe остаётся альтернативным провайдером; всё fail-closed. Причина: JEV early access без VPN недоступен.
- **Sys prompt вынесен в Core** (`AgentSystemPrompt`) — eval гоняет ровно production-промпт; `AgentPromptBuilder` делегирует.
- **Eval изолирует задачи**: на каждую задачу новый project document + сид `SC_Eval_*`; fixtures хранят `idMap` (kind/name/category/index) и на replay ремапят ElementId по имени — иначе записанные id невалидны в новом документе.
- **Await в eval без `ConfigureAwait(false)`**: пробник подтвердил, что TUnit-исполнитель возвращает продолжение на поток Revit (иначе транзакции apply/undo уходили бы в пул).
- Порог-гейт не срабатывает на отсутствующие категории (подмножество `SMARTCON_AGENT_EVAL_ONLY`) — иначе subset-прогон всегда «красный».

### Следующие шаги (в порядке)

1. Ручная проверка UI режимов подтверждения в Revit (ComboBox, баннер, «Выключить», риски в overlay, сброс при смене документа).
2. D: добавить plan-кейсы (`execute_plan`) в eval, записать live-fixtures, метрики до/после в `eval.md`.
3. Опционально: L2 critic (`SMARTCON_AGENT_PLAN_CRITIC=1`).
4. Перенести новые Agent-модели/интерфейсы в `docs/domain/**` после ручной приёмки.
5. Деплой (`build-and-deploy.bat`) — только вручную владельцем.

### Заметки для продолжения после суммаризации

- Decision-layer live: `SMARTCON_DECIDER_LIVE=1 SMARTCON_DECIDER_ENABLED=1 SMARTCON_DECIDER_TIMEOUT_MS=20000 dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 --filter "FullyQualifiedName~DecisionsLiveTests"` (ключ берётся из `secrets.env`, в тесте не печатается). Без `SMARTCON_DECIDER_LIVE=1` тесты — no-op; для TypeSafe/JEV нужен `SMARTCON_DECIDER_PROVIDER=typesafe` + ключ.
- Eval запуск: `SMARTCON_AGENT_EVAL_REPLAY=1 dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*AgentEvalTests*/*"`.
- Live-запись: `SMARTCON_AGENT_EVAL_RECORD=1` (+ ключ), подмножество — `SMARTCON_AGENT_EVAL_ONLY=id1,id2`; без `SMARTCON_AGENT_EVAL_SOFT=1` тест падает на порогах.
- `apply_copy_settings` fixture перезаписан после добавления retry на пустой ответ — не откатывать.
- Партиальные классы runner: `AgentEvalRunner.cs` (цикл), `.Toolset.cs` (инструменты + routing + правила), `.State.cs` (снимки состояния), `.Checks.cs` (оценка), `.Report.cs` (отчёты).

---

# Предыдущая сессия — 2026-11-18 (Phase A–F: write layer, confirmation gate, context budget, dogfooding)

## Состояние

Ветка `develop`. Коммиты этой сессии:

| Коммит | Содержание |
|---|---|
| `d79ee3d7` | feat(agent): write layer — 10 write-tools, confirmation gate, change sets, undo_last, integration/unit tests |
| `b1d30ccb` | feat(agent): context budget — ToolResultBudget, лимиты, broad-query guard, проекции, компакция истории |
| (следующий) | feat(agent): dogfooding — report_tooling_feedback, tool_calls/token_usage, AgentFrictionAnalyzer, live dogfood, улучшения по фидбэку |

### Что работает

- **Read-слой (P1)**: 12 инструментов (`get_project_overview`, `list_views`, `list_levels`,
  `list_categories`, `get_selected_elements`, `query_elements`, `get_element_details`,
  `summarize_elements`, `list_view_templates`, `get_view_settings`, `get_view_template_details`,
  `get_current_view_info`).
- **Write-слой (P2)** — `SmartCon.Revit/Agent/Tools/Write/`:
  `rename_views`, `rename_levels`, `apply_view_template`, `set_view_range`, `set_view_crop`,
  `set_view_visibility`, `configure_views`, `copy_view_settings`, `set_parameters`,
  `remap_parameter_values`, `undo_last`.
  Все: `dryRun=true` по умолчанию, отказ при `doc.IsWorkshared` (`workshared_not_supported`),
  лимиты на батч, отчёт `{dryRun, changed[], skipped[], failed[], total, affected, changeSetId?}`,
  change items в `agent.db` для undo.
- **Confirmation gate**: `AgentToolExecutor` требует подтверждения для любого write c
  `dryRun=false`; digest SHA-256 канонического JSON (`AgentActionDigest`); WPF-оверлей
  в `AgentPaneControl.xaml` (`IsConfirmationVisible`, команды Approve/Reject);
  событие `AgentConfirmationService.Requested`; audit-запись `<tool>#confirm`.
- **Context budget (P3)**:
  - `SmartCon.Core/Models/Agent/ToolResultBudget.cs` — структурная обрезка массивов
    (`elements/views/levels/groups/parameters/cards/changed/skipped/failed/items/templates`),
    добавляет `<name>Returned`, `<name>Total`, `truncated`, `hint`; JSON всегда валиден;
    fallback — digest-envelope. Никакого `Substring`.
  - `list_views`: default 50 / max 500, `summaryOnly`, `fields[]`.
  - `query_elements`: default 50 / max 200 (`QuerySpec.DefaultLimit/MaxLimit`), `offset`/`nextOffset`,
    `fields[]`, guard `query_too_broad` (без фильтров и `limit > 50`).
  - `get_element_details`: max 10, `includeTypeParameters=false` по умолчанию,
    `parameterNames[]`, `onlyNonEmpty`.
  - `AgentHistoryCompactor` (SmartCon.Agent): при оценке > 60% от
    `AgentLlmSettings.ContextWindowTokens` (default 200 000) старые tool-сообщения заменяются
    digest-ом `{compacted, success, message, affectedCount, truncated, hint}`; последние 4
    tool-результата сохраняются; факт компакции логируется.
  - System prompt: блоки «Write workflow», «Context budget», «Tooling feedback».

### Dogfooding / телеметрия (P4)

- **`report_tooling_feedback`** — meta-tool (`SmartCon.Agent/Services/Tools/ReportToolingFeedbackTool.cs`,
  Risk=Read): area(tools|prompt|ui), toolName, rating 1–5, issue, suggestion, example →
  таблица `agent_feedback` (schema v2) + `smartcon.log`. Модель вызывает его сама
  (инструкция в system prompt, «один раз на проблему»).
- **`tool_calls`** заполняется оркестратором: id, session_id, tool_name, arguments_json,
  result_json (budget-trimmed), status ok/error, error code, duration_ms.
- **`token_usage`** заполняется по каждому ходу: provider, model, prompt/completion/
  reasoning tokens, cost_usd.
- **`AgentFrictionAnalyzer`** (`SmartCon.Agent/Services/Analysis/`): SQL-отчёт по
  `tool_calls`/`agent_feedback` за N дней + markdown (`ToMarkdown()`): ошибки по кодам,
  truncated, средние длительности, топ проблемных тулов, рекомендации.
- **`AgentLiveDogfoodTests`** (IntegrationTests, read-only, skip без `SMARTCON_AGENT_LIVE=1`):
  реальный OpenAI-compatible клиент (`DogfoodClient`, без ссылки на SmartCon.Agent),
  10–11 русских задач, бюджет 30 LLM-запросов, транскрипт в `%TEMP%\smartcon-agent-dogfood`.
- **Итоги прогона 1** (11 задач, 10 ответов, 3 feedback): исправлены 3 замечания —
  алиасы проекций + `ignoredFields`, округление mm до 0.1, дедупликация параметров и
  единицы в displayValue. Подробно — `docs/agent/AGENT-FEEDBACK.md`.

## Проверено (в этой сессии)

- R25 build: 0/0 (App, Revit, IntegrationTests).
- Unit Agent: **76/76** (`dotnet test ... --filter "FullyQualifiedName~Agent"`).
- Integration Agent R25 (фильтр `/*/*/*Agent*/*`): **45/47** успешно, 2 skip
  (`LiveDogfood_ReadOnlyTooling` без env-флага; `SetViewRange_OnThreeDView` — нет 3D-вида
  в дефолтном шаблоне).
- Live dogfood: прогон 1 — 11 задач, 10 ответов, 3 feedback, 29/30 запросов;
  прогон 2 — 11/11, 1 feedback, 27/30; **прогон 3 — 11/11, 0 feedback, 24/30**
  (все замечания закрыты, см. `AGENT-FEEDBACK.md`).
- Multi-version sweep: **7 конфигураций 0/0** (`tools/build-all-configs.ps1`; R23/R24
  потребовали один фикс net48-nullable в `AgentViewWriteHelper.Deserialize`).
- Полный unit-сьют: **3360/3360**.
- Полный integration-сьют R25: **293/305** успешно, 12 пропущено (контент-зависимые
  скипы nut-файлов и 3D-вида), 0 сбоев, 5m39s.
- net48 (Revit 2023, без Agent-тестов): **245/257** успешно, 12 пропущено, 0 сбоев, 5m15s.
- R27 (Revit 2027, net10, Agent-фильтр): **45/47** успешно, 2 пропущено, 1m02s.

### Ограничение net48 для Agent (важно)

Agent-интеграционные тесты **не компилируются для net48** и исключены из net48-сборки
`SmartCon.IntegrationTests` (`<Compile Remove="Agent\**\*.cs" />` при
`'$(TargetFramework)' == 'net48'`). Причина (pre-existing, не из этой сессии):
на net48 `SmartCon.Dependencies` несёт ILRepack-merged `System.Text.Json` 8.0.6
(ADR-051), а тест-хосту нужен реальный 9.0.0 для TUnit. `JsonElement` из этих двух
сборок — разные типы: любая передача `JsonElement` через границу (а весь Agent API
принимает `JsonElement args`) даёт CS0433/CS1503. Плюс `AgentReadToolsTests` использует
`new ElementId(long)`, которого нет в R23 (есть только в R24+).

Что это значит:
- Agent на net48 **компилируется** (App R23/R24 в sweep — 0/0) и его логика покрыта
  unit-тестами на net8;
- проверка платформы net48 идёт полным сьютом без Agent — это штатная роль сьюта
  («проверка платформы, а не года Revit»);
- если понадобится Agent-прогон на net48: нужен либо отказ от merge STJ для
  IntegrationTests (сломает TUnit), либо `extern alias`-схема + правка тестов на
  `ElementIdCompat` — отдельная задача.

## Ключевые факты Revit API (проверено через Exa + интеграционные тесты)

1. `View.CropBox` setter **игнорирует Transform** — только Min/Max. Для копирования crop
   между видами используем `GetCropRegionShapeManager().GetCropShape()/SetCropShape()`
   (координаты в системе вида) + перенос Min/Max. Перед `SetCropShape` обязательно
   `CropBoxActive = true`, иначе исключение.
2. `View.ViewTemplateId` — setter; `ArgumentException`, если шаблон не подходит виду
   (другое `ViewFamily`) или вид сам шаблон/лист/ведомость. Проверяем `ViewFamily` через
   `ViewFamilyType` и ловим исключение; `overwriteTemplate=false` по умолчанию.
3. `Level.Name` каскадно переименовывает виды с точно таким же именем. Поэтому
   `rename_levels` проверяет `cascade_conflict` (новое имя не должно совпадать с именем
   другого вида).
4. `Element.Name`/`View.Name`/`Level.Name` бросают `ArgumentException` при дубле;
   циклические переименования внутри батча решаются временными именами
   (`AgentRenamePlanner`).
5. `Parameter.Set` — перегрузки Double/Int32/String/ElementId; `StorageType` обязателен
   для выбора. Значения Double в change items храним как **raw internal units** (`"R"`),
   иначе undo восстанавливает мм как футы. `Parameter.IsReadOnly` покрывает formula-параметры
   (свойства `IsDeterminedByFormula` в API нет).
6. `Category.GetCategory(doc, id)` — единственный способ найти категорию по ElementId
   (`Categories.get_Item` принимает только string/BuiltInCategory).

## Грабли, на которые уже наступили

- `ExecuteForTests` в `AgentWriteToolBase` берёт `documentId` из `IRevitContext`, иначе
  `undo_last` не находит change set (он ищет по document id).
- Приватный `Apply` в публичном базовом классе не может принимать `internal` тип —
  `WriteOutcome/WriteBatch/WriteItemReport` сделаны `public`.
- `IReadOnlySet<T>` недоступен на net48 — в `GetElementDetailsTool` используется `HashSet<T>`.
- Raw-интерполяция `$$"""..."""` ломается на JSON с `}}` — в тестах собираем строки
  конкатенацией.
- `ElementId` в тестах — только через `ElementIdCompat.Create/GetValue()` (R23 32-bit).
- Тестовый хост без UI: write-tools вызываются через `ExecuteForTests(args, approved, ct)`,
  `ITransactionService` — на `StubRevitContext`, change set store — `InMemoryAgentChangeSetStore`
  (SQLite-стор живет в SmartCon.Agent, а его нельзя тянуть в IntegrationTests — WPF/RevitAPIUI).

## Валидация write-layer (gate `general`) и фикс-проход

Первый вердикт валидатора: **FAIL** — BLOCKER (невалидная JSON-схема `set_view_visibility`)
+ HIGH-дефекты. Всё закрыто до повторной валидации:

| Пункт | Что было | Что сделано |
|---|---|---|
| BLOCKER | `set_view_visibility` InputSchemaJson не парсился | схема перегенерирована; `AgentToolRegistry.ValidateDefinition` парсит схемы всех инструментов на старте и падает с именем инструмента; тест `WriteTools_InputSchemas_AreValidJsonObjects` |
| HIGH | CancellationToken после commit мог отменить запись change set | персист/`MarkRolledBackAsync` только с `CancellationToken.None` |
| HIGH | частичные изменения `configure_views`/`copy_view_settings` не попадали в change set | `AddChangeItems` + `AffectedCount++` на failure-пути; `all_items_failed` только при `AffectedCount==0`; warning о partial |
| HIGH | rename swap/cycle не работал (preflight отклонял, temp-имена теряли OldValue) | `AgentRenamePlanner.Build` (head-first порядок, temp для циклов), валидация с учётом освобождаемых имён, OriginalName в change item; undo откатывает rename-ы пакетно через тот же плейнер |
| HIGH | crop shape нельзя было откатить | `ViewCropState.ShapeMm` (полигон в мм), `ApplyCropState` снимает shape до CropBox, `ApplyCropShape` включает CropBoxActive/Visible + `IsCropRegionShapeValid` |
| HIGH | `set_parameters` Double без unit писал внутренние единицы молча | отказ `invalid_value` с подсказкой; `unit="raw"` — явный opt-in; no-op по `RawValue`; type-scope change item ссылается на type element |
| HIGH | remap: неизвестная категория → молча пустой фильтр | `unknown_category`, display-name категорий, лимит 20 mapping, confirmation count учитывает `filter.limit` |
| HIGH | смена LLM-провайдера переносила старый ключ | `Apply` при смене провайдера грузит ключ целевого из env/secrets.env или очищает; тест-сим `StoredKeyProvider`, 4 unit-теста |
| MEDIUM | rename_levels: ложный cascade_conflict (swap, floor plan + RCP) | каскад проверяется по полному accepted-set, конфликт только внутри одного `ViewFamily`; тест `RenameLevels_SwapWithCascadeViews_*` |
| MEDIUM | undo молча пропускал неспланированные rename-ы и мог закрыть набор | неспланированные → `undo_conflict`; undo ничего не откатил и failed нет → `RollBackChangeSetId` (набор закрывается) |
| MEDIUM | temp-имя оставалось после сбоя rename | `RestoreTemporaryNames` + warning с `[Action]` |
| MEDIUM | `AgentViewWriteHelper` 635 / `UndoLastTool` 622 строк | partial-разбивка: `AgentViewWriteHelper.Crop.cs`, `UndoLastTool.Renames.cs` |
| MEDIUM | дубли `viewId`/`levelId` в одном вызове | `duplicate_item` |
| MEDIUM | `configure_views` rename на шаблоне/листе | `view_is_template` / `unsupported_view_type` guard |
| HIGH (второй проход) | пометка change set в обход реального коммита (silent rollback Revit) | в ветке `!committed` базового класса `RollBackChangeSetId = null`; маркировка только при `!dryRun` |
| MEDIUM | «залипание» набора: skipped/уже-восстановленные держали set applied | «уже восстановлено» → skipped до conflict-проверки; набор закрывается, если все items skipped; `undo_partial` только по реальным `failed` |

Осознанно не сделано: rename-секции `configure_views`/`copy_view_settings` не используют
плейнер (swap имён внутри одного вызова — только через `rename_views`); `TryReadCropShape`
поддерживает один loop из прямых сегментов (дуги/несколько loop → warning); undo уровня
не записывает каскадно переименованные виды (Revit переименовывает их обратно сам).
Известное ограничение undo: если старое имя навсегда занято новым элементом
(`undo_conflict`), неименованный `undo_last` будет снова и снова возвращаться к этому
набору — обход через явный `changeSetId` более раннего набора; «abandon change set» API
пока нет.

Проверка после прохода (финальный gate): unit 3368/3368; integration Agent R25
65 pass / 2 skip (67 total); R27 (net10) 65 pass / 2 skip; net48 (Revit 2023)
245 pass / 12 skip; 7 конфигураций — 0 ошибок. Валидатор `general` — **PASS**.

## Долг / что осталось

**Следующий прогон:** `docs/agent/00-strategy/initial-improvement-runbook.md` (эпик H1: tool catalog v2 + context budgeting + WorkingState).

1. Ручная приёмка в Revit (владелец): write-flow, оверлей подтверждения, undo.
2. **Фаза G** (частично сделано): token_usage persistence ✅, порог компакции —
   `AgentLlmSettings.ContextWindowTokens` (default 200 000) ✅; plan validation L0–L6
   (preflight → critic → digest) **не начата** — это отдельная крупная фича.
3. **Фаза H**: полный R25 integration-сьют, net48-прогон (`-p:RevitVersion=2023`),
   R27-прогон (Revit 2027 установлен), sweep 7 конфигураций, HANDOFF, коммиты, push.
4. Ручная приёмка записи в Revit (владелец, утро): dry-run → Apply → проверка модели →
   `undo_last`.
5. Ручная приёмка UI-оверлея подтверждения (кнопки Apply/Cancel, digest, таймаут).

## Команды

```bash
# сборка
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25
powershell -NoProfile -ExecutionPolicy Bypass -File "tools/build-all-configs.ps1"

# полный unit
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25

# полный integration R25 / net48 (без Agent) / R27 Agent
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R21 --framework net48 -p:RevitVersion=2023
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R27 --framework net10.0-windows -- --treenode-filter "/*/*/*Agent*/*"

# unit (Agent)
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25 --filter "FullyQualifiedName~Agent"

# integration (Agent), класс в 3-м сегменте фильтра
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*Agent*/*"
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*AgentWrite*/*"
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*AgentContextBudget*/*"

# net48 (Revit 2023 установлен)
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R21 --framework net48 -p:RevitVersion=2023 -- --treenode-filter "/*/*/*Agent*/*"
```

## Итоговый статус фаз

| Фаза | Статус |
|---|---|
| A baseline | ✅ |
| B write-layer (10 tools) | ✅ |
| C confirmation gate + change sets/undo | ✅ |
| D integration + unit тесты write | ✅ |
| E context budget | ✅ |
| F dogfooding + feedback | ✅ (3 прогона, 3→1→0 замечаний) |
| G extras | частично: token_usage ✅, порог компакции ✅; plan validation L0–L6 — нет |
| H финальный sweep | ✅ 7 конфигураций, unit, R25, net48, R27 |

## Ограничения

- Ветка `develop`; push в develop разрешён, в `main` — нет, PR не создавать.
- `build-and-deploy.bat` НЕ запускать (Revit может быть открыт) — деплой утром у владельца.
- Не коммитить `opencode.json` и `server/` (чужая работа), `secrets.env` вне репозитория.
- Ключи не логировать; Exa — только обезличенные технические запросы.
- Write-tools не пишут в workshared central — отказ `workshared_not_supported`.
- `docs/` вне `docs/agent/`, ADR и changelog не трогать до ручной приёмки.
