# План доведения AI-харнеса SmartCon до идеала

> **Статус:** draft для ревью · 2026-11-18 · ветка `develop`
> **Базис:** аудит собственного кода (4 агента: orchestration/LLM, tools, memory/storage, plan/policy/eval), source-code разбор 10 харнесов (OpenCode, Codex, OpenHands, Cline, Roo Code, Aider, goose, Gemini CLI, Claude Code, ZCode), исследование `arXiv:2609.00006` (11 систем, 29 паттернов), отдельные разборы memory/compaction/prompt-cache.
> **Не заменяет:** исторические `archive/mvp-plan.md` и `archive/night-run-2.md`. **Исполнение:** [`initial-improvement-runbook.md`](initial-improvement-runbook.md) (эпик H1). **Процесс:** [`harness-development-approach.md`](harness-development-approach.md).
> **Правило документа:** ничего из этого не считается реализованным до зелёной сборки, тестов, eval и ручной приёмки в Revit (см. §10).

---

## 1. Вердикт

Наш харнес уже сильнее большинства OSS в трёх вещах: **безопасная запись** (dry-run → подтверждение → change set → undo), **планирование** (L0-контракт, L1-preflight, L3-digest, L4-fidelity, L6-read-back; L2-critic и атомарность L5 — план, не факт) и **eval-контур** (40 задач, replay без ключа, пороги). Это редкое сочетание: большинство харнесов либо не умеют откатывать, либо не измеряют себя.

Слабость — всё, что связано с **длинным контекстом и памятью**. Компакция одноразовая, без LLM-summary и без персистенции; prompt cache не используется; токены считаются эвристикой; память между сессиями отсутствует; у инструментов нет политик; наблюдаемость write-only. Поэтому «идеал» — не переписать цикл, а **достроить пять подсистем**: контекст, память, политики, надёжность, наблюдаемость.

### 1.1 Что уже лучше рынка

| Возможность | У нас | Почему это сильнее |
|---|---|---|
| Запись с откатом | `AgentWriteToolBase` + change sets + `undo_last` | Большинство OSS-харнесов откатывают только файлы через git; у нас — по элементам модели |
| План | `AgentPlanExecutor` L0–L6 (L2 нет, L5 не атомарен) | L0/L1/L4/L6 есть далеко не у всех; атомарность — в P3-6 |
| Подтверждения | digest-binding + внешнее подтверждение; nonce/ledger — только прямой API | У Cline/Roo подтверждение — просто кнопка, без криптографической привязки |
| Eval | 40 фикстур, replay, порог | Aider/OpenCode/Goose не имеют встроенного eval-контура |
| Домен | 27 тулов (24 Revit + 3 meta), structural query builder | Универсальные харнесы не знают ElementId, view templates, worksets |
| Роутинг | BM25 + опциональный decision + `find_tools`; на 27 тулах хватает | У большинства — полный каталог или простой deferred loading; целевой hybrid vector 500–1000 — ADR-075 |

### 1.2 Десять главных пробелов

| # | Пробел | Доказательство | Следствие |
|---|---|---|---|
| 1 | Компакция одноразовая и только tool-результаты | `AgentOrchestrator.cs:95-97`, `AgentHistoryCompactor.cs:19-61` | 8 tool-раундов раздувают контекст после единственной проверки; assistant/user не сжимаются |
| 2 | Нет LLM-summary и персистенции компакции | `AgentHistoryCompactor.cs:63-119`, схема `AgentDatabase.cs:72-207` | После рестарта контекст восстанавливается только сырой историей; нет checkpoint |
| 3 | Нет prompt caching / стабильного префикса | `LlmPayloadBuilder.cs:10-29`; system пересобирается `AgentOrchestrator.cs:98-99` | Implicit cache провайдера не измеряется и не оптимизируется; каждый ход платим за префикс |
| 4 | Токены считаются эвристикой, `token_count=0` | `AgentTokenEstimator.cs:9-22`, `AgentMessage.cs:45` | Порог компакции врёт; нельзя строить бюджеты и стоимость |
| 5 | Нет памяти между сессиями | `WorkingState.cs` не используется; в `AgentDatabase.cs:72-207` нет таблиц памяти | Каждая сессия начинается с нуля; опыт не накапливается |
| 6 | Нет поиска по истории | `SqliteAgentConversationStore.cs:145-168` | Нельзя найти прошлое решение или ElementId |
| 7 | Схемная валидация только у `execute_plan` | `ExecutePlanTool.cs:66-74`; `AgentToolExecutor.cs:36-104` | Кривые аргументы доходят до тулов; ошибки лечатся вручную |
| 8 | Нет per-tool timeout и policy | `AgentAwaitableEvent.cs:27-55`; `AgentToolDefinition.cs:3-8` | Зависший Revit-вызов держит очередь; правила — только Risk enum |
| 9 | Нет loop/stuck detection | — | Повторные одинаковые вызовы съедают бюджет и токены |
| 10 | Наблюдаемость write-only | `audit_log`, `token_usage`, `tool_calls` не читаются UI | Нельзя ответить «сколько стоил ход», «какой cache hit», «почему план не сошёлся» |

> **Масштаб:** текущий BM25-shortlist 14 рассчитан на 27 тулов. Целевые 500–1000 тулов и 100–200 навыков требуют трёхуровневого каталога — см. §3.3; это приоритет №1 в §1.3.

### 1.3 Топ-12 по ROI

| # | Что внедрить | Фаза | Эффект | S/M/L |
|---|---|---|---|---|
| 1 | **Tool catalog v2**: паки, карточки, синонимы, `find_tools` top-5, `load_tool_pack`, фиксация набора | P1 | 1000 тулов становятся навигируемыми; top-5 recall ≥95 % | L |
| 2 | Store-and-reference + маскировка tool-результатов | P1 | Контекст не растёт квадратично | M |
| 3 | WorkingState + turn-context каждый ход | P0 | Модель всегда помнит цель, план и ID | S |
| 4 | Compaction v2: checkpoint-summary + хвост + constraint pinning | P1 | Долгие сессии без деградации | M |
| 5 | Skills index + `load_skill` + skill→tools | P2 | 100–200 навыков по требованию, набор тулов сужается | M |
| 6 | Стабильный префикс + cache accounting | P0 | Дешёвые повторы, измеримый cache hit | S |
| 7 | Schema validation + repair + loop detection | P0 | Меньше «сломанных» вызовов на большом каталоге | S |
| 8 | Policy engine + persisted approvals + plan mode | P3 | Автономность без потери контроля | M |
| 9 | Plan atomicity + verify-on-stop | P3 | План либо применяется целиком, либо откатывается | L |
| 10 | Usage ledger v2 + cost caps + cache metrics | P4 | Управление стоимостью на 1M-контексте | S |
| 11 | Memory v1 + FTS5 + session search | P2 | Опыт между сессиями, поиск решений | M |
| 12 | Eval v2: план, компакция, режимы, routing, restart | P5 | Регрессии ловятся до пользователя | M |

### 1.4 Стратегия

1. **Не переписывать цикл.** Текущий `while` + ExternalEvent + тулы — правильная основа. Все изменения — точечные, за флагами.
2. **Сначала baseline, затем каталог.** P0 измеряет токены/cache/usage; P1 строит каталог. Без baseline не докажем эффект.
3. **Каталог — фундамент масштаба.** 500–1000 карточек, паки, `find_tools`, фиксация набора. Без него ни память, ни компакция не спасут.
4. **Сначала контекст, потом память.** Компакция и store-and-reference дают больше, чем memory bank; память без компакции быстро упрётся в окно.
5. **Политики — данные, а не код.** `agent-policy.json` + persisted grants; plan mode — набор правил, а не отдельный цикл.
6. **Никакой мультиагентности в Revit-потоке.** Один документ, один API-поток; субагенты — только read-only и позже.
7. **Каждая фаза — самостоятельно шиппингуемая.** Флаг выключен → поведение не меняется → eval показывает «до/после» → флаг включается.

---

## 2. As-is: карта харнеса

### 2.1 Подсистемы и файлы

| Подсистема | Ключевые файлы | Состояние |
|---|---|---|
| Turn loop | `AgentOrchestrator.cs` (8 раундов, 120 сообщений) | Работает; последовательные tools |
| Prompt | `AgentSystemPrompt.cs`, `AgentPromptBuilder.cs`, `LlmPayloadBuilder.cs` | Статический текст + project context; cache отсутствует |
| LLM client | `OpenAiCompatibleLlmClient.cs`, `LlmResponseAccumulator.cs` | SSE streaming; retry только до первого токена |
| Tools | `AgentToolRegistry.cs`, `AgentToolExecutor.cs`, 27 тулов | DI-каталог; ручные JSON-схемы; sequential |
| Routing | `RuleToolRouter.cs`, `DecisionsToolRouter.cs`, `FindToolsTool.cs` | BM25 + опциональный decision; shortlist 16; vector branch — ADR-075 |
| Plan | `AgentPlanExecutor(.Steps/.Audit).cs`, `AgentPlanValidator.cs`, `ExecutePlanTool.cs` | L0–L6 (L2 отсутствует, L5 не атомарен) |
| Approvals | `AgentToolExecutor(.Bypass).cs`, `AgentConfirmationService.cs`, `AgentConfirmationLedger.cs` | 3 режима; ledger in-memory; tool-confirm без nonce |
| Change sets | `AgentWriteToolBase.cs`, `SqliteAgentChangeSetStore.cs`, `UndoLastTool.cs` | Old/new по элементам; undo после рестарта |
| Storage | `AgentDatabase.cs`, `Sqlite*Store.cs` | SQLite DELETE journal; schema v2; миграций нет |
| Context | `AgentHistoryCompactor.cs`, `ToolResultBudget.cs`, `AgentTokenEstimator.cs` | Только tool-digest, 1 раз/ход; эвристика |
| Memory | — | Отсутствует; `WorkingState` не используется |
| UI | `AgentMainViewModel*.cs`, `AgentPaneControl.xaml` | Чат, вложения, approvals, настройки |
| Eval | `AgentEvalRunner*.cs`, `Fixtures/`, `eval.md` | 40 задач, replay, gate PASS |

### 2.2 Измеренная база (2026-11-18)

| Проверка | Результат |
|---|---|
| Sweep конфигураций | 7/7, 0 ошибок |
| Unit `SmartCon.Tests` (Debug.R25) | 3500/3500 |
| Integration `AgentPlanExecutionTests` (Revit 2025) | 6/6 |
| Eval replay 40 задач | 40/40, gate PASS, routing 97.5 % |
| Agent integration R27 | 78 passed, 2 skipped, 0 failed |
| net48 (Revit 2023) | 245 passed, 12 skipped, 0 failed |

### 2.3 Чего мы не измеряем

