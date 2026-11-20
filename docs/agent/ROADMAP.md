# ROADMAP — AI-агент SmartCon

> **Статус:** active · 2026-11-20 · владелец: продукт
> **Единственный рабочий документ по AI-агенту.** Здесь: north star, принципы, приоритеты,
> карта эпиков и задач, правила нумерации и обновления. Конкретная работа и её статус —
> в GitHub Issues (метка `agent`).
> **Архив:** `archive/` — исторические планы, runbook'и, evidence и фидбэк. Не руководство
> к действию; читать только для истории. Внутренние ссылки архива исторические.

## 0. North star и принципы

**North star:** пользователь формулирует рутинную BIM-операцию обычным языком; агент сам
находит инструменты, строит план, безопасно применяет и отчитывается. Массовые операции —
часами, без человека, с предсказуемым откатом и проверяемым результатом.

**Границы:** BIM-ops по существующей модели (виды, листы, параметры, семейства, ФОП,
фильтры, worksets, связи, MEP, аннотации, аудит). Не создаём 3D-объекты «с нуля»;
не делаем runtime C# и per-method tools.

**Принципы:**

1. Надёжность важнее количества тулов: case success rate и unattended completion.
2. Цикл массовой работы принадлежит job engine, не LLM.
3. Каждая запись: preview → digest → approval → apply → read-back → отчёт об откате.
4. Revit API однопоточный: срезы 1–2 с, никаких фоновых вызовов.
5. Состояние job — в SQLite; durability = `Document.Save`.
6. Fail-closed: сомнение → не менять, вернуть typed error и следующий шаг.
7. Evidence-based: без теста/лога/метрики задача не закрыта.
8. Без постоянных rollout-флагов: продукт новый, новая логика единственная.

**Целевые метрики:** truthfulness 100 %, per-item ≥99.5 %, false-done 0,
resume/rollback/idempotency 100 %, routing top-5 ≥95 %, cache hit ≥50–80 %,
eval ≥75 задач. Полные метрики — в эпиках A/B/P/H4.

## 1. Иерархия источников правды

| Что | Где | Правило |
|---|---|---|
| Карта и контекст | **этот файл** | North star, приоритеты, ID ↔ issue # ↔ статус |
| Конкретная работа | GitHub Issue | SSOT: scope, критерии, статус, журнал |
| История и обоснования | `archive/` | Только исторический контекст, не руководство |
| Архитектурные решения | `docs/adr/` | Только необратимый выбор |
| Инварианты проекта | `docs/invariants.md` | Жёсткие правила I-01…I-17 |

## 2. Правила нумерации (стабильные ID)

- **Эпик:** `A`, `B`, `C`, `P`, `R`, `S`, `G`, `Q`, `H2`… ID выдаётся один раз и не переиспользуется.
- **Задача:** `<EPIC>.<N>` — `A.1`, `A.2`.
- **Вставка между задачами:** `<EPIC>.<N>.<M>` (`A.1.1`) или суффикс (`A.1a`).
  **Никогда не перенумеровывай существующие ID.** Порядок задаётся строкой таблицы, а не номером.
- **Удалённая задача:** статус `dropped`, ID остаётся занятым.
- **Поглощённая задача/эпик:** `absorbed by <ID>`.
- **GitHub issue number** — вторичный ключ; в коммитах `Refs #N` / `Closes #N`.
- Новая фича/баг без issue не начинается; найденный дефект сразу заводится issue.

## 3. Приоритеты

| Приоритет | Значение |
|---|---|
| `P0` | Блокер: без него нельзя доверять массовым операциям или релизу |
| `P1` | Следующий слой: реальные кейсы, надёжность, стоимость |
| `P2` | Расширение после P0/P1 |
| `P3` | По метрике или позже |

## 4. Сейчас (P0)

