> **ARCHIVED (2026-11-19).** Исторический документ. Не руководство к действию.
> Актуально: [`ROADMAP.md`](../../ROADMAP.md) + GitHub Issues (метка `agent`). Внутренние ссылки исторические.

# План развития харнеса: автономные длинные операции

> **Статус:** active · 2026-11-19 · владелец: продукт
> **Роль:** очередь исполнения — что делать первым. **Не SSOT статусов:** статусы и конкретные
> задачи живут в [`../ROADMAP.md`](../../ROADMAP.md) и GitHub Issues (метка `agent`).
> **Заменяет:** `h1b-tool-packs.md` (H1b–H1d) и `initial-improvement-runbook.md` (H1).
> **Не заменяет:** `harness-ideal-plan.md` — это долгосрочная архитектура и паттерны; здесь — порядок исполнения.
> **North star:** агент надёжно выполняет массовые операции в модели часами, без участия человека, с предсказуемым откатом и проверяемым результатом.

## 1. Где мы сейчас

- 45 тулов, 20 паков: виды, шаблоны, параметры, листы, ведомости, фильтры, семейства, meta.
- Каталог v2: карточки, BM25 top-5, core 8, `load_tool_pack`; decision-модель — опциональный reranker.
- Контекст: WorkingState, store-and-reference, compaction v2 (mask → checkpoint → shrink).
- Семейства: чтение/запись параметров и формул, backup/snapshot, undo.
- Eval: 46 задач replay, routing top-1 95.9 %, top-5 100 %, p95 1.7 мс.

Чего нет:

- пакетного/фонового job engine — агент вынужден делать цикл сам и блокировать Revit;
- поиска по каталогу сущностей (семейства/типы/параметры/виды/листы);
- памяти, скиллов, policy engine;
- vector/code mode (осознанно отложены).

Главный разрыв: **тулы есть, но массовая работа не автоматизирована**. 500 семейств = 500 вызовов агента, риск зависания и частичных изменений.

## 2. Принципы

1. **Надёжность важнее количества тулов.** Новая фича принимается по case success rate и unattended completion, не по числу карточек.
2. **Цикл принадлежит движку, не LLM.** Агент создаёт job и опрашивает статус; модель не крутит `while`.
3. **Каждая запись:** preview → digest → approval → apply → read-back → отчёт об откате.
4. **Revit API однопоточный.** `ExternalEvent.Execute` блокирует UI на всё время вызова; отзывчивость достигается только дроблением на юниты по 1–2 секунды, а не «фоновым» выполнением.
5. **Состояние job — в SQLite**, не в памяти: рестарт Revit не теряет прогресс.
6. **Fail-closed:** сомнение → не менять, вернуть типизированную ошибку и следующий шаг.
7. **Evidence-based гейты:** без теста/лога/метрики фаза не закрыта.

## 3. Что меняем в старом плане

| Было | Стало | Причина |
|---|---|---|
| H1 → H1b поиск → H1c → H1d → H8 | H1c/H1d закрыты → **A0 контракт проверки** → **A job engine** → B trust/recovery → C1 selectors → D breadth → C2 search → E approvals/recipes | Поиск не снимает bottleneck исполнения; batch без проверки = ускоритель порчи модели |
| H1b: живой поиск по 200k–1M элементов, one-shot cache | C1: `resolve_entities` по живым спискам (без индекса), затем C2 `search_catalog` по 5–20k сущностям только по гейту | Индекс элементов устаревает; для выбора id достаточно каталога |
| H8 «после H1d», без дизайна | A: job spec + **curated IJobAction** + SQLite v4 + time-sliced pump + resume | Нельзя переиспользовать тулы «как есть»: 500 change sets, 30-секундные таймауты, двойной dry-run |
| batchSize 20 / 120 с на батч | срез **1–2 с** на `ExternalEvent.Execute`, один срез в полёте, повторный `Raise()` | Код в Execute блокирует UI; 2 минуты — зависание |
| Метрика — число тулов/паков; «95 % job» | truthfulness 100 %, per-item success ≥99.5 %, job terminal without human, false-done 0, rollback/resume/idempotency 100 % | Tool count не отражает полезность; 95 % на 500 items = 99.99 % per item |
| Атомарность плана — «когда-нибудь» | Явная rollback matrix: change set на job/батч, `TransactionGroup` только на срез, family snapshot/backup+undo | Нельзя обещать атомарность там, где Revit её не даёт |
| B (надёжность) после A | **A0 входит в A как предусловие**; B остаётся recovery/UX | Непроверенная запись × N элементов — недопустимый риск |
| Resume «с последнего применённого элемента» | Resume **с последнего сохранённого** (`Document.Save`); иначе последний батч переделывается | В Revit изменения живут в памяти: без Save закрытие/краш теряет всё, а SQLite будет врать «done» |