- cache hit rate и `cached_tokens`;
- prompt-токены на задачу и на ход;
- долю ошибок инструментов по кодам;
- частоту invalid arguments;
- частоту зацикливаний;
- стоимость сессии и хода;
- качество компакции (сколько фактов выжило);
- latency LLM по фазам (TTFT, полный ответ);
- точность памяти (сколько записей реально пригодились).

---

## 3. To-be: целевая архитектура

```
┌────────────────────────── UI (WPF MVVM) ──────────────────────────┐
│ чат · tool-карточки · approvals · plan preview · memory inbox     │
│ diff change set · context breakdown · token/cost/cache индикатор  │
└───────────────────────────────▲───────────────────────────────────┘
                                │ события / команды
┌───────────────────── Agent runtime (SmartCon.Agent) ──────────────┐
│ Turn loop ──► Context assembler ──► LLM client (stream)           │
│   │               ├ static prefix (identity/rules/skills/memory)  │
│   │               ├ turn-context (date/doc/view/selection/mode)   │
│   │               ├ history projection (checkpoint + tail)        │
│   │               └ token budget / compaction                     │
│   ├ Tool registry + executor (sequential, timeouts, repair)       │
│   ├ Router (BM25/JEV) + deferred loading                          │
│   ├ Plan executor (atomic group, read-back, critic)               │
│   ├ Policy engine (deny-first, persisted grants, plan mode)       │
│   ├ Memory service (files + FTS5 + consolidation)                 │
│   └ Usage/audit/events/eval                                       │
└───────────────────────────────▲───────────────────────────────────┘
                                │ IExternalEvent → Revit main thread
┌──────────────────── SmartCon.Revit boundary ──────────────────────┐
│ read/write tools · ITransactionService · TransactionGroup         │
│ change sets · read-back · document identity · workshared guards   │
└───────────────────────────────────────────────────────────────────┘
```

### 3.1 Принципы

1. **Стабильный префикс — закон.** Static system + отсортированные tools + skills/memory index впереди; всё волатильное — в turn-context в хвосте.
2. **Компакция — проекция, а не разрушение.** Сырые сообщения остаются в БД; модель видит checkpoint + tail; UI видит всё.
3. **Память — файлы + FTS5.** Без эмбеддингов и vector RAG по содержимому сессий/модели: 0/11 исследованных харнесов используют vector RAG по коду. Эмбеддинги применяются **только для каталога тулов/навыков** (ADR-075), не для памяти.
4. **Политика — данные.** `agent-policy.json` + grants в БД; deny-first; fail-closed.
5. **План атомарен.** `TransactionGroup` на весь план; read-back до `Assimilate`; иначе `RollBack`.
6. **Наблюдаемость — продукт.** Каждый ход отвечает: сколько токенов, cache hit, какие tools, сколько стоил, что сжалось.
7. **Один агент, один Revit-поток.** Никакой параллельной записи; read-only батчи — в одном `ExternalEvent`.
8. **Каждая фича — за флагом.** Дефолт — текущее поведение; включение после eval.
9. **Tool — намерение, не API.** Никаких 1:1 обёрток методов и per-property tools; длинный хвост — через план или sandbox-интерпретатор поверх типизированного фасада, не через runtime C# (ADR-076).

### 3.2 Non-goals (осознанно не берём)

| Не берём | Почему |
|---|---|
| Vector RAG по содержимому модели/сессий | 0/11 используют; для Revit лучше structural query + FTS5. Эмбеддинги — только для retrieval каталога тулов/навыков (ADR-075) |
| Runtime C# / raw code eval в процессе Revit | net48 не выгружает сборки, API однопоточный, произвольный код обходит I-01/I-03/I-05; длинный хвост — план → sandbox-интерпретатор → C#-tool в релизе (ADR-076) |
| Tool на каждый метод API / на каждое свойство | Tool = пользовательское намерение; свойства — generic query/get/set; иначе 1000 схем и деградация выбора (ADR-076) |
| Event-sourcing переписывание | Дорого; добавляем только журнал событий для аудита/компакции |
| Параллельные tool calls в Revit | API однопоточный (I-01); батчим чтения в одном ExternalEvent |
| Multi-agent coordinator для записи | Один документ, конфликты транзакций; позже — read-only explorer |
| OS sandbox | Невозможен in-process; защита — approvals + change sets + undo |
| Client/server, ACP, marketplace | Не наш продукт |
| Self-improving skills без eval | Дрейф промпта; только draft + approve + eval |
| XML tool protocol | Native JSON tools работают |

### 3.3 Масштаб: 500–1000 карточек тулов, 100–200 навыков, 1M контекст

Целевой профиль — **BIM-ops**, не coding: агент выполняет операции с существующей моделью (виды, листы, параметры, семейства, фильтры, рабочие наборы, связи, MEP, аннотации, предупреждения). Создание новых 3D-объектов — вне scope.

**Тулы.** Модель не должна видеть 1000 схем — это физически не влезает и убивает выбор. Каталог из трёх уровней:

1. **Паки (12–20):** `views`, `sheets`, `params`, `families`, `schedules`, `filters`, `worksets`, `links`, `mep`, `annotations`, `rooms`, `warnings`… В промпте — 1 строка на пакет.
2. **Поиск (целевой — гибридный, ADR-075):** `find_tools(query, pack?, context)` → top-5 с полными схемами. Кандидаты собираются **двумя независимыми ретриверами**: BM25 по карточке (name + purpose + when-to-use + when-not + 2–3 примера + синонимы ru/en + negative examples) **∪** vector search по предвычисленным эмбеддингам карточки; слияние — RRF (`k=60`, top-50 от каждого). Decision-модель (JEV/pplx) — **reranker** на объединённых 100–200 кандидатах, не единственный prefilter. При ≤100 тулах допустим decision напрямую по каталогу; при 500–1000 vector обязателен. Ни один ретривер не отрезает кандидатов окончательно. **H1 реализует только BM25 + карточки + паки + decision reranker; vector-ветка — H6 после routing-eval.**
3. **Фиксация:** `load_tool_pack(pack)` / `enable_tools(names)` меняет набор **только на границе хода** и логирует cache invalidation. Внутри хода набор неизменен; `find_tools` — только discovery в tool-result, вызов найденного — после `enable_tools`.

**Два уровня каталога:** в индексе — 500–1000 карточек (включая атомарные операции); на витрине по умолчанию — 100–150 композитных действий с bulk-параметрами. Атомарные операции доступны через advanced-паки/`enable_tools`. `configure_views`, `set_parameters` с фильтром, `apply_view_template` уже показывают правильный паттерн. 1000 схем сразу — анти-паттерн: поддержка и ошибки выбора растут квадратично.

**Навыки (100–200).** В промпт — индекс: при 1M допустим полный (10–20k токенов), при 500k — подмножество по пакам/триггерам. `load_skill(name)` грузит тело ≤5k токенов. Навык объявляет `tools`, которые сужают shortlist — это главная связка «процедура + инструменты». Активны максимум 1–2 навыка; conditional activation по пакету/пути/ключевым словам; built-in — embedded resources, пользовательские — draft → approve → eval.

**Контекст 1M.** 500k сейчас; 1M можно выставить, но это не замена роутингу: 1000 схем ≈ 1.5M токенов. Порог компакции: 1M → ~783k, хвост ~147k; 500k → ~383k, хвост ~72k (формула §6.1). Маскировать tool-результаты старше последних 6 или >60k токенов tool-вывода; если экономия <20k — не трогать. Полный результат — в `tool_result_blobs`, модель получает превью + handle. WorkingState обновлять каждый ход; checkpoint-summary — последний эшелон. Стоимость на 1M растёт линейно — нужны caps и cache-метрики.

**Метрики масштаба:** top-1 ≥ 80 %, top-5 recall ≥ 95 %, false tool rate < 3 %, routing p95 < 100 ms (BM25) / < 150 ms (query embedding, local) / < 2 s (JEV reranker), 100 % вызовов — из зафиксированного набора. Recall@K измеряется **отдельно для BM25, vector и union**; кейсы «BM25 промахнулся, union нашёл» — обязательная часть отчёта.

**Следствия для плана:** P1-11 (каталог v2) — приоритет №1; P0-11 (WorkingState), P1-12 (1M profile), P1-6 (store-and-reference), P0-1 (prefix), P2-10/P2-12 (skills), P1-1…P1-4 (compaction), P4-5 (cost/cache). Векторный retrieval — **H6** после H1-11/H1-12 (порог по recall); typed facade + sandbox-интерпретатор — **H7** после H6 (ADR-076); в H1 не входят.

---

## 4. Gap-анализ: мы vs лучшие

