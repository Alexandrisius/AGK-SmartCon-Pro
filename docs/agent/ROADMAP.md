# ROADMAP — AI-агент SmartCon

> **Статус:** active · 2026-11-24 · владелец: продукт
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

**Обновлено 2026-11-24.** До «хорошего рабочего харнесса» осталось семь пунктов; статусы — в issues.

| № | Что | Issue | Состояние |
|---|---|---|---|
| 1 | Ручная приёмка владельца на `develop` (деплой `build-and-deploy.bat`); после неё закрыть A.5, A.7, A.8, B.2 и RUN v1 | #361, #363; #310, #312, #313, #317, #445 | идёт с 2026-11-24 |
| 2 | Главный прогон 10 кейсов на финальном коде (прод-цикл, glm-5.3, реальный шаблон) — проверка всех правок ночного прогона №2 | #478 | ждёт свободной машины |
| 3 | Полный integration-гейт агента: 65 IT-классов (на финальном дереве прогнано 25), затем весь R25 и net48 | #362 | после ручного теста |
| 4 | Повторяемость: повторы прогонов pass^k и калибровка судьи | #483 | не начато |
| 5 | Слабые кейсы 2 (FAIL, лимит времени), 5 и 6 (PARTIAL) — после фиксов; решение владельца по критериям кейса 2 | #478, #480, #473 | в главном прогоне |
| 6 | Откат и данные: `rollback_job` поверх ручной правки, слепой `undo_last`, change sets тремя транзакциями, изоляция повреждённой `agent.db` | #427, #474, #425, #426 | не начато |
| 7 | Revit на больших объёмах: O(n²) пересчёт итогов и 45-с undo-слайс; `rowCount` со строками группировки | #423, #424 | не начато |

Пункты 6–7 имеют метку P2, но входят в обязательный список: это принципы «предсказуемый откат» и «fail-closed» из §0.
Задачи с меткой `needs-owner-acceptance` в §6 показаны как `in-progress` до приёмки владельца.
Реестр находок ночного прогона №2 (144 ID) — `archive/00-strategy/archive/real-cases-v2-findings-2026-11-23.md`.

**История (сохранена без изменений):**

**Сделано в ночном прогоне 4 (2026-11-20):** job engine целиком — контракт A0/A.1,
Core-модели A.2, SQLite v5 A.3, pump A.4, save/resume A.5, действия A.6/A.7,
tool surface A.8 (без eval-кейсов), отчёт/откат/retry/reconciliation B.1–B.3.
Evidence: unit 3914/3914, `AgentProjectJobTests` 17/17 (net8 и net10), `AgentFamilyJobTests` 2/2
(100 семейств), storage 7/7; два прохода валидатора `general` — все блокеры закрыты.

**Ориентир сроков (владелец, 2026-11-21):** эпик G целиком (17 задач, каталог
63→107 тулов, dogfood ×2, sweep+полные IT) сделан одним агентом за **12 часов**.
Эпик сопоставимого размера ≈ полдня–день, задача ≈ 1–2 часа.

**Задача 2 закрыта (2026-11-21, та же сессия после суммаризации):** массовая
миграция семейств одним пайплайном — A.11+A.12 (ad0d066d) → A.10 (74676e0c,
replace_family_parameter action + фикс латентного бага backup) → B.6 (f71c59e3,
CompletedUnverified + SQLite v6). Sweep 6/6; dogfood живым LLM (glm-5.3-flash)
ЗЕЛЁНЫЙ с первого раза: модель сама выбрала start_job batch-first
(preview→digest→apply, 6 раундов, 0 поштучных вызовов), оценка 4/5 → фидбэк
issues #446–#449. Каталог 107 тулов (+1 job action), real-queries 326.

