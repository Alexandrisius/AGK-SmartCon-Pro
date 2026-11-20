# ADR-079: Durable batch job engine

**Date:** 2026-11-20
**Status:** accepted
**Related:** [ADR-076](076-tool-surface-and-code-mode.md), [ADR-077](077-model-entity-search.md),
эпик A (#289), B (#291), `docs/agent/ROADMAP.md`

## Context

Каталог агента дорос до 55 инструментов, но массовые операции (сотни видов, элементов,
семейств) выполнялись циклом LLM: один вызов инструмента на элемент. Это давало сотни
round-trip'ов, не давало durable-состояния (закрытие Revit теряло прогресс), не давало
проверяемого «done» (Commit ≠ результат) и не давало предсказуемого отката.

Revit API однопоточен: `ExternalEvent.Execute` блокирует UI на всё время вызова, а
`TransactionGroup` нельзя оставлять открытым между вызовами (Revit откатывает его при
выходе из команды). Значит, «фонового» выполнения нет — есть только дробление на срезы.

## Decision

### 1. Job engine — отдельный слой, не «ещё один batch-tool»

- **Core** (`SmartCon.Core/Models/Agent/Jobs`, `Services/Interfaces/Agent/IJobAction.cs`):
  `JobSpec`, `JobState`/`JobItemState`, `JobPreview` + digest, `JobRecord`/`JobItem`,
  `JobTotals`, `JobReport`, `IJobAction`/`IJobStore`/`IJobScheduler`/`IJobRecovery`.
  Revit API только как carrier (`Document` как opaque-параметр), вызовов нет (I-09).
- **SmartCon.Agent**: `SqliteAgentJobStore` (миграция v5) — durable-состояние.
- **SmartCon.Revit**: `JobRunner` (срезы), `JobPumpEventHandler` (свой `ExternalEvent`),
  `RevitJobActionBase` и конкретные действия, `JobReportBuilder`.
- **SmartCon.App**: DI и отдельный `ExternalEvent` (не общий `AgentAwaitableEvent`).

### 2. Контракт задания

- `JobSpec`: schemaVersion, documentId, action, mode (preview/apply/resume), spec (action-специфичный),
  options (`batchSize`, `sliceBudgetMs`, `onError`, `rollbackOnError`, `savePolicy`, `maxItems`).
- **Frozen preview**: `Expand` замораживает юниты (unitKey = idempotency key), `Preview`
  считает `expected/actual/undo`. Digest = SHA256(`action|documentId|selectorHash|specHash|count|willChange|expiresAt`),
  TTL 30 минут. Apply принимает только этот digest + documentId.
- **Preview-stale guard**: apply перечитывает модель; расхождение с `preview.actual` →
  `job_preview_stale` (чужое изменение не перезаписывается, undo не «стареет»).
- **Per-unit undo в SQLite до изменения** (J-04): undo считается в Preview от фактически
  прочитанного значения; family-действия пишут payload после backup и до первой записи.

### 3. Исполнение и durability

- **Один срез = один `ExternalEvent.Execute`**: бюджет 1–2 с, одна транзакция на батч
  (`ITransactionService.BeginTransaction`), никаких `Thread.Sleep/.Result/.Wait`.
- **Один активный job на документ**: частичный уникальный индекс `ux_jobs_active_document`
  (states PreviewRunning…Running, Cancelling, Interrupted). Прерванный job держит блокировку
  до resume/cancel — осознанный fail-closed.
- **Save policy**: `after_each_batch` (default) / `every_n_minutes` / `never`.
  `last_saved_cursor` обновляется **только после успешного `Document.Save()`**;
  ошибка Save → job `Failed(job_save_failed)`, `saved=false`, без ложного success.
- **Crash resume**: первый срез процесса делает `Reconcile` (осиротевшие Running/Preview/Cancelling
  → Interrupted). `start_job(mode=resume)` перепроверяет read-back'ом `Applying` и `Done`
  за курсором: совпало — остаётся Done (идемпотентно), нет — `Previewed` (переделка).
- **False-done guard**: перед финальным статусом все `Done/Skipped` перепроверяются
  независимым read-back; любое расхождение → `Mismatch` + `CompletedWithFailures`.
  `JobTotals.Incomplete` (Pending/Applying/Undoing/Undone) запрещает `Completed`.

### 4. Tool surface

`start_job` (preview → apply по digest; resume), `get_job_status` (long-poll ≤25 с без
Revit-маршалинга), `cancel_job`, `get_job_report` (per-item diff, CSV), `rollback_job`
(dryRun сначала), `retry_failed_items`. Пак `jobs`, карточки с русскими примерами,
секция системного промпта «никогда не крути цикл поэлементно». Поток ≤3 вызовов LLM:
preview → apply → status.

### 5. Границы

- Не runtime C# и не per-method tools (ADR-076): действие — это intent
  (`rename_views`, `set_parameters`, `map_family_parameters`), а не обёртка API.
- Workshared-модели пока не поддерживаются (preflight `job_workshared_unsupported`).
- Family-действие: 1 семейство = 1 юнит, 1 юнит на срез; `EditFamily`/`LoadFamily` вне
  транзакции проекта (I-03b); каждый family-документ закрывается в `finally`;
  после `LoadFamily` unit key протухает → резолв `unitKey → newFamilyUniqueId → familyName`.
- Verify family читает проектную сторону (`FamilySymbol`); цели instance-scope/elementId
  не верифицируются и дают честный `Mismatch`, а не ложный `done`.

## Consequences

- Массовая операция — один job; LLM-вызовов ≤3 вместо сотен; прогресс переживает рестарт.
- Каждый apply требует свежего preview (digest + TTL) — лишний шаг, зато нет «слепых» записей.
- Откат ограничен per-item undo: удаления и структурные изменения без undo не откатываются
  (эпик G/S).
- Мультипроцессный Revit не поддержан: lease-поле в схеме есть, owner-token не реализован;
  второй процесс может пометить живой job как Interrupted (fail-closed, документировано).
- `get_job_status` поднимает pump — read-tool продолжает ранее одобренный apply (осознанно).
- Оценка: движок добавляет ~8.5k строк и SQLite-миграцию v5; сложность локализована в
  `JobRunner*` и трёх действиях.