| Подсистема | Сейчас | Best-in-class | Цель | Приоритет | S/M/L |
|---|---|---|---|---|---|
| Prompt caching | Нет cache-полей | Claude/Codex/OpenCode | Стабильный префикс + `cached_tokens` | P0 | S |
| Волатильный контекст | В system prompt | goose MOIM, OpenHands CacheTier | turn-context в хвосте | P0 | S |
| Стабильность tools | BM25 shortlist меняется | Claude/Codex freeze | full catalog sorted / freeze per session | P0 | S |
| Token accounting | Эвристика, `token_count=0` | Codex/OpenCode usage | usage + калибровка estimator | P0 | S |
| Схемная валидация | Только `execute_plan` | Codex/Gemini | Все тулы, до исполнения | P0 | S |
| Tool repair | Нет | Cline/OpenCode/Hermes | parse/coerce/aliases/repair | P0 | S |
| Loop detection | Нет | Gemini/OpenHands/OpenCode | hash-циклы + nudge | P0 | S |
| Per-tool timeout | Нет | Codex/Cline | timeout + cancel | P0 | S |
| Compaction trigger | 1 раз/ход, 350k | 7 систем | per-request, graduated ladder | P1 | M |
| LLM summary | Нет | Claude/Gemini/OpenCode | 9 секций + tail | P1 | M |
| Compaction persistence | In-memory | Cline sidecar/Codex checkpoint | `compactions` + history projection | P1 | M |
| Verbatim user messages | Не сохраняются в summary | Claude/Codex | Все user-сообщения дословно | P1 | S |
| Constraint pinning | Нет | Claude/paper | Реинъекция I-правил после сжатия | P1 | S |
| Tool result masking | Последние 4 digest | OpenCode/OpenHands | Graduated mask + protected tools | P1 | M |
| Store-and-reference | 40k inline | Cline/Codex/Gemini | handle + `read_tool_result` | P1 | M |
| Cross-session memory | Нет | Codex/Gemini/Claude/ZCode | `MEMORY.md` + topics | P2 | M |
| Memory governance | Нет | Gemini inbox/Hermes limits | approve + budget + consolidation | P2 | M |
| Search | Нет FTS | Cline/Hermes/OpenClaw | FTS5 + BM25 | P2 | M |
| DB migrations | Version gate only | Codex/OpenCode | Ordered migrations + backup | P2 | S |
| Session UX | Нет delete/search/fork/export | OpenCode/Pi | Полный lifecycle | P2 | M |
| Skills | Не грузятся; нет масштаба 100–200 | 9/11 | Index + load + skill→tools + router | P2 | M |
| Rules | System prompt зашит | AGENTS.md 11/11 | Project rules file + memory | P2 | S |
| Tool policy | Risk enum | OpenCode/Codex | Metadata + policy engine | P3 | M |
| Persisted approvals | In-memory ledger | Codex/OpenCode | `approvals` + nonce table | P3 | M |
| Plan mode | Только ReadOnly | Claude/OpenCode | Policy mode + plan file | P3 | S |
| Plan atomicity | Транзакция на шаг | goose/Claude | `TransactionGroup` + rollback | P3 | M |
| Read-back enforcement | L6 логирует | Hermes verify-on-stop | Auto read-back + `verified` | P3 | S |
| Idempotency | Нет | Codex/OpenCode | Preflight recheck + repeat guard | P3 | S |
| Deferred loading | Shortlist + find_tools | Claude/Codex | Stable core + `select:` | P3 | S |
| Tool catalog scale | 27 тулов, shortlist 14 | Claude/Codex deferred + packs | Паки + `find_tools` top-5 + freeze набора | P1 | L |
| 1M context budget | 500k fixed, без caps | Codex/OpenCode dynamic | 1M config + 750k trigger + caps | P1 | S |
| Retry matrix | Только pre-stream | Cline/Gemini | Full matrix + stream errors | P4 | S |
| Stream errors | Игнорируются | OpenCode/Codex | Parse error, idle timeout, `Failed` | P4 | S |
| Output budget | Фиксированные 8192 | OpenCode | Расчёт от остатка окна | P4 | S |
| Model catalog | Один набор дефолтов | Roo/Codex | Capabilities/pricing per model | P4 | M |
| Usage ledger | Только успешные ходы | goose/OpenCode | Per request + cost + cache | P4 | S |
| Audit correlation | Нет session/turn/plan | Codex | Добавить id | P4 | S |
| Friction analyzer | Не подключён | Cline/OpenCode | UI/команда + метрики | P4 | S |
| Eval coverage | 40 задач | Gemini/Codex | 75 задач + новые метрики | P5 | M |
| Fake SSE/snapshots | Fake client | Codex | Fake server + snapshots | P5 | M |
| Steering | Нет | Codex/Pi | Очередь + инъекция | P5 | M |
| Reasoning UI | Только статус | OpenCode/Cline | Collapsible reasoning | P5 | S |
| Session timeline | Базовый | OpenCode/Pi | Search/fork/rewind/export | P5 | M |
| Memory inbox UI | Нет | Gemini | Pending review | P5 | M |
| MCP | Нет | 8/11 | Адаптер позже | P5 | L |
| Subagents | Нет | Claude/Codex | Read-only explorer позже | P5 | L |

---

## 5. Дорожная карта

### Фаза 0. Гигиена и быстрые победы (7–10 дней)

Цель: измеряемость + дешёвые исправления, которые не меняют архитектуру.

| ID | Задача | Суть | Приёмка | S/M/L | Риск |
|---|---|---|---|---|---|
| P0-0 | Schema groundwork | Ordered migrations + additive `v3a`: `compactions`, колонки `token_usage` (`cached_tokens`, `cache_write_tokens`, `latency_ms`, `status`, `request_id`, `compaction_id`), `messages.turn_id`; FTS5 smoke-test | Миграция на копии БД; FTS5 create/insert/query на net48; backup через SQLite API | S | Средний |
| P0-1 | Стабильный префикс | Разделить system на static/dynamic; turn-context в хвост; сортировка tools; заморозить набор tools на сессию (full catalog или frozen shortlist) | Hash static system не меняется при смене active view; поле cache заполнено | S | Низкий |
| P0-2 | Cache accounting | Читать `cached_tokens`/`prompt_cache_hit_tokens`, писать в usage | Поле в `token_usage`, лог на каждый запрос | S | Низкий |
| P0-3 | Token accounting | Учитывать system + tools в оценке; калибровать по usage | Оценка vs факт ≤ 15 % на 10 сессиях | S | Низкий |
| P0-4 | Compaction per round | Проверять порог перед каждым раундом, а не раз за ход | 8 раундов → компакция срабатывает при превышении | S | Низкий |
| P0-5 | Schema validation | Применить `AgentToolSchemaValidator` ко всем тулам; добавить `minLength` (additionalProperties уже поддержан) | 20 malformed payload → `invalid_arguments` с path | S | Низкий |
| P0-6 | Tool input repair | Tolerant JSON, coerce, aliases, unknown-tool suggestions | 20 кейсов; repaired=true в логе | S | Низкий |
| P0-7 | Loop detection v1 | SHA-256(tool+args): 2 одинаковые ошибки → warn, 3 одинаковых вызова → block+ask, 5 → abort | 3 одинаковых вызова → `loop_detected` | S | Низкий |
| P0-8 | Per-tool timeout | `TimeoutMs` в определении (локально, до P3-1); net48-совместимый helper `Task.WhenAny` + `Task.Delay`; таймаут на ожидание очереди/LLM, не на прерывание Revit-вызова; watchdog-лог | Зависший вызов логируется, ход отменяется по CT | S | Средний |
| P0-9 | Usage on failure | Писать usage при ошибке/отмене (колонки из P0-0) | `status=error/cancelled` в ledger | S | Низкий |
| P0-10 | Метрики компакции | before/after токены, duration, причина | Строка в логе + запись в usage | S | Низкий |
| P0-11 | WorkingState + turn-context | Расширить `WorkingState`, инжектить `<turn_context>` каждый ход, персистить, восстанавливать после рестарта | Hash static system не меняется; state восстанавливается | S | Низкий |

**Порядок:** P0-0 первым; P0-1 требует решения «full catalog vs frozen shortlist» (для 27 — full catalog; для 500–1000 — паки, §3.3). До выбора замерить baseline context/billed на 5 сессиях (P0-3). P0-2/P0-9/P0-10 зависят от колонок P0-0.

**DoD фазы:** сборка 7 конфигураций, unit 100 %, eval replay 40/40 без изменений поведения, новые unit-тесты на repair/loop/timeout, ручной прогон чата с логом.

### Фаза 1. Контекст и компакция v2 (14–18 дней)

Цель: длинные сессии без обрыва, с сохранением фактов и дешёвым повторным префиксом.

| ID | Задача | Суть | Приёмка | S/M/L | Риск |
|---|---|---|---|---|---|
| P1-1 | Compaction service | Вынести из оркестратора в `IAgentContextCompactor`; заменить `TrimHistory(120)` на проекцию «checkpoint + tail» (summary строится из store, не из обрезанного working) | Unit на алгоритм, без БД; 200 сообщений → ничего не потеряно молча | M | Средний |
| P1-2 | Ladder orchestrator | mask → summary → hard shrink, каждый уровень измеряется | 3 уровня, каждый с before/after | M | Средний |
| P1-3 | LLM summary | 9 секций, лимит входа + рекурсивная суммаризация, nudge, fallback, механическая проверка ID/имён | Fact-retention ≥ 95 % на синтетике | M | Высокий |
| P1-4 | Checkpoint persistence | `compactions` (v3a) + history projection + persisted assistant-ack | Рестарт → контекст из checkpoint | M | Средний |
| P1-5 | Constraint pinning | Пользовательские ограничения + safety-инварианты вне сжатия; I-правила остаются в static system | После компакции ограничения в промпте | S | Низкий |
| P1-6 | Store-and-reference | Сначала сохранить полный результат (новая таблица/колонка с retention), затем конверт `resultHandle` + `read_tool_result` | Агрегат по 100k элементов или query с limit 200: превью ≤40k, полный результат доступен, пагинация работает | M | Средний |
| P1-7 | Masking rules | Старые tool-результаты → digest, последние 6 + хвост, protected tools | Экономия > 20k токенов | M | Средний |
| P1-8 | Overflow recovery | Context overflow → compact + retry once | Ошибка провайдера не роняет ход | S | Средний |
| P1-9 | Summarizer config | Дешёвая модель, temp, max tokens, max input | Настройки в `AgentLlmSettings` | S | Низкий |
| P1-10 | Fact-retention smoke | 10 синтетических сессий с фактами; live-gated, не replay | ≥ 95 % ответов по фактам | M | Средний |
| P1-11 | Tool catalog v2 | Паки (12–20), карточки с синонимами/примерами/negative, `find_tools` top-5, `load_tool_pack`, фиксация набора | 1000 карточек: top-5 ≥ 95 %, top-1 ≥ 80 %, витрина ≤150 composite, p95 < 100 ms | L | Средний |
| P1-12 | 1M context profile | `ContextWindowTokens` per model, trigger/tail формула, caps; 500k vs 1M | 1M: trigger ~783k/tail ~147k; 500k: ~383k/~72k; caps работают | S | Низкий |

**DoD фазы:** R25/R27/net48 integration зелёные; eval 40/40 + 10 compaction-кейсов + routing-набор 200 запросов (top-5 recall ≥95 %); ручная сессия 100+ ходов без `context_length_exceeded`; в логе видны before/after.

### Фаза 2. Память и знания (14–18 дней)

Цель: опыт между сессиями, поиск по истории, процедуры Revit без раздувания промпта.

| ID | Задача | Суть | Приёмка | S/M/L | Риск |
|---|---|---|---|---|---|
| P2-1 | FTS5 verify | Проверить FTS5 в Microsoft.Data.Sqlite на net48/net8/net10 (smoke до создания таблиц) | `CREATE VIRTUAL TABLE` + insert/query | S | Низкий |
| P2-2 | Migrations v3b | Ordered migrations + additive `v3b`: memory/FTS/approvals/nonces/session columns; backup через SQLite API | v2→v3b на копии БД без потерь | S | Средний |
| P2-3 | Memory files | `MEMORY.md` + topic files + frontmatter; `<docKey>` = SHA-256(documentId)[:16] + `identity.json`; единый корень `%APPDATA%\AGK\SmartCon\Agent` | Файл индекса ≤25KB, инъекция ≤6k символов, загрузка/сохранение | M | Низкий |
| P2-4 | Memory tools | `remember`, `forget`, `recall`; отдельный lightweight diff-approval (не Revit-write approval) | Unit + approval + diff | M | Средний |
| P2-5 | Memory inbox | Pending-записи + approve/reject (backend + минимальный UI) | Запись после approve, reject не пишет | M | Средний |
| P2-6 | Message FTS | `messages_fts` (одна колонка) + триггеры + reconcile | Поиск по 1000 сообщений p95 < 100 ms | M | Средний |
| P2-7 | Search tools | `search_history`, `recall` | Top-5 со snippet и session title | S | Низкий |
| P2-8 | Consolidation | «Dream»: сначала эвристика (дедуп/даты), опционально LLM; не чаще 1/24 ч, бюджет | Дедуп, абсолютные даты, cap, audit | M | Средний |
| P2-9 | Session lifecycle | Search/delete/rename/export, active pointer; явные deletes для `tool_calls`/`change_sets`/`change_items`/`token_usage`/`agent_feedback`; audit retention | Удаление без сирот, audit-политика | M | Средний |
| P2-10 | Skills v1 | `SKILL.md` + index + `load_skill`; **embedded resources** для built-in + локализация | 4 встроенных навыка (`pipe-connect`, `family-manager`, `view-setup`, `parameters`), бюджет | M | Низкий |
| P2-11 | Rules file | Project rules + `extraRules` wiring | Правила попадают в static prefix | S | Низкий |
| P2-12 | Skills v2 | 100–200 навыков: router, skill→tools, conditional activation, trust tiers | ≤100 токенов/навык в индексе; активны ≤2; top-3 recall ≥90 % | M | Средний |

