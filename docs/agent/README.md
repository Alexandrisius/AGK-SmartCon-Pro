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
| Инструменты | 45 тулов (42 Revit + 3 meta), каталог v2: 20 паков, карточки, BM25 top-5 + `find_tools` + `load_tool_pack`; семейства, листы/ведомости, фильтры/overrides |
| Запись | dry-run → подтверждение → change set → `undo_last`/`discard_change_set`; family backup/snapshot + undo |
| План | `execute_plan` L0–L6 (≤20 шагов, не атомарен); массовые операции — job engine (эпик A, ещё не реализован) |
| Автономность | без лимитов раундов/времени; Stop, LoopGuard, compaction; массовые операции пока через вызовы агента |
| Approvals | ConfirmWrites / AutoApproveSession / ReadOnly, digest-binding |
| Контекст | WorkingState + `<turn_context>`, store-and-reference (`tool_result_blobs` + `read_tool_result`), compaction v2 (mask → 9-section checkpoint → hard shrink, persisted/restored) |
| Память | Сессии в SQLite; долговременной памяти и FTS пока нет |
| Eval | 46 задач replay, gate PASS; routing 170 фраз — top-1 95.9 %, top-5 100 %, p95 1.7 мс |
| Сборка | 7 конфигураций R19–R27, unit 3739, integration R25 (355 passed / 0 failed), net48/R27 — по запросу |

**Не заявлять как готовое:** L2-critic, атомарный план, память между сессиями, skills 100–200, MCP, run-time policy engine, гибридный retrieval (vector + embeddings), выбор embedding-модели/reranker'а в настройках, code mode / sandbox-интерпретатор / runtime C#, model entity search / BM25-каталог сущностей. Это roadmap, не факт. Архитектурные решения: retrieval — [ADR-075](../adr/075-hybrid-tool-retrieval.md), tool surface и code mode — [ADR-076](../adr/076-tool-surface-and-code-mode.md), model search — [ADR-077](../adr/077-model-entity-search.md).

## Карта документов

### Активные

| Документ | Роль |
|---|---|
| [`00-strategy/README.md`](00-strategy/README.md) | Индекс стратегии и статусов |
| [`00-strategy/harness-ideal-plan.md`](00-strategy/harness-ideal-plan.md) | Долгосрочная стратегия: gap-анализ, фазы P0–P5, 29 паттернов, схема БД, метрики |
| [`00-strategy/harness-development-approach.md`](00-strategy/harness-development-approach.md) | Единый процесс: жизненный цикл, гейты, роли, эпики |
| [`00-strategy/autonomy-roadmap.md`](00-strategy/autonomy-roadmap.md) | **Актуальный план:** job engine → надёжность → targeted search → breadth → memory; порядок, дизайн и метрики |
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
| [`00-strategy/archive/initial-improvement-runbook.md`](00-strategy/archive/initial-improvement-runbook.md) | archived: H1 выполнен |
| [`00-strategy/archive/h1b-tool-packs.md`](00-strategy/archive/h1b-tool-packs.md) | archived: H1c/H1d закрыты, H1b отложен |

## С чего начать

| Кто ты | Порядок чтения |
|---|---|
| Новый агент-исполнитель | этот файл → `HANDOFF.md` → `00-strategy/harness-development-approach.md` → `00-strategy/autonomy-roadmap.md` |
| Владелец/ревьюер | этот файл → `00-strategy/harness-ideal-plan.md` §1–§4 → `00-strategy/eval.md` |
| Архитектор | `harness-ideal-plan.md` §3/§6/§7 + ADR в `docs/adr/` |

## Эпики

| Эпик | Содержание | Документ |
|---|---|---|
| **H1 (закрыт)** | Tool catalog v2, store-and-reference, WorkingState, compaction v2 | архив |
| **H1c (закрыт)** | View configuration: фильтры, overrides, visibility, crop | архив |
| **H1d (закрыт 2026-11-19)** | Family editing pack: 9 тулов, EditFamily без UI, shared/формулы/lookup, map, undo, backup | [`00-strategy/h1d-evidence.md`](00-strategy/h1d-evidence.md) |
| **A — Job engine (следующий)** | Массовые операции: job spec, батчи 10–50, прогресс/отмена/resume, preview digest, per-item rollback, UI не блокируется | [`00-strategy/autonomy-roadmap.md`](00-strategy/autonomy-roadmap.md) §5 |
| **B — Reliability** | Единый read-back, идемпотентность, crash recovery, аудит, job-панель | roadmap §6 |
| **C — Targeted search** | `search_catalog` по семействам/типам/параметрам/видам/листам; без индекса элементов; decision только при неоднозначности | roadmap §7 |
| **D — Breadth** | Помещения, worksets, warnings, связи; массовые — через job | roadmap §8 |
| **E — Memory/skills/policy** | Долговременная память, skills 10–20, persisted approvals | roadmap §9 |
| **H1b (отложен)** | Полный поиск по модели/элементам не делаем; заменён targeted search (C) | архив |
| H7 (до H6) | Typed facade `IRevitOps` + sandbox (Jint/Lua), промоция скриптов в composite-tools; не runtime C# | [`../adr/076-tool-surface-and-code-mode.md`](../adr/076-tool-surface-and-code-mode.md) |
| H6 (последний) | Vector только при каталоге >200 видимых тулов или recall <95 % | [`../adr/075-hybrid-tool-retrieval.md`](../adr/075-hybrid-tool-retrieval.md) |

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
