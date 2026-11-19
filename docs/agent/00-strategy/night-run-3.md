# Night Run 3 — 12 часов: 6 эпиков размера H1

> **Статус:** active · 2026-11-19 · владелец: продукт
> **Формат:** автономный ночной прогон, 12 часов.
> **Темп:** H1 был закрыт AI-агентами за ~1.5 часа, поэтому 6 эпиков размера H1 за 12 часов — рабочий темп, а не фантастика. Но каждый эпик закрывается только с артефактами и гейтами; «код написан» — не готово.
> **Цель:** к утру иметь работающий job engine, family batch с Save-чекпоинтами и recovery, а не набор несвязанных тулов.

## 0. Правила прогона

- Работай в `D:\Project\dotNET\AGK-SmartCon-Pro`, ветка `develop`.
- Вопросов не задавай; при неоднозначности — безопасный вариант (fail-closed), решение в HANDOFF.
- В T+11:00 прекрати код; последний час — гейты, HANDOFF, отчёт.
- После каждого эпика: локальный коммит + `docs/agent/HANDOFF.md`.
- Не пушить, не PR, не запускать `build-and-deploy.bat`.
- Не трогать `opencode.json`, `server/`, секреты, docs вне `docs/agent`.
- Сборка: только затронутые проекты/конфигурации; 7 конфигураций — один раз в финале.
- Перед новым Revit API — 3–5 запросов Exa/MCP; не гадать.
- Каждый эпик: unit + targeted integration; полный R25/net48/R27 и eval replay — в финале.

## 1. Читать сначала

1. `docs/agent/README.md`
2. `docs/agent/HANDOFF.md` (верхние блоки)
3. `docs/agent/00-strategy/autonomy-roadmap.md`
4. `docs/agent/00-strategy/task-family-batch-v1.md`
5. `docs/invariants.md`, `docs/architecture/dependency-rule.md`
6. `docs/adr/076-tool-surface-and-code-mode.md`, `docs/adr/075-hybrid-tool-retrieval.md`
7. `src/SmartCon.Revit/Agent/Tools/Families/` — `AgentFamilySession`, `AgentFamilyUndo`, backup
8. `src/SmartCon.Agent/Services/AgentOrchestrator*`, `AgentAwaitableEvent`, `AgentToolExecutor`

Текущее состояние: 45 тулов, family-тулы (read/write/formula/backup/undo) есть; job engine нет; `execute_plan` ≤20 шагов и не атомарен.

## 2. Шесть эпиков

### Эпик 1 (T+0:30–2:00) — A0 + Family Batch v1 (размер H1c)

Реализовать `task-family-batch-v1.md` целиком:

- A0-lite: `expected/actual/verified`, idempotency key, read-before-write, per-item undo, document identity, batch lock.
- `run_family_batch`: `familyIds` 1–50, `map_parameter`/`set_parameter_values`, `dryRun`, `previewDigest`, `onError`, `rollbackOnError`, `savePolicy`.
- Preview: per-family/per-type `old → new`, digest, expiry.
- Apply: digest check, backup/undo, read-back, `Document.Save()` после пачки, `saved`/`volatile`.
- Откат пачки при ошибке; `restored[]`/`restoreFailed[]`.
- Тесты: 20 негативных кейсов; seeded family (shared+formula+instance/type); kill до/после Save.

DoD: 100 семейств изменены пачками по 20–50; preview/apply/save/rollback; false-done 0; unit+integration.

### Эпик 2 (T+2:00–4:00) — Job core + SQLite v4 (размер H1)

- Core: `JobSpec`, `JobState`, `IJobAction`, `IJobStore` без Revit API.
- Agent: `SqliteAgentJobStore`, `JobRunner`, `JobReportBuilder`.
- SQLite v4 аддитивно: `jobs`, `job_batches`, `job_items`, `job_events`; `last_saved_cursor`, `last_saved_at`.
- Состояния: `preview_running → preview_ready → awaiting_approval → running → completed|completed_with_failures|failed|cancelling|cancelled|interrupted|rolled_back|rollback_partial`.
- Frozen preview: count+sample+selectorHash+documentId+expectedHash+expiry.
- Тесты: state machine, crash windows, resume, digest mismatch, document change, schema v3→v4 self-heal.

DoD: job создаётся/возобновляется на фейковом action без Revit; миграция идемпотентна.

### Эпик 3 (T+4:00–6:30) — A1 project bulk + save (размер H1)