| ID | Задача | Issue | Почему сейчас |
|---|---|---|---|
| `A.8` | Tool surface job'а: eval-кейсы и приёмка | #313 | Остаток эпика A: eval + ручная приёмка |
| `G.5` | Листы: создание, размещение видов, номера | #347 | Без write-тулов листов альбом физически не собрать (прогон 2026-11-20) |
| `Q.1` | Ручная UI-приёмка и валидация лога | #361 | Единственный источник истины для UI |
| `Q.2` | Полный integration-гейт R25 + net48 + R27 | #362 | Финальный гейт ночного прогона |

**Сделано в ночном прогоне 4 (2026-11-20):** job engine целиком — контракт A0/A.1,
Core-модели A.2, SQLite v5 A.3, pump A.4, save/resume A.5, действия A.6/A.7,
tool surface A.8 (без eval-кейсов), отчёт/откат/retry/reconciliation B.1–B.3.
Evidence: unit 3914/3914, `AgentProjectJobTests` 17/17 (net8 и net10), `AgentFamilyJobTests` 2/2
(100 семейств), storage 7/7; два прохода валидатора `general` — все блокеры закрыты.

**Следующий шаг:** eval-кейсы job (нужна запись с ключом), ручная UI-приёмка Q.1,
полный гейт Q.2, деплой Q.3. **P.1–P.5 закрыты 2026-11-20; P.6–P.9 (P2) — отдельной сессией.**

## 5. Эпики

| ID | Эпик | Prio | Статус | Issue | Задач | Зависимости |
|---|---|---|---|---|---|---|
| **A** | Job engine: A0 → движок → project/family bulk | P0 | `in-progress` | #289 | 9 | Код и интеграционные тесты готовы (2026-11-20); осталось: eval-кейсы job, ручная приёмка, деплой, A.9 (variants). |
| **B** | Trust & recovery: отчёт, откат, resume, false-done guard | P0 | `in-progress` | #291 | 5 | B.1–B.3 реализованы и покрыты тестами (2026-11-20); B.4 (UI-панель) и B.5 (частичный откат) — backlog. |
| **P** | Prompt cache & context ordering: стабильный префикс | P0 | `in-progress` | #298 | 9 | P.1–P.5 закрыты 2026-11-20; остались P.6–P.9 (P2). |
| **Q** | Release readiness: ручная приёмка, полный гейт, деплой | P0 | `ready` | #299 | 4 | Задачи эпиков A–S, готовые к приёмке. |
| **C** | Targeted search: C1 resolve_entities → C2 search_catalog по гейту | P1 | `backlog` | #292 | 3 | Параллельно эпику A; для C2 нужен результат гейта C1. |
| **D** | Breadth packs: warnings, worksets, rooms, links | P1 | `backlog` | #293 | 4 | Эпик A — для write-части. |
| **R** | Element reading profile: определения → экземпляры | P1 | `backlog` | #295 | 7 | P0 definition-first закрыт; ADR-076/077; R.7 — стабильные значения enum. |
| **S** | Harness hardening: стабильные ключи, карантин, паки | P1 | `backlog` | #296 | 7 | H1/H1d закрыты; часть — параллельно A. |
| **G** | Missing operations: удаления, спецификации, листы, ФОП, семейства, MEP | P0 | `backlog` | #297 | 10 | S.5 для destructive; A.6 для массовых операций; G.5 поднят до P0 — без него не собрать альбом. |
| **A3** | External orchestrator для >1000 .rfa и часовых прогонов | P2 | `backlog` | #290 | 2 | Эпик A закрыт. Нужен стабильный job spec и SQLite v5. |
| **E** | Approvals & job recipes | P2 | `backlog` | #294 | 3 | B, D. |
| **H2** | Memory & knowledge: MEMORY.md, FTS5, skills + project memory в ES | P1 | `backlog` | #300 | 6 | H2.6 — первый шаг; ES-only отклонён, принят гибрид ES + SQLite/FTS. |
| **H3** | Policy engine и атомарные планы | P2 | `backlog` | #301 | 4 | B, E; H3.4 — commitOnMatch. |
| **H4** | Observability & eval v2 | P2 | `backlog` | #302 | 4 | P.1 (cache-лог) желателен. |
| **V** | Agent на Revit 2019–2022: совместимость параметров | P2 | `backlog` | #397 | 2 | Бета 2023+ принята; R23 отделён от R21. |
| **H5** | UX и расширяемость: steering, timeline, MCP | P3 | `backlog` | #303 | 4 | A–E стабильны. |
| **H6** | Vector retrieval — только по метрике | P3 | `backlog` | #304 | 2 | H1/H1b; каталог должен вырасти. |
| **H7** | Typed facade IRevitOps и sandbox | P3 | `backlog` | #305 | 2 | ADR-076; после A–E. |
| **W** | Visual feedback: тул снимка, агент сам решает когда смотреть | P3 | `backlog` | #413 | 0 | Задел на будущее по запросу владельца; scope в теле эпика, задачи заводятся при старте. |

