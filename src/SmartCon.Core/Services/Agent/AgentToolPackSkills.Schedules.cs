namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> SchedulesSkills() =>
        new[]
        {
            ("schedules", Schedules),
            ("filters", Filters)
        };

    private const string Schedules = @"## Пак schedules: ведомости и спецификации

### Карта тулов
- `list_schedules` — список: `categories`, `nameContains`, `includeTemplates`, `fields`.
  `fieldCount` = все поля, `fields[]` — видимые; пустые — `emptySchedules`; дубли по
  определению (поля+фильтры+сортировка) — `duplicateGroups` (одинаковые колонки с разными
  фильтрами/сортировкой — не дубли).
- `get_schedule_data` — строки: `scheduleId|scheduleName`, `limit` ≤500, `offset`,
  `includeHidden` (скрытые колонки с выборками значений). 0 строк под фильтром со
  значением → предупреждение «проверь значение фильтра».
- `get_schedule_definition` — структура: поля (у общих — `guid` + `parameterName`, у built-in —
  имя; у каждого `heading`, `columnWidthMm`, `totalsDisplay`, `hidden`, `scope` — область для
  write-тулов), сортировка/группировка с флагами, фильтры (фазные — `phaseFilters`,
  ссылки — `valueName`), стиль (`stripedRows`, текстовые стили, `showTitle`) и `placements[]`
  (лист/точка/поворот) + `placedOnSheet`/`sheetNumbers`.
- `create_schedule` — `category` (OST-имя / русское / id / `multicategory` — сквозная по
  категориям, поля только общие), `name`, `fields` (0..15), `itemized`; dry-run id — PROVISIONAL.
- `add_schedule_field` / `remove_schedule_field` — поля (1..15) по отображаемым именам; undo
  возвращает.
- `set_schedule_field_order` — порядок колонок: ПОЛНЫЙ список имён в новом порядке.
- `set_schedule_field_visibility` — скрыть/показать: `fields[{name,hidden}]`.
- `set_schedule_totals` — итоги: `fields[{name,mode}]` (standard|totals|min|max|minmax) +
  `grandTotal`/`grandTotalCount`/`grandTotalTitle` (grandTotal:false снимает строку и зависимые
  флаги без ошибки).
- `set_schedule_filter` — фильтры (0..8, только AND; `[]` — очистить); dry-run показывает
  `estimatedRowsAfter` + `estimatedSampleNames` (до 5).
- `set_schedule_sort` — сортировка (0..4) и `itemized`; флаги группы
  `showHeader/showFooter/showBlankLine`.
- `set_schedule_field_format` — заголовки и ширины: `fields[{field,heading?,widthMm?}]` (мм;
  вычисляемым колонкам ширины недоступны — `field_not_formatable`).
- `set_schedule_style` — `stripedRows`/`useStripedRowsOnSheets` (2021+) и текстовые стили
  body/header/title.
- `rename_schedule` — переименовать (шаг архива «… (Архив)»); имя должно быть свободно.
- `place_schedule_on_sheet` — координаты ЛЕВОГО ВЕРХНЕГО угла в мм, `rotation: none|cw90|ccw90`.
- `move_schedule_on_sheet` — переместить (`instanceId|лист+спецификация`, мм) и/или повернуть.
- `remove_schedule_from_sheet` — снять вынос (спецификация остаётся); undo возвращает точку и
  поворот.
- `clone_schedule` — точный аналог ведомости на других параметрах: `fieldMapping[{sourceField,
  targetGuid|targetField}]` (guid — из `get_schedule_definition`), переносит порядок/заголовки/
  ширины/скрытость, фильтры, сортировку с флагами, итоги и стиль; колонки без аналога —
  `skippedFields`. Составные колонки (CombinedParameter) адресуются по имени (GUID нет) и
  клонируются как обычные; фильтр/сортировка по замапленной колонке переезжают на цель.
  Одна спека за вызов; `created{}` в dry-run PROVISIONAL.