**Корень данных:** канонический `%APPDATA%\AGK\SmartCon\Agent` (там уже `secrets.env`). `agent.db` мигрирует из `%APPDATA%\SmartCon\Agent` при первом запуске: copy + verify + backup; при ошибке — остаёмся на старом пути (fallback).

**DoD фазы:** FTS5 на net48/net8/net10; memory precision ≥ 85 % на 30 запросах; session delete не оставляет сирот; skills 100+ не ломают cache prefix, skill→tools работает.

### Фаза 3. Инструменты, политики, планы (10–14 дней)

Цель: автономность с предсказуемыми правами и атомарными планами.

| ID | Задача | Суть | Приёмка | S/M/L | Риск |
|---|---|---|---|---|---|
| P3-1 | Tool metadata | Kind/maxAffected/defer/requiredContext (Idempotent/Timeout/Bulk уже в H1) | Каталог enriched, тесты | S | Низкий |
| P3-2 | Policy engine | `agent-policy.json`: deny wins; среди allow/ask — последнее совпадение; fail-closed | 20 правил, unparsable → deny writes | M | Средний |
| P3-3 | Persisted grants | `approvals` + «always allow» | Рестарт → grant жив | M | Средний |
| P3-4 | Plan mode | Ruleset deny writes + plan file | Plan mode не меняет toolset | S | Низкий |
| P3-5 | Persisted nonces | `confirmation_nonces` + подключение в production-путь `execute_plan` | Nonce переживает рестарт, replay блокируется | S | Средний |
| P3-6 | Plan atomicity | Один `ExternalEvent`-колбэк: все шаги + read-back + `Assimilate`; переиспользовать `ITransactionGroupSession`/`BeginGroupSession`; change sets отложить до `Assimilate` | Сбой шага → `RollBack`, модель без изменений, change sets не применяются | L | Высокий |
| P3-7 | L6 compensation | Read-back **до** `Assimilate`; mismatch → `RollBack`; для одиночных writes — undo + пометка | 0 unhandled mismatch | S | Средний |
| P3-8 | Verify-on-stop | Auto read-back после одиночного write; для плана — внутри группы | `verified=true/false` в результате | S | Низкий |
| P3-9 | Idempotency | Preflight recheck документа + repeat guard | Повторный write не дублируется | S | Средний |
| P3-10 | Deferred v2 | Stable core + `select:` + cache key; набор фиксируется на сессию | Cache не ломается при find_tools | S | Низкий |

**DoD фазы:** integration на atomic rollback и read-back; eval plan-кейсы; workshared по-прежнему отказ; doc/ADR после ручной приёмки.

### Фаза 4. Надёжность LLM и наблюдаемость (6–8 дней)

Цель: ход не падает от сетевой ошибки, стоимость и качество видны.

| ID | Задача | Суть | Приёмка | S/M/L | Риск |
|---|---|---|---|---|---|
| P4-0 | Fake SSE harness | Скриптованный OpenAI-compatible сервер (retry/stream/errors/usage) | 10 сценариев, snapshot запроса стабилен | M | Средний |
| P4-1 | Retry matrix | Pre/mid-stream, 429/5xx, jitter, cap | 10 сценариев fake server | S | Низкий |
| P4-2 | Stream errors | `{"error":...}`, idle timeout, `Failed` | Ошибка не игнорируется | S | Средний |
| P4-3 | Output budget | `remaining = window - measured - reserve` | Доля `finish_reason=length` < 2 % | S | Низкий |
| P4-4 | Model catalog | context/maxOutput/cache/reasoning/price | Настройки per model | M | Средний |
| P4-5 | Usage ledger v2 | Per request: cached/cost/latency/status | SQL-агрегаты по сессии | S | Низкий |
| P4-6 | Audit correlation | session/turn/request/plan id | Запрос по плану находит все события | S | Низкий |
| P4-7 | Friction UI | Команда/панель + метрики | Отчёт за 30 дней в UI | S | Низкий |
| P4-8 | Request log | Ring-buffer JSONL + redaction | 10 последних запросов | S | Средний |

**DoD фазы:** ни одна сетевая ошибка не оставляет «пустой» ход; cost/cache видны в UI; логи редактируются.

### Фаза 5. Eval v2 и UX (10–14 дней)

Цель: регрессии ловятся автоматически; работа с агентом не требует «перезапустить чат».

| ID | Задача | Суть | Приёмка | S/M/L | Риск |
|---|---|---|---|---|---|
| P5-1 | Eval: plan | 10 кейсов preview/apply/rollback/mismatch | Gate PASS | M | Средний |
| P5-2 | Eval: compaction | 10 fact-retention кейсов; live-gated + judge, отдельно от replay | ≥ 95 % | M | Средний |
| P5-3 | Eval: modes/loop/cache | 20 кейсов; routing — отдельный benchmark 200 | 100 % ожидаемых кодов; routing в §8 | M | Средний |
| P5-4 | Context snapshots | Нормализованные snapshot'ы запроса (fake SSE уже P4-0) | Стабильны к порядку/ID | M | Средний |
| P5-5 | Steering | Очередь + инъекция на границе шага | Сообщение доходит без нового хода | M | Средний |
| P5-6 | Reasoning UI | Collapsible reasoning | Видны делиберации, не только «Думаю» | S | Низкий |
| P5-7 | Timeline/fork/rewind | Fork/rewind/export + timeline (search/delete/rename — P2-9) | Rewind к сообщению + change set | M | Высокий |
| P5-8 | Context breakdown | system/tools/history/summary | Панель как у goose | S | Низкий |
| P5-9 | Memory inbox polish | Pending review UI (backend/min UI — P2-5) | Approve/reject с diff | M | Средний |
| P5-10 | MCP adapter | Backlog, не входит в план спринтов | Не ломает 27 тулов | L | Высокий |

**DoD фазы:** eval ≥ 75 задач, gate; 3 ручных сценария (steering, rewind, memory inbox) с чистым логом.

---

## 6. Детальные спецификации

### 6.1 Compaction v2

**Константы (конфиг, не хардкод):**

```text
ContextWindowTokens      = 500_000   // существующая настройка
MaxOutputTokens          = 8_192     // до P4-3
OutputReserve            = min(MaxOutputTokens, 20_000)
CompactionBuffer         = 13_000
Usable                   = Window - OutputReserve - CompactionBuffer
TriggerRatio             = 0.80      // от Usable
TailTokens               = clamp(0.15 * Usable, 25_000, 150_000)
MinWindowForSummary      = 16_000
SummaryMaxOutput         = 8_192
SummaryMaxInput          = 120_000   // вход суммаризатора, иначе рекурсия
MaxSummaryAttempts       = 2         // 1 nudge
MaxFailuresPerTurn       = 3
ToolOutputInTranscript   = 2_000     // символов
ProtectedRecentResults   = 6
```

Для 500k: `Usable ≈ 478.8k`, триггер ≈ 383k, tail ≈ 72k. Для 1M: `Usable ≈ 979k`, триггер ≈ 783k, tail ≈ 147k. Формула должна учитывать system+tools и проверяться каждый раунд.

**Алгоритм (перед каждым LLM-запросом):**

1. `estimate = lastKnownPromptTokens + estimate(newMessages) + estimate(system+tools)`.
2. Если `estimate < Usable * TriggerRatio` → выход.
3. **Mask:** оставить последние `ProtectedRecentResults` tool-результатов и хвост `TailTokens`; остальные заменить digest'ом. Защищённые: `load_skill`, `recall`, `search_history`, read-back плана. Если экономия < 20k токенов — пропустить (инвалидация кэша не окупается).
4. Пересчитать; если всё ещё выше — **summary**:
   - найти границу разреза: новейшее user-сообщение, при котором хвост ≥ `TailTokens`; никогда не разрывать пару `tool_call`/`tool_result`;
   - собрать транскрипт: user-сообщения дословно, assistant-текст, tool calls (имя+аргументы), tool results — первые 2 000 символов;
   - **ограничить вход суммаризатора** (`SummaryMaxInput ≈ 120k` токенов); если больше — рекурсивная суммаризация чанками (Aider `history.py`), хвост сохраняется дословно;
   - вызвать summarizer с фиксированным шаблоном;
   - проверить наличие всех заголовков; один nudge; при провале — детерминированный fallback (план + последний user + digest);
   - **механически проверить**, что ID/имена из плана и последних user-сообщений присутствуют в summary; отсутствующие — дописать в `Current state`;
   - вставить `<conversation-checkpoint>` как user-сообщение + assistant-ack; **ack сохранить** (`compactions.ack_message_id`), иначе проекция после рестарта неполная;
   - записать `compactions` row.
5. Пересчитать; если всё ещё выше — **hard shrink**: удалять старые обмены (никогда новейший), tool results → 2 000 → 500 → omitted; в крайнем случае оставить system + summary + последний user.
6. Если 3 неудачи за ход → `context_compaction_failed` + actionable hint (новая сессия / сузить задачу).

**TrimHistory:** существующий hard-cap 120 сообщений (`AgentOrchestrator.cs:441-451`) заменить на проекцию «checkpoint + tail». Summary строится из полного store, а не из уже обрезанного working-списка; иначе старые факты теряются молча.

**Шаблон summary:**

```markdown
## Goal
## Constraints & invariants
## Decisions
## Completed
## Current state (document, views, elements, change sets)
## Open questions / blockers
## Next steps
## Touched entities (≤15: views/levels/element ids)
## Important context
```

Требования: точные имена/ID, не упоминать суммаризацию, критичные user-ограничения — цитатой.

**Хранение:**

```sql
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
```

