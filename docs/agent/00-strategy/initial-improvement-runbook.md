# H1 — Первичное улучшение харнеса: масштаб и контекст

> **Статус:** active · runbook · 2026-11-18
> **Эпик:** H1 · **Ветка:** `develop` · **Код:** ещё не начат
> **Timebox:** 5–6 недель: H1.0 5–6 дней, H1.1 10–12, H1.2 10–12, буфер 3–5 дней.
> **Основание:** [`harness-ideal-plan.md`](harness-ideal-plan.md) §3.3, P0, P1, P1-11
> **Процесс:** [`harness-development-approach.md`](harness-development-approach.md)
> **Заменяет:** тактические runbook'и `archive/night-run-2.md` и `archive/autonomous-execution-plan.md` для нового цикла.

## 1. Зачем именно этот эпик

Сейчас харнес рассчитан на **27 тулов и короткие сессии**. Целевой продукт — **500–1000 тулов, 100–200 навыков, 1M контекста**. При текущей архитектуре:

- BM25-shortlist 16 из 1000 — лотерея; без vector-ветки (H6) это не лечится;
- каждый tool-результат до 40k символов лежит в истории — контекст умирает за 10–20 вызовов;
- компакция одноразовая, без summary и без персистенции;
- модель не имеет структурного состояния (цель, план, ID, change sets);
- динамический набор tools ломает prompt cache.

H1 закрывает **фундамент**: каталог тулов, сжатие результатов, рабочее состояние и компакцию. Без него H2–H5 (память, skills, policy, atomic plan) будут строиться на песке.

## 1a. Граница H1: vector retrieval не реализуем

H1 должен **понимать**, что 500–1000 тулов и 100–200 навыков невозможно
обслуживать одним BM25 — целевая схема зафиксирована в
[ADR-075](../../adr/075-hybrid-tool-retrieval.md): два независимых ретривера
(BM25 ∪ vector), RRF-слияние и decision-модель как reranker на 100–200
кандидатах.

Но **H1 не реализует vector-ветку**: ни embedding-провайдера, ни индекса, ни
выбора embedding-модели/reranker'а в настройках. Это отдельный эпик **H6**,
который запускается только после H1-11/H1-12 по измеренному recall. Причины:

- H1 и так самый большой эпик; embedding-провайдер, индекс и UI — новая
  инфраструктура и новые точки отказа;
- без 1000-карточного routing-eval нельзя доказать, что vector вообще нужен
  раньше 200–300 видимых инструментов;
- decision-модель уже проверена на 100–200 кандидатах, и H1 должен довести
  BM25+карточки+паки до этой границы, а не строить второй ретривер вслепую.

**Что H1 обязан оставить для H6:** enriched-карточки, паки, `find_tools`/
`load_tool_pack`, фиксацию набора, routing-eval с числами top-1/top-5 и явный
вердикт «vector нужен / не нужен». Если recall проваливается — записать цифры и
передать в H6, **не добавляя embeddings в H1**.

**Граница tool surface (ADR-076).** H1 также **не добавляет** runtime C#,
`send_code_to_revit`, per-property tools и обёртки 1:1 по методам Revit API.
Новый tool — только пользовательское намерение/композитная операция или
meta-tool; свойства — generic `query/get/set` с проекциями. Code mode и
sandbox-интерпретатор — эпик **H7** (до H6), не H1.

**Live model search — H1b, не H1 (ADR-077).** Поиск по семействам/типам/
категориям/уровням/видам/параметрам — **без постоянного индекса**: нормализация
запроса (стеммер/синонимы/алиасы), `list_family_types`,
`find_families_with_parameter`, `query_elements` с `parameterContainsAny`;
100k экземпляров — scoped-проход, one-shot cache максимум. Следующий эпик
**H1b** сразу после H1. H1 не индексирует модель и не расширяется под него.

## 2. Цель и метрики

**Цель:** агент уверенно работает с каталогом 500–1000 карточек (витрина 100–150 композитных действий) и сессией 100+ ходов, не теряя контекст и не превышая бюджет.

| Метрика | Baseline | Цель H1 |
|---|---|---|
| Routing top-1 | не измеряется | ≥ 80 % |
| Routing top-5 recall | не измеряется | ≥ 95 % |
| Tool error rate | не измеряется | < 8 % |
| Invalid args rate | не измеряется | < 3 % |
| Context-токены на read-задачу | измерить в H1.0 | −20 % |
| Cache hit (cached/prompt) | не измеряется | ≥ 50 % (40 % — промежуточный H1.0) |
| Compaction fact retention | n/a | ≥ 95 % на 20 фактах |
| Session 100+ ходов | не проверялась | без `context_length_exceeded` |
| Eval 40 задач | 40/40 | 40/40 (без регрессий) |