## 4. Новый порядок эпиков

Старый порядок `H1 → H1b поиск → H1c → H1d → H8` заменён. H1c/H1d уже закрыты; поиск не снимает главный bottleneck.

| # | Эпик | Зачем | Зависимости | Оценка |
|---|---|---|---|---|
| **A0** | **Verification & idempotency contract** | `expected/actual/verified`, per-item undo, idempotency key, typed errors, document identity, job lock | H1/H1c/H1d | 3–5 дн |
| **A** | **Job engine v1** | Curated `IJobAction`, time-sliced pump 1–2 с, 500 project-элементов; family — второй срез (до ~100 за сессию) | A0 | 2–2.5 нед |
| **A3** | **External orchestrator** | >1000 `.rfa` / часовые unattended-прогоны: отдельная сессия Revit, таймаут на файл, рестарт, checkpoint (RBP pattern) | A | 1–2 нед |
| **B** | **Trust & recovery** | `rollback_job`, `retry_failed_items`, audit export, false-done guard, UI job-панель | A | 1–1.5 нед |
| **C1** | **Selector helpers** | `resolve_entities` по живым спискам (без индекса): семейства/типы/виды/листы → frozen ids | параллельно A | 3–5 дн |
| **D** | **Breadth packs** | Помещения, worksets, warnings, связи; write только через job | A+B | 2 нед |
| **C2** | **Entity search** | `search_catalog` по 5–20k сущностям — только если C1 не даёт ≤2 вызовов/90 % | C1, метрика | 3–5 дн |
| **E** | **Approvals & job recipes** | Batch-approval (count+sample+scope+undo), policy grants, готовые job-рецепты | B+D | 1–1.5 нед |
| **F** | **H6 vector / H7 code mode** | Только по метрике: каталог >200 или top-5 <95 %; sandbox после typed facade | E | позже |

Память/skills 100+ — не отдельный ранний эпик: selective memory помогает на трудных задачах и вредит на лёгких; вернуться после A–E по метрике.

Почему поиск (C) не первый:

- 45 тулов навигируются: top-1 95.9 %, top-5 100 %;
- поиск не убирает 20-минутный цикл и частичные изменения;
- поиск по элементам (H1b в старом виде) не нужен: живой модели с индексом нельзя доверять;
- достаточно `search_catalog` по сущностям, когда появится массовый сценарий.

## 5. Эпик A — Job engine v1

### 5.0 A0 — Verification & idempotency contract (предусловие)

Без этого контракта batch-движок только ускоряет порчу модели.

- Каждый write возвращает `expected`/`actual`/`verified`/`mismatch`; `done` ставится только после read-back, а не после `Commit()`.
- **Idempotency key** на юнит: project — `UniqueId`; family type — `familyName + typeName + (shared GUID | BuiltInParameter)`; `.rfa` — `path + type + parameter`.
- **Read-before-write:** если значение уже равно целевому — `skipped`, без транзакции/открытия семейства.
- **Per-item undo записывается до изменения модели**: обратная операция/старое значение в SQLite; без неё write запрещён.
- **Document identity + job lock:** один активный job на документ; смена документа/worksharing → abort, не продолжать вслепую.
- **Typed errors** с `[Action: ...]`; неизвестная ошибка — stop, не auto-dismiss.
- Гейт A0: 20 items × 3 действия (значение, имя, формула) — verification 100 %, rollback 100 %, повторный запуск no-op 100 %.

### 5.1 Job spec

```json
{
  "schemaVersion": 1,
  "documentId": "doc-...",
  "action": {"kind": "map_family_parameters", "args": {"source": "...", "target": "..."}},
  "selector": {"kind": "family_ids", "ids": [123, 124], "count": 2, "hash": "sha256..."},
  "mode": "preview",
  "limits": {"sliceMs": 1500, "maxItemsPerSlice": 50, "maxWallClockMs": 86400000, "maxFailures": 20},
  "onError": "stop",
  "rollback": "on_error",
  "savePolicy": {"mode": "after_each_batch", "intervalMinutes": 10},
  "preview": {"digest": "sha256...", "expiresAt": "..."}
}
```

- `savePolicy`: `after_each_batch` (default) | `every_n_minutes` | `never`. Для `never` job помечается `volatile`: прогресс не гарантирован, закрытие/краш теряет всё.