**Сборка истории:** последняя `compactions` для сессии; если `source_prefix_hash` совпадает с хешем сообщений до `to_rowid` — история = summary + сообщения после `to_rowid`; иначе проекция отбрасывается (warn + raw history). UI всегда показывает сырые сообщения + маркер.

**Тесты:** синтетическая сессия 200 ходов с 20 фактами; после компакции 19/20 фактов; пары tool не разорваны; новый estimate < старый; рестарт восстанавливает checkpoint; ошибка summarizer → fallback; cache hit на запросе суммаризации.

### 6.2 Prompt prefix и turn-context

- `AgentSystemPrompt` разделить на `BuildStatic()` и `BuildTurnContext(...)`.
- Static: роль, правила, I-инварианты, tool-usage policy, отсортированный skills index, memory index. Без даты, document id, режима и плана. **Индекс памяти/скиллов фиксируется на сессию**; изменения применяются со следующего хода delta-сообщением, а не переписыванием prefix (иначе cache инвалидируется).
- Tools: для 27 — полный каталог, сортировка по имени. Для 500–1000 — только паки + `find_tools` top-5 + `load_tool_pack` с фиксацией набора (§3.3); полный каталог не отправляется. Внутри `find_tools` кандидаты собираются hybrid-retrieval (BM25 ∪ vector → RRF → decision reranker, ADR-075); в промпт хода попадают только загруженные инструменты.
- Turn-context — отдельное сообщение перед последним user:

```xml
<turn_context>
date: 2026-11-18T14:00
document: {title} | {documentId} | revit 2025 | workshared=false
active_view: {id}/{name}/{type}
selection: {n} elements (ids: ...)
mode: ConfirmWrites
plan: none|draft|approved
turn_budget: 3/8
</turn_context>
```

- `AgentProjectContext`: стабильные части (title/version/counts) можно оставить в system; active view/selection — только turn-context.
- Метрики: `usage.prompt_tokens_details.cached_tokens` (OpenAI) и `usage.prompt_cache_hit_tokens` (DeepSeek); писать в `token_usage`. `cached_tokens` — подмножество `prompt_tokens`: отдельно считаем context-токены и billed-токены/стоимость.
- Приёмка: поле cache заполнено; hash static system не меняется при смене active view; два одинаковых хода дают одинаковый префикс.

### 6.3 Store-and-reference и graduated compression

- **Сначала сохранить полный redacted-результат** (новая таблица `tool_result_blobs` или колонка `full_result_json` с retention); `tool_calls.result_json` сейчас хранит уже обрезанный бюджетом результат, поэтому как источник для store-and-reference не годится.
- `ToolResultBudget.Serialize` возвращает конверт:
  ```json
  { "resultHandle": "call_abc", "preview": {...}, "truncated": true,
    "returned": 50, "total": 12000, "hint": "read_tool_result(...)" }
  ```
- `resultHandle` = `toolCallId`; полный результат читается из blob-хранилища.
- Новый read-only tool `read_tool_result {handle, offset, limit}`: читает сохранённый JSON, отдаёт страницу ≤ 20k символов, поддерживает `path` (dot-path) для точечного чтения.
- Строки: head 60 % + tail 40 % + `…[N chars omitted]…`; массивы — как сейчас; JSON всегда валиден (не позиционная резка).
- Защита от pruning: `load_skill`, `recall`, `search_history`, read-back плана.
- Лимиты: `query_elements` 200, `get_element_details` 10, `list_views` 500, результат ≤ 40k символов; blob хранится 30 дней или до удаления сессии.
- Приёмка: `summarize_elements`/агрегат по 100k элементов или `query_elements` с `limit=200` → превью ≤40k, `truncated=true`, пагинация работает; полный результат доступен после рестарта.

### 6.4 Tool input repair и schema validation

Пайплайн:

1. `AgentJson.ParseTolerant`: code fences, trailing commas, smart quotes, double-encoded JSON string.
2. `AgentToolSchemaValidator.Validate`: добавить `minLength`; `additionalProperties` уже поддержан; `$ref` в текущих схемах не используется — поддержать простой случай, но не блокировать.
3. Coerce: `"42"→42`, `"true"→true`, single value → array, enum case-insensitive.
4. Aliases: `view_id/viewId`, `ids/viewIds`, `element_id/elementId`; tool aliases (`rename_view→rename_views`, `get_views→list_views`).
5. Unknown tool: top-3 suggestions (Levenshtein/BM25); alias match → исполнить canonical и сообщить модели.
6. Ошибка: `{code:"invalid_arguments", path, expected, got, hint, repaired:false}`.
7. Все ремонты писать в `tool_calls.arguments_json._repairs[]` и audit; молчаливых подмен нет.
8. Применять ко всем тулам, не только `execute_plan`.

Приёмка: 20 malformed payload unit-тестов; invalid args rate < 1 % на eval.

### 6.5 Loop / stuck detection

- `LoopGuard` (Agent): `Register(toolName, canonicalArgs, result) -> Ok|Warn|Block|Abort`.
- Ключ: `SHA256(tool + canonical JSON args)`.
- Пороги (единая семантика): 2 одинаковые ошибки → warning в tool-result + hint; 3 одинаковых вызова → сообщение «ты повторяешься, смени подход или спроси» + блок; 5 → `loop_detected` + завершить ход.
- Сброс при других аргументах/успехе.
- План: duplicate step на L0; повторный fail плана → stop + audit.
- Контентный уровень (позже): 50-символьные чанки, 10 повторов → warning; LLM-проверка после 30 ходов.
- Метрика `loop_detected` в usage/audit.

### 6.6 Memory v1

**Layout:**

```text
%APPDATA%\AGK\SmartCon\Agent\
  memory\
    MEMORY.md                 # индекс; файл ≤25KB, в промпт ≤200 строк / 6k символов
    user.md                   # предпочтения пользователя
    projects\
      <docKey>\               # SHA-256(documentId)[:16] — '|', '\', ':' недопустимы в пути
        identity.json         # {documentId, title, path, revitVersion}
        MEMORY.md             # индекс проекта
        <topic>.md            # views, families, parameters, pipe-connect
    inbox\
      pending-<id>.md          # предложенные записи
  skills\<name>\SKILL.md
  plans\<sessionId>.md
```

**Frontmatter:**

```yaml
---
name: pipe-connect-connectors
description: Как соединять трубные коннекторы в PipeConnect
type: project|user|feedback|reference
updated: 2026-11-18
---
```

**Правила:**
- Индекс всегда в static prefix (name + description + 1 строка): файл ≤25KB, но в промпт попадает не более 200 строк / 6k символов.
- Тело — только по `recall`/`load_memory`; не грузить всё.
- `remember` пишет файл + index + FTS; `forget` архивирует (не удаляет).
- Approval для памяти — отдельный lightweight diff-диалог, не Revit-write approval; в AutoApproveSession diff показывается после записи.
- Бюджет: 6k символов индекса, 2k на запись; максимум 50 активных записей на scope.
- Consolidation: после 5 сессий или 24 ч, не чаще 1/24 ч; сначала эвристика (дедуп по name/description hash, нормализация дат), LLM — опционально; stale 30/90 дней; бюджет и audit.
- Память — untrusted: обёртка `<memory_context>`; из памяти нельзя менять права/tools.
- Документ переименован/перемещён: `identity.json` хранит прежние пути; при смене `documentId` — предупреждение и предложение связать истории.

### 6.7 FTS5

```sql
CREATE VIRTUAL TABLE messages_fts USING fts5(
  content,
  tokenize = "unicode61 remove_diacritics 2 tokenchars '_-@'",
  content = 'messages',
  content_rowid = 'rowid'
);

CREATE TRIGGER messages_ai AFTER INSERT ON messages BEGIN
  INSERT INTO messages_fts(rowid, content) VALUES (new.rowid, new.content);
END;
CREATE TRIGGER messages_ad AFTER DELETE ON messages BEGIN
  INSERT INTO messages_fts(messages_fts, rowid, content)
    VALUES ('delete', old.rowid, old.content);
END;
CREATE TRIGGER messages_au AFTER UPDATE ON messages BEGIN
  INSERT INTO messages_fts(messages_fts, rowid, content)
    VALUES ('delete', old.rowid, old.content);
  INSERT INTO messages_fts(rowid, content) VALUES (new.rowid, new.content);
END;
```

- `search_history(query, sessionId?, limit=10)`: `bm25(messages_fts)` (одна колонка — один вес) + prefix `"tok"*` + AND; группировка по сессии, title из `sessions`. Если нужен вес заголовка — отдельная FTS `session_search(title, content)`, а не фиктивные веса.
- `recall(query)`: тот же FTS по `memory_fts` + messages; top-5; snippet.
- Reconcile при старте: rebuild FTS, если таблица пуста или версия схемы изменилась; tombstones для удалённых сессий.
- Проверить FTS5 на net48 (SQLitePCLRaw bundle); fallback — `LIKE` с ограничением.

### 6.8 Policy engine и persisted approvals

**Конфиг** `%APPDATA%\AGK\SmartCon\Agent\agent-policy.json` + проект `.smartcon/agent-policy.json`:

```json
{
  "rules": [
    { "effect": "deny",  "tool": "set_parameters", "args": { "parameterName": "Mark*" } },
    { "effect": "ask",   "tool": "rename_*" },
    { "effect": "allow", "tool": "get_*|list_*|query_*" },
    { "effect": "allow", "tool": "set_view_visibility", "args": { "viewIds": "*" }, "scope": "project" }
  ],
  "defaults": { "read": "allow", "write": "ask" }
}
```

- Оценка: сначала любой `deny` (fail-closed), затем последнее подходящее `allow/ask`; нет совпадения → default.
- Ресурсы: имя тула + нормализованные аргументы (glob); shell/скриптов у нас нет, но правило пригодится для будущих tools.
- `approvals` table: `project_id, tool, args_hash, decision, scope, expires_at`; «Always allow for this project» создаёт запись.
- `confirmation_nonces`: nonce, plan_digest, document_id, expires_at, used_at — переживает рестарт.
- Plan mode: ruleset `deny all writes`, allow только plan file/read-back; `execute_plan` — только dry-run.
- UI: чекбокс «Всегда разрешать для проекта», список grants в настройках, отзыв.
- Приёмка: 20 правил unit; рестарт сохраняет grant; просроченный grant → ask; unparsable config → deny writes.

### 6.9 Plan atomicity и verify-on-stop