## 3. Scope

### Входит

- Схема БД `v3a`, метрики токенов/кэша/usage.
- `WorkingState` + turn-context.
- Tool catalog v2: карточки, паки, `find_tools` top-5, `load_tool_pack`, фиксация набора.
- Schema validation, input repair, loop guard, per-tool timeout.
- Store-and-reference полных tool-результатов + `read_tool_result`.
- Маскировка старых результатов.
- Compaction v2: checkpoint-summary, хвост, constraint pinning, персистенция.
- Eval-кейсы и routing-набор 200 запросов.

### Не входит (следующие эпики)

- Policy engine, persisted grants, plan mode (H3).
- Memory v1, FTS5, session search, skills 100–200 (H2).
- Атомарный план через `TransactionGroup`, verify-on-stop (H3).
- UI: steering, timeline/rewind, memory inbox, context breakdown (H5).
- MCP, subagents, codegen (backlog).
- Новые Revit-тулы, кроме тестовых; H1 — про масштабирование существующего каталога.

## 4. Порядок чтения для исполнителя

1. `docs/agent/README.md` — контекст модуля.
2. `docs/agent/HANDOFF.md` — текущее состояние.
3. `docs/agent/00-strategy/harness-development-approach.md` — процесс и гейты.
4. Этот runbook — задачи H1.
5. `docs/invariants.md` + `docs/architecture/dependency-rule.md` — жёсткие правила.
6. `harness-ideal-plan.md` §3.3, §6.1–§6.5, §7 — детали.

## 5. Архитектурные решения H1

| Решение | Почему |
|---|---|
| Трёхуровневый каталог: packs → find → load | 1000 схем не влезают; модель видит 12–20 паков и top-5 |
| Набор tools фиксируется на сессию | иначе prompt cache не работает |
| Полный tool-результат — в `tool_result_blobs`, в промпт — preview + handle | контекст не растёт квадратично |
| `WorkingState` инжектится каждый ход | структура надёжнее пересказа |
| Summary — только при реальном превышении | не платим за пересказ каждый ход |
| Constraint pinning | после сжатия не теряем ограничения пользователя |
| Все фичи за флагами | откат без переписывания |

## 6. Work breakdown

### H1.0 — Foundation (5–6 дней)

| ID | Задача | Что сделать | Файлы | Приёмка |
|---|---|---|---|---|
| H1-01 | Migration framework + `v3a` | Ordered migrations, `PRAGMA user_version`, backup через `BackupDatabase`/`VACUUM INTO`, транзакция. Таблицы: `compactions`, `tool_result_blobs`; колонки `token_usage` (`cached_tokens`, `cache_write_tokens`, `latency_ms`, `status`, `request_id`, `compaction_id`), `messages.turn_id` | `Services/Storage/AgentDatabase.cs`, новый `Services/Storage/Migrations/…` | Миграция v2→v3a на копии; повторный запуск — no-op; backup создан; FTS5 smoke (create/insert/query) на net48 |
| H1-02 | Token/cache/usage | Учитывать system+tools в оценке; калибровать по `usage.prompt_tokens`; читать `cached_tokens`/`prompt_cache_hit_tokens`; писать usage при error/cancel | `AgentTokenEstimator.cs`, `LlmPayloadBuilder.cs`, `LlmResponseAccumulator.cs`, `SqliteAgentUsageStore.cs`, `AgentLlmSettings.cs` | 5 сессий: оценка vs факт ≤15 %; cache-поле заполнено; usage при ошибке |
| H1-03 | WorkingState + turn-context | Расширить `WorkingState` (goal, constraints, plan, done, current view/selection, change set IDs, touched IDs, open questions); новый `AgentTurnContextBuilder`; вставка `<turn_context>` перед последним user; персист | `Core/Models/Agent/WorkingState.cs`, новый `Services/AgentTurnContextBuilder.cs`, `AgentSystemPrompt.cs`, `AgentOrchestrator.cs`, `AgentSessionContext.cs` | После рестарта state восстанавливается; static system hash не меняется |
| H1-04 | Baseline | Прогнать 5 read + 5 write сессий, зафиксировать токены/cache/ошибки в `eval.md` | `00-strategy/eval.md` | Таблица baseline с датой/моделью |
| H1-05 | Флаги | `tool_catalog_v2`, `context_compaction_v2`, `working_state` — выключены по умолчанию; документировать; 1M-профиль — H1.2/P1-12 | `AgentLlmSettings.cs`, `docs/agent/README.md` | Флаг выключен → поведение 1:1 |

