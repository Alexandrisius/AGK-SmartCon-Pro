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

## Внешний прогон 4 — 2026-11-18, владелец + реальная задача (rename + template)

Четыре замечания и что с ними сделано (`13ffb360`):

| # | Инструмент | Проблема | Что изменено |
|---|---|---|---|
| 1 | `rename_views` | описан в промпте как штатный, но отсутствовал в routed shortlist — понадобился `find_tools` | `rename_views` и `apply_view_template` закреплены в `RuleToolRouter.CoreTools`, shortlist поднят до 16 |
| 2 | `execute_plan` | в применённом плане `items.dryRun=true`; hint «All expectations verified» при `verified=0` | apply-отчёт берёт per-item diff из `ApplyResult` (`dryRun=false`); при `expectationsChecked=0` hint говорит «проверок не задавали» |
| 3 | `list_views` | в `summaryOnly`/`byFolder` сегменты пути `???` | имя сегмента разрешается через `LabelUtils.GetLabelFor` / имя элемента; Revit 2026+ — `GetGroupingParameterIdPath()`; добавлены `browserFolderPath[]` и `browserFolderParameters[{segment,parameterId,parameterName,isShared,isBuiltIn}]` — видно, что «Сводные» это значение общего параметра, а не физическая папка |
| 4 | `list_views` | `fields` принимает не те имена, что в карточке (`type`→`viewType`), допустимые значения не перечислены | ключи ответа = запрошенные имена; `substitutedFields`; допустимые имена/алиасы перечислены в схемах `list_views`, `get_view_settings`, `get_view_template_details` |

Отдельное наблюдение по роутингу: при 27 тулах decision-модель не даёт выигрыша
над BM25, `find_tools` ограничен поиском (не листингом), `context` необязателен.
Решение: decision остаётся опциональной; для 500–1000 тулов и 200 навыков
принят гибридный retrieval BM25 ∪ vector → RRF → decision reranker
(**ADR-075**, эпик H6 после H1). H1 vector не реализует.

## Внешний прогон 5 — 2026-11-19, стресс-тест 42 тулов (сторонний агент)

Прогон: все 42 инструмента каталога вызваны, все 21 write-инструмент применены
(не только dry-run), отдельно — целевые репро исправлений. Ниже **только
уникальные** запросы, которых ещё нет в `stress-test-followups.md`,
`harness-ideal-plan.md` и `HANDOFF.md`. Формулировки — со слов агента.

> **Статус 2026-11-19: P0 реализован.** Каталог 52 инструмента: добавлены
> `list_filters`, `get_filter_details`, `set_filter_rules`, `delete_filter`,
> `delete_views`, `delete_sheets`, `delete_family_types`,
> `remove_family_parameter`, `set_view_parameters`; снятие шаблона —
> `apply_view_template(templateId=0|null)`. P1/P2 остаются в очереди.

### P0 — блокеры прогона

| Чего не хватило | Реальная задача, где упёрся |
|---|---|
| `list_filters` / `get_filter_details` / `set_filter_rules` / `delete_filter` | Создал фильтр «ТЕСТ_Атомарность» (1705685), но списка фильтров проекта, их правил и мест применения нет. `create_view_filter` переиспользует фильтр по имени — то есть читает модель, а наружу чтения нет; починить/убрать нечем |
| `delete_*` (filter, view, sheet, family type) + `remove_family_parameter` | «ТЕСТ_…» объекты остаются в модели; единственный путь — `undo_last`, а он работает только по своим change set'ам. Добавленный `ТЕСТ_Параметр` из семейства не убрать |
| `set_view_parameters` + чтение параметров вида | Браузер проекта организован параметрами (`browserFolderParameters`): «Назначение вида / Раздел / ADSK_Имя вида в штампе» не прочитать и не задать — папки браузера фактически недоступны |
| Снятие шаблона с вида (`apply_view_template` с `templateId=null`) | Вид 72842 под шаблоном: crop не отсоединяется (ограничение Revit), а «снять шаблон» нечем — обходного пути нет |

### P1 — целые сценарии

| Группа | Нужно | Задача |
|---|---|---|
| Листы | `create_sheet`, `place_view_on_sheet` (координаты из `get_sheet_content`), `move_viewport`, `set_sheet_number`, `rename_sheet`, `sanitize_sheet_numbers` | 5 листов с визуально одинаковыми номерами из-за невидимых U+202A/U+202C: инструмент находит и предупреждает, а исправить нечем |
| Спецификации | `create_schedule`, `add_schedule_field`, `set_schedule_filter`/`sort`, `place_schedule_on_sheet` | «Сделай спецификацию по трубам» невыполнима — доступно только чтение 37 существующих |
| Виды | `duplicate_view`, `create_view` (разрез/3D/легенда), `delete_view`, `set_browser_folder` | «Продублируй вид с настройками на каждый раздел», «создай 3D-вид для раздела» |
| Семейства | `duplicate_family_type`, `rename_family_type`, `set_type_values` (по типам проекта, не в редакторе) | 35 типоразмеров марок труб; `set_parameters(scope=type)` работает от элемента — у размещённых типов это лотерея |
| Проектные параметры | `add_project_shared_parameter`, `list_project_parameters` | Миграция ФОП: `remap_parameter_values` переносит значения, но создать новый общий параметр в проекте нечем |
| Массовые записи | `set_parameters_by_query` (QuerySpec вместо списка id, ≤500) | «Проставь марки всем трубам системы Т1» — 104 трубы вручную не собрать |

### P2 — удобство и доверие