- `selector` — frozen ids + count + hash; никаких «все элементы модели».
- `mode`: `preview` | `apply`; apply принимает только preview с тем же digest, documentId и не истёкшим expiry.
- `limits.sliceMs` 1000–2000; `maxItemsPerSlice` — по замеру `Commit()` (не «20 семейств = 120 с»).

### 5.2 Состояния

`preview_running → preview_ready → awaiting_approval → running → completed | completed_with_failures | failed | cancelling | cancelled | interrupted | rolled_back | rollback_partial`

`partially_applied` — не состояние, а исход + item-статусы. Переходы персистятся; после рестарта Revit job продолжается с последнего незавершённого юнита.

### 5.3 Хранение (agent.db v4)

- `jobs(id, document_id, spec_json, state, created_at, started_at, finished_at, cursor, last_saved_cursor, last_saved_at, totals_json, preview_digest, error)`
- `job_batches(job_id, batch_index, state, started_at, finished_at, saved_at, counts_json)`
- `job_items(job_id, batch_index, item_id, status, attempts, error_code, before_json, expected_json, after_json, undo_json, verified)`
- `job_events(job_id, seq, kind, payload_json, created_at)` — append-only лог для аудита и resume.

Схема — аддитивная миграция v3→v4; старые БД не ломаются.

### 5.4 Модель исполнения Revit

- **In-process job runner на отдельном `ExternalEvent`** — один статический handler, очередь юнитов, `IsPending`-guard, `Raise()` только если работа осталась. Не использовать общий `AgentAwaitableEvent.ProcessQueue`: он дренирует всю очередь в одном колбэке.
- **Curated `IJobAction`, а не вызов произвольных тулов.** Action выполняется внутри уже открытого pump-колбэка (без `RaiseAsync`), сам пишет per-item change items и undo. Change set — на job/батч, а не на каждый элемент: иначе 500 change sets/undo-записей и таймауты по 30 с.
- **Time-slice на `Execute` 1–2 с.** После каждого юнита проверяется время; бюджет исчерпан — выйти и снова `Raise()`. Никаких `Thread.Sleep`, `.Result`, `.Wait()`.
- **Транзакция/`TransactionGroup` на срез** (один атомарный юнит); `Commit`/`Assimilate` проверяется по статусу. `TransactionGroup` не на весь job.
- **Durability:** после батча — `Document.Save()` (по `savePolicy`), и только после успешного Save обновляется `last_saved_cursor`. `cursor` без Save — «сделано в памяти», не прогресс.
- **Crash-окна:** intent (`expected`/undo) пишется в SQLite до изменения модели; после `Assimilate` и до записи статуса resume делает read-back по `expected_json` — совпало → `applied`, иначе повтор. После Save — durable; до Save — батч переделывается.
- **Family-юниты:** 1 семейство = `EditFamily` → правка копии → read-back → `LoadFamily`/save. До `LoadFamily` откат — `Close(false)`; после — это изменение проекта. Закрывать каждый family document. Перезапуск Revit — **только если замеры показывают деградацию** (для пачки 50 открыть-изменить-загрузить-закрыть обычно не требуется; для тысяч в одной сессии — да).
- **Исключения внутри `Execute` проглатываются Revit** — обязательный `try/catch/log`.
- **`ExternalEvent` может висеть `Pending`** без фокуса/при модальном состоянии; watchdog: >5 с warn, >30 с stalled.
- **Для >1000 `.rfa`/часовых прогонов** in-process недостаточен: внешний orchestrator (Revit Batch Processor pattern) или APS Design Automation — эпик A3.

### 5.5 Preview / apply / откат

- **Frozen preview:** `count + sample + selectorHash + documentId + expectedHash + expiresAt`. Apply принимает только этот envelope; иначе `job_changed`/`preview_expired`.
- `preview` проходит все юниты без сохранения: project — транзакция + read-back + `RollBack`; family — копия + read-back + `Close(false)`. Возвращает per-item `old → new`.
- Project: change set на юнит/пачку; при `rollbackOnError` — откат change sets пачки; `TransactionGroup` только для малых атомарных пачек.
- Family: `TransactionGroup` не откатывает; snapshot/backup + `AgentFamilyUndo`. `LoadFamily` только вне открытой транзакции target; `overwriteParameterValues` осознанно (перезаписывает type-значения).
- `.rfa`: backup до сохранения, staging/временная копия, атомарная замена; `.0001` — не единственный механизм восстановления; `SaveAsOptions.MaximumBackups` минимум 1.
- Structural family changes (add/delete parameter) откатываются только при backup/snapshot; иначе `rollback_unavailable` до apply.