**Задача 3 выполнена (2026-11-21, needs-owner-acceptance):** real-cases
benchmark — 2 волны живых прогонов всех 5 кейсов (glm-5.3-flash), цикл
«прогон→фидбэк→issue→фикс→повтор»: 15 issues, 12 закрыто фазами F1–F4
(коммиты b56c5d9e, dfe5ac28, 9e02c96f, 2e359b7e; каталог 107→108 тулов,
SQLite v6+v7, скилл-бюджет re-based). Итог волны 2: кейс 4 PASS 1.0, кейс 2
батч-чистка (2 вызова вместо 26), кейс 5 feedback+scope-обход, кейс 3 —
повторяющийся паттерн (→ #S.30 message-WARNING), кейс 1 ждёт ручной шаблон
организации браузера от владельца (инструкция в #445). Полная сводка — #445.

**Ночная сессия 2026-11-22 (real-cases v2, #466):** все 10 тул-блоков 10 кейсов
реализованы за одну ночь (каталог 108→129 тулов, паки 21→24: +semantic, +relations,
+documents; real-queries 328→377): пакетные планы/3D-виды, пересборка ведомостей
(clone_schedule/rotation/стиль), джоба systemName+includeSubComponents, semantic_match
(топ-K малой моделью + компактный режим classifierPath), связи модели (коннекторы/цепи/
разрывы/помещения), аудит заполненности, группировка семейств по ФОП, сборщик листов
(титульник/кратность/inside-check), мульти-модельность (open_model_background +
documentId-роутинг + реестр #465). Живые прогоны всех 10 кейсов (glm-5.3-flash):
PASS — кейсы 3 (спеки), 8 (аудит), 10 (связи); PARTIAL — 1, 2 (41/42), 4, 6, 7;
FAIL — 5 (semantic_match-шторм модели, контр-средства внедрены), 9 (documentId —
исправлено, перезапуск). Findings → issues #468–#476. Транскрипты:
%LOCALAPPDATA%\Temp\smartcon-bench-dogfood-stable.

**Следующий шаг по плану владельца от 2026-11-21 (после таблицы выше):** эпик B (#291); B.4 UI-панель джоб,
B.5 частичный откат), затем D (#293). По желанию после #S.30 — повторный
прогон кейса 3 до зелёного. Q.1/Q.2/деплой — по готовности к приёмке;
P.6–P.9 (P2) — отдельной сессией.

## 5. Эпики

| ID | Эпик | Prio | Статус | Issue | Задач | Зависимости |
|---|---|---|---|---|---|---|
| **A** | Job engine: A0 → движок → project/family bulk | P0 | `in-progress` | #289 | 12 | Код и интеграционные тесты готовы (2026-11-20); осталось: eval-кейсы job, ручная приёмка, деплой, A.9 (variants). |
| **B** | Trust & recovery: отчёт, откат, resume, false-done guard | P0 | `in-progress` | #291 | 6 | B.1–B.3 реализованы и покрыты тестами (2026-11-20); B.4 (UI-панель) и B.5 (частичный откат) — backlog. |
| **P** | Prompt cache & context ordering: стабильный префикс | P0 | `in-progress` | #298 | 10 | P.1–P.5 закрыты 2026-11-20; P.10 (эффективные 500k) реализована, ждёт ручной проверки отображения; остались P.6–P.9 (P2). |
| **Q** | Release readiness: ручная приёмка, полный гейт, деплой | P0 | `in-progress` | #299 | 5 | Q.5 закрыта; Q.1 и Q.3 идут — ручная приёмка и деплой владельца с 2026-11-24; Q.2 — полный гейт после ручного теста. |
| **C** | Targeted search: C1 resolve_entities → C2 search_catalog по гейту | P1 | `backlog` | #292 | 3 | Параллельно эпику A; для C2 нужен результат гейта C1. |
| **D** | Breadth packs: warnings, worksets, rooms, links | P1 | `backlog` | #293 | 4 | Эпик A — для write-части. |
| **R** | Element reading profile: определения → экземпляры | P1 | `backlog` | #295 | 7 | P0 definition-first закрыт; ADR-076/077; R.7 — стабильные значения enum. |
| **S** | Harness hardening: стабильные ключи, карантин, паки | P1 | `in-progress` | #296 | 44 | S.5/S.10/S.11 (2026-11-21), S.8/S.9 (2026-11-24), S.14a–c, S.15a–c, S.16a, S.17a (2026-11-23), S.25 (2026-11-24) закрыты; S.50/S.51 (#535/#536, фикс-паки v12–v13 миграции ФОП, коммит 9196929d) закрыты 2026-11-26; S.53 (#538, v14-пилот: typeScoped-чтения, sourceValueEmpty, projectBinding, remove_project_parameter=141-й тул, remap familyNames/offset + фикс stale read-back, shared-nested маркеры) — код и тесты готовы, ждёт ручной приёмки; S.52 (#537) — backlog; S.14–S.24, S.26–S.28 в работе (S.18–S.24 — обратная связь ручного теста 2026-11-24, S.18 воспроизведён в живом прогоне deepseek 2026-11-24 и остаётся в работе; S.25 — обезличивание рантайм-текста; S.26–S.28 — системный вид: allow-list видимости/notHidable, detailLevel 3D и шаблон без фильтров, видимые категории листа); S.29 — `blocked`, ждёт решения владельца по сиду кейса 4; S.32 — остаток находок ночного прогона №3 (P3). |
| **G** | Missing operations: удаления, спецификации, листы, ФОП, семейства, MEP | P0 | `done` | #297 | 17 | Закрыт 2026-11-21: все 17 задач done (G.1–G.10 + G.2a/b + G.4a + G.5a-c + G.7a); каталог 63→107 тулов; G.8-write невозможен по API (read-only, зафиксировано). |
| **A3** | External orchestrator для >1000 .rfa и часовых прогонов | P2 | `backlog` | #290 | 2 | Эпик A закрыт. Нужен стабильный job spec и SQLite v5. |
| **E** | Approvals & job recipes | P2 | `backlog` | #294 | 3 | B, D. |
| **H2** | Memory & knowledge: MEMORY.md, FTS5, skills + project memory в agent.db | P1 | `in-progress` | #300 | 9 | H2.6 — первый шаг; запись в ES отклонена владельцем (2026-11-21): вся память в agent.db (SQLite/FTS5); H2.7a закрыта, H2.7 в работе. |
| **H3** | Policy engine и атомарные планы | P2 | `backlog` | #301 | 7 | B, E; H3.4 — commitOnMatch. |
| **H4** | Observability & eval v2 | P2 | `in-progress` | #302 | 7 | H4.5 реализована, ждёт ручной приёмки; H4.7 закрыта (2026-11-24); H4.6 (объективность бенчмарка) — backlog. |
| **V** | Agent на Revit 2019–2022: совместимость параметров | P2 | `backlog` | #397 | 2 | Бета 2023+ принята; R23 отделён от R21. |
| **H5** | UX и расширяемость: steering, timeline, MCP | P3 | `in-progress` | #303 | 5 | A–E стабильны; H5.5 (reasoning stream + сворачивание work) реализована, ждёт ручной приёмки; повторная проверка 2026-11-24 добавила автопрокрутку тела reasoning. |
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
| `A.5` | Save policy, durable cursor и crash resume | P0 | `in-progress` (ждёт приёмки) | #310 |
| `A.6` | A1 project bulk: rename_views и set_parameters на движке | P0 | `done` | #311 |
| `A.7` | A2 family batch: map_family_parameters как IJobAction | P0 | `in-progress` (ждёт приёмки) | #312 |
| `A.8` | Tool surface job'а и приёмка эпика | P0 | `in-progress` | #313 |
| `A.9` | Job variants: несколько групп значений за одну preview+apply | P1 | `backlog` | #414 |
| `A.10` | action джобы replace_family_parameter — массовая замена общего параметра | P1 | `done` | #430 |
| `A.11` | map_family_parameters: createTarget — авто-создание цели | P1 | `done` | #431 |
| `A.12` | start_job: селектор familyNames[] (массив) | P2 | `done` | #433 |

### B — Trust & recovery: отчёт, откат, resume, false-done guard

Эпик: #291 · приоритет `P0` · статус `in-progress`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `B.1` | get_job_report: JSON/CSV с diff и verified/mismatch | P0 | `done` | #316 |
| `B.2` | rollback_job и retry_failed_items | P0 | `in-progress` (20 инжекций, приёмка) | #317 |
| `B.3` | False-done guard и crash reconciliation | P0 | `done` | #318 |
| `B.4` | UI job-панель и приёмка recovery | P1 | `backlog` | #319 |
| `B.5` | Частичный откат задания: подмножество юнитов и change set на батч | P1 | `backlog` | #412 |
| `B.6` | CompletedUnverified — честная семантика непроверяемых целей джобы | P1 | `done` | #432 |

### P — Prompt cache & context ordering: стабильный префикс

Эпик: #298 · приоритет `P0` · статус `in-progress`

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
| `P.10` | Эффективные 500k: поздняя компакция и честный лимит в UI | P1 | `in-progress` | #420 |

### Q — Release readiness: ручная приёмка, полный гейт, деплой

Эпик: #299 · приоритет `P0` · статус `in-progress`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `Q.1` | Ручная UI-приёмка и валидация лога | P0 | `in-progress` | #361 |
| `Q.2` | Полный integration-гейт R25 + net48 + R27 | P0 | `ready` | #362 |
| `Q.3` | Деплой владельцем | P0 | `in-progress` | #363 |
| `Q.4` | CI-блокер: NU1109 на любом PR (#282) | P1 | `backlog` | #364 |
| `Q.5` | validate-docs красный: 13 типов/интерфейсов job engine не задокументированы | P1 | `done` | #410 |

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

Эпик: #296 · приоритет `P1` · статус `in-progress`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `S.1` | Стабильный ключ семейства и плейсхолдеры шагов | P1 | `backlog` | #336 |
| `S.2` | Политика overwrite шаблона и прогресс больших батчей | P2 | `backlog` | #337 |
| `S.3` | Fidelity guard для формул | P2 | `backlog` | #338 |
| `S.4` | Судьба пустых паков: реализовать или удалить | P2 | `backlog` | #339 |
| `S.5` | Карантин/confirmNames для destructive-операций | P1 | `done` | #340 |
| `S.6` | Персистентность загруженных паков на диалог | P2 | `backlog` | #341 |
| `S.7` | copy_document/save_as_copy и регрессионный стресс-набор | P2 | `backlog` | #342 |
| `S.8` | find_families_with_parameter: instance-параметры и правда о shared/remove | P1 | `done` | #417 |
| `S.9` | execute_plan: digest auto-fill, видимость family read-back, byName-пути | P1 | `done` | #418 |
| `S.10` | find_tools/start_job: bulk-намерение и потолки в схеме | P1 | `done` | #419 |
| `S.11` | discovery пакетных аналогов: кросс-ссылки в скиллах/карточках + правило промпта | P1 | `done` | #435 |
| `S.12` | I-03: new Transaction в MapFamilyParametersJobAction.Undo и TryPokeLocal | P3 | `backlog` | #437 |
| `S.14` | [RUN] Harness v3: бенчмарк на прод-цикле (headless-хост, единая регистрация графа), reasoning max по формату API, бюджет вывода 64k, компактная инвентаризация семейств, объективные артефакты прогона | P0 | `in-progress` | #478 |
| `S.14a` | Write-тулы: dry-run на документе без права записи — честный отказ вместо «0 planned» | P1 | `done` | #479 |
| `S.14b` | Панель агента не регистрировалась в проде: незарегистрированный UndoLastTool | P0 | `done` | #486 |
| `S.14c` | query_elements: структурные поля условий и unknown_parameter вместо пустого списка | P0 | `done` | #487 |
| `S.15` | Кейс 5: классификация типоразмеров по таблице документа — read-тул `classify_types` + `set_parameters` (scope пересмотрен 2026-11-23: не джоба) | P1 | `in-progress` | #480 |
| `S.15a` | Классификация: чтение таблицы документа, подключение файла, guard кодов в set_parameters и джобе | P1 | `done` | #490 |
| `S.15b` | read_classifier: коды любой глубины и буквенные коды | P1 | `done` | #491 |
| `S.15c` | classify_types: подбор кодов типоразмерам по таблице документа одним вызовом | P1 | `done` | #499 |
| `S.15d` | read_classifier: плоский экспорт Revit теряет дерево | P2 | `backlog` | #492 |
| `S.15e` | Файл классификатора как источник проверки кодов, пока таблица не загружена | P2 | `backlog` | #493 |
| `S.15f` | Селекторы джобы set_parameters: структурные поля и unknown_parameter | P2 | `backlog` | #494 |
| `S.15g` | read_classifier: одиночные числовые коды дают дерево без корня | P3 | `backlog` | #495 |
| `S.15h` | read_classifier: MasterFormat разбирается как код из первого числа | P3 | `backlog` | #496 |
| `S.16` | Харнесс-защиты в проде: повторные чтения, стоп записи после сбоя, коуч-заметки, гейт verify-before-finish | P1 | `in-progress` | #481 |
| `S.16a` | load_tool_pack: скилл пака — только для явно запрошенного пака и write-тулов | P1 | `done` | #488 |
| `S.17` | Масштаб: паки ≤10 тулов, гайд добавления тула, Tests→Revit в dependency rule, known-workarounds, validate-docs, net48 IT | P2 | `in-progress` | #485 |
| `S.17a` | Release-сборка не собиралась: 11 ошибок анализаторов в модуле агента и тестах | P1 | `done` | #497 |
| `S.17b` | Интеграционные хосты пишут в рабочий smartcon.log пользователя | P2 | `backlog` | #498 |
| `S.18` | find_tools: составной многозадачный запрос отдаёт noMatch при существующих тулах | P1 | `in-progress` | #503 |
| `S.19` | audit_model: сквозная (альбомная) нумерация листов подаётся как дефект и провоцирует sanitize | P1 | `in-progress` | #504 |
| `S.20` | Листовые отчёты: система координат титульника, унаследованный размер после Формат А и parameterName в set_browser_folder | P1 | `in-progress` | #505 |
| `S.21` | set_view_parameters: алиасы вида для масштаба, уровня детализации и дисциплины | P2 | `in-progress` | #506 |
| `S.22` | Мультикатегорийные спецификации: create_schedule(category=multicategory) и explication.sourceScheduleIds[] | P1 | `in-progress` | #507 |
| `S.23` | Системный вид: контракт скрытия прочих MEP-категорий и убрать кейсы/имена заказчика из скиллов | P1 | `in-progress` | #508 |
| `S.24` | query_elements: exists/notExists по непривязанному параметру → unknown_parameter | P2 | `in-progress` | #509 |
| `S.25` | Рантайм-текст агента без маркеров кейсов бенчмарка + регрессионный гейт | P1 | `done` | #510 |
| `S.26` | Системный вид: allow-list видимости (`hideAllExcept`) и честный `notHidable[]` вместо блок-листа | P0 | `in-progress` | #511 |
| `S.27` | 3D-вид: `detailLevel` при создании, `displayStyle` в аудите, шаблон без владения фильтрами | P1 | `in-progress` | #512 |
| `S.28` | Сборщик листов: `visibleCategories` и обязательный read-back через `summarize_elements` | P1 | `in-progress` | #513 |
| `S.29` | Сид кейса 4: насосы должны принадлежать системам — «сердце кейса» недоказуемо из модели (вердиктор уже честный) | P1 | `blocked` | #514 |
| `S.32` | Ночной прогон №3 (deepseek, pass A): остаток находок P2/P3 харнесса | P3 | `backlog` | #516 |
| `S.33` | family audit read-surface: discovery/prefix/summary, compact groups, `read_tool_result` projection | P1 | `done` | #518 |
| `S.34` | ФОП-миграция: `audit_parameter_migration` и `compare_families_with_shared_parameter_file` | P2 | `backlog` | #517 |
| `S.35` | Harness feedback v2: честный `returned`/пагинация, `groupBy:"prefix"`, действие ФОП из пака params | P1 | `done` | #519 |
| `S.36` | Harness feedback v3: `familiesScanned` vs `typesScanned`, `fields[]` find-тула, `familiesWithoutTypes` в summary | P1 | `done` | #520 |
| `S.37` | Harness feedback v4: `byCategory[]` в find-туле, явные `familyIdsReturned`/`familyIdsNote` в group-туле | P1 | `done` | #521 |
| `S.38` | Harness feedback v5: `familyIdsOffset`/`familyIdsLimit` для больших групп | P1 | `done` | #522 |
| `S.39` | Harness feedback v6: классификация замечаний и расширение каталога только для критичных случаев; ясность `matchedParameterCount` vs `matchedTypeCount` | P1 | `in-progress` | #523 |
| `S.40` | Fixed discovery cost: предзагрузка/кэш паков тулов по контексту вместо новых аргументов | P2 | `backlog` | #524 |
| `S.41` | Harness feedback v7: честный отчёт `set_schedule_field_format`, маркер `isMulticategory`, `fieldType` vs `scope` | P1 | `in-progress` | #525 |
| `S.42` | Harness feedback v8 (стресс-тест): кросс-шаговое превью параметров, `onError` шагов, видимость фильтров при merge, ближайший writable уровень браузера, стили `create_schedule`, storm-дискриминатор, `$step`-ссылки между шагами, `create_level` | P1 | `in-progress` | #526 |
| `S.43` | Worksharing-write: батч-checkout до транзакции, Probe dry-run, per-item skip чужих/stale, detached-копии, undo-гейт владения | P0 | `done` | #528 |
| `S.44` | Harness feedback v9: `get_schedule_elements` (FEC по спеке), scope типового общего параметра, `list_families`(OST_*), `list_family_types`(placedOnly), `documentKind`/`isDetached`, `get_current_view_info` на ведомостях | P1 | `done` | #529 |
| `S.45` | Harness feedback v10: петли чтений — `PaginationGuard` (warn 4/block 6), `duplicate_call`, сводка `byCategory` в digest, `invalid_arguments`, `list_views(viewIds)` | P0 | `done` | #530 |
| `S.46` | Harness feedback v11 (дефекты): `start_job` per-action select, `classify_types` oversized-батч (`decision_batch_too_large`/авто-дробление), `summarize_elements` честность (typeId collapse, `(empty)`/`(no value)`), classification leaf-фолбэк | P1 | `done` | #531 |
| `S.47` | Harness feedback v11 (round-экономия): `resultHandle` всегда, `skippedByReason`, `byType`, `values[]` в start_job, `typeId` в query/groupBy, `$result`-проекция как аргумент, `undo_last` для джобы | P2 | `done` | #532 |
| `S.48` | ФОП-миграция (подготовка): impact(`fieldGuid`) по спецификациям + prefix-summary `list_shared_parameters` | P2 | `done` | #533 |
| `S.49` | Decision-канал: бюджет промптов на вызов classify_types (`AgentClassificationBudget`, настройка `SMARTCON_DECIDER_MAX_QUESTIONS`, loop-safe чанкирование) | P1 | `done` | #534 |

### G — Missing operations: удаления, спецификации, листы, ФОП, семейства, MEP

Эпик: #297 · приоритет `P0` · статус `done`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `G.1` | delete-инструменты: элементы, листы, ведомости | P1 | `done` | #343 |
| `G.2` | Запись спецификаций: поля, фильтры, сортировка, размещение | P1 | `done` | #344 |
| `G.3` | Проектные общие параметры: чтение и создание | P1 | `done` | #345 |
| `G.4` | Семейства: duplicate type, rename type, load, purge, replace | P1 | `done` | #346 |
| `G.4a` | duplicate/rename/replace для СИСТЕМНЫХ типов + list_system_types | P1 | `done` | #441 |
| `G.2a` | Спецификации: поля (удаление/порядок), totals, единицы | P2 | `done` | #442 |
| `G.2b` | Спецификации на листе: перемещение и секции | P3 | `done` | #443 |
| `G.5` | Листы: создание, размещение видов, номера | P0 | `done` | #347 |
| `G.5a` | move_viewports: пакетное перемещение видовых окон | P2 | `done` | #438 |
| `G.5b` | remove_viewport: снять вид с листа | P2 | `done` | #439 |
| `G.5c` | renumber_sheets: перенумерация со сдвигом | P3 | `done` | #440 |
| `G.6` | Виды: duplicate, create, delete, browser folder | P2 | `done` | #348 |
| `G.7` | MEP: системы трубопроводов и материалы | P2 | `done` | #349 |
| `G.7a` | set_mep_system_type: abbreviation/name типа системы | P3 | `done` | #444 |
| `G.8` | Browser organization: чтение и запись | P2 | `done` | #350 |
| `G.9` | Crop-from-template, select-наборы и preview зависимых шагов | P2 | `done` | #351 |
| `G.10` | Change sets list/undo_until, save_document, export, audit_model | P2 | `done` | #352 |

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

### H2 — Memory & knowledge: MEMORY.md, FTS5, skills + project memory в agent.db

Эпик: #300 · приоритет `P1` · статус `in-progress`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `H2.6` | Project memory в agent.db (SQLite/FTS5), без записи в модель | P1 | `backlog` | #411 |
| `H2.7` | Системный промпт и доменные скиллы Revit: метод работы через абстракции, контракт отчёта | P1 | `in-progress` | #482 |
| `H2.7a` | Скилл semantic и карточки: источник истины для кодов, без спойлеров кейсов; правило 9 промпта о справочных значениях | P1 | `done` | #489 |
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
| `H3.5` | start_job как шаг execute_plan (jobStep) | P2 | `backlog` | #434 |
| `H3.6` | execute_plan: авто-подстановка toolName при единственном очевидном инструменте | P3 | `backlog` | #436 |
| `H3.7` | execute_plan: превью всей цепочки — шаги, адресующие созданное в том же плане, не превьюируются | P2 | `backlog` | #502 |

### H4 — Observability & eval v2

Эпик: #302 · приоритет `P2` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `H4.1` | Usage ledger и cost caps | P2 | `backlog` | #371 |
| `H4.2` | Eval v2: 200 routing-фраз и snapshots | P2 | `backlog` | #372 |
| `H4.3` | Dashboard/export метрик | P3 | `backlog` | #373 |
| `H4.4` | LLM reliability: fake SSE, retry matrix, stream errors, request log | P2 | `backlog` | #386 |
| `H4.5` | Аналитика хода и сессии в чате | P1 | `in-progress` | #422 |
| `H4.6` | Объективность бенчмарка v2: калибровка грейдеров (null/эталон), повторы pass^k, LLM-судья отчёта, отчёт потерь из JSON | P2 | `backlog` | #483 |
| `H4.7` | Реальный шаблон как база бенчмарка: сиды кейсов 1/2/3/9; dogfood-сьюты и EvalLlmClient на прод-цикле | P2 | `done` | #484 |

### H5 — UX и расширяемость: steering, timeline, MCP

Эпик: #303 · приоритет `P3` · статус `backlog`

| ID | Задача | Prio | Статус | Issue |
|---|---|---|---|---|
| `H5.1` | Steering и timeline/rewind | P3 | `backlog` | #374 |
| `H5.2` | MCP-сервер (backlog) | P3 | `backlog` | #375 |
| `H5.3` | Web search через Exa REST с privacy guard | P3 | `backlog` | #387 |
| `H5.4` | Сессии по документу: dropdown и миграция при Save As | P3 | `backlog` | #388 |
| `H5.5` | UI: streaming размышлений и сворачивание рабочей области | P1 | `in-progress` | #421 |

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
- `S.31` (find_tools на многоцелевых запросах) — **absorbed by S.18** (#503): issue #515 закрыта как дубль, факты перенесены в #503 (2026-11-24).

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