- `delete_schedules` — удаление: `scheduleIds` и/или `scheduleNames` (суммарно ≤50),
  `confirmNames`. Удаление спецификации с листа убирает только `ScheduleSheetInstance`;
  удаление самой спецификации — `delete_schedules`.

### Кухня Revit
- `ViewSchedule` — вид, но не `ViewSheet`: без вьюпорта, на лист ставится `ScheduleSheetInstance`.
- `CreateSchedule(doc, categoryId)` — пустое; InvalidElementId — мультикатегорийное;
  key/material — отдельные фабрики; в транзакции.
- Копия расписания — `View.Duplicate(ViewDuplicateOption)`, не «создать заново».
- `GetSchedulableFields()` — допустимые не-вычисляемые поля; `AddField` в конец (порядок —
  `SetFieldOrder`), повтор — `ArgumentException`.
- `IsHidden=true` скрывает колонку, но поле работает в фильтрах/сортировке/группировке;
  служебные поля (уровень, маркер) прячут, а не удаляют.
- Итоги — `DisplayType` (Totals/Min/Max, `CanTotal()`); «Итого» — `ShowGrandTotal*` (пока строка
  выключена, зависимые флаги не ставятся). Split на листе — только UI.
- Фильтры — только AND, лимит 8; подстрочные (`contains`/`beginswith`/`endswith`) — только строковым полям (`CanFilterBySubstring`).
- Изменённый `ScheduleFilter` не применяется сам — нужен `SetFilter(index, filter)`.
- Тело читается `GetTableData()` → `GetCellText(row, col)`; обход большого расписания — секунды: `limit/offset` обязательны.
- Группировка: `IsItemized=false` сливает одинаковые строки; итоги — `ShowFooter*`.
- Фильтр/сортировка — только по колонкам спеки (`ScheduleFieldId`, не имя параметра):
  нет колонки → `field_not_in_schedule` → сначала `add_schedule_field`.
- `ScheduleSheetInstance.Create(doc, sheetId, scheduleId, XYZ)` — точка это **левый верхний
  угол** таблицы (не центр, как у Viewport). Один вынос на лист: дубль → no_changes.
- Поворот выноса — `instance.Rotation` (ViewportRotation): только `none`/`cw90`/`ccw90`
  (±90°); 180° API не умеет. Полосатые строки и ширины колонок — API 2021+;
  на старых — честная ошибка.

### Инварианты
- Критичное = на листе: `placedOnSheet`/`sheetNumbers` отличают отчётность от черновиков;
  `audit_fill_rate` (пак elements) считает только ЛИСТОВЫЕ спеки.
- «Покажи спецификацию» — `get_schedule_definition`, потом `get_schedule_data` с `limit`.
- Значения в ячейках — отформатированный текст; числа — из параметров (`query_elements`).
- «Сделай спецификацию по X и помести на лист»: `create_schedule` → `add_schedule_field` (если
  полей мало) → `set_schedule_filter` → `set_schedule_sort` → `place_schedule_on_sheet`; каждый
  шаг dry-run → apply, после apply — read-back.
- Фильтров не больше 8 (`filter_limit_exceeded`), условия только AND — для «ИЛИ» отдельные
  спецификации.
- Паттерн правки наборов (фильтры/порядок колонок): `get_schedule_definition` → измени →
  передай ПОЛНЫЙ набор — `set_schedule_filter`/`set_schedule_field_order` заменяют целиком.
- `scheduleName` — точное имя; несколько совпадений → `ambiguous_schedule` (берите id),
  шаблон → `schedule_is_template`; надёжнее `scheduleId` из `list_schedules`.
- Внутренние (keynote) и titleblock-revision спеки скрыты по умолчанию
  (`includeInternal`/`includeRevision=false`); `delete_schedules` их не удаляет
  (`schedule_not_deletable`) — ими управляет Revit.
