# Agent — AI-харнес для Revit (BIM-ops)

> **Статус:** active · 2026-11-18 · ветка `develop`
> **Профиль:** не coding-harness, а **BIM-ops**: пользователь поручает агенту рутинные операции с `.rvt` вместо скриптов/Dynamo.
> **Масштаб:** 500–1000 инструментов, 100–200 навыков, контекст до 1M токенов (сейчас выставлено 500k).
> **Вне scope:** создание новых 3D-объектов «с нуля»; остальные кейсы (виды, листы, параметры, семейства, ФОП, фильтры, worksets, связи, MEP, аннотации, аудит, purge) — целевые.

## Что уже есть

| Подсистема | Состояние |
|---|---|
| Чат-панель | DockablePane, WPF MVVM, вложения/скриншоты, streaming |
| LLM | OpenAI-compatible streaming, DeepSeek V4.1 Flash через OpenRouter/OpenCode Go, reasoning |
| Инструменты | 28 тулов (24 Revit + 4 meta), каталог v2: 20 паков, карточки, BM25 top-5 + `find_tools` + `load_tool_pack`; гибридный retrieval 500–1000 — ADR-075, ещё не реализован |
| Запись | dry-run → подтверждение → change set → `undo_last`/`discard_change_set` |
| План | `execute_plan` L0–L6 (L2 нет, L5 не атомарен — план H1/H3) |
| Approvals | ConfirmWrites / AutoApproveSession / ReadOnly, digest-binding |
| Контекст | WorkingState + `<turn_context>`, store-and-reference (`tool_result_blobs` + `read_tool_result`), compaction v2 (mask → 9-section checkpoint → hard shrink, persisted/restored) |
| Память | Сессии в SQLite; долговременной памяти и FTS пока нет |
| Eval | 40 задач replay, gate PASS; routing 102 запроса — top-1 95.1 %, top-5 100 %, p95 2.05 мс; 1000 карточек 100 % |
| Сборка | 7 конфигураций R19–R27, unit 3743, integration R25/R27/net48 |

**Не заявлять как готовое:** L2-critic, атомарный план, память между сессиями, skills 100–200, MCP, run-time policy engine, гибридный retrieval (vector + embeddings), выбор embedding-модели/reranker'а в настройках, code mode / sandbox-интерпретатор / runtime C#, model entity search / BM25-каталог сущностей. Это roadmap, не факт. Архитектурные решения: retrieval — [ADR-075](../adr/075-hybrid-tool-retrieval.md), tool surface и code mode — [ADR-076](../adr/076-tool-surface-and-code-mode.md), model search — [ADR-077](../adr/077-model-entity-search.md).

## Карта документов

### Активные

| Документ | Роль |
|---|---|
| [`00-strategy/README.md`](00-strategy/README.md) | Индекс стратегии и статусов |
| [`00-strategy/harness-ideal-plan.md`](00-strategy/harness-ideal-plan.md) | Долгосрочная стратегия: gap-анализ, фазы P0–P5, 29 паттернов, схема БД, метрики |
| [`00-strategy/harness-development-approach.md`](00-strategy/harness-development-approach.md) | Единый процесс: жизненный цикл, гейты, роли, эпики |
| [`00-strategy/initial-improvement-runbook.md`](00-strategy/initial-improvement-runbook.md) | Эпик **H1** — первый большой рывок, исполнимый runbook |
| [`00-strategy/h1b-tool-packs.md`](00-strategy/h1b-tool-packs.md) | **H1b–H1d**: model search, view configuration, family editing, кейсы и границы (cyclic → H8) |
| [`00-strategy/h1d-evidence.md`](00-strategy/h1d-evidence.md) | **H1d**: матрица requirement→artifact, негативная матрица, масштаб, гейты |
| [`family-tools-error-catalog.md`](family-tools-error-catalog.md) | Каталог ошибок и контракт ответа family-тулов |
| [`family-editing-runbook.md`](family-editing-runbook.md) | Ручная приёмка family editing pack (чек-лист, pending UI) |
| [`00-strategy/eval.md`](00-strategy/eval.md) | Журнал eval-прогонов и baseline |
| [`00-strategy/decisions-benchmark.md`](00-strategy/decisions-benchmark.md) | Research note: decision-модели |
| [`HANDOFF.md`](HANDOFF.md) | Текущее состояние, что сделано/осталось |
| [`AGENT-FEEDBACK.md`](AGENT-FEEDBACK.md) | Живой dogfooding-фидбэк модели |

### Архив

| Документ | Статус |
|---|---|
| [`00-strategy/archive/mvp-plan.md`](00-strategy/archive/mvp-plan.md) | archived: исторический MVP |
| [`00-strategy/archive/autonomous-execution-plan.md`](00-strategy/archive/autonomous-execution-plan.md) | archived: runbook первого прогона |
| [`00-strategy/archive/night-run-2.md`](00-strategy/archive/night-run-2.md) | archived: ночной прогон №2 |

## С чего начать

| Кто ты | Порядок чтения |
|---|---|
| Новый агент-исполнитель | этот файл → `HANDOFF.md` → `00-strategy/harness-development-approach.md` → `00-strategy/initial-improvement-runbook.md` |
| Владелец/ревьюер | этот файл → `00-strategy/harness-ideal-plan.md` §1–§4 → `00-strategy/eval.md` |
| Архитектор | `harness-ideal-plan.md` §3/§6/§7 + ADR в `docs/adr/` |

## Эпики

