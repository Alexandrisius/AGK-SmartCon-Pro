# Единый подход развития AI-харнеса SmartCon

> **Статус:** active · 2026-11-18 · владелец: Александр Климович
> **Заменяет:** разрозненные правила из `archive/mvp-plan.md` / `archive/autonomous-execution-plan.md` / `archive/night-run-2.md` (архив).
> **Связанные документы:** [`harness-ideal-plan.md`](harness-ideal-plan.md) — стратегия; [`initial-improvement-runbook.md`](initial-improvement-runbook.md) — эпик H1; [`../HANDOFF.md`](../HANDOFF.md) — состояние.

## 1. Зачем этот документ

Чтобы каждый агент и человек отвечал на одни и те же вопросы одинаково:

- что мы строим и что **не** строим;
- как решение превращается в код;
- какие гейты нельзя обходить;
- где лежит правда и что считается устаревшим;
- как измеряется «стало лучше».

## 2. North star

**BIM-ops харнес:** пользователь формулирует рутинную операцию с моделью Revit обычным языком, агент сам находит нужные инструменты, строит план, безопасно применяет и отчитывается.

| Параметр | Цель |
|---|---|
| Инструменты | 500–1000 в каталоге, 100–150 композитных действий |
| Навыки | 100–200 `SKILL.md` с progressive disclosure |
| Контекст | до 1M токенов (сейчас 500k), но не как замена роутингу |
| Запись | dry-run, подтверждение, change set, undo, read-back |
| Кейсы | виды, листы, параметры, семейства, ФОП, фильтры, worksets, связи, MEP, аннотации, аудит, purge |
| Вне scope | создание новых 3D-объектов «с нуля», codegen как основной путь |
| Не строим | coding-harness, IDE, MCP-сервер в MVP, мультиагентную запись в Revit |

## 3. Карта подсистем

```
UI (WPF MVVM)
  └─ Agent runtime (SmartCon.Agent)
       ├─ Turn loop / LLM client
       ├─ Context assembler (prefix, turn-context, compaction)
       ├─ Tool catalog (packs, cards, find/load, freeze)
       ├─ Tool executor (validation, repair, timeout, loop guard)
       ├─ Plan executor (L0–L6, atomic group, read-back)
       ├─ Policy engine (deny-first, grants, plan mode)
       ├─ Memory (files + FTS5) и skills
       └─ Observability (usage, audit, events, eval)
  └─ Revit boundary (SmartCon.Revit)
       ├─ IExternalEventHandler + ExternalEvent
       ├─ ITransactionService / ITransactionGroupSession
       ├─ Change sets / undo / read-back
       └─ Document identity / workshared guards
```

Подробная целевая архитектура — `harness-ideal-plan.md` §3.

## 4. Принципы

1. **Один агент, один Revit-поток.** Никакой параллельной записи; чтения батчатся в одном `ExternalEvent`.
2. **Каталог важнее промпта.** Модель не видит 1000 схем; видит паки, ищет top-5, фиксирует набор.
3. **Контекст — бюджет, а не свалка.** Tool-результаты хранятся отдельно, в промпт идёт превью + handle.
4. **Состояние — структурное.** `WorkingState` (цель, план, ID, change sets) важнее пересказа истории.
5. **Память — файлы + FTS5.** Без vector RAG: 0/11 исследованных харнесов используют эмбеддинги для кода.
6. **Политика — данные.** `agent-policy.json` + grants; deny-first; fail-closed.
7. **План атомарен.** Один `TransactionGroup`; read-back до `Assimilate`; иначе `RollBack`.
8. **Измеряй до и после.** У каждой фичи есть метрика и eval-кейс; «стало лучше» — цифра, не мнение.
9. **Флаг выключен = старое поведение.** Любая рискованная фича за флагом, откат — выключение.
10. **Документ следует за фактом.** «Сделано» пишется после зелёных гейтов и ручной приёмки.

## 5. Жизненный цикл

```
Идея → Стратегия → Эпик → Runbook → Реализация → Валидация → Приёмка → Docs/ADR → Релиз
```

| Шаг | Артефакт | Кто | Гейт |
|---|---|---|---|
| Идея | запись в `HANDOFF`/issue/`AGENT-FEEDBACK` | владелец/агент | — |
| Стратегия | `harness-ideal-plan.md` (обновление раздела) | архитектор | G0 |
| Эпик | таблица эпиков + scope/out-of-scope | архитектор | G0 |
| Runbook | `initial-improvement-runbook.md` или новый | исполнитель | G0 |
| Реализация | код + unit-тесты + флаги | исполнитель | G1/G2 |
| Валидация | build 7, unit, integration, eval | валидатор `general` | G3 |
| Приёмка | ручной сценарий в Revit + лог | владелец | G4 |
| Docs/ADR | обновление docs, ADR, changelog | исполнитель | G5 |
| Релиз | PR/merge, deploy | владелец | G5 |

## 6. Гейты

| Гейт | Что проверяется | Блокирует |
|---|---|---|
| **G0. План** | scope, out-of-scope, метрики, риски, зависимости, ссылки на SSOT | старт кода |
| **G1. Сборка** | 7 конфигураций R19–R27, 0 warnings/errors | дальнейшую работу |
| **G2. Тесты** | unit 100 %, integration R25 + net48 (+R27), новые тесты на границу | merge |
| **G3. Eval** | replay gate PASS; для фичи — новые кейсы; «до/после» в `eval.md` | merge |
| **G4. Приёмка** | ручной сценарий в Revit, `smartcon.log` без необъяснимых Error/Warn, OpId целые | «сделано» |
| **G5. Docs** | docs/ADR/changelog обновлены, ссылки живые, архив пополнен | релиз |