- `RevitJobActionBase` (Preview/Execute/Verify/Undo); `JobPumpEventHandler` — отдельный `IExternalEventHandler`.
- Срез 1–2 с на `Execute`, один срез в полёте, `IsPending`-guard, re-raise; не использовать общий `AgentAwaitableEvent.ProcessQueue`.
- Действия: `rename_views`, `set_parameters` (project-level).
- Save policy: `after_each_batch` default; cursor только после `Document.Save()`.
- Cancel между юнитами; kill -9 после Save → resume; до Save → переделка среза.
- Тесты: 500 элементов, p95 Execute ≤2 с, cancel ≤2 с, failure injection в 7-м, doc change abort.

DoD: 500 элементов изменены пачками; truthfulness 100 %; false-done 0.

### Эпик 4 (T+6:30–8:30) — A2 family batch on engine (размер H1)

- `map_family_parameters` как `IJobAction`: 1 семейство/юнит, preview per type, apply, read-back.
- Backup/undo через `AgentFamilySession`; `LoadFamily` только вне открытой транзакции target.
- Save после пачки; volatile при `savePolicy=never`/save failure.
- Тесты: 100 семейств, ошибка в середине, rollback пачки; kill после Save; повторный apply no-op.

DoD: 100 семейств; per-family report; save-чекпоинт; resume без дублей.

### Эпик 5 (T+8:30–10:00) — B trust & recovery (размер H1)

- `get_job_report` (JSON/CSV: diff, backup, verified/mismatch), `rollback_job`, `retry_failed_items`.
- False-done guard: отчёт сверяется с независимым read-back; расхождение → mismatch.
- Crash reconciliation по `job_events`.
- UI/агентские статусы: queued/running/saved/unsaved/failed/cancelled.
- Тесты: 20 failure injections, rollback 100 %, false-done 0, report = read-back.

DoD: recovery-сценарии зелёные; отчёт пригоден для аудита.

### Эпик 6 (T+10:00–11:00) — C1 selectors + один read-pack (размер H1, урезаемый)

- `resolve_entities` по family/type/params/views/sheets (живые списки, без индекса).
- Один breadth-read pack на выбор по реальному кейсу: rooms/worksets/warnings/links.
- Если время поджимает: только family/type selectors; read-pack переносится.
- Тесты: correct entity ≤2 вызова ≥90 %, false <5 %; routing/eval на новых фразах.

DoD: агент находит нужные id без полного перебора; карточки/паки/тесты.

### Финал (T+11:00–12:00)

- Unit 100 %; integration R25 + net48 + R27 (0 failed); eval replay 46/46 + job-кейсы; 7 конфигураций; Release 0 warnings.
- HANDOFF: что сделано, метрики, что не проверено, manual pending.
- Финальный отчёт.

## 3. Жёсткие правила

| ID | Правило |
|---|---|
| J-01 | Один API-писатель: только job runner; агент ставит job и читает статус |
| J-02 | `Execute` 1–2 с; отмена только между юнитами; никаких `Thread.Sleep`/`.Result`/`.Wait()` |
| J-03 | Транзакция/TransactionGroup на срез, не на job; `Commit/Assimilate` проверять по статусу |
| J-04 | Durable state: intent/undo в SQLite до изменения; `last_saved_cursor` только после Save |
| J-05 | Read-back — часть `done`; без него success запрещён |
| J-06 | Явный scope: project instance / project type / family definition / `.rfa` |
| J-07 | `try/catch/log` вокруг `Execute`; preflight; watchdog `Pending` >30 с; политика рестарта |

Дополнительно:
- `TransactionGroup` — только на один атомарный срез; не на весь job.
- Family-документы всегда закрывать; при ошибке — без сохранения.
- `.rfa` — backup/staging; `.0001` не считать надёжным backup.
- Workshared: preflight; не писать молча; `Save` ≠ SynchronizeWithCentral.
- Никаких удалений параметров/семейств; никакого runtime C#.

## 4. Если отстаёшь или опережаешь

- Отстаёшь: режь Эпик 6, затем Эпик 5 до report+false-done. Нельзя резать A0, save-политику, read-back, crash-resume и финальные гейты.
- Опережаешь: добавь A3 external orchestrator (дизайн + каркас) или второй read-pack; зафиксируй в HANDOFF.

## 5. Финальный отчёт

1. Какие 6 эпиков закрыты, коммиты.
2. Метрики: truthfulness, per-item success, false-done, rollback/resume/idempotency, p95 Execute, LLM calls/job.
3. Что проверено в Revit (500 элементов, 100 семейств, kill/resume, save failure).
4. Что не успел/не проверено; manual pending.
5. Ссылка на HANDOFF и обновлённые документы.
6. Где срезали scope и почему.