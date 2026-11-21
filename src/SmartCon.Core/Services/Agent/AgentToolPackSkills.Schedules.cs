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
- `get_schedule_data` — строки: `scheduleId|scheduleName`, `limit` ≤500, `offset`.
- `get_schedule_definition` — структура: поля, сортировка/группировка, фильтры.
- `delete_schedules` — удаление: `scheduleIds` и/или `scheduleNames` (суммарно ≤50),
  `confirmNames`. Удаление спецификации с листа убирает только `ScheduleSheetInstance`;
  удаление самой спецификации — `delete_schedules`.

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
  если пользователь просит перестроить спецификацию, скажите об ограничении прямо. Удалить —
  можно (`delete_schedules`).
- `scheduleName` ищется по точному имени; несколько совпадений → `ambiguous_schedule`
  (с candidates — берите id), шаблон → `schedule_is_template`. Надёжнее брать `scheduleId`
  из `list_schedules`.
- Внутренние (keynote) и titleblock-revision расписания скрыты по умолчанию
  (`includeInternal`/`includeRevision=false`). `delete_schedules` их удалять отказывается
  (`schedule_not_deletable`) — ими управляет Revit.
- `delete_schedules` необратим (`undoSupported=false`): сначала `dryRun=true` (вернёт
  `names[]`), apply — с `confirmNames` = `names[]` СВЕЖЕГО dry-run. Отказ
  `confirm_names_required`/`confirm_names_mismatch` = модель изменилась: повтори dry-run и
  передай его свежий names[].
- Расписание на листе ищется через `get_sheet_content`, а не по номеру листа в параметре.

### Рецепты
1. «Что в спецификации дверей?»: `list_schedules(nameContains:""Двери"")` → `get_schedule_definition` → `get_schedule_data(limit=50)`.
2. «Сколько строк в ведомости?»: `get_schedule_data(limit=1)` → `total`/`rowCount`.
3. «Где эта спецификация стоит?»: `list_schedules(fields=[name,sheetNumbers])`.
4. «Удали тестовые спецификации»: `list_schedules(nameContains:""ТЕСТ"")` →
   `delete_schedules(scheduleIds, dryRun=true)` → подтверждение →
   `delete_schedules(scheduleIds, dryRun=false, confirmNames=names[])`.

### Что нельзя
- Считать, что спецификация — обычный вид: у неё нет подрезки/диапазона, на лист она ставится иначе.
- Читать тысячи строк без `limit`: ответ придёт обрезанным и с `hint`.
- Ожидать OR в фильтрах расписания: только AND, до 8 условий.
- Удалять спецификации через `delete_views` — он откажет с `use_delete_schedules`.
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

}