**Выход H1.0:** схема готова, метрики собираются, поведение не изменилось.

### H1.1 — Tool scale (10–12 дней)

| ID | Задача | Что сделать | Файлы | Приёмка |
|---|---|---|---|---|
| H1-06 | ToolCard | Расширить `AgentToolDefinition`: `Pack`, `Purpose`, `WhenToUse`, `WhenNotToUse`, `Examples[]`, `Synonyms[]`, `NegativeExamples[]`, `Idempotent`, `TimeoutMs`, `RequiredContext`, `Bulk`. Карточка описывает **пользовательское намерение**, не метод API | `Core/Models/Agent/AgentToolDefinition.cs`, `Core/Services/Agent/AgentToolCatalog.cs`, описания 27 тулов | Все 27 карточек enriched; unit на сериализацию; review-гейт ADR-076: нет 1:1 обёрток API, нет per-property tools |
| H1-07 | Packs | 12–20 паков: `views`, `sheets`, `params`, `families`, `schedules`, `filters`, `worksets`, `links`, `mep`, `annotations`, `rooms`, `warnings`, `model`, `audit`, `meta`… Каждый тул — ровно в одном паке; core-набор | `AgentToolCatalog.cs`, новый `Core/Services/Agent/AgentToolPacks.cs` | Каждый тул в одном паке; core 8–10 всегда доступны |
| H1-08 | Router v2 + find/load | **BM25 по карточке** (name+purpose+when+examples+synonyms) → top-50 → decision-модель как reranker на 100–200 кандидатах (опционально, при включённой модели) → top-5. `find_tools(query, pack?, context)` + режим листинга `fields=["name","description"]`; `load_tool_pack(pack)` / `enable_tools(names)`; набор фиксируется в `AgentSessionContext`. **Vector-ветка в H1 не входит** — отдельный эпик H6 по ADR-075, только после routing-eval на 1000 карточках | `RuleToolRouter.cs`, `FindToolsTool.cs`, новый `LoadToolPackTool.cs`, `AgentOrchestrator.cs`, `AgentSessionContext.cs` | top-5 recall ≥95 % на 200 запросах; после load набор не меняется; `find_tools` не расширяет до полного каталога; выключенный decision = текущий BM25 |
| H1-09 | Validation + repair | Валидация всех тулов; tolerant JSON, coerce, aliases, unknown-tool suggestions; `_repairs[]` в tool_calls | `AgentToolSchemaValidator.cs`, новый `AgentToolInputRepair.cs`, `AgentToolExecutor.cs`, `AgentOrchestrator.cs` | 20 malformed payload → `invalid_arguments` с path/hint; repair залогирован |
| H1-10 | Loop guard + timeout | SHA-256(tool+args): 2 одинаковые ошибки → warn, 3 одинаковых вызова → block+ask, 5 → abort. `TimeoutMs`, net48-совместимый `Task.WhenAny`+`Delay`, watchdog | новый `Core/Services/Agent/LoopGuard.cs`, `AgentOrchestrator.cs`, `AgentAwaitableEvent.cs` (watchdog) | 3 одинаковых вызова → `loop_detected`; зависший вызов логируется |
| H1-11 | Routing eval | 200 запросов: 100 реальных BIM-фраз, 100 синтетических; режимы `rules` и `decisions` (decision reranker); top-1/top-5/false tool/p95; каталог 1000 карточек, витрина ≤150 composite | `SmartCon.Tests/Agent/Routing/…`, fixtures | Метрики в `eval.md`; gate ≥80/95; зафиксировано, нужен ли vector (порог — следующий эпик) |
| H1-12 | 1000-card test | Синтетический каталог 1000 карточек; размер промпта с паками; качество top-5 на BM25 + decision reranker | `SmartCon.Tests/Agent/Routing/…` | Prompt с паками ≤ ~8k токенов; top-5 ≥95 %; вердикт по vector-ветке (нужна/нет) |
| H1-13 | Cache stability | Тест: одинаковый набор tools/prefix между ходами; смена active view не меняет static hash | `SmartCon.Tests/Agent/…` | Hash стабилен; поле cache заполнено |

