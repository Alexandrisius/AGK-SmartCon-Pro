> **ARCHIVED (2026-11-19).** Исторический документ. Не руководство к действию.
> Актуально: [`ROADMAP.md`](../../ROADMAP.md) + GitHub Issues (метка `agent`). Внутренние ссылки исторические.

# Стресс-тест harness: разбор, фиксы и очередь улучшений (2026-11-19)

Источник: полный прогон внешним агентом — 42 инструмента каталога вызваны, все 21
write-инструмент применены (не только dry-run), проверены ветки ошибок
(`duplicate_name`, `template_view_type_mismatch`, `view_controlled_by_template`,
`invalid_value`, `family_not_found`, `change_set_not_applied`, no-op, partial failure).
Ниже: что исправлено сразу, что осталось улучшениями, какие крупные фичи переданы.

## 1. Исправлено в этом проходе

| # | Дефект | Фикс | Файлы |
|---|---|---|---|
| 1 | `get_family_parameters` отдавал `isDeterminedByFormula:false` / `canSetValue:true` для параметра с формулой; `set_family_parameter_values` планировал фиктивную запись | `AgentFamilyReader.IsFormulaDriven` — `IsDeterminedByFormula \|\| Formula != null`; проверка в записи значений; `canSetValue` считается по тому же признаку | `Tools/Families/AgentFamilyReader.cs`, `Tools/Write/SetFamilyParameterValuesTool.cs` |
| 2 | `familyId` пересоздаётся после каждой записи в семейство → цепочки по id ломаются | `uniqueId` уже принимается всеми read/write-тулами; добавлен явный `familyIdNote` в ответе при смене id и указание в карточке `list_families`, что для цепочек нужно `familyUniqueId` | `Tools/Families/AgentFamilyWriteToolBase.cs`, `Core/Services/Agent/AgentToolCards.Families.cs` |
| 3 | `find_families_with_parameter`: `families: []` при `familiesTotal: 5` (128 КБ уходило в resultHandle) | inline-лимит 25 типов на семейство + `matchedTypesReturned/Truncated`; `ToolResultBudget.Trim` больше никогда не обнуляет массив и сохраняет `*Total` в digest-конверте | `Tools/Read/FindFamiliesWithParameterTool.cs`, `Core/Models/Agent/ToolResultBudget.cs` |
| 4 | `copy_view_settings(sections=["visibility"])` с вида под шаблоном давал `hiddenCategoryIds: []` («показать всё») | fail-closed: `source_controlled_by_template` с рецептом «копируйте шаблон / правьте шаблон» | `Tools/Write/CopyViewSettingsTool.cs` |
| 5 | `create_view_filter`: `success=false`, но фильтр уже создан | если ни один вид фильтр не принял — фильтр удаляется в той же транзакции (`filterRolledBack`), результат «ничего не изменено»; если часть видов применилась — `success=true` + `failed[]` | `Tools/Write/CreateViewFilterTool.cs` |
| 6 | Подозрение на пересечение пространства `planDigest` и `planId` | проверено: `PlanId` — Guid, digest — SHA-256 (64 hex); в ответы добавлен `planDigestAlgorithm: "sha256"` для однозначности | `Services/Tools/ExecutePlanTool.cs` |
| 7 | Мелочи | `set_parameters`: для безразмерного параметра сообщение сначала предлагает `unit="raw"`; `rename_views` при `all_items_failed` выводит первый `errorCode/Reason` в message; пустые reserved-паки больше не рекламируются (`AgentToolPacks.Populated`), `load_tool_pack` отвечает `pack_reserved_empty` | `AgentParameterWriteHelper.cs`, `AgentWriteToolBase.cs`, `AgentToolPacks.cs`, `FindToolsTool.cs`, `LoadToolPackTool.cs` |

### 1.1 Второй прогон тестировщика (2026-11-19)

Подтверждено: №1, 3, 4, 6, 7a исправлены; №2 — частично (сюрприз убран, но совет был
неверным); №5 — исправлен (откат работает), но нашёлся регресс A.

