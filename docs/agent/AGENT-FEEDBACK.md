# Agent feedback — live dogfooding

> Источник: `AgentLiveDogfoodTests` (интеграционный тест в реальном Revit 2025,
> read-only). Запуск: `SMARTCON_AGENT_LIVE=1` + ключ в `%APPDATA%\AGK\SmartCon\Agent\secrets.env`
> или `SMARTCON_AGENT_API_KEY`. Транскрипт: `%TEMP%\smartcon-agent-dogfood\<timestamp>.md`.

## Прогон 1 — 2026-11-18, openrouter / deepseek/deepseek-v4.1-flash-1017

- Задач: 11, ответов: 10, LLM-запросов: 29/30, feedback-записей: 3.
- Модель сама вызвала `report_tooling_feedback` три раза — по одному на каждую
  реальную проблему (без спама).

### Что модель назвала неудобным

| # | Инструмент | Проблема | Что изменено |
|---|---|---|---|
| 1 | `list_views`, `query_elements` | `fields: ["viewId","name","type","level"]` молча выбрасывал `type` и `level`: в карточке свойства называются `viewType` и `levelName`. Модель получила меньше данных, чем просила, и не узнала об этом. | `AgentCardProjection` получил алиасы (`id`→viewId/elementId, `type`→viewType/typeName, `level`→levelName, `template`→templateName, `elevation`→elevationMm и т.д.). Неизвестные поля больше не теряются молча: ответ содержит `ignoredFields[]` и `availableFields[]`. |
| 2 | `get_view_settings` (и все mm-значения) | Смещения view range приходили как `2299.9999999999995` вместо `2300` — модель тратила шаг на объяснение «плавающей погрешности». | `AgentUnitsFormat.ToMillimeters` округляет до 0.1 мм (`MidpointRounding.AwayFromZero`). Действует на все инструменты: elevationMm, offsetMm, crop, суммы. |
| 3 | `get_element_details` | Параметр `Category` возвращался дважды (ELEM_CATEGORY_PARAM и ELEM_CATEGORY_PARAM_MT), а `Length` отдавал `displayValue: "3048"` без единиц, тогда как `Area` — `"9.29 m²"`. | Дубликаты `name+scope+storageType+value` схлопываются (`Deduplicate`). Для `Double`-параметров без единиц в `AsValueString()` единица добавляется из `Definition.GetDataType()` + `Units.GetFormatOptions` + `LabelUtils.GetLabelForUnit`. |

### Что осталось (не критично)

- В задаче «что за проект» модель сделала 4 раунда tool-вызовов и упёрлась в
  per-task лимит харнесса (4) — это бюджет теста, не баг инструментов. В
  production-оркестраторе лимит 8 раундов.
- `get_current_view_info` в тест-хосте возвращает `no_active_view` — корректное
  поведение (у инжектора нет UI-сессии), модель это объяснила пользователю.
- Модель предложила «создать шаблон вида из образца» — инструмента создания
  шаблонов в MVP нет по плану (§7A.8), план был корректен в рамках возможностей.

## Прогон 2 — 2026-11-18, после первых трёх улучшений

- Задач: 11, ответов: 11, LLM-запросов: 27/30, feedback: 1.
- Подтверждено: `fields: ["viewId","name","type","level"]` → `viewType` + `levelName`;
  `offsetMm` = `2300`/`1200` (без плавающей погрешности); `Category` в
  `get_element_details` одна запись.
- **Новое замечание модели**: `fields: ["template"]` уходил в `ignoredFields`, а в
  `availableFields` не было `templateName`/`sheetNumber`. Root cause: сериализация
  карточек использовала `WhenWritingNull`, и null-свойства выпадали из карточки —
  выглядело как «поле не поддерживается».

### Улучшение #4

`AgentJson.SerializeCard` (без `WhenWritingNull`) + `AgentCardProjection` теперь
резолвит и null-значения: `fields` с существующим, но пустым свойством возвращает
его со значением `null` и не попадает в `ignoredFields`. Покрыто тестами
`ListViews_NullValuedFields_AreStillProjected`, `ListViews_UnknownField_IsReported`.

## Прогон 3 — 2026-11-18, финальное подтверждение

- Задач: 11, ответов: 11, LLM-запросов: **24/30**, feedback: **0**.
- Модель не нашла ни одной проблемы: все замечания прогонов 1–2 закрыты.
- Сокращение числа запросов (29 → 24) — прямое следствие алиасов проекций и
  целых мм: модель перестала делать «уточняющие» вызовы.

## Как использовать дальше

1. `AgentFrictionAnalyzer` (SmartCon.Agent/Services/Analysis) читает `tool_calls` и
   `agent_feedback` из `agent.db` за N дней и печатает markdown-отчёт:
   частоты `invalid_arguments`, `tool_failed`, `document_changed`, `truncated`,
   средние длительности, топ проблемных инструментов и рекомендации.
2. `tool_calls` заполняется оркестратором для каждого вызова (status/error/duration).
3. `token_usage` заполняется по итогам каждого хода (провайдер, модель, токены, cost).
4. Ручной запуск: `SMARTCON_AGENT_LIVE=1 dotnet run --project src/SmartCon.IntegrationTests
   -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*AgentLiveDogfood*/*"`.