**Выход H1.1:** 1000 тулов навигируются; модель не видит лишнего; ошибки аргументов лечатся.

### H1.2 — Context (10–12 дней)

| ID | Задача | Что сделать | Файлы | Приёмка |
|---|---|---|---|---|
| H1-14 | Store-and-reference | Полный redacted-результат в `tool_result_blobs` до обрезки; `ToolResultBudget` отдаёт `resultHandle` + preview; новый `read_tool_result(handle, offset, limit, path?)` | `ToolResultBudget.cs`, `AgentOrchestrator.cs`, новый `ReadToolResultTool.cs`, `SqliteAgentToolCallStore.cs`, `AgentDatabase.cs` | Полный результат доступен после рестарта; preview ≤40k; пагинация |
| H1-15 | Masking | Старые результаты → digest; последние 6 + хвост; protected: `load_skill`, `recall`, `search_history`, read-back плана; digest содержит handle | `AgentHistoryCompactor.cs` | Экономия >20k токенов; protected не маскируются |
| H1-16 | Compaction v2 | `IAgentContextCompactor`; trigger перед каждым раундом; ladder mask→summary→hard shrink; 9 секций; user messages verbatim; constraint pinning; persist `compactions`; history projection; fallback; circuit breaker 3 | новый `Services/Agent/AgentContextCompactor.cs` (+ partials), `AgentOrchestrator.cs`, `AgentSystemPrompt.cs`, `AgentDatabase.cs` | 200 ходов/20 фактов ≥95 %; пары tool не разорваны; рестарт восстанавливает checkpoint; fallback работает |
| H1-17 | Summarizer cap | `SummaryMaxInput` + рекурсивная суммаризация чанками; cheap model config | `AgentContextCompactor.cs`, `AgentLlmSettings.cs` | 1M история суммируется без overflow |
| H1-18 | Compaction eval | 10 кейсов: факты, ID, change set, план, рестарт, ошибка summarizer | `SmartCon.IntegrationTests/Agent/Eval/…` | ≥95 %; gate PASS |
| H1-19 | Integration | Store-and-reference, masking, checkpoint, projection, cancel | `SmartCon.IntegrationTests/Agent/…` | R25 + net48 (+R27) 0 failed |
| H1-20 | Manual Revit | Сценарий: 100+ ходов, 3 пака, запись+undo, рестарт; лог | — | Лог чистый, владелец принял |

**Выход H1.2:** длинная сессия не деградирует; контекст сжимается предсказуемо; факты и ID сохраняются.

## 7. Контракты

### 7.1 ToolCard

```csharp
public sealed class AgentToolDefinition
{
    public string Name { get; init; }
    public string Description { get; init; }
    public string InputSchemaJson { get; init; }
    public AgentToolRisk Risk { get; init; }
    public string Pack { get; init; }              // views|params|...
    public string Purpose { get; init; }           // одна строка
    public string WhenToUse { get; init; }
    public string WhenNotToUse { get; init; }
    public IReadOnlyList<string> Examples { get; init; }
    public IReadOnlyList<string> Synonyms { get; init; }       // ru/en
    public IReadOnlyList<string> NegativeExamples { get; init; }
    public bool Idempotent { get; init; }
    public int TimeoutMs { get; init; }
    public string? RequiredContext { get; init; }  // active_view|selection|...
    public bool SupportsBulk { get; init; }
}
```

### 7.2 `find_tools` / `load_tool_pack`

```json
// find_tools
{ "query": "переименовать виды по эталону", "pack": "views", "context": "78 видов, шаблон есть", "limit": 5 }
// → { "tools": [ { "name": "rename_views", "card": {...}, "schema": {...} } ], "packs": ["views"], "hint": "..." }

// load_tool_pack
{ "pack": "views" }  // или { "tools": ["rename_views","apply_view_template"] }
// → { "loaded": [...], "frozen": true, "totalTools": 7 }
```