## 6. Задачи по эпикам

### A — Job engine: A0 → движок → project/family bulk

Эпик: #289 · приоритет `P0` · статус `in-progress`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `A.1` | A0: контракт verification & idempotency | P0 | `done` | #306 |
| `A.2` | Job spec, state machine и Core-модели | P0 | `done` | #307 |
| `A.3` | SQLite v5: jobs, job_batches, job_items, job_events | P0 | `done` | #308 |
| `A.4` | Job pump: отдельный ExternalEvent, срезы 1–2 с | P0 | `done` | #309 |
| `A.5` | Save policy, durable cursor и crash resume | P0 | `done` | #310 |
| `A.6` | A1 project bulk: rename_views и set_parameters на движке | P0 | `done` | #311 |
| `A.7` | A2 family batch: map_family_parameters как IJobAction | P0 | `done` | #312 |
| `A.8` | Tool surface job'а и приёмка эпика | P0 | `in-progress` | #313 |
| `A.9` | Job variants: несколько групп значений за одну preview+apply | P1 | `backlog` | #414 |

### B — Trust & recovery: отчёт, откат, resume, false-done guard

Эпик: #291 · приоритет `P0` · статус `in-progress`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `B.1` | get_job_report: JSON/CSV с diff и verified/mismatch | P0 | `done` | #316 |
| `B.2` | rollback_job и retry_failed_items | P0 | `done` | #317 |
| `B.3` | False-done guard и crash reconciliation | P0 | `done` | #318 |
| `B.4` | UI job-панель и приёмка recovery | P1 | `backlog` | #319 |
| `B.5` | Частичный откат задания: подмножество юнитов и change set на батч | P1 | `backlog` | #412 |

### P — Prompt cache & context ordering: стабильный префикс

Эпик: #298 · приоритет `P0` · статус `ready`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `P.1` | Cache-лог и baseline | P0 | `done` | #353 |
| `P.2` | Turn-context отдельным сообщением | P0 | `done` | #354 |
| `P.3` | Статический system + session context | P0 | `done` | #355 |
| `P.4` | Заморозка tools на диалог | P0 | `done` | #356 |
| `P.5` | Session cache key и provider policy | P0 | `done` | #357 |
| `P.6` | Persist FrozenToolNames per conversation | P2 | `backlog` | #358 |
| `P.7` | Summary-запрос с тем же префиксом и cache-aware компакция | P2 | `backlog` | #359 |
| `P.8` | UI cache-метрики и ADR-078 | P2 | `backlog` | #360 |
| `P.9` | 1M context profile, overflow recovery и output budget | P2 | `backlog` | #383 |

### Q — Release readiness: ручная приёмка, полный гейт, деплой

