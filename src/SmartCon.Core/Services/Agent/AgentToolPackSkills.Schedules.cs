namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> SchedulesSkills() =>
        new[]
        {
            ("schedules", Schedules),
            ("filters", Filters),
            ("sheets", Sheets)
        };

    private const string Schedules = @"## Пак schedules: ведомости и спецификации

### Карта тулов
- `list_schedules` — список: `categories`, `nameContains`, `includeTemplates`, `fields`.
- `get_schedule_data` — строки: `scheduleId|scheduleName`, `limit` ≤500, `offset`.
- `get_schedule_definition` — структура: поля, сортировка/группировка, фильтры.

### Кухня Revit
- `ViewSchedule` — это вид, но **не** `ViewSheet`: он не имеет вьюпорта, на лист ставится `ScheduleSheetInstance`.
- `ViewSchedule.CreateSchedule(doc, categoryId)` создаёт пустое расписание без полей; `ElementId.InvalidElementId` вместо категории — мультикатегорийное; key schedule и material takeoff создаются отдельными фабриками. Всё — внутри транзакции.
- Копия существующего расписания со всей структурой — `View.Duplicate(ViewDuplicateOption)`, а не «создать и настроить заново».
- `GetSchedulableFields()` отдаёт только допустимые не-вычисляемые поля; `AddField` всегда добавляет в конец (порядок меняется `SetFieldOrder`), повторное поле — `ArgumentException`.
- `ScheduleField.IsHidden=true` скрывает колонку, но поле продолжает работать в фильтрах, сортировке и группировке. Служебные поля (уровень, маркер) прячут, а не удаляют.
- Фильтры расписания объединяются **только по AND**, лимит — 8. Подстрочные фильтры (`contains`, `beginswith`, `endswith`) применимы только к строковым полям: `CanFilterBySubstring`.
- Изменённый объект `ScheduleFilter` не применяется сам — нужен `SetFilter(index, filter)`.
- Данные тела читаются `GetTableData()` → `GetCellText(row, col)`; полный обход большого расписания — секунды, поэтому `limit/offset` в наших тулах обязательны.
- Группировка: при `IsItemized=false` строки с одинаковыми группирующими значениями сливаются; итоги — `ShowFooter*`.

### Инварианты
- «Покажи спецификацию» — сначала `get_schedule_definition`, потом `get_schedule_data` с
  `limit`: без структуры непонятно, что за колонки.
- Значения в ячейках — отформатированный текст как в ведомости (с единицами); типизированных
  чисел в ответе нет. Для вычислений берите числа из параметров элементов (`query_elements`).
- Структуру ведомости (поля, фильтры, сортировку) тулы только **читают** — изменить её нельзя;
  если пользователь просит перестроить спецификацию, скажите об ограничении прямо.
- `scheduleName` ищется по точному имени; несколько совпадений → `ambiguous_schedule`,
  шаблон → `schedule_is_template`. Надёжнее брать `scheduleId` из `list_schedules`.
- Внутренние (keynote) и titleblock-revision расписания скрыты по умолчанию
  (`includeInternal`/`includeRevision=false`).
- Расписание на листе ищется через `get_sheet_content`, а не по номеру листа в параметре.

### Рецепты
1. «Что в спецификации дверей?»: `list_schedules(nameContains:""Двери"")` → `get_schedule_definition` → `get_schedule_data(limit=50)`.
2. «Сколько строк в ведомости?»: `get_schedule_data(limit=1)` → `total`/`rowCount`.
3. «Где эта спецификация стоит?»: `list_schedules(fields=[name,sheetNumbers])`.

### Что нельзя
- Считать, что спецификация — обычный вид: у неё нет подрезки/диапазона, на лист она ставится иначе.
- Читать тысячи строк без `limit`: ответ придёт обрезанным и с `hint`.
- Ожидать OR в фильтрах расписания: только AND, до 8 условий.
";

    private const string Filters = @"## Пак filters: фильтры видов

Проектный фильтр (`ParameterFilterElement`) — это именованный набор категорий и правил,
независимый от видов. К видам он подключается через `View.AddFilter`, а enabled/visibility/
overrides хранятся **в каждом виде отдельно**.

### Карта тулов
- `list_filters` — список с применёнными видами и правилами.
- `get_filter_details` — фильтр: категории, правила, `filterEnabled`, `visible`, `mode`, overrides.
- `list_filterable_parameters` — pre-flight: какие параметры фильтруемы для категорий.
- `create_view_filter` — создать и применить к видам; `visibility: none|hide|show`, `reused`.
- `set_filter_rules` — заменить категории/правила; `undoSupported=false`.
- `rename_filter`, `delete_filter`, `set_filter_overrides`.

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
- `set_filter_overrides` сам фильтр на вид **не добавляет**: если фильтр не назначен виду,
  запись overrides упадёт (`override_apply_failed`). Сначала `create_view_filter`.
