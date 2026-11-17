# Agent — AI-харнес для Revit

Модуль встроенного AI-ассистента SmartCon: чат-панель внутри Revit, типизированные инструменты чтения/записи модели, структурный поиск элементов и безопасное применение изменений с dry-run, подтверждением и откатом.

## Документы

| Документ | Содержание |
|---|---|
| [`00-strategy/mvp-plan.md`](00-strategy/mvp-plan.md) | Детальный план MVP: scope, архитектура, инструменты, query builder, ФОП-миграция, безопасность, тесты, фазы |
| [`00-strategy/autonomous-execution-plan.md`](00-strategy/autonomous-execution-plan.md) | Runbook для агента с нулевым контекстом: задачи P0–P3, команды, DoD, тестовый стенд, stop conditions, handoff |

## Статус

**Draft** — план готов к ревью. Реализация не начата.

## Ключевые решения (кратко)

- Встроенная dockable-панель в Revit, MVVM, все вызовы Revit API — через `IExternalEventHandler`.
- Облачная LLM: OpenRouter + DeepSeek V4.1 Flash (tools + vision), OpenCode Go как альтернатива; ключи — вне репозитория и логов.
- Multi-version сразу: Revit 2023 (net48), 2025 (net8), 2027 (net10).
- Инструменты: 15–20 read + 8–10 write (включая composite `configure_views`); без codegen и без 1000 узких tools.
- Поиск: структурный query builder (SQL-подобные условия), без FTS5 и векторов в MVP.
- `rename_views` принимает явный список `viewId → newName`; имена предлагает агент.
- `copy_view_settings` — главный кейс R23: взять область видимости/шаблон с эталонного вида и применить к целевым.
- `configure_views` — явная настройка: имя + шаблон + область видимости + view range (опционально) + visibility.
- `remap_parameter_values` переносит значения при смене файла общих параметров (ФОП).
- Каждая запись — `ITransactionService`, dry-run через `RunAndRollback`, audit и change set для отката.
- Сессии — выпадающий список сверху, привязка к документу; без левой колонки истории.
- Контекст проекта подаётся агенту автоматически, без tool call.
- Память: session + project memory, summary/handoff, token counter.
- Skills, MCP/Exa и валидация плана зафиксированы в roadmap (раздел 20 плана).
