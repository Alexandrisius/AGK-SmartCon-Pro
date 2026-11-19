# Night Run 3 — 12 часов: от family batch к job engine

> **Статус:** active · 2026-11-19 · владелец: продукт
> **Формат:** автономный ночной прогон, 12 часов, 6 вертикальных срезов.
> **Важно:** «6 задач размера H1» здесь — 6 срезов размера **подзадачи H1** (H1-01…H1-05), а не 6 полных эпиков H1. Полный H1 в 12 часов с качеством невозможен; если срезать проверку — получим ускоренную порчу модели.
> **Цель:** получить работающий job engine + family batch, а не набор несвязанных тулов.

## 0. Правила прогона

- Работай в `D:\Project\dotNET\AGK-SmartCon-Pro`, ветка `develop`.
- Вопросов не задавай; при неоднозначности — безопасный вариант (fail-closed), решение в HANDOFF.
- В T+11:00 прекрати код; последний час — гейты, HANDOFF, отчёт.
- После каждого среза: локальный коммит + `docs/agent/HANDOFF.md`.
- Не пушить, не PR, не запускать `build-and-deploy.bat`.
- Не трогать `opencode.json`, `server/`, секреты, docs вне `docs/agent`.
- Сборка: только затронутые проекты/конфигурации; 7 конфигураций — один раз в финале.
- Перед новым Revit API — 3–5 запросов Exa/MCP; не гадать.
- Любой срез закрывается только с артефактом: тест, лог, метрика. «Код написан» — не готово.

## 1. Читать сначала

1. `docs/agent/README.md`
2. `docs/agent/HANDOFF.md` (верхние блоки)
3. `docs/agent/00-strategy/autonomy-roadmap.md` — актуальный план
4. `docs/agent/00-strategy/task-family-batch-v1.md` — текущая задача (family batch)
5. `docs/invariants.md`, `docs/architecture/dependency-rule.md`
6. `docs/adr/076-tool-surface-and-code-mode.md`, `docs/adr/075-hybrid-tool-retrieval.md`
7. `src/SmartCon.Revit/Agent/Tools/Families/` — `AgentFamilySession`, `AgentFamilyUndo`, backup
8. `src/SmartCon.Agent/Services/AgentOrchestrator*`, `AgentAwaitableEvent`, `AgentToolExecutor`

Текущее состояние: 45 тулов, family-тулы (read/write/formula/backup/undo) есть; job engine нет; `execute_plan` ≤20 шагов и не атомарен; `report_tooling_feedback` удалён.

## 2. Срезы и DoD

### Срез 1 (T+0:00–0:45) — план и API-ресёрч

- Прочитать документы, проверить git log/status, HANDOFF.
- Exa/MCP: ExternalEvent/Idling, TransactionGroup, EditFamily/LoadFamily, Save/SaveAs, IFamilyLoadOptions, SharedParameterFile.
- Записать в HANDOFF: план среза, риски, что не входит.

DoD: HANDOFF обновлён, решения по API зафиксированы.

### Срез 2 (T+0:45–3:00) — A0: verification/idempotency contract

- Единый результат write: `expected/actual/verified/mismatch`; `done` только после read-back.
- Idempotency key: project `UniqueId`; family `familyId+typeName+(shared GUID|BuiltInParameter)`.
- Read-before-write: равно целевому → `skipped`, без транзакции.
- Per-item undo записывается до изменения; без undo write запрещён.
- Document identity + job/batch lock; typed errors (§2 task-family-batch-v1).
- Тесты: 20 items × 3 действия (значение, имя, формула); verification/rollback/repeat no-op 100 %.

DoD: unit+integration на контракт; 20 негативных кейсов; лог с `verified/mismatch`.

### Срез 3 (T+3:00–5:30) — Job core + SQLite v4

- Core: `JobSpec`, `JobState`, `IJobAction`, `IJobStore` (без Revit API).
- Agent: `SqliteAgentJobStore`, `JobRunner`, `JobReportBuilder`.
- SQLite v4 аддитивно: `jobs`, `job_batches`, `job_items`, `job_events`; `last_saved_cursor`, `last_saved_at`.
- Состояния: `preview_running → preview_ready → awaiting_approval → running → completed|completed_with_failures|failed|cancelling|cancelled|interrupted|rolled_back|rollback_partial`.
- Frozen preview: count+sample+selectorHash+documentId+expectedHash+expiry.
- Тесты: state machine, crash windows, resume, digest mismatch, document change.