| Эпик | Содержание | Документ |
|---|---|---|
| **H1 (закрыт)** | Tool catalog v2, store-and-reference, WorkingState, compaction v2 — готово | [`initial-improvement-runbook.md`](00-strategy/initial-improvement-runbook.md) |
| **H1b (следующий)** | Живой поиск по модели без индекса: нормализация запроса (стеммер/синонимы/алиасы), `list_family_types`, `find_families_with_parameter`, `list_parameters`, `query_elements` с `parameterContainsAny`; 100k экземпляров — scoped-проход, one-shot cache (ADR-077) | [`../adr/077-model-entity-search.md`](../adr/077-model-entity-search.md), [`00-strategy/h1b-tool-packs.md`](00-strategy/h1b-tool-packs.md) |
| **H1c** | View configuration: шаблон или текущий вид, visibility категорий/осей, фильтры по условиям, overrides (покрасить стены), crop/scope boxes | [`00-strategy/h1b-tool-packs.md`](00-strategy/h1b-tool-packs.md) §3 |
| **H1d (закрыт 2026-11-19)** | Family editing pack: 9 тулов, EditFamily без UI, shared/формулы/lookup, map, точный undo, бэкап; ручной UI-тест pending | [`00-strategy/h1d-evidence.md`](00-strategy/h1d-evidence.md), [`family-tools-error-catalog.md`](family-tools-error-catalog.md), [`family-editing-runbook.md`](family-editing-runbook.md) |
| H8 (после H1d) | Bulk/cyclic job engine: select + action + батчи, прогресс/отмена/resume; альтернатива — sandbox-скрипт (H7), не runtime C#. Отдельный дизайн, не в H1 | [`00-strategy/h1b-tool-packs.md`](00-strategy/h1b-tool-packs.md) §5 |
| H2 | Memory v1 + FTS5 + skills 100–200 | `harness-ideal-plan.md` P2 |
| H3 | Policy engine, persisted approvals, atomic plan | `harness-ideal-plan.md` P3 |
| H4 | Observability, cost caps, eval v2 | `harness-ideal-plan.md` P4–P5 |
| H5 | UX: steering, timeline, fork/rewind, MCP (backlog) | `harness-ideal-plan.md` P5 |
| H7 (до H6) | Tool surface и длинный хвост: intent-tools, generic data tools, запрет runtime C#; typed facade `IRevitOps` + sandbox-интерпретатор (Jint/Lua), промоция скриптов в composite-tools (ADR-076) | [`../adr/076-tool-surface-and-code-mode.md`](../adr/076-tool-surface-and-code-mode.md) |
| H6 (последний) | Гибридный retrieval 500–1000: vector + BM25 ∪ RRF + decision reranker, выбор embedding-модели/reranker'а в настройках (ADR-075); запускается только после H1b и по метрике recall | [`../adr/075-hybrid-tool-retrieval.md`](../adr/075-hybrid-tool-retrieval.md) |

## Поведение H1 (без флагов)

Легаси-ветки v1 удалены: пользователей старого поведения нет, совместимость не нужна.
Харнес всегда работает по новой логике:

- tool catalog v2: паки, карточки, `find_tools` (только discovery) и `load_tool_pack`;
- учёт system+tools в оценке контекста и калибровка по `usage.prompt_tokens`;
- persisted WorkingState и вставка `<turn_context>`;
- store-and-reference (`tool_result_blobs` + `read_tool_result`) и compaction v2
  (mask → summary → hard shrink).

Настраиваются только параметры контекста (не поведение): `SMARTCON_AGENT_CONTEXT_TOKENS`,
`SMARTCON_AGENT_CONTEXT_THRESHOLD`, `SMARTCON_AGENT_SUMMARY_MODEL`,
`SMARTCON_AGENT_SUMMARY_MAX_INPUT_TOKENS`, `SMARTCON_AGENT_SUMMARY_MAX_OUTPUT_TOKENS`.

## Жёсткие ограничения

- Revit API — **только** через `IExternalEventHandler` (I-01); никаких вызовов из WPF/async.
- Транзакции — только через `ITransactionService`; `TransactionGroup` — через `ITransactionGroupSession` (I-03/I-04).
- `SmartCon.Core` не вызывает Revit API и не содержит `System.Windows` (I-09).
- SQLite — `DELETE` journal, без WAL (I-14); один writer.
- Не хранить `Element`/`Connector` между транзакциями — только `ElementId`/`UniqueId` (I-05).
- Файл `.cs` — цель ≤500 строк, жёсткий лимит 600; partial-разбивка.
- Логи — через `SmartConLogger.BeginScope`, `Warn` с `[Action: ...]`.

## Быстрые команды

```bash
# Сборка всех 7 конфигураций
powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-all-configs.ps1

# Unit (всегда R25)
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25

# Integration (Revit 2025 / 2027 / net48)
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 --framework net8.0-windows
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R27 --framework net10.0-windows
dotnet run --project src/SmartCon.IntegrationTests -c Debug.R21 --framework net48 -p:RevitVersion=2023

# Eval replay (без ключа)
SMARTCON_AGENT_EVAL_REPLAY=1 dotnet run --project src/SmartCon.IntegrationTests -c Debug.R25 \
  --framework net8.0-windows -- --treenode-filter "/*/*/*AgentEvalTests*/*"
```

## Правила документации

1. Один вопрос — один SSOT. Стратегия не дублируется в runbook; runbook не переписывает стратегию.
2. Новый документ начинается с шапки: статус, дата, владелец, `supersedes`.
3. Завершённый runbook → `00-strategy/archive/` + баннер + ссылка на актуальный документ.
4. `HANDOFF.md` — только состояние и следующие шаги, не план на месяц.
5. Код и документы «сделано» — только после зелёных гейтов и ручной приёмки в Revit.