- `delete_schedules` необратим (`undoSupported=false`): `dryRun=true` вернёт `names[]`, apply —
  с `confirmNames` СВЕЖЕГО dry-run; `confirm_names_required`/`confirm_names_mismatch` = модель
  изменилась → повтори dry-run и передай свежий names[]. Остальные write-тулы откатываются
  `undo_last`.
- Лист-вынос ищется через `get_sheet_content`, не по номеру листа в параметре.

### Рецепты
1. «Что в спецификации дверей?»: `list_schedules(nameContains:""Двери"")` → `get_schedule_definition` → `get_schedule_data(limit=50)`.
2. «Сколько строк в ведомости?»: `get_schedule_data(limit=1)` → `total`/`rowCount`.
3. «Где эта спецификация стоит?»: `list_schedules(fields=[name,sheetNumbers])`.
4. «Сделай спецификацию по трубам и помести на лист»: `list_categories` →
   `create_schedule(category=""OST_Pipes"", name=""Спецификация труб"", fields=[...])` →
   `set_schedule_filter` → `set_schedule_sort` → `create_sheet`/`list_sheets` →
   `place_schedule_on_sheet(pointMmX=..., pointMmY=...)` → `get_sheet_content` (проверка).
5. «Удали тестовые спецификации»: `list_schedules(nameContains:""ТЕСТ"")` → `delete_schedules(dryRun=true)` → подтверждение → `delete_schedules(dryRun=false, confirmNames=names[])`.
6. «Переведи ведомость / оборудование на ФОП заказчика» — сначала развилка уровня:
   - **по умолчанию — семейство** (данные переживают перезагрузку и новые типы):
     `list_shared_parameters` → `start_job(action:""map_family_parameters"")` с
     `createTarget:true` (mode formula — вычисляемые, mode values — текстовые) → новая
     спецификация из полей-семейных общих параметров ФОП, старая в архив.
   - **проектный путь — только по явному запросу**: `add_project_shared_parameter` +
     `set_parameters`/`remap_parameter_values`; предупреди: значения статичны, новый
     типовой каталог их не обновит, формулы семейства считают по-своему.
   - Точный аналог готовой ведомости: `get_schedule_definition` (guid полей и `placements[]`)
     → `clone_schedule(sourceScheduleId, name=имя старой, fieldMapping: старое → targetGuid)`
     → `rename_schedule` старой в «… (Архив)» → `remove_schedule_from_sheet` →
     `place_schedule_on_sheet` новой на её `xMm/yMm` (при необходимости `rotation`);
     `skippedFields` покажите списком.
   - Оба уровня возможны — два коротких вопроса (где ФОП; семейство или проект), второй не
     решай молча.

### Кухня: порядок групп
«Инженерный порядок» без явного списка — шкала значимости: 10 Оборудование →
20 Арматура → 30 Трубопроводы → 40 Фитинги → 50 Материалы (шаг 10; массивнее — выше).

### Что нельзя
- Считать спецификацию обычным видом: нет подрезки/диапазона.
- Переводить данные на параметры ПРОЕКТА по умолчанию: сначала уровень семейства (см.
  рецепт 6) — проектные привязки замораживают значения и отключают формулы семейства.
- Читать тысячи строк без `limit`: ответ придёт обрезанным и с `hint`.
- Ожидать OR в фильтрах расписания: только AND, до 8 условий.
- Фильтровать/сортировать по полю, которого нет в колонках: сначала `add_schedule_field`.
- Считать точку `place_schedule_on_sheet` центром таблицы: это левый верхний угол.
- Удалять спецификации через `delete_views` — он откажет с `use_delete_schedules`.
";

    private const string Filters = @"## Пак filters: фильтры видов

Проектный фильтр (`ParameterFilterElement`) — это именованный набор категорий и правил,
независимый от видов. К видам он подключается через `View.AddFilter`, а enabled/visibility/
overrides хранятся **в каждом виде отдельно**.