| # | Дефект | Фикс |
|---|---|---|
| A | `create_view_filter` (dryRun=false) на шаблонном виде падал `InvalidObjectException`: после `doc.Delete(filterId)` код читал `filter.Name` | имя/id/uniqueId кэшируются до цикла; откат удаляет фильтр и чистит change-item только при успешном Delete; при частичном успехе — `success=true` + `failed[]`, иначе actionable `view_controlled_by_template` |
| B | `familyIdNote` советовал `familyUniqueId`, но uniqueId тоже меняется при пересоздании | note и все сообщения `family_not_found` теперь говорят: стабилен только `familyName`; `familyId` и `uniqueId` меняются на load-back |
| C | секция `views` считала и созданный фильтр («2 changed» на один вид) | отдельные счётчики `viewChanged/viewSkipped/viewFailed`, считают только виды |
| D | `rename_views` при дубликате отдавал `all_items_failed` сверху | если все failed-элементы имеют один и тот же `errorCode`, он поднимается наверх (`duplicate_name`), иначе остаётся `all_items_failed` |
| E | `find_families_with_parameter` тяжёлый: полный `matchedTypes` (40 типоразмеров) | параметр `maxMatchedTypes` (default 10, max 50) + `matchedTypesReturned/Truncated`; описание инструмента обновлено |

### 1.2 Третий прогон тестировщика (2026-11-19, каталог 52)

P0 — фильтры:

| # | Дефект | Фикс |
|---|---|---|
| A | `get_filter_details(includeAppliedViews=true)` → `tool_failed`: `InvalidOperationException: The view type does not support Visibility/Graphics Overriddes`. Ломалось всё, что строит карту применения (`list_filters` — всегда, `delete_filter` — без флага) | `View.GetFilters()` бросает на расписаниях, легендах, листах и спецификациях колонн. `AgentFilterUsage.FiltersOf` глушит исключение на каждом виде, `SupportsFilters` использует `View.AreGraphicsOverridesAllowed()` (есть с Revit 2019) |
| B | `list_filters(viewId=…)` не сужал итерацию | `AgentFilterUsage.Map(doc, ct, onlyViewId)` сканирует ровно один вид; для вида без V/G — actionable `unsupported_view_type` вместо падения |
| C | Нельзя было удалить фильтр: `list_filters`/`delete_filter` падали на карте видов | то же; плюс `includeAppliedViews` в `list_filters` и `delete_filter` (по умолчанию true), при `false` — `appliedViewCount: "notComputed"` (и в `get_filter_details`) |
| D | `create_view_filter` на шаблонном виде терял причину (`tool_failed`/`InvalidObjectException`, `failed[]: []`) | виды валидируются ДО создания фильтра; если пригодных нет — фильтр не создаётся, `failed[]` содержит `view_controlled_by_template` (или общий код при одинаковой причине); в `AgentWriteToolBase` добавлен safety-net: неожиданное исключение сохраняет `failed[]` и откатывает транзакцию |
| E | `set_filter_rules` необратим, но старый rule tree не отдавал | `previousRules` (parameterId + parameterName + op + value, полное дерево and/or), `previousCategories`, `previousCategoryIds`; `AgentFilterRuleDescriber` резолвит имя параметра (элемент или `LabelUtils` для built-in) |
| F | `remove_family_parameter("Комментарии")` → `wouldRemove: true` вместо отказа built-in | `AgentFamilyReader.IsBuiltIn` (`InternalDefinition.BuiltInParameter != INVALID`); в dry-run удаление реально выполняется в откатываемой транзакции и `ArgumentException` превращается в `builtin_not_removable`; `get_family_parameters` отдаёт `isBuiltIn` |
| G | секция `views` в `create_view_filter`: «2 changed» на один вид | `viewsChanged/viewsSkipped/viewsFailed` + `viewCounters{total,changed,skipped,failed}`; созданный фильтр считается в секции `filter`, не в видах |

P2:

| # | Дефект | Фикс |
|---|---|---|
| H | `load_tool_pack`: `totalTools 14 + remainingAvailable 26 = 40` при каталоге 52 | `catalogTotal = coreTools + loadedTools + inRequestTools + remainingAvailable` + поле `counting` с самой формулой; счётчики считаются по каталогу, не по `LoadedToolNames` |
| I | каждая загрузка пака требовала `find_tools`, чтобы узнать сигнатуры | `load_tool_pack` возвращает `schemas[]` (`name/description/inputSchema`) для загруженных инструментов, до 8, `schemasTruncated` |
| J | `get_filter_details()` без аргументов: «No view filter named '?' exists» | единое сообщение «Pass filterId or filterName…» |
| K | `rename_views`: `all_items_failed` сверху при `duplicate_name` внутри | доминирующий `errorCode` (≥60 % падений) поднимается наверх; добавлен `errorCodes[{code,count}]` |
| L | `find_families_with_parameter`: 40 `matchedTypes` у двери при `maxMatchedTypes=10` | лимит применялся к одному типу, а не к семейству; теперь к семейству + `compact=true` → `matchedTypeNames` вместо полных строк |
| M | Объекты «исчезали» между раундами (фильтр, familyId), агент не отличал «нет объекта» от «модель откатили» | каждый результат несёт `documentVersion` (`DocumentVersion` GUID + число сохранений); исполнитель помнит предыдущее значение и для `*_not_found` с изменившейся версией добавляет подсказку «id устарел, перечитайте модель» |