- **Использовать существующий `ITransactionGroupSession`** (`ITransactionService.BeginGroupSession`, `RevitTransactionGroupSession`); новый `ITransactionGroupService` не нужен.
- **Все шаги плана + L6 read-back + `Assimilate` выполняются в одном `IExternalEventHandler.Execute`** (одном `RaiseAsync`-колбэке). Нельзя открыть группу в одном `RaiseAsync`, а шаги выполнять в других: Revit откатывает незакрытую группу при выходе из API-контекста.
- `AgentPlanExecutor` не должен вызывать `AgentWriteToolBase.ExecuteAsync` per-step с отдельным `RaiseAsync`; нужен режим `ExecuteStepsInGroup` внутри Revit-boundary, который получает список шагов и выполняет их в одном колбэке.
- **Seam-контракт:** Core объявляет `IAgentPlanStepRunner` (или `IAgentTool.ExecuteInRevitContext`), реализация в Revit выполняет шаг **без** повторного `RaiseAsync` — она уже внутри колбэка. Read-back идёт тем же путём. Иначе повторный `RaiseAsync` = deadlock.
- **Change sets отложить:** `AgentWriteToolBase` сейчас пишет change set сразу после commit шага. В группе собирать `PendingChangeSet` в памяти; после `Assimilate` — записать; при `RollBack` — не писать (только audit `plan_atomic_rollback` с per-step outcome).
- L5: ошибка шага → `RollBack`; change sets не применяются.
- L6: read-back внутри группы до `Assimilate`; mismatch → `RollBack`.
- Workshared уже отклоняется preflight; компенсирующий undo-fallback не нужен. Если группа недоступна по API — hard fail `plan_group_unavailable`, не частичное применение. Флаг `plan.atomic=false` — только legacy поэтапный режим (не атомарный), для отладки; в атомарном пути частичного применения нет.
- Отмена: проверять `CancellationToken` между шагами и перед `Assimilate`; при отмене — `RollBack` группы и `OperationCanceledException`, а не частичный commit.
- Progress: события шагов складывать в очередь и отдавать UI после возврата из колбэка (не блокировать Revit-поток).
- Single write: после commit — auto read-back затронутых сущностей; `verified=true/false`; `false` → warning + hint, не «готово».
- Verify-on-stop: если в ходе были writes и нет свежего read-back — добавить автоматический read-back перед финальным ответом; модель обязана сослаться на evidence.
- Приёмка: integration «шаг 2 падает → модель не изменилась»; «L6 mismatch → откат»; «workshared → отказ»; «после отката change sets отсутствуют».

### 6.10 Usage ledger и cost caps

- `token_usage` per request: provider, model, prompt, completion, reasoning, cached, cache_write, cost, latency_ms, status, request_id, compaction_id.
- `ModelCatalog`: context, maxOutput, supportsTools, supportsCache, supportsReasoning, priceIn/priceOut/priceCached/priceCacheWrite.
- Стоимость: `(prompt - cached) * priceIn + cached * priceCached + (cacheWrite * priceCacheWrite) + completion * priceOut`; цена провайдера главнее каталога; всё в decimal.
- Caps: `MaxTurnTokens`, `MaxSessionTokens`, `MaxSessionCostUsd`; при превышении — graceful stop с сообщением, не exception.
- Output budget: `max_tokens = clamp(remaining - reserve, 1024, model.MaxOutput)`.
- UI: header «tokens / cost / cache %»; клик — breakdown.
- Приёмка: SQL-агрегат сессии; cap останавливает ход; usage пишется при ошибке/отмене.

### 6.11 Skills v1

- Каталоги: built-in (поставляются с плагином), user (`%APPDATA%\...\skills`), project (`<document memory>\skills`).
- `SKILL.md`: frontmatter `name`, `description`, `version`, `tools` (опционально), `paths`/`triggers`, `trust`.
- В static prefix — только `name + description` (≈100 токенов/навык); при 1M допустим полный индекс 100–200 навыков (10–20k токенов), при 500k — фильтр по пакам/триггерам.
- `load_skill(name)` → тело ≤5k токенов + список `references/`, `scripts/`, `assets/`; `read_skill_resource`.
- Триггеры: активный модуль (FamilyManager/PipeConnect), тип документа, ключевые слова через BM25; conditional activation.
- Agent-draft навыки: `pending` → approve → eval → active; не расширяют права.
- Встроенные навыки v1: `pipe-connect`, `family-manager`, `view-setup`, `parameters`.
- Приёмка: 4 навыка, индекс не ломает cache, `load_skill` не превышает бюджет.

### 6.12 Что берём из OpenCode и ZCode в первую очередь

| Источник | Приём | Куда |
|---|---|---|
| OpenCode | Permission rules `{action,resource,effect}`, last match, deny-first, once/always/reject | P3-2, P3-3 |
| OpenCode | Tool output 2 000 строк/50 KiB + полный вывод в файл + маркер | P1-6 |
| OpenCode | Стабильный порядок tools и system-частей «общее→частное» | P0-1 |
| OpenCode | Compaction: keep 15k tail, checkpoint-секции, keepID, background precompute | P1-1…P1-4 |
| OpenCode | Tool input repair + alias (`bash→shell`) | P0-6 |
| OpenCode | Subagent как фоновая сессия с sessionID и глубиной 1 | P5 (позже) |
| OpenCode | Doom-loop: 3 идентичных вызова → permission ask | P0-7 |
| ZCode | Memory dir + `MEMORY.md` index + typed files + recall | P2-3…P2-5 |
| ZCode | AGENTS.md global/project + skills index + on-demand load | P2-10, P2-11 |
| ZCode | Isolated subagents, parallel dispatch, SendMessage | P5 (позже) |
| ZCode | Plan mode + TodoWrite | P3-4 |
| ZCode | Compaction + tool budgets + truncation hints | P1 |
| ZCode | Permission modes; denied call не ретраится | P3-2 |
| ZCode | Hooks и workflow-оркестрация | P5 (позже) |
| ZCode | Memory hygiene: update not duplicate, absolute dates | P2-8 |

---

## 7. Схема `agent.db` v3–v4

**Миграции:** ordered list, каждая в транзакции; backup через `SqliteConnection.BackupDatabase` (или `VACUUM INTO`), не копированием открытого файла; `PRAGMA user_version`; ошибка → лог + история недоступна, приложение живо. WAL не включать (I-14), journal `DELETE`. Между процессами Revit «один writer» не гарантирован — миграция берёт exclusive lock с busy-timeout и при неудаче откладывается.

**v3a — контекст и наблюдаемость (P0-0, до P1):**

```sql
ALTER TABLE messages ADD COLUMN turn_id TEXT;

CREATE TABLE compactions (...);            -- см. §6.1
CREATE TABLE tool_result_blobs (            -- полный результат до обрезки
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

**v3b — память, поиск, approvals (P2-2):**

```sql
ALTER TABLE messages ADD COLUMN visibility TEXT NOT NULL DEFAULT 'both';
-- both|ui|agent|agent_invisible
ALTER TABLE sessions ADD COLUMN is_active INTEGER NOT NULL DEFAULT 0;  -- active pointer
ALTER TABLE sessions ADD COLUMN last_opened_at TEXT;

CREATE TABLE memory_entries (...);         -- кэш файлов памяти
CREATE VIRTUAL TABLE memory_fts USING fts5(...);
CREATE VIRTUAL TABLE messages_fts USING fts5(...);
CREATE TRIGGER messages_ai/ad/au ...;

CREATE TABLE approvals (...);              -- §6.8
CREATE TABLE confirmation_nonces (...);
```

**v4 — аудит и события (P4):**

```sql
ALTER TABLE audit_log ADD COLUMN session_id TEXT;
ALTER TABLE audit_log ADD COLUMN turn_id TEXT;
ALTER TABLE audit_log ADD COLUMN plan_id TEXT;

CREATE TABLE agent_events (
  id INTEGER PRIMARY KEY AUTOINCREMENT,
  session_id TEXT, turn_id TEXT, seq INTEGER,
  type TEXT NOT NULL, payload_json TEXT,
  created_at TEXT NOT NULL
);
CREATE INDEX ix_agent_events_session ON agent_events(session_id, seq);
```

**Удаление сессии:** явные `DELETE` для `messages`, `tool_calls`, `change_sets`, `change_items`, `token_usage`, `agent_feedback`, `tool_result_blobs`, `compactions`; `audit_log` — политика retention (например, 90 дней, без содержимого); либо `ON DELETE CASCADE` для новых таблиц. Иначе остаются сироты.

**Ограничения:** FTS5 проверить на net48/net8/net10 до v3b (P2-1); `messages.content` без base64 (вложения не индексировать); `memory_entries` — кэш файлов, источник правды — файлы; rebuild при старте, если hash каталога изменился; `tool_result_blobs` чистить по `expires_at` и при удалении сессии.

---

## 8. Метрики и гейты

| Метрика | Baseline | Target P1 | Target final |
|---|---|---|---|
| Cache hit rate (cached / prompt) | не измеряется | ≥ 50 % | ≥ 80 % |
| Context-токены на read-задачу | измерить в P0 | −20 % | −35 % |
| Billed-токены/стоимость на read-задачу | измерить в P0 | −15 % | −30 % |
| Context-токены на write-план | измерить в P0 | −20 % | −35 % |
| Tool error rate | измерить в P0 | < 8 % | < 3 % |
| Invalid args rate | измерить в P0 | < 3 % | < 1 % |
| Routing top-1 / top-5 recall | не измеряется | top-1 ≥ 80 %, top-5 ≥ 95 % | top-1 ≥ 85 %, top-5 ≥ 98 % |
| Routing p95 (BM25 / embedding / reranker) | не измеряется | < 100 ms / < 150 ms / < 2 s | < 50 ms / < 100 ms / < 1 s |
| Recall@K BM25 / vector / union | не измеряется | фиксируется в routing-отчёте | vector добавляет ≥5 п.п. top-5 |
| `finish_reason=length` rate | измерить в P0 | < 5 % | < 2 % |
| Loop detections / ход | 0 (не детектит) | — | < 1 % |
| Compaction fact retention | n/a | ≥ 95 % | ≥ 97 % |
| Compaction failures / ход | n/a | 0 | 0 |
| Unhandled L6 mismatch | 100 % | 0 | 0 |
| Plan apply success | integration 6/6 | ≥ 95 % | ≥ 98 % |
| Eval-задач | 40 | 50 | ≥ 75 |
| Cost visibility | нет | per turn | per session + cap |
| FTS p95 (1000 сообщений) | n/a | < 100 ms | < 50 ms |
| Memory precision (30 запросов) | n/a | ≥ 85 % | ≥ 90 % |
| Session resume correctness | авто-выбор | 100 % | 100 % |

**Eval v2 (добавить к 40):**

- 10 plan-кейсов: preview, apply, atomic rollback, L6 mismatch, workshared refusal.
- 10 compaction: синтетика 200 ходов, 20 фактов, вопросы после сжатия.
- 5 loop: одинаковые вызовы, одинаковые ошибки, повтор шага плана.
- 5 modes: ConfirmWrites, AutoApproveSession, ReadOnly, plan mode, bypass limits.
- 5 cache: одинаковый префикс, смена active view, смена tools, смена модели.
- 3 restart/resume: kill после tool call, resume, checkpoint.
- 3 memory: remember/recall/forget, precision.
- 3 FTS: поиск по 3 сессиям, snippet, фильтр.
- Routing benchmark (отдельно, не входит в 86): 200 запросов, top-1/top-5 recall, false tool rate, p95.
- 2 steering: сообщение во время хода.
- Итого eval-кейсов: 40 + 46 = 86; routing benchmark (200) — отдельный gate. Gate — по критичным (schema 100 %, unauthorized 0, plan ≥ 95 %, compaction ≥ 95 %). Compaction-fact-retention — live-gated + judge; replay-часть проверяет только структуру/размер/пары tool.

---

## 9. Риски и rollback

| Риск | Вероятность | Влияние | Митигация | Rollback |
|---|---|---|---|---|
| Summary теряет факты | Средняя | Высокое | Verbatim user, pinned constraints, fact-retention eval, fallback | Флаг `compaction.summary=false` → digest-only |
| Compaction ломает prompt cache | Средняя | Среднее | Сжатие только при реальной необходимости; логировать cache hit | Флаг `compaction.enabled=false` |
| Cache-предположения не работают у провайдера | Средняя | Низкое | Измерять, не завязывать функциональность | Отключить cache-метрики/поля |
| FTS5 недоступен на net48 | Низкая | Среднее | `SQLitePCLRaw.bundle_e_sqlite3` включает FTS5; smoke P2-1; fallback `LIKE` | Флаг `search.fts=false` |
| Миграция БД портит историю | Низкая | Высокое | `BackupDatabase`/`VACUUM INTO`, транзакция, тест на копии | Восстановить backup, откатить версию |
| `TransactionGroup` недоступен/тяжёл | Средняя | Высокое | Один `ExternalEvent`-колбэк; проверить на реальной модели; иначе `plan_group_unavailable` | Флаг `plan.atomic=false` → legacy поэтапный (не атомарный) режим |
| Перенос `agent.db` в новый корень | Средняя | Высокое | Copy + verify + backup; fallback на старый путь | Остаться на старом пути |
| Второй Revit-процесс пишет в ту же БД | Низкая | Среднее | Exclusive lock + busy-timeout; миграция откладывается | Read-only режим истории |
| Memory prompt injection | Средняя | Высокое | `<memory_context>` untrusted, no permissions from memory | Очистить память, отключить inject |
| Persisted grant устарел | Средняя | Среднее | TTL, scope, recheck affected | Отозвать grants, флаг |
| File > 600 строк | Высокая | Низкое | Partial-разбивка сразу | — |
| Multi-version API | Средняя | Высокое | `#if`, интеграционные R25/R27/net48 | Откат конфигурации |
| Стоимость LLM растёт | Средняя | Среднее | Caps, дешёвый summarizer, cache | Уменьшить лимиты |

