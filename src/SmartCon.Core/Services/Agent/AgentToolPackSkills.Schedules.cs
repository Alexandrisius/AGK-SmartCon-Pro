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
  Пустые спецификации — блоком `emptySchedules` (все сразу, не потеряешь при диагностике);
  дубли по одинаковому определению (поля+фильтры+сортировка) — `duplicateGroups`.
- `get_schedule_data` — строки: `scheduleId|scheduleName`, `limit` ≤500, `offset`.
- `get_schedule_definition` — структура: поля (у общих — `guid` + `parameterName`,
  у built-in — имя параметра; у каждого — `heading`, `columnWidthMm`, `totalsDisplay`,
  `hidden`), сортировка/группировка с флагами, фильтры (фазные — в `phaseFilters`
  с именем фазы; фильтры-ссылки — `valueName`), стиль таблицы (`stripedRows`,
  текстовые стили, `showTitle`) и `placements[]` (лист/точка/поворот каждого выноса)
  + `placedOnSheet`/`sheetNumbers` — вынесена ли на лист.
- `create_schedule` — создать: `category` (OST_имя / русское имя / categoryId), `name`
  (уникальное), `fields` (0..15), `itemized`.
- `add_schedule_field` — добавить поля (1..15) по отображаемым именам.
- `remove_schedule_field` — удалить поля (1..15); undo_last возвращает.
- `set_schedule_field_order` — порядок колонок: ПОЛНЫЙ список имён в новом порядке.
- `set_schedule_field_visibility` — скрыть/показать колонки: `fields[{name,hidden}]`.
- `set_schedule_totals` — итоги: `fields[{name,mode}]` (standard|totals|min|max|minmax)
  + `grandTotal`/`grandTotalCount`/`grandTotalTitle`.
- `set_schedule_filter` — заменить фильтры (0..8, только AND; `[]` — очистить). Dry-run
  сразу показывает эффект: `estimatedRowsAfter` + `estimatedSampleNames` (до 5 имён).
- `set_schedule_sort` — заменить сортировку (0..4) и `itemized`; у каждой группы флаги
  `showHeader/showFooter/showBlankLine` (заголовок группы, итоги группы, пустая строка).
- `set_schedule_field_format` — заголовки и ширины колонок: `fields[{field,heading?,widthMm?}]`
  (мм; ширины недоступны вычисляемым колонкам — `field_not_formatable`).
- `set_schedule_style` — стиль таблицы: полосатые строки (`stripedRows`,
  `useStripedRowsOnSheets`, Revit 2021+) и текстовые стили body/header/title.
- `rename_schedule` — переименовать (шаг архива: «… (Архив)»); новое имя должно быть свободно.
- `place_schedule_on_sheet` — разместить на листе: координаты левого верхнего угла в мм,
  опционально `rotation: none|cw90|ccw90` (Revit умеет только ±90°).
- `move_schedule_on_sheet` — переместить размещённую таблицу (`instanceId|лист+спецификация`,
  мм) и/или повернуть (`rotation`).
- `remove_schedule_from_sheet` — снять вынос с листа (спецификация остаётся в проекте);
  undo возвращает вынос с точкой и поворотом.
- `clone_schedule` — точный аналог ведомости на других параметрах (ФОП заказчика):
  `fieldMapping[{sourceField,targetGuid|targetField}]` (guid полей — из `get_schedule_definition`),
  переносит порядок/заголовки/ширины/скрытость колонок, фильтры, сортировку с флагами,
  итоги и стиль; колонки без аналога — в `skippedFields` отчёта. Одна спека за вызов.
- `delete_schedules` — удаление: `scheduleIds` и/или `scheduleNames` (суммарно ≤50),
  `confirmNames`. Удаление спецификации с листа убирает только `ScheduleSheetInstance`;
  удаление самой спецификации — `delete_schedules`.

### Кухня Revit
- `ViewSchedule` — вид, но не `ViewSheet`: без вьюпорта, на лист ставится `ScheduleSheetInstance`.
- `ViewSchedule.CreateSchedule(doc, categoryId)` — пустое, без полей; InvalidElementId — мультикатегорийное; key/material — отдельные фабрики; внутри транзакции.
- Копия существующего расписания со всей структурой — `View.Duplicate(ViewDuplicateOption)`, а не «создать и настроить заново».
- `GetSchedulableFields()` отдаёт только допустимые не-вычисляемые поля; `AddField` всегда добавляет в конец (порядок меняется `SetFieldOrder`), повторное поле — `ArgumentException`.
- `ScheduleField.IsHidden=true` скрывает колонку, но поле продолжает работать в фильтрах, сортировке и группировке. Служебные поля (уровень, маркер) прячут, а не удаляют.
- Итоги колонки — `ScheduleField.DisplayType` (Totals/Min/Max, гвард `CanTotal()`); строка «Итого» — `ShowGrandTotal*` на определении. Разбиение таблицы на части на листе — только UI (`ViewSchedule.Split`, Revit 2022.1+), тула нет.
- Фильтры — только AND, лимит 8; подстрочные (`contains`/`beginswith`/`endswith`) — только строковым полям (`CanFilterBySubstring`).
- Изменённый `ScheduleFilter` не применяется сам — нужен `SetFilter(index, filter)`.
- Тело читается `GetTableData()` → `GetCellText(row, col)`; обход большого расписания — секунды: `limit/offset` обязательны.
- Группировка: при `IsItemized=false` строки с одинаковыми группирующими значениями сливаются; итоги — `ShowFooter*`.
- Фильтровать и сортировать можно только по полям-колонкам спецификации: Revit требует
  `ScheduleFieldId`, а не имя параметра. Нет колонки → `field_not_in_schedule` →
  сначала `add_schedule_field`.