- `set_filter_overrides` — delta только для цветов и pattern; `transparency` и `halftone`
  **всегда** берутся из аргументов (по умолчанию 0/false). Вызов только с `color` сбросит
  прозрачность и halftone.
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

### Рецепты
1. Создать, пока не скрывая: `create_view_filter(visibility=""none"")`; включить скрытие —
   тот же фильтр с `visibility=""hide""`.
2. Проверить эффект: `get_filter_details(includeOverrides=true)` → `filterEnabled` + `visible`
   + `mode`; состав — `query_elements(viewId=...)`.
3. «Не вижу элементы»: сначала проверить, не выключен ли фильтр (`filterEnabled=false`),
   потом `visible=false` у фильтра, потом скрытие категории в V/G.

### Что нельзя
- Добавлять категорию без pre-flight: фитинги и арматура часто не имеют параметра системы.
- Считать `SetCategories` безопасным для правил: он их удаляет.
- Создавать дубль фильтра: `create_view_filter` переиспользует фильтр с тем же именем
  и категориями (`reused=true`).
";

    private const string Sheets = @"## Пак sheets: листы и размещение видов

### Карта тулов
- `list_sheets` — листы: `numberContains`, `nameContains`, `excludePlaceholders`, `fields`.
  Возвращает `widthMm/heightMm`, `viewCount`, `scheduleCount` и `visualDuplicates[]` —
  номера, которые выглядят одинаково, но различаются невидимыми символами.
- `get_sheet_content` — состав листа: виды (с координатами вьюпорта в мм) и расписания
  (`instanceId`, координаты); `sheetIds` — пакетно, ≤50.
- `delete_sheets` — удаление листов, `sheetIds` ≤50.

### Кухня Revit
- Лист (`ViewSheet`) — вид, но не размещается на листах. `ViewSheet.Create` требует тип
  рамки (`FamilySymbol` категории `OST_TitleBlocks`); лист без рамки — `InvalidElementId`.
- Размер листа хранится **не на листе**, а в параметрах экземпляра рамки `SHEET_WIDTH`/
  `SHEET_HEIGHT`; id рамки — `ViewSheet.SheetTitleBlockId`.
- Обычные виды ставятся на лист через `Viewport.Create`, расписания — через
  `ScheduleSheetInstance.Create` (у расписания нет вьюпорта).
- `Viewport.Create` принимает координаты листа в дюймах, `[0,0]` — левый нижний угол; после
  создания нужен `Regenerate`, затем позиционирование `GetBoxOutline`/`SetBoxCenter`.
- Один вид нельзя разместить на двух листах (`Viewport.CanAddViewToSheet`): для второго
  листа вид дублируют (`View.Duplicate`: WithDetailing / AsDependent). Исключения — легенды
  и расписания.
- «Снять вид с листа» = удалить `Viewport`; сам вид остаётся в проекте. Обратное неверно:
  удаление вида удаляет его размещения.
- `ViewSheet.GetAllViewports()` **не показывает расписания** — их надо собирать отдельно
  (`ScheduleSheetInstance`). «Лист пуст» по одному GetAllViewports — неверный вывод.
- `SheetNumber` уникален (в 2026 — в пределах коллекции листов); дубль бросает исключение.

### Инварианты
- Прежде чем удалять лист, прочитайте `get_sheet_content`: размещённые виды останутся в
  проекте (удаляются только размещения), но пользователь должен это знать.
- `delete_sheets` — необратимо (`undoSupported=false`); всегда сначала `dryRun=true`.
- Для «поставь вид на лист» проверяйте `isOnSheet` (`list_views`): занятый вид на второй
  лист не встанет.

### Рецепты
1. «Что на листе А-101?»: `list_sheets(numberContains:""А-101"")` → `get_sheet_content(sheetId)`.
2. «Найди листы без видов»: `list_sheets(fields=[sheetId,number,viewCount,scheduleCount])`
   → кандидаты с нулями → уточнить `get_sheet_content`.
3. «Удали пустые листы»: список → `delete_sheets(dryRun=true)` → подтверждение → apply.

### Что нельзя
- Считать `viewCount=0` доказательством пустого листа: расписания считаются отдельно.
- Удалять вид, чтобы освободить его для другого листа: удаляйте вьюпорт.
- Ставить один вид на два листа без дублирования.
";
}