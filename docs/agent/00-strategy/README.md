# Agent — стратегия и планы (индекс)

> **Назначение:** единая точка входа в стратегические документы модуля `SmartCon.Agent`.
> **Статус:** active · 2026-11-18.
> **Правило:** один вопрос — один SSOT-документ. Новый документ обязан указать, что он заменяет.

## Активные документы

| Документ | Роль | Когда читать |
|---|---|---|
| [`autonomy-roadmap.md`](autonomy-roadmap.md) | **Исполнение сейчас:** job engine, надёжность, поиск, breadth, память — порядок и гейты | Перед планированием любого эпика |
| [`task-family-batch-v1.md`](task-family-batch-v1.md) | Текущая задача: `run_family_batch` — пачки 20–50 семейств, preview/apply/Save/rollback | Перед стартом Family Batch v1 |
| [`night-run-3.md`](night-run-3.md) | Ночной прогон 12 ч: A0 → job core → A1 → A2 → B/C1, 6 срезов | Перед ночным запуском |
| [`harness-ideal-plan.md`](harness-ideal-plan.md) | Долгосрочная стратегия: as-is, gap-анализ, фазы P0–P5, 29 паттернов, схема БД, метрики | Перед любым крупным решением по харнесу |
| [`harness-development-approach.md`](harness-development-approach.md) | Единый процесс: жизненный цикл, гейты, статусы документов, роли, эпики | Перед стартом любой задачи по харнесу |
| [`h1d-evidence.md`](h1d-evidence.md) | Доказательства и метрики H1d (family editing) | При аудите family-тулов |
| [`eval.md`](eval.md) | Журнал eval-прогонов и baseline-метрик | При измерении «до/после» |
| [`decisions-benchmark.md`](decisions-benchmark.md) | Research note: бенчмарк decision-моделей | При выборе роутера/модели |

## Архив (не руководство к действию)

| Документ | Почему архив |
|---|---|
| [`archive/mvp-plan.md`](archive/mvp-plan.md) | Исторический MVP-план; заменён `harness-ideal-plan.md` |
| [`archive/autonomous-execution-plan.md`](archive/autonomous-execution-plan.md) | Runbook первого MVP-прогона; выполнен |
| [`archive/night-run-2.md`](archive/night-run-2.md) | Ночной прогон №2; завершён |
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
| «Что делать прямо сейчас?» | `autonomy-roadmap.md` |
| «Что уже сделано и что сломано?» | `../HANDOFF.md` |
| «Какие цифры на eval?» | `eval.md` |
| «Почему архитектурно так?» | ADR в `docs/adr/` |
| «Что модель считает неудобным?» | `../AGENT-FEEDBACK.md` |

## Правила

1. **Не дублировать план в трёх местах.** Стратегия — в `harness-ideal-plan.md`, исполнение — в `autonomy-roadmap.md`, состояние — в `../HANDOFF.md`.
2. **Каждый новый документ — с шапкой:** статус, дата, владелец, что заменяет.
3. **Завершённый runbook — в `archive/`** с баннером и ссылкой на актуальный документ.
4. **Ссылки — относительные**, от файла; после перемещения — обновить индекс.
5. **Не коммитить план как «сделано»** — только после ручной приёмки и зелёных гейтов.