**Общий rollback:** каждая фича — флаг в `AgentLlmSettings`/конфиге; выключение возвращает текущее поведение. Схема БД — только additive (`ADD COLUMN`, новые таблицы), старый код продолжает работать.

---

## 10. Порядок, зависимости, DoD

### 10.1 Зависимости

```text
P0-0 (schema v3a) ──► P0-2/9/10, P1-4, P1-6
P0-2/3 (token/cache) ──► P1-1..P1-10 ──► P5-2 (compaction eval)
P0-5/6 (validation/repair) ──► P3-1 (metadata) ──► P3-2 (policy)
P2-1 (FTS5 smoke) ──► P2-2 (v3b) ──► P2-3..P2-9 (memory/FTS/sessions)
P3-6 (atomic plan) ──► P3-7/8 (compensation/verify)
P4-0 (fake SSE) ──► P4-1/2 (reliability)
P1/P2/P3 ──► P5-1/3 (eval v2)
P1-11 (tool catalog) ──► P2-12 (skill→tools)
P1-11 (tool catalog) ──► P5-3 (routing eval)
P1-12 (1M profile) ──► P4-5 (cost/cache)
```

### 10.2 Спринты (ориентир, 1 разработчик; 9–12 спринтов)

| Спринт | Содержание | Выход |
|---|---|---|
| 1 | P0-0 + P0 (P0-0 — отдельный gate миграции) | Схема v3a, метрики, repair, loop, timeout, стабильный префикс |
| 2 | P1-1…P1-6, P1-11 (start) | Compaction v2 + checkpoint + store-and-reference + tool catalog v2 |
| 3 | P1-7…P1-12, P2-1…P2-4 | Masking, memory v1, FTS smoke, migrations v3b, 1M profile |
| 4 | P2-5…P2-12 | FTS, inbox, sessions, skills 100+, rules |
| 5 | P3-1…P3-5 | Metadata, policy, grants, plan mode, nonces |
| 6 | P3-6…P3-10, P4-0…P4-4 | Atomic plan, verify, idempotency, fake SSE, reliability |
| 7 | P4-5…P4-8, P5-1…P5-4 | Observability, eval v2 |
| 8 | P5-5…P5-9 | Steering, timeline, memory inbox polish |
| 9–12 (буфер) | P3-6/P5-7 хвосты, стабилизация | Если atomic plan/rewind займут больше |

### 10.3 Definition of Done (каждая фаза)

1. Сборка 7 конфигураций: 0 warnings / 0 errors (`tools\build-all-configs.ps1`).
2. Unit `SmartCon.Tests -c Debug.R25`: 100 %.
3. Integration R25 + net48 (+ R27, если установлен): 0 failed; новые тесты на Revit-boundary.
4. Eval replay: gate PASS; для P1+ — новые кейсы; live-gated (fact-retention judge) — отдельный nightly-прогон, не блокирует replay.
5. Ручной сценарий в Revit: лог `smartcon.log` без необъяснимых Error/Warn, цепочки OpId целые.
6. Нет файлов > 600 строк; partial-разбивка.
7. Инварианты I-01…I-17 и dependency rule соблюдены.
8. Docs/ADR/changelog — только после ручной приёмки (правило проекта).
9. Флаг фичи задокументирован в `docs/agent/` и отключаем.

---

## Приложение A. Каталог фишек по источникам

### A.1 OpenCode (sst/opencode, v2 `packages/core/src`)

| Приём | Механика | Файл |
|---|---|---|
| Permission ruleset | `{action,resource,effect}`, wildcard, last match, default ask, deny-first | `permission.ts:102-115,193-234` |
| once/always/reject | `always` сохраняет allow-правила в БД | `permission.ts:369-390` |
| Tool output truncation | 2 000 строк / 50 KiB, полный вывод в файл, маркер | `tool-output.ts:75-143` |
| Стабильный префикс | tools сортируются, system-части «общее→частное», `promptCacheKey` | `tool.ts:322-327`, `session/context.ts:170-181` |
| Compaction | keep 15k tail, checkpoint-секции, `keepID`, background precompute | `session/compaction.ts:441-540,1002-1015` |
| Input repair | tolerant JSON, aliases (`bash→shell`), объяснение модели | `plugin/tool-input-repair.ts:21-62` |
| Doom loop | 3 идентичных вызова → permission ask | `session/prompt.ts` (v1) |
| Subagent | фоновая сессия, `sessionID`, глубина 1 | `tool/plugin/subagent.ts`, `session/subagent-depth.ts` |
| Skills | индекс name/description, тело по tool call | `skill/instructions.ts:25-46` |

### A.2 Codex (openai/codex, Rust)

| Приём | Механика | Файл |
|---|---|---|
| Parallel barrier | read-lock для safe, write-lock для exclusive | `core/src/tools/parallel.rs:205,330-342` |
| Head+tail truncation | середина режется, маркер, 20 % запас | `utils/string/src/truncate.rs:28-53` |
| apply_patch DSL | `*** Begin Patch`, fuzzy seek, delta | `apply-patch/src/seek_sequence.rs:12-111` |
| AGENTS.md | иерархия, override, бюджет 32 KiB | `core/src/agents_md.rs:1-18,279-303` |
| Compaction checkpoint | `replacement_history`, 90 %, 20k retained | `core/src/compact.rs:733-857` |
| Rollout JSONL + SQLite | append-only журнал + индекс, resume от `Compacted` | `rollout/src/recorder.rs`, `state_db.rs` |
| Approvals | untrusted/on-request/never, session cache, prefix rules | `protocol/src/protocol.rs:1198-1239` |
| Tool search | BM25, deferred tool loading | `core/src/tools/handlers/tool_search.rs` |
| Model catalog | context/max output/cache/reasoning per model | `protocol/src/openai_models.rs` |

### A.3 OpenHands (software-agent-sdk)

| Приём | Механика | Файл |
|---|---|---|
| Event log | append-only, View-проекция, tombstones | `events/stream.py`, `memory/view.py` |
| Condenser pipeline | recent → browser → LLM summary, hard reset | `context/condenser/` |
| Token pressure | HARD по токенам, SOFT по событиям | `llm_summarizing_condenser.py:140-207` |
| Observation masking | старые observations → `<MASKED>` | `observation_masking_condenser.py` |
| Microagents/skills | progressive disclosure, path-trigger | `skills/skill.py`, `skills/trigger.py` |
| Security risk | per-tool enum, `ConfirmRisky`, UNKNOWN fail-closed | `security/confirmation_policy.py` |
| Sectioned prompt | `CacheTier.STATIC/DYNAMIC`, дата в конце | `context/prompts/section.py` |
| Stuck detector | 5 сценариев, nudge, truncate-to-loop-start | `controller/stuck.py:38-131` |

### A.4 Cline (cline/cline, v4 SDK)

| Приём | Механика | Файл |
|---|---|---|
| Compaction sidecar | immutable log + `source_count` + `prefix_hash` | `session/models/session-compaction.ts:156-193` |
| Tolerant schemas | aliases, coerce, JSON-in-string repair | `core/src/extensions/tools/schemas.ts:66-176` |
| Retry matrix | 3 attempts, no retry after visible content, max-tokens nudge | `agent-runtime.ts:1269-1329,1378-1423` |
| Loop detection | signature sorted JSON, soft 3 / hard 5 | `runtime/safety/loop-detection.ts` |
| Tool result cache | >8k → URI, TTL 5 итераций, 16 MB | `session/services/tool-result-cache.ts` |
| FTS5 session search | bm25 title 8/content 1, tokenchars `_-@` | `session/search/session-history-search.ts` |
| Plan/act presets | mode → tools, command guard | `core/src/extensions/tools/presets.ts` |
| Resume repair | synthetic `interrupted` tool_result | `Task.ts:2070-2120` |

### A.5 Roo Code (RooCodeInc/Roo-Code)

