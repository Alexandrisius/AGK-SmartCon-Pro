> **ARCHIVED (2026-11-19).** Исторический документ. Не руководство к действию.
> Актуально: [`ROADMAP.md`](../../ROADMAP.md) + GitHub Issues (метка `agent`). Внутренние ссылки исторические.

# Agent — стратегия и планы (индекс)

> **Назначение:** единая точка входа в стратегические документы модуля `SmartCon.Agent`.
> **Статус:** active · 2026-11-18.
> **Правило:** один вопрос — один SSOT-документ. Новый документ обязан указать, что он заменяет.

## Активные документы

| Документ | Роль | Когда читать |
|---|---|---|
| [`../ROADMAP.md`](../../ROADMAP.md) | **Единая карта работ:** эпики/задачи → GitHub issue → статус | Перед любой задачей |
| [`autonomy-roadmap.md`](autonomy-roadmap.md) | **Порядок исполнения:** job engine, надёжность, поиск, breadth, память — очередь и гейты | Перед планированием любого эпика |
| [`task-family-batch-v1.md`](task-family-batch-v1.md) | Текущая задача: `run_family_batch` — пачки 20–50 семейств, preview/apply/Save/rollback | Перед стартом Family Batch v1 |
| [`night-run-3.md`](night-run-3.md) | Ночной прогон 12 ч: A0 → job core → A1 → A2 → B/C1, 6 срезов | Перед ночным запуском |
| [`harness-ideal-plan.md`](harness-ideal-plan.md) | Долгосрочная стратегия: as-is, gap-анализ, фазы P0–P5, 29 паттернов, схема БД, метрики | Перед любым крупным решением по харнесу |
| [`prompt-cache-plan.md`](prompt-cache-plan.md) | План prompt cache: стабильный префикс, заморозка tools, provider policy, cache-aware компакция, метрики; карта кода и фазы | Перед работами по стоимости/контексту и при аудите кеша |
| [`harness-development-approach.md`](harness-development-approach.md) | Единый процесс: жизненный цикл, гейты, статусы документов, роли, эпики | Перед стартом любой задачи по харнесу |
| [`h1d-evidence.md`](h1d-evidence.md) | Доказательства и метрики H1d (family editing) | При аудите family-тулов |
| [`eval.md`](eval.md) | Журнал eval-прогонов и baseline-метрик | При измерении «до/после» |
| [`decisions-benchmark.md`](decisions-benchmark.md) | Research note: бенчмарк decision-моделей | При выборе роутера/модели |
| [`stress-test-followups.md`](stress-test-followups.md) | Разбор стресс-теста 42 тулов: фиксы, очередь улучшений, handoff крупных фич | Перед работой над найденными дефектами |
| [`real-cases-benchmark.md`](real-cases-benchmark.md) | Реальные кейсы проектировщика: 5 задач без имён инструментов, протокол прогона, журнал эволюции харнеса | Перед приёмочным прогоном и при добавлении новых возможностей |
| [`element-reading-followups.md`](element-reading-followups.md) | **Backlog P1/P2** после P0 «definition-first чтение элемента»: list_types, list_parameters, essence в карточках, протокол, eval | Перед следующей итерацией по чтению/поиску элементов |

## Архив (не руководство к действию)

| Документ | Почему архив |
|---|---|
| [`archive/mvp-plan.md`](archive/mvp-plan.md) | Исторический MVP-план; заменён `harness-ideal-plan.md` |
| [`archive/autonomous-execution-plan.md`](archive/autonomous-execution-plan.md) | Runbook первого MVP-прогона; выполнен |
| [`archive/night-run-2.md`](archive/night-run-2.md) | Ночной прогон №2; завершён |
| [`archive/real-cases-v2-findings-2026-11-23.md`](archive/real-cases-v2-findings-2026-11-23.md) | Реестр находок прогона на прод-цикле 2026-11-23 (144 ID): что нашли и что с этим сделали; статусы задач — в GitHub Issues |
| [`archive/initial-improvement-runbook.md`](archive/initial-improvement-runbook.md) | H1 выполнен; заменён `autonomy-roadmap.md` |
| [`archive/h1b-tool-packs.md`](archive/h1b-tool-packs.md) | H1c/H1d закрыты, H1b отложен; заменён `autonomy-roadmap.md` |

В архивных документах внутренние ссылки могут быть историческими. Актуальные ссылки — только в этом индексе и `docs/agent/README.md`.

## Статусы документов

| Статус | Значение |
|---|---|
| `draft` | Черновик, решения не приняты |
| `active` | Действующий SSOT |
| `reference` | Справочник/журнал, не руководство |
| `completed` | Работа выполнена, документ сохранён для истории |
| `archived` | Заменён/устарел, не использовать для решений |
| `superseded by <doc>` | Явно заменён другим документом |

## Что где писать

| Вопрос | Документ |
|---|---|
| «Куда движется харнес?» | `harness-ideal-plan.md` |
| «Как мы ведём разработку?» | `harness-development-approach.md` |
| «Что делать прямо сейчас?» | `autonomy-roadmap.md` (очередь) + `../ROADMAP.md` (карта) |
| «Статус конкретной задачи?» | GitHub Issue: `gh issue view N` |
| «Что уже сделано и что сломано?» | `../HANDOFF.md` (состояние) + GitHub Issues (история) |
| «Какие цифры на eval?» | `eval.md` |
| «Почему архитектурно так?» | ADR в `docs/adr/` |
| «Что модель считает неудобным?» | `../AGENT-FEEDBACK.md` |

## Правила

1. **Статусы работ — только в GitHub Issues.** `../ROADMAP.md` — карта `ID ↔ issue # ↔ статус`; документы стратегии не дублируют статусы задач.
2. **Не дублировать план в трёх местах.** Стратегия — в `harness-ideal-plan.md`, очередь — в `autonomy-roadmap.md`, карта и статусы — в `../ROADMAP.md` + issues, состояние — в `../HANDOFF.md`.
3. **Каждый новый документ — с шапкой:** статус, дата, владелец, что заменяет.
4. **Завершённый runbook — в `archive/`** с баннером и ссылкой на актуальный документ.
5. **Ссылки — относительные**, от файла; после перемещения — обновить индекс.
6. **Не коммитить план как «сделано»** — только после ручной приёмки и зелёных гейтов.
