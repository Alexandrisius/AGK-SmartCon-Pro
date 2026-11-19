# Agent — AI-харнес для Revit (BIM-ops)

> **Статус:** active · 2026-11-19
> **Единственный рабочий документ — [`ROADMAP.md`](ROADMAP.md).** Там north star,
> принципы, приоритеты, эпики/задачи и `ID ↔ issue # ↔ статус`.
> **Конкретная работа и статусы — GitHub Issues** (метка `agent`).
> **Архив — [`archive/`](archive/):** исторические планы, runbook'и, evidence и фидбэк.
> Не руководство к действию; читать только для истории.

## С чего начать

1. [`ROADMAP.md`](ROADMAP.md) — карта работ и текущий фокус.
2. `gh issue list --label ready` — очередь к работе.
3. `gh issue view <N>` — тело задачи; оно самодостаточно.
4. `docs/invariants.md` + `docs/architecture/dependency-rule.md` — жёсткие правила.

## Текущий фокус

**P — порядок динамического контекста и KV-кэш:** `P.1` → `P.2` → `P.3` → `P.4` → `P.5`.
Следом — `A` (job engine), начиная с `A.1`. Полный порядок — `ROADMAP.md` §4.

## Что уже есть (кратко)

| Подсистема | Состояние |
|---|---|
| Чат-панель | DockablePane, WPF MVVM, вложения/скриншоты, streaming |
| LLM | OpenAI-compatible streaming, DeepSeek V4.1 Flash через OpenRouter/OpenCode Go |
| Инструменты | 55 тулов каталога, 20 паков, карточки, BM25 top-5 + `find_tools` + `load_tool_pack` |
| Запись | dry-run → подтверждение → change set → `undo_last`/`discard_change_set`; family backup/undo |
| План | `execute_plan` L0–L6 (≤20 шагов, не атомарен) |
| Автономность | без лимитов раундов/времени; Stop, LoopGuard, compaction |
| Approvals | ConfirmWrites / AutoApproveSession / ReadOnly, digest-binding |
| Контекст | WorkingState + `<turn_context>`, store-and-reference, compaction v2 |
| Eval | 46 задач replay, gate PASS; routing 170 фраз — top-1 95.9 %, top-5 100 % |
| Сборка | 7 конфигураций R19–R27 |

**Не заявлять как готовое:** job engine, атомарный план, долговременная память,
skills 100+, policy engine, гибридный retrieval (vector), code mode / sandbox.
Это roadmap — см. `ROADMAP.md` и соответствующие issues.

## Жёсткие ограничения

- Revit API — только через `IExternalEventHandler` (I-01).
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

1. **Новых планов и runbook-файлов не создаём.** План — тело эпика, статус — issue.
2. **`ROADMAP.md` обновляется при любом изменении работ** (ID ↔ issue ↔ статус).
3. **Архив не используется для решений.** Актуально только `ROADMAP.md` + issues.
4. **«Сделано»** — после зелёных гейтов и ручной приёмки в Revit.