Правила:
- `find_tools` — **только discovery**: возвращает карточки/схемы в tool-result и **не меняет** tools в payload. В H1 кандидаты — BM25 по карточке (+ decision reranker, если модель включена); vector-ветка — эпик H6 (ADR-075), не H1.
- Режим листинга: `find_tools(fields=["name","description"])` возвращает каталог/пак без `inputSchema` — модель может просмотреть его целиком и потом запросить схему выбранного инструмента.
- `load_tool_pack`/`enable_tools` фиксирует набор в `AgentSessionContext`; повторный вызов **добавляет**, не заменяет. Набор меняется только на границе хода; внутри хода — неизменен.
- В LLM payload уходят только загруженные tools + core.
- Вызвать найденный tool можно только после `load_tool_pack`/`enable_tools`; смена набора логирует cache invalidation.

### 7.3 `WorkingState`

```csharp
public sealed class WorkingState
{
    public string Goal { get; set; }
    public List<string> Constraints { get; set; }
    public string? PlanId { get; set; }
    public List<string> CompletedSteps { get; set; }
    public List<string> PendingSteps { get; set; }
    public string? ActiveViewId { get; set; }
    public List<string> SelectedUniqueIds { get; set; }
    public List<string> TouchedElementIds { get; set; }
    public List<string> ChangeSetIds { get; set; }
    public List<string> OpenQuestions { get; set; }
    public DateTime UpdatedAt { get; set; }
}
```

Инжектится как `<turn_context>`; при компакции попадает в summary; после рестарта восстанавливается из БД.

### 7.4 Compaction

- `Usable = Window − min(MaxOutput, 20k) − 13k`.
- `TriggerRatio = 0.80`; `TailTokens = clamp(0.15 * Usable, 25k, 150k)`.
- 1M: Usable ≈ 979k, trigger ≈ 783k, tail ≈ 147k.
- 500k: Usable ≈ 479k, trigger ≈ 383k, tail ≈ 72k.
- Ladder: mask (last 6 / tail) → summary → hard shrink.
- Summary: 9 секций, user messages verbatim, ID/имена проверяются механически.
- Persist `compactions`; history = checkpoint + tail; сырые сообщения не удаляются.

### 7.5 DB `v3a`

```sql
ALTER TABLE messages ADD COLUMN turn_id TEXT;

CREATE TABLE compactions (
  id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL,
  from_rowid INTEGER NOT NULL,
  to_rowid INTEGER NOT NULL,
  summary TEXT NOT NULL,
  tokens_before INTEGER NOT NULL,
  tokens_after INTEGER NOT NULL,
  source_prefix_hash TEXT NOT NULL,
  model TEXT,
  ack_message_id TEXT,
  created_at TEXT NOT NULL
);

CREATE TABLE tool_result_blobs (
  tool_call_id TEXT PRIMARY KEY,
  session_id TEXT NOT NULL,
  result_json TEXT NOT NULL,
  created_at TEXT NOT NULL,
  expires_at TEXT
);

ALTER TABLE token_usage ADD COLUMN cached_tokens INTEGER NOT NULL DEFAULT 0;
ALTER TABLE token_usage ADD COLUMN cache_write_tokens INTEGER NOT NULL DEFAULT 0;
ALTER TABLE token_usage ADD COLUMN latency_ms INTEGER;
ALTER TABLE token_usage ADD COLUMN status TEXT NOT NULL DEFAULT 'ok';
ALTER TABLE token_usage ADD COLUMN request_id TEXT;
ALTER TABLE token_usage ADD COLUMN compaction_id TEXT;
```

## 8. Тесты и eval

| Уровень | Что |
|---|---|
| Unit | ToolCard, packs, router scoring, repair, loop guard, token estimator, WorkingState, compaction pure logic |
| Integration | migration on real `agent.db` copy; store-and-reference; masking; compaction; restart; cancel |
| Eval replay | 40 существующих задач — без регрессий |
| Eval new | 200 routing; 10 compaction; 5 cache; 5 loop |
| Manual | 100+ ходов, 3 пака, write+undo, restart, log |

**Baseline:** до H1.1 зафиксировать в `eval.md`: токены, cache, tool errors, latency, success.

## 9. Команды

```bash
# 1. Сборка всех 7 конфигураций
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-all-configs.ps1

# 2. Unit (R25)
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25

# 3. Integration
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R27 --framework net10.0-windows
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R21 --framework net48 -p:RevitVersion=2023

# 4. Eval replay
SMARTCON_AGENT_EVAL_REPLAY=1 dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 \
  --framework net8.0-windows -- --treenode-filter "/*/*/*AgentEvalTests*/*"
```

## 10. Гейты и DoD