### 1.2.1 Подтверждение тестировщиком (2026-11-19, реальная модель)

Все P0/P1 и почти все P2 подтверждены на рабочих объектах: `list_filters` → 47 фильтров,
`get_filter_details(74758)` → полное дерево правил и 31 вид без исключений,
`list_filters(viewId=72822)` → `narrowedByView:true`, 25 фильтров, `delete_filter` реально
удалён (`removed from 1 view`, `undoSupported:false`), `create_view_filter` на шаблонном виде →
`created:false` + `view_controlled_by_template` сверху и в `failed[]`, `set_filter_rules` →
`previousRules`, `viewCounters {1,1,0,0}`, `load_tool_pack` 10+8+4+31=53, `rename_views` →
`duplicate_name` + `errorCodes`, `compact:true` → `matchedTypeNames`.

**P1-4 (built-in) — ложная тревога:** «Комментарии» и «Диаметр_Комментарии» в семействе
тестировщика оказались пользовательскими параметрами (`isBuiltIn:false`, spec yesno), так что
`wouldRemove:true` было корректным. Поле `isBuiltIn` остаётся полезным как предполётная проверка.

Три оставшиеся мелочи закрыты в этом же проходе:

| # | Дефект | Фикс |
|---|---|---|
| N | `get_filter_details.appliedViews` без пагинации (на 70 видах тяжёлый ответ) | `appliedViewsLimit` (default 100, max 500) и `appliedViewsOffset`; в ответе `appliedViewsTotal/Returned/NextOffset/Truncated` |
| O | `documentVersion` не связан с ошибками `*_not_found` | текст ошибки содержит `documentVersion` и объяснение «объект отсутствует в текущем состоянии; если он был раньше — модель откатили/переоткрыли/изменили, перечитайте list_*»; исполнитель по-прежнему добавляет «X → Y», когда версия менялась между вызовами |
| P | `create_view_filter` при `atomic:false` не создавал фильтр, если ни один вид его не принимает — выглядело как недокументированный all-or-nothing | это защита от orphan-фильтра, а не атомарность: описание инструмента и сообщение теперь явно говорят «фильтр создаётся только когда хотя бы один вид его принимает»; в ответе добавлен `atomic` |

## 2. Bypass и decision-модель (решение владельца)

- **Bypass = «Без подтверждений».** В режиме `AutoApproveSession` диалогов нет вообще:
  ни для destructive, ни для `discard_change_set`, ни для батчей >200, ни для вложенных
  планов. Риски взяты владельцем; каждая запись по-прежнему создаёт change set, откат —
  `undo_last`.
- **Decision-модель больше не оценивает риск.** `DecisionsActionRiskGate` и модели
  `IActionRiskGate`/`AgentActionRiskRequest`/`AgentRiskDecision` удалены. Decision-модель
  используется только для поиска инструментов: `find_tools` и динамический стартовый
  набор (`IDynamicToolRanker`).
- UI: подписи переименованы — «Без подтверждений», поля настройки «Поиск инструментов:
  провайдер/модель/API-ключ».

## 3. Очередь улучшений (не сделано)

1. **Стабильный ключ семейства.** `familyId` и `familyUniqueId` пересоздаются на
   `LoadFamily` после `EditFamily` — стабилен только `familyName`. Улучшение: alias-кэш
   «старый id/uniqueId → familyName» на процесс, чтобы цепочки по старому ключу не
   рвались; либо плейсхолдеры `${step1.familyName}` в планах.
2. **Пакет `families` в планах.** Поддержать плейсхолдеры вида `${step1.familyUniqueId}`
  в аргументах зависимых шагов — сейчас агент вынужден перечитывать семейство.
3. **`apply_view_template(overwriteTemplate=true)`** — выработать политику: dry-run должен
  явно помечать, что замена шаблона снимет локальные overrides (сейчас это видно только
  по diff).