| Приём | Механика | Файл |
|---|---|---|
| Custom modes | groups, `fileRegex`, always available | `shared/modes.ts:28-44` |
| Non-destructive condense | summary как user, `condenseParent` фильтрация | `condense/index.ts:459-480` |
| File context tracker | прочитанные файлы, stale reads | `FileContextTracker.ts:56-100` |
| Skills | progressive disclosure + mode filter | `skills.ts:19-105` |
| Delegation lineage | status/awaitingChildId/childIds | `ClineProvider.ts:2783-2911` |
| Repetition detector | 3 одинаковых вызова → ask | `ToolRepetitionDetector.ts:9-70` |
| Result ID repair | dedup + missing results | `validateToolResultIds.ts:49-120` |
| Task history index | reconcile, resume repair | `TaskHistoryStore.ts` |

### A.6 Aider (Aider-AI/aider)

| Приём | Механика | Файл |
|---|---|---|
| Repo map | tree-sitter tags → graph → PageRank → 1–4k токенов | `repomap.py:365-706` |
| Context order | system → examples → readonly → map → history → files → cur | `chat_chunks.py:16-26` |
| Prompt cache | breakpoints, map стабилизируется | `chat_chunks.py:28-55` |
| Edit diagnostics | did-you-mean, partial success, 3 retry | `editblock_coder.py:76-126` |
| Reflection | lint/test → next prompt | `base_coder.py:1599-1623` |
| Architect/editor | plan и edit разделены | `architect_coder.py:11-54` |
| Recursive summary | weak model, keep tail, depth 3 | `history.py:7-123` |
| Token report | per file/block + cost | `commands.py:445-548` |

### A.7 goose (block/goose)

| Приём | Механика | Файл |
|---|---|---|
| Durable state machine | session reload each iteration, synthetic interrupted results | `goose-agent/src/machine.rs:163-300` |
| Turn budget | hint after half, `ResourceExhausted` | `goose-agent/src/max_turns.rs:22-70` |
| MCP namespacing | `extension__tool`, provider-safe names + hash | `extension_manager/lease.rs:148-159` |
| Schema normalize | `oneOf` const → enum | `tool_schema_normalize.rs:6-24` |
| Compaction via visibility | `agent_invisible` / `agent_only` | `context_mgmt/mod.rs:185-196` |
| Turn context MOIM | volatile data outside system prompt | `agents/moim.rs:37-105` |
| Recipes | params contract, subrecipes, final JSON schema, retry checks | `recipe/mod.rs`, `validate_recipe.rs` |
| Approval annotations | read_only → auto-allow, unknown → ask | `permission_inspector.rs:55-66` |
| Usage ledger | per request, cost source, GenAI spans | `session_manager.rs:1231-1244` |

### A.8 Gemini CLI (google-gemini/gemini-cli)

| Приём | Механика | Файл |
|---|---|---|
| Declarative tools + kind | policy matrix allow/deny/ask, modes | `tools/tools.ts:473`, `policy/types.ts:12,49` |
| Validation as tool result | invalid params → model, Levenshtein unknown tool | `scheduler.ts:403-472` |
| Output caps | 40k → file + head 20%/tail 80%, 64 KiB cap | `utils/fileUtils.ts:814-834` |
| GEMINI.md hierarchy | global/project/extension, `@imports` realpath | `utils/memoryDiscovery.ts`, `memoryImportProcessor.ts` |
| JIT folder context | подгрузка правил при доступе | `tools/jit-context.ts`, `memoryDiscovery.ts:512` |
| Compression | 50 %, keep 30 %, XML snapshot, verify pass, inflation guard | `chatCompressionService.ts:45,367,749-873` |
| Session JSONL + rewind | `$patch`, `$rewindTo`, retention | `chatRecordingService.ts`, `sessionCleanup.ts` |
| Loop detection | hash cycles 1..5×5, chunks, LLM after 30 turns | `loopDetectionService.ts:174,313-349` |
| Checkpoint | shadow git before mutation | `gitService.ts:136,365-408` |

### A.9 Claude Code (публичные разборы + snapshot)

| Приём | Механика |
|---|---|
| Deferred tool loading | `shouldDefer`, ToolSearch, `select:<tool>`, кэш набора |
| Compaction 5 слоёв | budget reduction → snip → microcompact → collapse → auto-compact |
| Threshold | `window − min(maxOutput,20K) − 13K`; warning 20K, manual 3K |
| Summary | 9 секций, все user messages verbatim, re-attach ≤5 файлов/50K, skills ≤25K |
| Memory | CLAUDE.md 4 уровня + auto `MEMORY.md` 200 строк/25KB + Auto Dream |
| Constraint pinning | инварианты вне сжатия, реинъекция |
| Subagents | isolated context, fork copирует prefix байт-в-байт, sidechain, только финальный отчёт |
| Skills | metadata ~100 токенов, тело <5K, resources on demand |
| Plan mode | permission transition, plan file, approval возвращает текст |
| Checkpoints | снимок перед каждым prompt, restore code/conversation |
| Hooks | ~30 событий, `PreCompact`, `SessionStart(compact)` |
| Prompt cache | stable order, no tool changes, no timestamps |

### A.10 ZCode (текущий харнес)

| Приём | Механика |
|---|---|
| Memory dir | `MEMORY.md` index + typed files (user/feedback/project/reference), recall by relevance |
| Memory hygiene | обновлять существующую запись, не дублировать; абсолютные даты; удалять неверное |
| AGENTS.md | global + project instructions, override |
| Skills | SKILL.md + frontmatter, index в контексте, загрузка по требованию |
| Subagents | isolated context, parallel dispatch, SendMessage для продолжения |
| Plan mode | EnterPlanMode/ExitPlanMode + approval |
| TodoWrite | видимый пользователю план работ |
| Compaction | summary + remaining context; tool result budget + hints |
| Permissions | режимы; denied call — feedback, не retry |
| Workflows | scripted subagent topology, typed results, gating on commands |
| Background tasks | cron/off-peak, persistent schedule |
| UI | streaming, tool cards, markdown, attachments, images |

### A.11 29 паттернов arXiv:2609.00006: решение по каждому

| # | Паттерн | Решение |
|---|---|---|
| 1 | Event Sourcing | Позже, только журнал событий (не переписывание) |
| 2 | Policy-as-Code | Да, P3-2 |
| 3 | Recursive Composition | Позже, read-only explorer |
| 4 | Polymorphic Edits | Нет: наши tools структурные |
| 5 | Deferred Loading | Да, P3-10 |
| 6 | Template Method | Уже есть (tool base) |
| 7 | Protocol Interfaces | Да, `IAgentTool` уже |
| 8 | LLM Summarization | Да, P1-3 |
| 9 | Stuck Detection | Да, P0-7 |
| 10 | Reflection Loop | Да, verify-on-stop P3-8 (build/lint — позже) |
| 11 | Prompt Caching | Да, P0-1 |
| 12 | Context Forking | Позже, subagent |
| 13 | Middleware Pipeline | Да, компакция/policy pipeline |
| 14 | JIT Repo Context | Да, rules/memory по документу P2-11 |
| 15 | Skills | Да, P2-10 |
| 16 | Conditional Activation | Да, skills triggers |
| 17 | Turn-Level Checkpoint | Да, change sets + rewind P5-7 |
| 18 | Agent-Maintained Memory | Да, bounded inbox P2-5/P2-8 |
| 19 | Outer Verification Loop | Да, P3-8 |
| 20 | Self-Improving Skill Loop | Позже, только с eval |
| 21 | Lineage Compaction | Позже (checkpoint достаточно) |
| 22 | Session-Tree Version Control | Частично: fork P5-7 |
| 23 | Minimal-Core / Extension-Host | Нет: WPF-плагин |
| 24 | Client/Server Harness | Нет |
| 25 | Model-Family Prompt Matrix | Да, P4-4 |
| 26 | Cache-Dialect Fanout | Нет: один диалект |
| 27 | Syntax-Aware Command Permissioning | Позже: shell нет |
| 28 | Untrusted-Content Delimiting | Да, memory/skills |
| 29 | Harness Mimicry | Нет |

---

## Приложение B. Библиография

**Исследования и статьи**

- arXiv:2609.00006 — Harness Engineering: Anatomy, Architecture, and Evolution of Coding Agents (11 систем, 29 паттернов), июль 2026.
- arXiv:2604.14228 — Dive into Claude Code.
- arXiv:2610.36114 — Truncation poisoning.
- arXiv:2606.22528 — Governance decay / constraint pinning.
- Claude Code docs: memory, context window, prompt caching, checkpoints, hooks, skills.
- OpenCode docs: compaction, permissions; source `packages/core/src`.
- Codex docs/source: `codex-rs/core`, `codex-rs/compact.rs`, `codex-rs/memories`.
- Gemini CLI docs: auto-memory, chat compression, extensions.
- OpenHands SDK: `context/condenser`, `skills`, `security`.
- Cline/Roo/Aider/goose — исходники репозиториев (см. A.1–A.8).

**Репозитории (снимки на 2026-11-18)**

| Репозиторий | Снимок |
|---|---|
| `sst/opencode` | v2, `packages/core/src` |
| `openai/codex` | commit `4389a52` |
| `All-Hands-AI/OpenHands` | HEAD TS + tag 0.62.0 + `OpenHands/software-agent-sdk` |
| `cline/cline` | v4.1.26, commit `eff43e19` |
| `RooCodeInc/Roo-Code` | v3.53.0, commit `b867ec9` |
| `Aider-AI/aider` | commit `5dc9490` |
| `block/goose` | v1.60.0, commit `927005e` |
| `google-gemini/gemini-cli` | `4f343cabc`, 0.70.0-nightly |
| ZCode | текущий харнес (system prompt/память/скиллы) |

**Внутренние документы**

- `docs/agent/00-strategy/archive/mvp-plan.md` — исходный scope.
- `docs/agent/00-strategy/archive/night-run-2.md` — тактический прогон.
- `docs/agent/00-strategy/eval.md` — текущие метрики.
- `docs/agent/HANDOFF.md` — состояние на 2026-11-18.
- `docs/invariants.md` — I-01…I-17.
- `docs/architecture/dependency-rule.md` — куда класть код.

---

## Приложение C. Быстрый чек-лист первого спринта

- [ ] P0-0: schema v3a + FTS5 smoke + backup через SQLite API.
- [ ] P0-1: static/dynamic system + turn-context.
- [ ] P0-2: `cached_tokens` в usage.
- [ ] P0-3: оценка токенов с system+tools.
- [ ] P0-4: компакция перед каждым раундом.
- [ ] P0-5: schema validation для всех тулов.
- [ ] P0-6: tolerant JSON + coerce + aliases.
- [ ] P0-7: loop guard (2/3/5).
- [ ] P0-8: per-tool timeout.
- [ ] P0-9: usage при ошибке/отмене.
- [ ] P0-10: метрики компакции.
- [ ] Unit + integration + eval replay + ручной лог.