### 5.6 Обязательные правила job engine

| ID | Правило |
|---|---|
| J-01 | Один API-писатель: все изменения — через один job runner; агент только ставит job и читает статус |
| J-02 | Time-box на `Execute` 1–2 с; отмена только между юнитами |
| J-03 | Транзакция на юнит/малую пачку; `TransactionGroup` не на весь job |
| J-04 | Durable state + идемпотентность: stable key, attempts, before/after, lease |
| J-05 | Read-back — часть приёмки: `done` только после совпадения expected/actual |
| J-06 | Явный scope: project instance / project type / family definition / `.rfa` file |
| J-07 | Failure/cancel/watchdog/restart: `try/catch/log`, preflight, таймауты, политика рестарта |

### 5.7 Приёмка A

- 500 project-элементов (значения/имена), батчи по 100–300, UI tick p95 ≤200 мс, p95 `Execute` ≤2 с.
- 100 семейств за сессию: пачки 10–20, каждый family doc закрыт; перезапуск Revit — только если замеры памяти показывают деградацию.
- Отмена на середине: terminal ≤2 с, применённые юниты откатаны при `rollbackOnError=true`.
- kill -9 **после Save**: `resume` с `last_saved_cursor`, без дублей; потеря ≤1 батча.
- kill -9 **до Save**: последний батч переделывается, `false-done` 0.
- Save запрещён/не удался (read-only, workshared, отказ): job `volatile`, явное предупреждение, прогресс не гарантируется.
- Повторный apply: no-op 100 %; смена документа в процессе — abort.
- Failure injection: ошибка в 7-м элементе → честный отчёт + rollback/continue.
- Workshared-документ: preflight-отказ или отдельный сценарий, не молчаливая запись.
- Truthfulness отчёта: 100 % (отчёт = независимый read-back), false-done 0.
- LLM-вызовов на job: ≤3 (`start_job` + `run_job` long-poll + финал).

## 6. Эпик B — Trust & recovery

- `rollback_job` (по per-item undo), `retry_failed_items`, `get_job_report` (JSON/CSV с diff, backup-путями, verified/mismatch).
- False-done guard: отчёт сверяется с независимым read-back; расхождение → `mismatch`, не success.
- Crash reconciliation: `interrupted` jobs восстанавливаются по `job_events` и read-back, без дублей.
- UI: панель job-статуса, прогресс, отмена, «показать изменения»; approvals на batch (count+sample+scope+undo).
- Приёмка: 20 failure-injection, 100 % rollback, 0 false-done, 0 потерянных backup.

## 7. C1/C2 — выбор сущностей и поиск

**C1 `resolve_entities`** (3–5 дней, параллельно A): по живым спискам (семейства/типы/параметры/виды/листы) находит id для job без индекса; синонимы/стемминг/алиасы категорий. Приёмка: correct entity ≤2 tool calls ≥90 %, false <5 %. Этого может хватить — тогда C2 не нужен.

**C2 `search_catalog`** (только по гейту C1): families, types, parameters, views, sheets; 5–20k сущностей, живой проход + BM25, без индекса элементов. Decision-модель — только при неоднозначном BM25, не default. Приёмка: top-5 ≥95 % на 100 фразах, p95 <100 мс.

## 8. Эпик D — Breadth packs

- Read: rooms, worksets, warnings, links, sheets/schedules (расширение).
- Write: только через job engine для массовых; одиночные — обычные тулы с dry-run.
- Каждый пакет: карточки, схемы, eval-кейсы, интеграционные тесты на сидированной модели.

## 9. Эпик E — Approvals & job recipes

- Batch-approval: count + sample + scope + undo; frozen ids; короткий expiry.
- Persisted grants по scope (не по тулу): «одобрить 200 переименований в этом фильтре».
- Готовые job-рецепты (шаблоны job spec) для частых кейсов владельца.
- Память/skills 100+ — после A–E по метрике: selective memory помогает на трудных задачах и вредит на лёгких.

## 10. Метрики плана