**Нельзя:** «собралось — значит готово»; «unit зелёный — значит UI работает»; «агент сказал PASS — значит можно коммитить».

## 7. Документы и статусы

| Документ | Роль | Обновляется |
|---|---|---|
| `00-strategy/README.md` | индекс стратегии | при добавлении/архивации |
| `harness-ideal-plan.md` | долгосрочная стратегия | при смене направления/фаз |
| `harness-development-approach.md` | процесс (этот файл) | редко, при смене правил |
| `initial-improvement-runbook.md` | исполнение эпика | по ходу эпика |
| `eval.md` | цифры прогонов | после каждого eval |
| `HANDOFF.md` | состояние и следующие шаги | после каждой фазы |
| `AGENT-FEEDBACK.md` | фидбэк модели | после dogfooding |
| ADR `docs/adr/` | архитектурные решения | при необратимом выборе |

Статусы: `draft`, `active`, `reference`, `completed`, `archived`, `superseded by <doc>`. Полная таблица — `00-strategy/README.md`.

**Правило одного SSOT:** если факт уже есть в стратегии, runbook ссылается на него, а не копирует. Дубликат — повод удалить один из документов.

## 8. Eval-first

1. До фичи — baseline: токены, cache, tool errors, latency, success.
2. Фича получает минимум 3 кейса: happy path, граница, отказ.
3. После — тот же прогон; результат в `eval.md` с датой и моделью.
4. Регрессия = падение метрики или новый skip без объяснения.
5. Живые (LLM) кейсы отделены от replay; replay не требует ключа.

Минимальные метрики: routing top-1/top-5, cache hit, context/billed tokens, tool error rate, invalid args, loop rate, compaction fact retention, plan success, cost.

## 9. Флаги и откат

- Каждая рискованная фича — флаг в `AgentLlmSettings`/конфиге (`SMARTCON_AGENT_*`).
- Дефолт — текущее поведение; включение — после G3/G4.
- Схема БД — только additive (`ADD COLUMN`, новые таблицы); старый код работает.
- Откат фичи — выключение флага; откат схемы — backup/restore, не downgrade-миграция.
- `TransactionGroup`/атомарный план — отдельный флаг, потому что меняет семантику apply.

## 10. Роли

| Роль | Ответственность |
|---|---|
| Владелец | продукт, приоритеты, ручная приёмка, релиз |
| Главный агент | код, архитектура, интеграция, docs после приёмки |
| Субагент `explore` | быстрые read-only исследования |
| Субагент `general` | обязательная валидация этапа/эпика |
| Валидатор | проверяет инварианты, dependency rule, полноту, регрессии |

Правило: **каждый логический этап** (фаза/issue/эпик) закрывается валидатором `general`. FAIL → фикс → повторная валидация.

## 11. Эпики

| Эпик | Цель | Статус |
|---|---|---|
| **H1** | Масштаб и контекст: tool catalog v2, WorkingState, store-and-reference, compaction v2 | runbook готов, код не начат |
| H2 | Память и знания: `MEMORY.md`, FTS5, skills 100–200 | backlog |
| H3 | Политики и планы: policy engine, grants, atomic plan, verify-on-stop | backlog |
| H4 | Наблюдаемость и eval v2: usage ledger, cost caps, snapshots, 200 routing | backlog |
| H5 | UX и расширяемость: steering, timeline/rewind, MCP (backlog) | backlog |

Эпик H1 — `initial-improvement-runbook.md`. Остальные — разделы P2–P5 в `harness-ideal-plan.md`.

## 12. Шаблон новой задачи

```markdown
## Задача
<одна фраза>

## Зачем
<метрика/боль>

## Scope
- ...

## Out of scope
- ...

## Контракты
<интерфейсы/схема БД/формат>

## Приёмка
- [ ] build 7
- [ ] unit
- [ ] integration R25/net48/R27
- [ ] eval replay + новые кейсы
- [ ] ручной Revit + лог

## Риски/откат
- ...

## Ссылки
<SSOT-раздел>
```

## 13. Handoff и логи

- `HANDOFF.md` — короткий: где мы, что сделано, что дальше, какие флаги/схемы изменены.
- После каждой фазы — обновить `HANDOFF`, `eval.md`, при необходимости `AGENT-FEEDBACK.md`.
- Логи — `SmartConLogger`, `OpId`, `Warn` с `[Action: ...]`; full path в scope запрещён.
- Ручной тест — сценарий + ожидание + лог; без лога UI-задача не считается проверенной.

## 14. Антипаттерны

- 1000 тулов в одном system prompt.
- LLM-summary как первый механизм экономии контекста.
- Vector RAG «на всякий случай».
- Параллельные tool calls, трогающие Revit API.
- «Сначала код, потом план» и «сначала коммит, потом приёмка».
- Дублирование плана в трёх документах.
- Коммит `opencode.json`, `server/`, секретов и личных файлов.
- Валидатор, который смотрит только diff и не проверяет инварианты.

## 15. Definition of Done эпика

1. Все задачи runbook закрыты или явно перенесены с причиной.
2. 7 конфигураций — 0 warnings/errors.
3. Unit 100 %, integration R25 + net48 (+R27) — 0 failed.
4. Eval replay PASS; новые кейсы и метрики «до/после» в `eval.md`.
5. Ручной сценарий в Revit принят владельцем, лог чистый.
6. Валидатор `general` — PASS.
7. Docs/ADR/changelog обновлены; старый runbook — в архиве.
8. Флаги описаны, откат проверен.