### Карта тулов
- `list_filters` — список с применёнными видами и правилами; duplicateGroups показывают
  группы дубликатов с `appliedViewCount` каждой копии (кто живой).
- `get_filter_details` — фильтр: категории, правила, `filterEnabled`, `visible`, `mode`, overrides.
- `list_filterable_parameters` — pre-flight: какие параметры фильтруемы для категорий.
- `create_view_filter` — создать и применить к видам; `visibility: none|hide|show`, `reused`.
  Дефолт `none` = фильтр применён, но ВЫКЛЮЧЕН (элементы видны) — результат предупреждает
  `appliedButDisabled`; dry-run даёт `readBack[].previewFilterId`/`filterIdPreview=true`
  (id выброшенного probe-элемента), после commit вид перепроверяется (`filter_not_applied`,
  если фильтр снял шаблон вида).
- `set_filter_rules` — заменить категории/правила; `undoSupported=false`.
- `delete_filter` — удаление одного фильтра или партии (`filterIds`/`filterNames`, до 100):
  батч = 2 вызова (dryRun → apply с `confirmNames` = один перечень на всю партию);
  `deadOnly=true` — гард: применённый фильтр в списке → отказ `filter_in_use` с видами.
  Перед удалением каждого фильтра — карантин-снимок в agent.db (90 дней).
- `merge_duplicate_filters` — слить дубль в канонический: во всех видах дубль заменяется
  на канонический, затем дубль удаляется (с карантином); батч = 2 вызова
  (dryRun-план видов → apply с `confirmNames`).
- `rename_filter`, `set_filter_overrides`.

### Две галочки Revit UI (не путать!)
- **«Фильтр включён»** = `View.SetIsFilterEnabled`. Выключен → фильтр не действует, overrides
  сохраняются, элементы видны. Это **не** `RemoveFilter`.
- **«Видимость»** = `View.SetFilterVisibility`. Снята → совпавшие элементы скрыты.
- `hide` = включён + «Видимость» снята; `show` = включён + «Видимость» стоит; `none` =
  фильтр **выключен** (`SetIsFilterEnabled(false)`) → элементы снова видны.
- Вернуть «как было» — выключить фильтр, не удаляя: `create_view_filter(visibility=""none"")`.
- Приоритет: overrides элемента > фильтры вида > фазы > V/G категории. Внутри фильтров выше
  тот, что выше в списке V/G; порядок задаётся в каждом виде (или шаблоне).

### Кухня Revit
- `SetCategories` **молча удаляет** правила, чьи параметры не применимы к новому набору.
  Поэтому сначала категории, потом правила, и всегда перечитывать `GetElementFilter()`.
- `GetFilterableParametersInCommon(doc, categories)` — **пересечение**, а не объединение:
  мультикатегорийный фильтр видит только общие параметры. Для проверки «параметр подходит
  категории» вызывайте по одной категории.
- Две независимые проверки: структура (`ElementFilterIsAcceptableForParameterFilterElement`)
  и применимость параметров (`AllRuleParametersApplicable` / `GetInapplicableParameters`).
  Структурной проверки недостаточно.
- `SetElementFilter` возвращает true только при реальном изменении; `GetElementFilter()`
  возвращает null, если остались только категории.
- Shared-параметр, пришедший из семейства и не созданный как проектный, в правилах фильтра
  **не работает** by design; правило «параметр существует» — отдельный тип.
- Правило по type-параметру срабатывает на экземпляры (Revit читает значение с типа) — это
  источник «ложных» попаданий; чтобы требовать именно instance-значение, нужно правило
  «параметр существует».
- Строковые сравнения в Revit **всегда регистронезависимы**: `caseSensitive` игнорируется
  и с 2023 объявлен obsolete. Регистрозависимый фильтр через API невозможен.