- `list_change_sets` / `get_change_set_details` / `undo_until` — после 10 change set'ов известен только последний, откат конкретного шага — перебор вслепую.
- `save_document` — «применить и сохранить» просят постоянно.
- `audit_model` (пак `audit` был пуст): дубли имён, невидимые символы, виды без шаблона, пустые спецификации.
- `export_pdf` / `export_dwg`, `print_set` — выдача документации.
- MEP: системы трубопроводов (47) и материалы (115) — ни чтения параметров, ни записи; пак `mep` был объявлен и оказался пуст.

### Что уже отслеживается (не дублируем)

- Пустые паки `worksets`/`links`/`mep`/`annotations`/`audit` — `stress-test-followups.md` §3.4.
- `set_parameters_by_query` частично закрывается H8 bulk/cyclic job engine (`harness-ideal-plan.md`), но синхронный QuerySpec-тул всё равно нужен.
- Снятие шаблона с вида — `HANDOFF.md` («снять шаблон → изменить crop» либо `set_view_template_crop`).
- Стабильный ключ семейства / alias-кэш — `stress-test-followups.md` §3.1.

### Топ-5 приоритетов тестировщика

1. `list_filters` + `delete_filter` — иначе фильтры «одноразовые».
2. `set_view_parameters` — без этого браузер проекта не управляем.
3. `remove_family_parameter` + `delete_family_type` — семейные правки сейчас необратимы.
4. `place_view_on_sheet` + `set_sheet_number` — самый частый сценарий оформления.
5. `set_parameters_by_query` — снимает ограничение «500 id вручную».

> Пока не запланировано. Кандидаты в следующий roadmap-цикл (H1c / H1d / листы
> и спецификации) после стабилизации текущего каталога.

## Внешний прогон 6 — 2026-11-19, проверка каталога 52 инструментов

Агент проверил P0-поверхность (фильтры, удаления, параметры вида) и нашёл 1 блокер,
5 дефектов корректности и 5 эргономики. Разбор по каждому — в
`00-strategy/stress-test-followups.md` §1.2.

| Пункт | Статус |
|---|---|
| P0: `View.GetFilters()` падает на расписаниях/легендах/листах (list/delete/details фильтра) | ✅ `AreGraphicsOverridesAllowed` + try/catch, `includeAppliedViews`, `notComputed` |
| P0: `list_filters(viewId=…)` не сужал итерацию | ✅ один вид вместо всего документа |
| P1: `create_view_filter` терял причину на шаблонном виде (`tool_failed`, `failed[]: []`) | ✅ валидация видов до создания фильтра + safety-net в write-базе |
| P1: `set_filter_rules` не отдавал старый rule tree | ✅ `previousRules` (parameterId+parameterName+op+value), `previousCategories` |
| P1: `remove_family_parameter` не отклонял built-in | ✅ `isBuiltIn` в `get_family_parameters` + проверка удалением в dry-run |
| P1: счётчики `sections.views` считали фильтр | ✅ `viewsChanged/viewsSkipped/viewsFailed` + `viewCounters` |
| P2: `load_tool_pack` не сходился арифметически | ✅ `catalogTotal = coreTools + loadedTools + inRequestTools + remainingAvailable` |
| P2: `load_tool_pack` не отдавал схемы загруженных инструментов | ✅ `schemas[]` (до 8) |
| P2: `get_filter_details()` без аргументов — другое сообщение | ✅ единый текст |
| P2: `rename_views` прятал `duplicate_name` | ✅ доминирующий код наверх + `errorCodes[]` |
| P2: `find_families_with_parameter` раздувал `matchedTypes` (лимит на тип) | ✅ лимит на семейство + `compact` |
| State: маркер версии документа и подсказка при `*_not_found` | ✅ `documentVersion` в каждом результате + hint при смене версии |
| Продолжение прогона 6: `appliedViews` без пагинации | ✅ `appliedViewsLimit/Offset` + `appliedViewsTotal/Returned/NextOffset/Truncated` |
| Продолжение прогона 6: версия в тексте `*_not_found` | ✅ ошибка несёт `documentVersion` и объяснение про откат/переоткрытие |
| Продолжение прогона 6: `create_view_filter` при `atomic:false` не создаёт orphan-фильтр | ✅ это задумано; описано в схеме и сообщении, в ответе `atomic` |
| P1-4 из брифа (built-in «Комментарии») | ⚠️ бага не было: параметры пользовательские (`isBuiltIn:false`); `isBuiltIn` оставлен как предполётная проверка |
| P2: destructive без отката (карантин/токен) | ⏳ в очередь — followups §3.10 |
| Просьба: не сбрасывать набор паков между запросами | ⏳ исследовано (Claude Code/Codex ведут себя так же); варианты A/B/C — followups §3.11 |
| Просьба: тесты на копии файла | ⏳ в очередь — followups §3.12 |

## Как использовать дальше

1. `AgentFrictionAnalyzer` (SmartCon.Agent/Services/Analysis) читает `tool_calls` и
   `agent_feedback` из `agent.db` за N дней и печатает markdown-отчёт:
   частоты `invalid_arguments`, `tool_failed`, `document_changed`, `truncated`,
   средние длительности, топ проблемных инструментов и рекомендации.
2. `tool_calls` заполняется оркестратором для каждого вызова (status/error/duration).
3. `token_usage` заполняется по итогам каждого хода (провайдер, модель, токены, cost).
4. Ручной запуск: `SMARTCON_AGENT_LIVE=1 dotnet run --project src/SmartCon.IntegrationTests
   -c Debug.R25 --framework net8.0-windows -- --treenode-filter "/*/*/*AgentLiveDogfood*/*"`.