| Гейт | Условие |
|---|---|
| G0 | Этот runbook принят владельцем; scope/out-of-scope согласованы |
| G1 | 7 конфигураций — 0 warnings/errors |
| G2 | Unit 100 %; integration R25 + net48 (+R27) — 0 failed; новые тесты |
| G3 | Eval replay 40/40 + новые кейсы; метрики в `eval.md` |
| G4 | Ручной сценарий в Revit; лог без необъяснимых Error/Warn |
| G5 | Валидатор `general` PASS; docs/HANDOFF обновлены; флаги описаны |

**DoD H1:**
- H1.0–H1.2 закрыты; задачи не перенесены без причины.
- 1000-туловый тест и 200 routing-запросов зелёные.
- 200-ходовая сессия без `context_length_exceeded`; факты ≥95 %.
- Eval 40/40; новых skip нет.
- Флаги: выключены → поведение 1:1; включены → метрики лучше baseline.
- Ручная приёмка владельца.

## 11. Риски и откат

| Риск | Митигация | Откат |
|---|---|---|
| Миграция портит `agent.db` | BackupDatabase, транзакция, тест на копии | restore backup, `user_version` назад |
| Prompt cache не растёт | Измерять; не завязывать функциональность; фиксировать набор | отключить cache-метрики |
| Summary теряет факты | Verbatim user, pinned constraints, ID-check, fallback | `context_compaction_v2=false` |
| Router не даёт 95 % | Улучшить карточки/синонимы; JEV reranker; fallback full pack | `tool_catalog_v2=false` |
| 1000 тулов раздувают промпт | Только паки + top-5; лимит схем | уменьшить паки/лимит |
| Timeout не отменяет Revit | watchdog + CT между элементами; не обещать мгновенную отмену | отключить per-tool timeout |
| Файлы >600 строк | Partial сразу | — |
| net48 API | `Task.WhenAny`, без `WaitAsync`; integration net48 | откат конфигурации |

## 12. Handoff-шаблон

```markdown
# H1 handoff — <date>

## Сделано
- H1-xx: ...

## Текущее состояние
- флаги: ...
- схема БД: v3a
- метрики: ...

## Следующее
- H1-xx: ...

## Изменённые контракты
- AgentToolDefinition: ...
- DB: ...

## Риски/открытые вопросы
- ...

## Команды/прогоны
- build: ...
- unit: ...
- integration: ...
- eval: ...
```

## 13. Stop conditions

Остановиться и вернуться к владельцу, если:

1. Миграция на копии падает дважды.
2. Routing top-5 < 90 % после двух итераций улучшения карточек.
3. Compaction fact retention < 90 % после двух итераций.
4. Любой инвариант I-01…I-17 нарушен.
5. Сборка/тесты не зелёные больше одного дня без понятной причины.
6. Ручной сценарий в Revit стабильно падает/вешает Revit.
7. Объём задачи вырос больше чем на 30 % без согласования.

## 14. Что дальше

После H1:

- **H1b (следующий):** model entity search — BM25 по семействам/типам/категориям/уровням/видам/параметрам, `search_model_entities` + структурный `query_elements`, ADR-077.
- **H1c:** view configuration — шаблон или текущий вид, visibility, фильтры по условиям, overrides, crop/scope boxes.
- **H1d:** family editing — EditFamily без UI, instance/type, shared parameters, формулы/lookup, перенос значений, save/load.
- **H8 (после H1d):** bulk/cyclic job engine — отдельный дизайн; в H1/H1b–H1d его нет. Альтернатива — sandbox-скрипт (H7), не runtime C#.
- **H2:** память (`MEMORY.md`), FTS5, session search, skills 100–200.
- **H3:** policy engine, persisted grants, atomic plan, verify-on-stop.
- **H4:** usage ledger v2, cost caps, eval v2, snapshots.
- **H5:** steering, timeline/rewind, memory inbox, MCP (backlog).
- **H7:** typed facade + sandbox-интерпретатор для длинного хвоста (Jint/Lua поверх `IRevitOps`), ADR-076; без runtime C#.
- **H6 (последний):** гибридный retrieval (BM25 ∪ vector → decision reranker), ADR-075 — только после H1b и по метрике recall.

Детали H1b–H1d и границы cyclic: [`h1b-tool-packs.md`](h1b-tool-packs.md).

Ссылки: `harness-ideal-plan.md` §5 (фазы), §6 (спеки), §7 (схема), §8 (метрики).