| Метрика | Цель |
|---|---|
| Case success rate (20–30 реальных задач) | ≥90 % |
| Job truthfulness (отчёт = независимый read-back) | 100 % |
| Per-item success (seeded, детерминированные действия) | ≥99.5 % (95 % CI) |
| Job terminal without human | ≥95 % |
| Zero-intervention (сверх batch-approval) | ≥80 % job'ов |
| Rollback correctness (20 injections) | 100 % |
| Resume correctness (10 kill -9) | 100 % с последнего Save |
| Потеря работы при kill -9 | ≤1 батч |
| Idempotency (повторный apply → no-op) | 100 % |
| False-done / scope violations | 0 / 0 |
| p95 `ExternalEvent.Execute` / UI tick | ≤2 с / ≤200 мс |
| Cancel → terminal | p95 <2 с |
| Память Revit за job | baseline +1.5 ГБ → плановый рестарт |
| LLM calls per job | ≤3 |
| pass^3 (10 job'ов × 3 прогона) | ≥90 % |
| Время на 100 / 1000 элементов | зафиксировать baseline и улучшать |
| Routing top-1 / top-5 / p95 | ≥80 % / ≥95 % / <100 мс |
| Eval replay | 46/46 + job-кейсы (seeded + failure injection), 0 failed |

## 11. Что явно отложено

- Полный поиск/индекс по 200k–1M элементов — не делать.
- Vector/embeddings — только если каталог >200 видимых тулов или recall <95 %.
- Runtime C#/code mode — после типизированного фасада `IRevitOps`.
- 1M контекст и skills 100+ — после стабилизации A–D.
- Мультипользовательский/облачный job runner — вне текущего плана.

## 12. Ближайшие шаги

1. A0: контракт verification/idempotency/per-item undo/document identity/job lock (3–5 дней, до движка).
2. Дизайн job engine v1: схема v4, unit model, `IJobAction`, frozen preview, time-sliced pump, rollback matrix (1 день).
3. A1: project-модель — `rename_views`/`set_parameters`, срезы 1–2 с, read-back, отмена, resume, watchdog.
4. A2: family — `map_family_parameters`, 1 семейство/юнит, пачки 10–20, backup/undo, перезапуск каждые ~100.
5. A3 (отдельно): external orchestrator для >1000 `.rfa`.
6. C1 `resolve_entities` параллельно A; C2 — только по гейту.
7. Расширить eval и зафиксировать метрики job (truthfulness, p95 Execute, память, resume, pass^3).

## 13. Основание и валидация

План сверен с двумя независимыми исследованиями (Exa + официальные docs, 2026-11-19):

- **Long-running harnesses:** Anthropic «Effective harnesses for long-running agents» и «Harness design» — прогресс-артефакты, инкрементальность, независимая проверка; premature completion — главный failure mode. Отсюда A0 (truthfulness, read-back) и job event log.
- **Tool search:** Anthropic tool-search docs и бенчмарки BM25 vs embeddings — после 30–50 тулов выбор деградирует; BM25 близок к embedding на 1–4k тулов. Отсюда C1/C2 split и запрет vector до метрики.
- **Durable execution:** Temporal/LangGraph, MCP Tasks (`working/input_required/completed/cancelled`, `pollIntervalMs`), OpenAI Agents HITL (`needs_approval`, durable RunState) — job как durable handle, batch-approval, resume после рестарта.
- **Job design:** background jobs (job identity vs attempt, at-least-once, idempotency key, DLQ), SQLite queue (`BEGIN IMMEDIATE`, lease/reaper).
- **Revit API:** Autodesk docs по ExternalEvent/Idling (Execute блокирует UI, `Pending`), TransactionGroup (rollback только пока группа открыта), EditFamily/LoadFamily (копия до LoadFamily), SaveAs backup `.0001`; практика RBP/pyRevit — внешний orchestrator для тысяч файлов, перезапуск каждые ~100 семейств.
- **Наши данные:** `h1d-evidence.md` (7.7 мс/значение, family TransactionGroup — stretch), `eval.md` (routing 95.9/100 %, 46 replay), HANDOFF (45 тулов).

Ключевые поправки, внесённые после валидации:

1. A0 (verification/idempotency/undo/lock) — **до** job engine, не после.
2. Не вызывать существующие тулы на каждый элемент: curated `IJobAction`, change set на job/батч.
3. Time-sliced pump 1–2 с вместо batchSize 20 / 120 с; отдельный `IExternalEventHandler`, не общий `ProcessQueue`.
4. Frozen preview envelope (count+sample+selectorHash+documentId+expectedHash+expiry).
5. Метрики: truthfulness, per-item success, false-done, pass^3, LLM calls ≤3 — вместо «95 % job».
6. **Durability = `Document.Save`**, а не «тул отработал»: cursor обновляется только после Save; resume — с последнего сохранённого, иначе последний батч переделывается. Для `savePolicy=never` job `volatile`.
6. C split: C1 selectors сейчас, C2 search только по гейту; vector последним.
7. E сужен до approvals + job recipes; память/skills — после A–E по метрике (selective memory помогает на трудных и вредит на лёгких).