DoD: job создаётся/читается/возобновляется на фейковом action без Revit; schema v4 миграция v3→v4 и self-heal.

### Срез 4 (T+5:30–8:00) — A1 project bulk + save

- `RevitJobActionBase` (Preview/Execute/Verify/Undo); `JobPumpEventHandler` — отдельный `IExternalEventHandler`.
- Срез 1–2 с на `Execute`, один срез в полёте, `IsPending`-guard, re-raise.
- Действия: `rename_views`, `set_parameters` (project-level).
- Save policy: `after_each_batch` default; cursor обновляется только после `Document.Save()`.
- Cancel между юнитами; kill -9 после Save → resume; до Save → переделка последнего среза.
- Тесты: 500 элементов, p95 Execute ≤2 с, cancel ≤2 с, failure injection в 7-м, doc change abort.

DoD: 500 элементов изменены пачками, отчёт truthfulness 100 %, `false-done=0`.

### Срез 5 (T+8:00–10:00) — A2 family batch on engine

- `map_family_parameters` как `IJobAction`: 1 семейство/юнит, preview per type, apply, read-back.
- Backup/undo через `AgentFamilySession`; `LoadFamily` только вне открытой транзакции target.
- Save после пачки; volatile при `savePolicy=never`/save failure.
- Тесты: 100 семейств, 1 ошибка в середине, rollback пачки; kill после Save; повторный apply no-op.

DoD: 100 семейств, per-family report, save-чекпоинт, resume без дублей.

### Срез 6 (T+10:00–11:00) — B trust + C1 selectors (минимум)

- `get_job_report` (JSON/CSV: diff, backup, verified/mismatch), `rollback_job`, `retry_failed_items`.
- False-done guard: отчёт сверяется с независимым read-back.
- `resolve_entities` по family/type/params/views/sheets (живые списки, без индекса).
- Если время поджимает: C1 урезать до family/type; B — только report+false-done.

DoD: 20 failure injections (rollback 100 %), false-done 0; C1: correct entity ≤2 вызова ≥90 %.

### Срез 7 (T+11:00–12:00) — гейты, HANDOFF, отчёт

- Unit 100 %; integration R25 + net48 + R27 (0 failed); eval replay 46/46 + job-кейсы; 7 конфигураций; Release 0 warnings.
- HANDOFF: что сделано, метрики, что не проверено, manual pending.
- Финальный отчёт (см. §5).

## 3. Жёсткие правила

| ID | Правило |
|---|---|
| J-01 | Один API-писатель: только job runner; агент ставит job и читает статус |
| J-02 | `Execute` 1–2 с; отмена только между юнитами; никаких `Thread.Sleep`/`.Result`/`.Wait()` |
| J-03 | Транзакция/TransactionGroup на срез, не на job; `Commit/Assimilate` проверять по статусу |
| J-04 | Durable state: intent/undo в SQLite до изменения; `last_saved_cursor` только после Save |
| J-05 | Read-back — часть `done`; без него success запрещён |
| J-06 | Явный scope: project instance / project type / family definition / `.rfa` |
| J-07 | `try/catch/log` вокруг `Execute`; preflight; watchdog `Pending` >30 с; policy рестарта |

Дополнительно:
- `TransactionGroup` — только на один атомарный срез; откат всей группы на job запрещён (теряется вся работа, нет resume).
- Family-документы всегда закрывать; при ошибке — без сохранения.
- `.rfa` — backup/staging; `.0001` не считать надёжным backup.
- Workshared: preflight; не писать молча; `Save` ≠ SynchronizeWithCentral.
- Никаких удалений параметров/семейств в этом прогоне.
- Никакого runtime C#.

## 4. Если отстаёшь

Приоритет: A0 → job core → A1 → A2. B/C1 можно урезать до report/false-done и family/type selectors. Нельзя урезать: A0, save-политику, read-back, crash-resume, гейты. Лучше 4 среза с проверкой, чем 6 без.

## 5. Финальный отчёт

1. Какие срезы закрыты, коммиты.
2. Метрики: truthfulness, per-item success, false-done, rollback/resume/idempotency, p95 Execute, LLM calls/job.
3. Что проверено в Revit (500 элементов, 100 семейств, kill/resume, save failure).
4. Что не успел/не проверено; manual pending.
5. Ссылка на HANDOFF и обновлённые документы.
6. Явно: где срезали scope и почему.