Эпик: #299 · приоритет `P0` · статус `ready`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `Q.1` | Ручная UI-приёмка и валидация лога | P0 | `ready` | #361 |
| `Q.2` | Полный integration-гейт R25 + net48 + R27 | P0 | `ready` | #362 |
| `Q.3` | Деплой владельцем | P0 | `backlog` | #363 |
| `Q.4` | CI-блокер: NU1109 на любом PR (#282) | P1 | `backlog` | #364 |

### C — Targeted search: C1 resolve_entities → C2 search_catalog по гейту

Эпик: #292 · приоритет `P1` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `C.1` | C1: resolve_entities по живым спискам | P1 | `backlog` | #320 |
| `C.2` | Гейт C1 и решение по C2 | P1 | `backlog` | #321 |
| `C.3` | C2: search_catalog по каталогу сущностей (только по гейту C1) | P2 | `backlog` | #322 |

### D — Breadth packs: warnings, worksets, rooms, links

Эпик: #293 · приоритет `P1` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `D.1` | get_warnings: warnings модели с пагинацией | P1 | `backlog` | #323 |
| `D.2` | list_worksets: worksets и их состояние | P1 | `backlog` | #324 |
| `D.3` | Помещения и связи: read-паки rooms и links | P2 | `backlog` | #325 |
| `D.4` | Write для breadth-паков через job engine | P2 | `backlog` | #326 |

### R — Element reading profile: определения → экземпляры

Эпик: #295 · приоритет `P1` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `R.1` | list_types и фикс list_family_types.includePlaced | P1 | `backlog` | #330 |
| `R.2` | list_parameters: какие параметры есть и что можно менять | P1 | `backlog` | #331 |
| `R.3` | Essence + matchedParameter/matchedValue + parameterContainsAny | P1 | `backlog` | #332 |
| `R.4` | list_families в core-наборе | P1 | `backlog` | #333 |
| `R.5` | Человекочитаемая карточка, readOnlyReason, классификация параметров | P2 | `backlog` | #334 |
| `R.6` | Integration-покрытие, payload-бюджет, протокол чтения и eval | P2 | `backlog` | #335 |
| `R.7` | Стабильные значения enum-параметров (классификация систем) | P1 | `backlog` | #416 |

### S — Harness hardening: стабильные ключи, карантин, паки

Эпик: #296 · приоритет `P1` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `S.1` | Стабильный ключ семейства и плейсхолдеры шагов | P1 | `backlog` | #336 |
| `S.2` | Политика overwrite шаблона и прогресс больших батчей | P2 | `backlog` | #337 |
| `S.3` | Fidelity guard для формул | P2 | `backlog` | #338 |
| `S.4` | Судьба пустых паков: реализовать или удалить | P2 | `backlog` | #339 |
| `S.5` | Карантин/confirmNames для destructive-операций | P1 | `backlog` | #340 |
| `S.6` | Персистентность загруженных паков на диалог | P2 | `backlog` | #341 |
| `S.7` | copy_document/save_as_copy и регрессионный стресс-набор | P2 | `backlog` | #342 |

### G — Missing operations: удаления, спецификации, листы, ФОП, семейства, MEP

Эпик: #297 · приоритет `P0` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `G.1` | delete-инструменты: элементы, листы, ведомости | P1 | `backlog` | #343 |
| `G.2` | Запись спецификаций: поля, фильтры, сортировка, размещение | P1 | `backlog` | #344 |
| `G.3` | Проектные общие параметры: чтение и создание | P1 | `backlog` | #345 |
| `G.4` | Семейства: duplicate type, rename type, load, purge, replace | P1 | `backlog` | #346 |
| `G.5` | Листы: создание, размещение видов, номера | P0 | `backlog` | #347 |
| `G.6` | Виды: duplicate, create, delete, browser folder | P2 | `backlog` | #348 |
| `G.7` | MEP: системы трубопроводов и материалы | P2 | `backlog` | #349 |
| `G.8` | Browser organization: чтение и запись | P2 | `backlog` | #350 |
| `G.9` | Crop-from-template, select-наборы и preview зависимых шагов | P2 | `backlog` | #351 |
| `G.10` | Change sets list/undo_until, save_document, export, audit_model | P2 | `backlog` | #352 |

### A3 — External orchestrator для >1000 .rfa и часовых прогонов

Эпик: #290 · приоритет `P2` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `A3.1` | Spike: внешний orchestrator и RBP pattern | P2 | `backlog` | #314 |
| `A3.2` | Реализация orchestrator и приёмка на 1000 .rfa | P2 | `backlog` | #315 |

### E — Approvals & job recipes

Эпик: #294 · приоритет `P2` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `E.1` | Batch-approval: count + sample + scope + undo | P2 | `backlog` | #327 |
| `E.2` | Persisted grants по scope | P2 | `backlog` | #328 |
| `E.3` | Job-рецепты и eval | P2 | `backlog` | #329 |

### H2 — Memory & knowledge: MEMORY.md, FTS5, skills + project memory в ES

Эпик: #300 · приоритет `P1` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `H2.6` | Project memory в ES + SQLite/FTS-кэш | P1 | `ready` | #411 |
| `H2.1` | Memory v1: файлы и MEMORY.md | P2 | `backlog` | #365 |
| `H2.2` | FTS5-поиск по памяти | P2 | `backlog` | #366 |
| `H2.3` | Skills v1: 10–20 навыков и routing | P2 | `backlog` | #367 |
| `H2.4` | Memory tools, inbox и консолидация | P2 | `backlog` | #384 |
| `H2.5` | Session lifecycle, rules file и handoff-триггеры | P2 | `backlog` | #385 |

### H3 — Policy engine и атомарные планы

Эпик: #301 · приоритет `P2` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `H3.1` | Policy engine deny-first и grants | P2 | `backlog` | #368 |
| `H3.2` | Атомарный план и verify-on-stop | P2 | `backlog` | #369 |
| `H3.3` | L2 critic (только по метрике) | P3 | `backlog` | #370 |
| `H3.4` | execute_plan: commitOnMatch в AutoApproveSession | P1 | `backlog` | #415 |

### H4 — Observability & eval v2

Эпик: #302 · приоритет `P2` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `H4.1` | Usage ledger и cost caps | P2 | `backlog` | #371 |
| `H4.2` | Eval v2: 200 routing-фраз и snapshots | P2 | `backlog` | #372 |
| `H4.3` | Dashboard/export метрик | P3 | `backlog` | #373 |
| `H4.4` | LLM reliability: fake SSE, retry matrix, stream errors, request log | P2 | `backlog` | #386 |

### H5 — UX и расширяемость: steering, timeline, MCP

Эпик: #303 · приоритет `P3` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `H5.1` | Steering и timeline/rewind | P3 | `backlog` | #374 |
| `H5.2` | MCP-сервер (backlog) | P3 | `backlog` | #375 |
| `H5.3` | Web search через Exa REST с privacy guard | P3 | `backlog` | #387 |
| `H5.4` | Сессии по документу: dropdown и миграция при Save As | P3 | `backlog` | #388 |

### H6 — Vector retrieval — только по метрике

Эпик: #304 · приоритет `P3` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `H6.1` | Гейт и гибридный retrieval BM25 ∪ vector | P3 | `backlog` | #376 |
| `H6.2` | Decision-reranker и настройки embedding-модели | P3 | `backlog` | #377 |

### H7 — Typed facade IRevitOps и sandbox

Эпик: #305 · приоритет `P3` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `H7.1` | Typed facade IRevitOps | P3 | `backlog` | #378 |
| `H7.2` | Sandbox и промоция скриптов | P3 | `backlog` | #379 |

### W — Visual feedback: тул снимка, агент сам решает когда смотреть

Эпик: #413 · приоритет `P3` · статус `backlog`

Scope и проверенные факты API — в теле эпика. Задачи заводятся при старте; план:
`W.1` `capture_view_image` + мост «tool result → вложение» → `W.2` change feed
(`DocumentChanged`) → `W.4` отображение снимка в панели; `W.3` UI-снимок окна
(`PrintWindow`/`PW_RENDERFULLCONTENT`) — только после проверки на живом Revit.
Решение владельца: отдельный тул, агент сам решает, когда смотреть на модель;
автоматических снимков каждого хода нет. Пайплайн изображений уже работает
(`AgentAttachment` → `image_url`), не хватает тула и моста к tool-результату.

### V — Agent на Revit 2019–2022: совместимость параметров

Эпик: #397 · приоритет `P2` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `V.1` | Агент на Revit 2021–2022 (R21) | P2 | `backlog` | #398 |
| `V.2` | Агент на Revit 2019–2020 (R19) | P3 | `backlog` | #399 |

## 7. Закрытые эпики (историческая запись)

| ID | Эпик | Issue | Итог |
|---|---|---|---|
| **H1** | Tool catalog v2, WorkingState, store-and-reference, compaction v2 (закрыт) | #380 | Каталог v2 (27 карточек, 20 паков, core 9), WorkingState, store-and-reference, compaction v2; единое поведение без флагов. |
| **H1c** | View configuration: фильтры, overrides, visibility, crop (закрыт) | #381 | Фильтры/overrides/visibility/crop/шаблоны: 13+ тулов, undo фильтров; integration и eval зелёные. |
| **H1d** | Family editing pack: 9 тулов, EditFamily без UI, undo (закрыт) | #382 | 9 family-тулов, EditFamily без UI, shared/формулы/lookup, map 5000 значений, backup и точный undo. |

### Закрытые дефекты

| ID | Дефект | Issue | Итог |
|---|---|---|---|
| **AGT-KEYS-1** | API-ключ из настроек агента не сохранялся на чистой машине (пустой `secrets.env`) | #409 | Root cause: `PasswordBoxBehavior` подписывался на `PasswordChanged` только в callback'е `BoundPassword`, а WPF не вызывает его, когда значение равно metadata default (`""`) → ввод в поле ключа молча терялся. Фикс: `BindPassword` + `SetCurrentValue`, честная валидация сохранения, per-provider черновики ключей. Unit 3973/3973; ручная приёмка Revit 2025 (лог: `hasChatKey=True`, `LLM turn completed`). |

## 8. Поглощённые и отложенные ID

- `H8` (bulk/cyclic job engine) — **absorbed by A**.
- `H1b` (полный поиск по 200k–1M элементов) — **absorbed by C1/C2**; индекс элементов запрещён ADR-077.
- `H1`/`H1c`/`H1d` — закрыты, см. §7.
- `F` (H6 vector / H7 code mode) — не отдельный эпик: см. `H6` и `H7`.

## 9. Как добавить или изменить задачу

1. **Найди эпик** в §5. Если работа не входит ни в один — создай новый эпик (следующая свободная буква/номер).
2. **Создай issue** по шаблону из `AGENTS.md` (метки: `agent`, `task`, приоритет).
3. **Свяжи sub-issue**:
   - `gh >= 2.94`: `gh issue create ... --parent <EPIC>`;
   - `gh 2.83`: `POST /repos/{owner}/{repo}/issues/{parent}/sub_issues` с `sub_issue_id`.
4. **Добавь строку** в таблицу эпика в §6 и обнови счётчик/статус в §5.
5. **Вставка между задачами:** выдай `A.<N>.<M>` или `A.<N>a`; не перенумеровывай соседей.
6. **Изменение scope** — полная замена тела issue + строка журнала; при смене приоритета обнови §5 и §6.
7. **Закрытие** — `gh issue close N`, статус `done` в таблице; эпик закрывается, когда все задачи `done`/`dropped`.

## 10. Легенда статусов

| Статус | Значение |
|---|---|
| `backlog` | Заведена, не начата |
| `ready` | Готова к работе, зависимости закрыты |
| `in-progress` | В работе (одна задача = один исполнитель) |
| `blocked` | Заблокирована зависимостью/вопросом; причина в теле |
| `done` | Закрыта, критерии приёмки выполнены |
| `dropped` | Отменена, ID не переиспользуется |
| `absorbed` | Поглощена другой задачей/эпиком |