- Числа в правилах — во внутренних единицах (футы); `Epsilon` — допуск в тех же единицах.
- `list_filterable_parameters` отдаёт **объединение** параметров по категориям с флагом
  `supportedByAllCategories` (и списком `missingFromCategories`) — фильтровать можно только
  параметр с `true`.
- `create_view_filter` при совпадении имени **и** категорий переиспользует фильтр (`reused=true`)
  и **не применяет переданные `conditions`**: правила остаются прежними. Если имя совпало,
  а категории другие — создаётся новый фильтр с суффиксом `(2)`.
- `set_filter_overrides` сам добавляет фильтр на вид, если он ещё не назначен (Revit:
  «If the filter is not currently applied to the view, this will add the filter with the
  assigned overrides»). Проверяйте `view_controlled_by_template`: шаблон может управлять V/G.
- `set_filter_overrides` — delta для всех ключей, включая `transparency` и `halftone`:
  незаданные поля сохраняют текущие значения. `clear=true` сбрасывает весь набор.
- Много фильтров замедляет вид: Autodesk описывает ускорение с 160 до 2 с после сокращения
  сотен фильтров. Не плодите фильтры там, где хватает V/G категории.

### Инварианты
- Pre-flight обязателен: `list_filterable_parameters(categoryIds=..., parameterName=...)` →
  `supportedByAllCategories=true`. Несовместимость → `parameter_not_supported_for_categories`.
- `dryRun=true` реально применяет изменение в транзакции и откатывает: `validated=true` =
  «Revit принял». Ошибка dry-run = изменение не пройдёт.
- После `success=false` — read-back `get_filter_details` до вывода «ничего не изменилось».
- `set_filter_rules` не откатывается `undo_last`: откат — повторный вызов с `previousRules`
  и `previousCategories`.
- Удаление фильтра и merge дубликатов необратимы (`undoSupported=false`): перед каждым
  удалением тул сам кладёт карантин-снимок (правила+категории+виды+overrides) в agent.db
  на 90 дней — восстановление только ручное, по snapshotId из отчёта. Массовая чистка
  мёртвых фильтров — ОДИН вызов `delete_filter` с массивом (13 фильтров = 2 вызова:
  preview+apply), не поштучно.
- `merge_duplicate_filters` не переносит overrides дубликата на канонический фильтр
  (Revit добавляет фильтр с дефолтными overrides): виды с настроенной графикой попадают
  в `overridesNotTransferred` — переложи через `set_filter_overrides`.

### Рецепты
1. Создать, пока не скрывая: `create_view_filter(visibility=""none"")`; включить скрытие —
   тот же фильтр с `visibility=""hide""`.
2. Проверить эффект: `get_filter_details(includeOverrides=true)` → `filterEnabled` + `visible`
   + `mode`; состав — `query_elements(viewId=...)`.
3. «Не вижу элементы»: сначала проверить, не выключен ли фильтр (`filterEnabled=false`),
   потом `visible=false` у фильтра, потом скрытие категории в V/G.
4. «Почисти мёртвые фильтры»: `list_filters` (мёртвые = `appliedViewCount=0`) →
   `delete_filter(filterNames=[...], dryRun=true)` → подтверждение →
   `delete_filter(filterNames=[...], dryRun=false, confirmNames=names[])`.
5. «Слей дубликаты»: `list_filters` → группа в `duplicateGroups` (живая копия —
   каноническая, у неё `appliedViewCount`>0) → `merge_duplicate_filters(duplicateFilterId,
   canonicalFilterId, dryRun=true)` → `merge_duplicate_filters(..., dryRun=false,
   confirmNames=names[])`.

### Что нельзя
- Добавлять категорию без pre-flight: фитинги и арматура часто не имеют параметра системы.
- Считать `SetCategories` безопасным для правил: он их удаляет.
- Создавать дубль фильтра: `create_view_filter` переиспользует фильтр с тем же именем
  и категориями (`reused=true`).
";

}