4. **Пустые паки.** Решить судьбу reserved-паков: реализовать (`worksets`, `links`,
  `mep`, `annotations`, `audit`) или удалить из `AgentToolPacks`, пока не начаты.
5. **Большие батчи.** Добавить в `set_parameters`/`remap_parameter_values` явный
  прогресс/лимит и рекомендацию дробить >200 элементов; после снятия guard'а в bypass
  это единственная защита от «тихого» зависания на большом объёме.
6. **Fidelity guard для формул.** Если формула меняет значение параметра, `expectedChanges`
  в плане может не сойтись; добавить в preview предупреждение «formula-driven parameter».
7. **`copy_view_settings`**: добавить секцию `template` в `all` по умолчанию? Сейчас `all`
   включает `template` первым — проверить на виде под шаблоном с `overwriteTemplate=false`.
8. **Тестовая фикстура стресс-теста**: оформить сценарий (42 тула, 21 write) как opt-in
   integration/live набор, чтобы регрессии ловились автоматически.
9. **Запрошенные, но отсутствующие операции** (фильтры, удаления, параметры вида,
   листы/спецификации, проектные общие параметры, bulk-by-query): полный
   дедуплицированный список — `docs/agent/archive/AGENT-FEEDBACK.md`, «Внешний прогон 5».
10. **Карантин/токен подтверждения для destructive.** `delete_views`, `delete_sheets`,
    `delete_family_types`, `delete_filter` и `set_filter_rules` не откатываются
    `undo_last` (Revit не восстанавливает удалённое). Варианты: (а) «карантин» —
    перед удалением `SaveAs` копии/экспорт списка имён + токен; (б) обязательный
    `confirmNames[]` при `dryRun=false`, сверяемый с dry-run перечнем (защита от
    удаления после смены модели); (в) собственный слой отмены — хранить снимок
    удаляемых элементов в ExtensibleStorage (дорого). Сейчас честно пишем
    `undoSupported=false` и требуем dry-run.
11. **Персистентность загруженных паков.** Сейчас `LoadedToolNames` только в памяти:
    рестарт Revit или смена чата сбрасывает набор — это совпадает с Claude Code/Codex
    (deferred tools, новая сессия = новый поиск). Решения A/B/C (per-conversation
    снапшот в SQLite, восстановление по последнему ходу, «горячий набор» из
    истории) разобраны в памяти `agent-tool-set-request-cap`; выбрать один и
    реализовать вместе с H6, когда каталог вырастет.
12. **Тесты на копии файла.** Для деструктивных сценариев агенту нужен дешёвый
    способ: `copy_document`/`save_as_copy` (или инструкция «откройте копию») и
    маркер в ответе, что запись идёт в копию. Пока тестировщик проверяет только
    dry-run именно из-за необратимости.

## 4. Крупные фичи, переданные в план

- **H8 bulk/cyclic job engine** (см. `harness-ideal-plan.md`): job spec
  `{select, action, batchSize, dryRun, onError}` → jobId; нужен для задач
  «переименуй 500 видов», «разложи параметры по 5000 элементам».
- **H1c View configuration**: именованные crop/Scope Box области, шаблоны, фильтры —
  расширить `configure_views` до декларативного конфига.
- **H1d Family editing**: довести до конца стабильные ключи, shared parameters через
  `ExternalDefinition`, формулы read-only, unit-конверсию, закрытие family-документа
  при ошибке (часть уже сделана).
- **H6 vector retrieval**: decision-модель надёжна до ~100–200 кандидатов; при росте
  каталога до 100+ — гибридный retrieval (BM25 ∪ vector → RRF), decision как reranker.
- **Ветка «сначала семейства»** (решение владельца): Ф1 read-only семейства →
  Ф2 write (`add_family_parameter`/`map_family_parameters`/`save_family`) → stretch
  листы/спецификации → stretch фильтры/overrides.

## 5. Как перепроверить

1. Собрать все конфигурации: `powershell -NoProfile -ExecutionPolicy Bypass -File tools\build-all-configs.ps1`.
2. Юнит-тесты: `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25`.
3. Ручной/агентный стресс-сценарий: 42 тула, 21 write, ветки ошибок из §1 — по логу
   `smartcon.log` проверить `Agent v2 tool catalog`, `find_tools`, `create_view_filter`,
   `copy_view_settings`, `set_family_parameter_values` (ожидается `formula_not_allowed`).