- `ScheduleSheetInstance.Create(doc, sheetId, scheduleId, XYZ)` — точка это **левый верхний
  угол** таблицы (не центр, как у Viewport). `instance.Point` возвращает ту же точку —
  `get_sheet_content` показывает её как `xMm/yMm`. Один ScheduleSheetInstance на лист
  через наш тул: дубль → no_changes (Revit допускает дубли, агент — нет).
- Поворот выноса — `instance.Rotation` (ViewportRotation): только `none`/`cw90`/`ccw90`
  (±90°); 180° API не умеет. Полосатые строки и ширины колонок (мм) — API 2021/2022+,
  на старых версиях тулы отвечают честной ошибкой.

### Инварианты
- Критичное = на листе: `placedOnSheet`/`sheetNumbers` (list_schedules и
  get_schedule_definition) отличают выдаёмую отчётность от черновых спек вне листов;
  обязательные параметры для миграции/аудита (`audit_fill_rate` из пака elements)
  выводятся из полей именно ЛИСТОВЫХ спек, черновые не считаются.
- «Покажи спецификацию» — сначала `get_schedule_definition`, потом `get_schedule_data` с
  `limit`: без структуры непонятно, что за колонки.
- Значения в ячейках — отформатированный текст как в ведомости (с единицами); типизированных
  чисел в ответе нет. Для вычислений берите числа из параметров элементов (`query_elements`).
- «Сделай спецификацию по X и помести на лист» — цепочка: `create_schedule` →
  `add_schedule_field` (если полей мало) → `set_schedule_filter` → `set_schedule_sort` →
  `place_schedule_on_sheet`; каждый шаг dry-run → apply, после apply читайте read-back.
- Фильтров не больше 8 (инвариант Revit, `filter_limit_exceeded`); условия только AND —
  для «ИЛИ» стройте отдельные спецификации.
- Паттерн правки наборов (фильтры/порядок колонок): прочитай `get_schedule_definition` →
  измени → передай ПОЛНЫЙ набор — `set_schedule_filter`/`set_schedule_field_order`
  заменяют его целиком.
- `scheduleName` ищется по точному имени; несколько совпадений → `ambiguous_schedule`
  (с candidates — берите id), шаблон → `schedule_is_template`. Надёжнее брать `scheduleId`
  из `list_schedules`.
- Внутренние (keynote) и titleblock-revision расписания скрыты по умолчанию
  (`includeInternal`/`includeRevision=false`). `delete_schedules` их удалять отказывается
  (`schedule_not_deletable`) — ими управляет Revit.
- `delete_schedules` необратим (`undoSupported=false`): сначала `dryRun=true` (вернёт
  `names[]`), apply — с `confirmNames` = `names[]` СВЕЖЕГО dry-run. Отказ
  `confirm_names_required`/`confirm_names_mismatch` = модель изменилась: повтори dry-run и
  передай его свежий names[]. Остальные write-тулы пака откатываются `undo_last`.
- Расписание на листе ищется через `get_sheet_content`, а не по номеру листа в параметре.

### Рецепты
1. «Что в спецификации дверей?»: `list_schedules(nameContains:""Двери"")` → `get_schedule_definition` → `get_schedule_data(limit=50)`.
2. «Сколько строк в ведомости?»: `get_schedule_data(limit=1)` → `total`/`rowCount`.
3. «Где эта спецификация стоит?»: `list_schedules(fields=[name,sheetNumbers])`.
4. «Сделай спецификацию по трубам и помести на лист»: `list_categories` →
   `create_schedule(category=""OST_Pipes"", name=""Спецификация труб"", fields=[...])` →
   `set_schedule_filter` → `set_schedule_sort` → `create_sheet`/`list_sheets` →
   `place_schedule_on_sheet(pointMmX=..., pointMmY=...)` → `get_sheet_content` (проверка).
5. «Удали тестовые спецификации»: `list_schedules(nameContains:""ТЕСТ"")` → `delete_schedules(dryRun=true)` → подтверждение → `delete_schedules(dryRun=false, confirmNames=names[])`.
6. «Переведи ведомость на ФОП заказчика» (замена на месте): `get_schedule_definition`
   (взять `guid` полей и `placements[]` с точкой выноса) → `clone_schedule(sourceScheduleId,
   name=имя старой, fieldMapping: старое поле → targetGuid нового)` → `rename_schedule`
   старой в «… (Архив)» → `remove_schedule_from_sheet` старой с листа →
   `place_schedule_on_sheet` новой на её точку (та же `xMm/yMm` из placements, при
   необходимости `rotation`). Колонки без аналога — `skippedFields` из отчёта клона,
   показать пользователю списком.

### Что нельзя
- Считать спецификацию обычным видом: нет подрезки/диапазона, на лист ставится иначе.
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