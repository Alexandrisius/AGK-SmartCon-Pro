namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> ViewsSkills() =>
        new[]
        {
            ("views", Views),
            ("view_settings", ViewSettings),
            ("view_edit", ViewEdit),
            ("templates", Templates)
        };

    private const string Views = @"## Пак views: виды

Обзор, поиск, создание, дублирование, папки браузера, переименование и удаление видов.

### Карта тулов
- `list_views` — список видов: `types`, `levelIds`, `nameContains`, `folders`, `fields`, `limit` (≤500).
  Поле `section` карточки = значение проектного параметра «Раздел» (нет параметра → null, без эвристик).
- `get_current_view_info` — активный вид (id, тип, шаблон, масштаб) — старт, если пользователь говорит «этот вид».
- `rename_views` — пакетно `items[{viewId,newName}]`, `atomic`.
- `delete_views` — `viewIds` (≤50), `includeTemplates` (по умолчанию false), `confirmNames`.
- `duplicate_view` — копия одного вида (`viewId|viewName`, `newName`, `option`).
- `create_view` — новый вид (`kind: plan|section|3d|legend`, `name`, план/разрез — `levelId`).
- `create_floor_plans` — пачка планов по уровням (1..200) + НАЗНАЧЕНИЕ шаблона за один вызов:
  `levels[]`, `templateId` (семейство FloorPlan), `namePattern` с `{floor}` (карта `floors` по
  levelId → последнее число в имени уровня → отметка в целых метрах) и `{level}`. Оба шага
  ручной работы (создать + применить шаблон) делает тул; `undo_last` уносит всю пачку.
- `create_views_batch` — разнородная пачка до 100 видов: `items[{kind:""plan""|""3d"",name,levelId?
  (plan),templateId?,viewFamilyTypeId?,scale?}]`, одна транзакция; каждому виду можно сразу
  назначить шаблон (`templateId` → ViewTemplateId).
- `set_browser_folder` — подпапка браузера: `viewIds|viewNames|sheetIds` (1..100) + `folder`.
- `get_browser_organization` — схемы организации браузера (`target: views|sheets`): список,
  текущая, параметр сортировки, путь папок типового элемента.

### Кухня Revit
- Имя вида уникально **в пределах семейства видов** («Этаж 1» есть и как план этажа, и как
  план потолка). `rename_views` сравнивает имена по всему документу (консервативно): отказ
  `duplicate_name` при свободном в своём типе имени — ограничение тула, не Revit.
- Шаблон вида — тоже `View` (`IsTemplate=true`): rename/delete отказывают
  (`view_is_template`). Лист (`ViewSheet`) — не вид: `use_delete_sheets`; спецификация —
  `use_delete_schedules`.
- Удаление каскадно: уходят `Viewport`, аннотации и **зависимые виды**; уровень уносит свой
  план, камера — 3D-вид. Предупреждайте.
- `list_views` отдаёт `folder` (браузер), `sheet`, `isOnSheet` — не гадайте, где живёт вид.
- `View.Duplicate`: `with_detailing` (с аннотациями), `without_detailing` (модель),
  `dependent` (зависимый план). Отказы `view_not_duplicatable`, `dependent_not_supported`.
- **Легенды API не создаёт**: `create_view(kind:""legend"")` дублирует существующую; нет
  легенд → `legend_source_missing`.
- Пакетные тулы видов назначают шаблон с перезаписью дефолтного: новый вид получает
  `DefaultTemplateId` своего ViewFamilyType — явный `templateId` обязан его заменить, иначе
  «создал и применил» распадается на два шага. `scale` пишется ПОСЛЕ шаблона: назначение шаблона пере-применяет все
  контролируемые им параметры (вкл. VIEW_SCALE; если масштабом управляет шаблон —
  тул честно предупреждает). `view_name_taken` ловит и дубли внутри пачки.
- Папка браузера — не параметр вида: её задаёт **параметр группировки текущей организации
  браузера** (напр. «Раздел»). Группировка по read-only свойству →
  `browser_folder_parameter_read_only` (перестройте схему). Виды и листы — разные схемы.
- Организация браузера в API **read-only**: переключить текущую схему или создать новую
  нельзя (нет setter/builder) — только диалог Browser Organization в UI Revit.

### Аргументы
- `fields` сокращает ответ (`viewId,name,type,level,template,sheet,isOnSheet,folder`);
  `summaryOnly=true` — только количество и типы.

### Инварианты
- Перед переименованием возьмите `viewId` из `list_views` (имена не уникальны между семействами).
- Имя: trim, ≤250, без `\ : { } [ ] | ; < > ? ' ~`; листы/браузерные виды — `unsupported_view_type`.
- `atomic=true` в `rename_views` откатывает пакет при занятом имени (`duplicate_name`).
- `delete_views` необратим (`undoSupported=false`): только `dryRun=true` перед применением; apply
  требует `confirmNames` = `names[]` свежего dry-run (`confirm_names_required`/`_mismatch` =
  «повтори dry-run и передай свежий names[]» — защита, не ошибка).
- `duplicate_view`/`create_view`/`set_browser_folder` откатываются `undo_last`; `duplicate_view` —
  один вид за вызов. Write-тулы по умолчанию `dryRun=true`.
- Массовые переименования — `start_job(action:""rename_views"")`.

### Рецепты
1. «Переименуй все планы по шаблону»: `list_views(types=[""FloorPlan""])` → `rename_views(items, atomic=true)`.
2. «Какой вид открыт?»: `get_current_view_info`.
3. «Удали лишние виды»: `list_views(fields=[viewId,name])` → `delete_views(dryRun=true)` →
   подтверждение → `delete_views(dryRun=false, confirmNames=names[])`.
4. «Вид на каждый раздел»: `duplicate_view(newName=...)` → `set_browser_folder(viewIds, folder=раздел)`.
5. «Планы систем на всех уровнях по образцу»: список уровней (`list_levels`) и шаблон
   (`list_view_templates`) → `create_floor_plans(levels, templateId, namePattern=""В_Этаж {floor}_Отопление"")`
   → dry-run → apply → read-back `list_views`.
6. «3D-вид на каждую систему»: `create_views_batch(items:[{kind:""3d"",name,templateId}], atomic=true)`.

### Что нельзя
- Удалять виды, размещённые на листах, без предупреждения: они пропадут с листов.
- Переименовывать шаблоны через `rename_views` — тул откажет; шаблоны ведутся паком `templates`.
- Считать, что имя вида уникально в документе: оно уникально только в своём ViewFamily.
";

    private const string ViewSettings = @"## Пак view_settings: настройки вида

### Карта тулов
- `get_view_settings` — снимок: `viewRange`, `crop`, `hiddenCategories`, `controlledParameters`, `scale`, `templateName`; `viewIds` (≤200) и `fields`.
- `set_view_range`/`set_view_crop`/`set_view_visibility` — `items[{viewId,...}]` **или** `select:{type|viewType?,levelId?,folder?,nameContains?,templateId?,section?}` + одноимённый верхний аргумент (`viewRange`/`crop`/`visibility`): ids руками не нужны (`section` — точное значение параметра «Раздел»).
- `set_view_crop` — `crop:{active,visible,minMm,maxMm,copyFromViewId}`; `set_view_visibility` — `visibility:{categories:[{category|categoryId,hidden}],worksets:[…]}`.
- `set_view_parameters` — `parameters:[{name|parameterId,value,unit}]` для общих свойств вида.
- `list_scope_boxes` — боксы: `scopeBoxId`, `name`, `minMm/maxMm`; `assign_scope_box` — `viewIds|viewSelect` + один из `scopeBoxId|scopeBoxName|clear:true`.

### Кухня Revit
- Любой write-тул отказывает на **workshared-документе** (`workshared_not_supported`); читать можно.
- Диапазон — **только у планов** (`ViewPlan`); разрезы/3D → `unsupported_view_type`. Четыре
  плоскости: `top`, `cutPlane`, `bottom`, `viewDepth` = уровень + смещение; меняются только
  переданные. Порядок валидируется (план: Top > Cut > Bottom; потолок зеркально,
  `PlanViewRangeError`), невалидный набор отклоняется целиком.
- Подрезка: **две галочки** — `active` (включена) и `visible` (рамка показана). Форму
  (`ViewCropRegionShapeManager`) поддерживают не все виды: чертёжные — без подрезки, 3D — только `CropBox`.
- `minMm`/`maxMm` — только парой; `copyFromViewId` игнорирует остальные поля; явные границы
  заменяют контур (warning).
- **Scope Box**: пока назначен — задаёт границы подрезки (`CropBox` писать бесполезно), снимается
  `assign_scope_box(clear:true)`. Бокс — Element (OST_VolumeOfInterest): создать/изменить через API
  нельзя (UI Revit); повёрнутый отдаётся охватывающим боксом. Подконтролен шаблону —
  `view_controlled_by_template`.
- Аннотационная подрезка (`VIEWER_ANNOTATION_CROP_ACTIVE`, `*AnnotationCropOffset`) — не `CropBox`,
  через `set_view_crop` не меняется.
- Скрытие категории (`View.SetCategoryHidden`) — настройка **вида**; `GetCategoryHidden=false` ≠ «видно»
  (поверх — фильтры и поэлементное скрытие). «Виден ли элемент» — `Element.IsHidden(view)` /
  `query_elements(viewId=...)`.
- `set_view_visibility`: неизвестная/нередактируемая категория и `worksets` — **warning**, не `failed`.
- Настройку, контролируемую шаблоном, записать в вид **молча не получается** (Revit не бросает
  исключение) — тулы возвращают `view_controlled_by_template` с рецептом.

### Инварианты
- `get_view_settings` отдаёт `writable{...}` и `templateControls{...}` — по ним виден контроль
  шаблона. `view_controlled_by_template` → не повторяйте вызов:
  `set_view_template_inclusion(parameters=[""viewRange""|""visibility""], included=false)` (crop —
  невозможно) или правьте шаблон.
- `set_view_parameters`: только instance-параметры (`scope_mismatch`), GUID нет, `value` — строка,
  для `Double` нужен `unit`; `undo_last` не откатывает — повторный вызов со значениями из
  `changed[].oldValue`. После записи — read-back из модели.

### Рецепты
1. Диапазон: `get_view_settings(viewId, fields=[viewRange])` → `set_view_range` → read-back.
2. Скрыть категорию на всех планах этажа: `set_view_visibility(select:{type:""FloorPlan"",levelId},...)`.
3. Подрезка по границам: `crop:{active:true,minMm:[x,y],maxMm:[x,y]}`; форма — `copyFromViewId`; «как эталон» на пачку — `copy_crop_from_view` (пак `view_edit`).
4. «Привяжи к боксу»: `list_scope_boxes` → `assign_scope_box(scopeBoxId, viewSelect)`; отвязать — `clear:true`.
5. «Скрыть раздел на плане» — фильтр вида (пак `filters`), если нужно правило по параметру.

### Что нельзя
- Править диапазон у не-планов; считать `visible=false` выключенной подрезкой (это `active`).
- Писать в настройку под шаблоном и «чинить» повторной записью.
";

    private const string ViewEdit = @"## Пак view_edit: композитная настройка и копирование

### Карта тулов
- `configure_views` — один вызов на много видов (имя, шаблон, диапазон, подрезка, видимость): `items[]` или `select` + `settings` (`type` — точное имя `ViewType`).
- `copy_view_settings` — `sourceViewId` → `targetViewIds`, секции `crop|viewRange|visibility|template|all` (по умолчанию только `crop`).
- `copy_crop_from_view` — только подрезка: `sourceViewId|sourceViewName`, цели `targetViewIds(≤50)|targetSelect`, `includeActive` (по умолч. true).

### Кухня Revit
- Копирование настроек между видами разного типа бессмысленно: диапазон скопируется только в
  план, подрезка — в виды с подрезкой. Тул отчитывается по секциям, а не «успех целиком».
  `set_view_parameters` и `set_view_template_inclusion` (пишут в один шаблон/набор параметров).
- `copy_crop_from_view`: координаты — в системе вида, источник и цели нужны **одной ориентации**
  (иначе прямоугольник уедет). Цель под шаблоном с включённой подрезкой → per-item
  `crop_controlled_by_template`: снять шаблон (`apply_view_template templateId=0`) или править в
  нём. Неактивная подрезка источника — warning, копируются хранящиеся границы.
- `copy_view_settings` копирует **только** эти четыре секции: имя, масштаб, уровень детализации,
  дисциплину, фильтры и overrides он не переносит. `all` разворачивается в порядке
  `template → viewRange → crop → visibility`.
- Установка шаблона (`templateId`) делает контролируемые им параметры read-only; если в том
  же вызове вы просите изменить диапазон, порядок операций решает исход — тул сообщит
  `view_controlled_by_template` для конфликтующих полей.
- `overwriteTemplate` управляет заменой уже существующего шаблона. Без него вид с шаблоном
  пропускается (это skip, а не ошибка).
- **Снять шаблон через `configure_views(templateId=0)` нельзя** — будет `invalid_template`;
  для detach нужен `apply_view_template(templateId=0|null)`.
- Дублирование вида (`View.Duplicate`) имеет три режима: `Duplicate` (модель и настройки),
  `WithDetailing` (+ аннотации и детализация), `AsDependent` (зависимый вид, синхронный с
  основным — для нарезки большого плана на зоны). Проверять `CanViewBeDuplicated` под
  конкретный режим. Скрытая детализация в клон **не попадает**.
- Каждый новый вид получает `DefaultTemplateId` своего `ViewFamilyType` — только что
  созданный вид может уже быть под шаблоном.

### Инварианты
- `configure_views`/`copy_view_settings` — `dryRun=true` по умолчанию, `changed/skipped/failed`
  по виду: читайте `skipped`. `atomic=true` — первый отказ откатывает пакет (`atomic_aborted`).

### Рецепты
1. «Одинаковый диапазон на все планы»: `configure_views(select:{type:""FloorPlan"",levelId}, settings:{viewRange:...})`.
2. «Сделай как эталон»: `copy_view_settings(sourceViewId, targetViewIds, sections=[""all""])` + read-back на 1–2 целях.
3. «Вырежи как на эталоне»: `copy_crop_from_view(sourceViewId, targetSelect:{type:""FloorPlan"",levelId})`.
4. «Переименуй и назначь шаблон одной операцией»: `configure_views(items:[{viewId,newName,templateId}])`.

### Что нельзя
- `settings` без `select` (нужен критерий); ждать от `copy_view_settings` фильтров вида
  (секции: crop/viewRange/visibility/template); игнорировать `skipped[]` — там причина «не применилось».
";

    private const string Templates = @"## Пак templates: шаблоны видов

### Карта тулов
- `list_view_templates` — список шаблонов с типом вида и включёнными настройками.
- `get_view_template_details` — карта шаблона: `includesViewRange`, `includesCrop`, `includesVisibility`, `settings`.
- `apply_view_template` — назначить/снять шаблон: `items[{viewId,templateId}]`, `templateId:0|null` снимает;
  `select.section:""АР""` — отбор по параметру «Раздел»; `exceptViewIds`/`exceptNameContains` — минус-выборка
  (исключённые — в `excludedViewIds`).
- `set_view_template_inclusion` — галочка «Include in template»: `parameters:[""crop""|""visibility""|""viewRange""|""scale""|""detailLevel""|""discipline""|""filters""|""worksets""]`, `included`.

### Кухня Revit
- Шаблон — `View` с `IsTemplate=true`; создаётся из вида (`CreateViewTemplate`), не отображает
  элементы, не назначается другому шаблону.
- **Назначить ≠ применить.** Assign (`appliedViewTemplate`) — постоянная связь: правки шаблона
  приходят в вид, контролируемые параметры менять нельзя. Разовая «Apply Template Properties» —
  `View.ApplyViewTemplateParameters`, связи не возникает.
- Галочки — `GetTemplateParameterIds()` (whitelist) и `GetNonControlledTemplateParameterIds()`
  (снятые); read-modify-write, id вне whitelist игнорируются. Сопоставляйте по
  `BuiltInParameter`, не по имени.
- Масштаб — это **три** параметра (`VIEW_SCALE` + два `*_PULLDOWN_*`): включение только
  `VIEW_SCALE` оставляет шаблон в несогласованном состоянии.
- `apply_view_template` требует не только совместимого семейства, но и **точного `ViewType`**:
  иначе `template_view_type_mismatch`. Если у вида уже другой шаблон и `overwriteTemplate=false`,
  это skip, а не ошибка.
- `set_view_template_inclusion` меняет **сам шаблон**: затрагивает все виды с ним
  (`affectedViewCount`, первые 50 в `affectedViews`).
- Подрезку (`crop`) открепить через include-list нельзя (нет в `GetTemplateParameterIds`,
  `parameter_not_in_template`): правьте в шаблоне или снимайте шаблон с вида.
- У шаблона уровни диапазона только относительные (Current / Level Above / Below / Unlimited);
  жёсткий уровень «перекинет» вид на чужой уровень.
- Снятие шаблона/галочки Include **не возвращает** прежние значения — остаётся заданное шаблоном.
- Вид, получивший шаблон со снятым Include (viewRange/visibility/crop), **сохраняет свои значения** —
  отчёт помечает это `skippedByTemplateInclude` per-view + summary-строками `templateIncludeSkips`;
  «шаблон применился ко всем настройкам» в этом случае неверно (кейс 3 бенчмарка).
- Пока шаблон назначен и параметр включён, запись того же параметра в вид игнорируется Revit
  (`view_controlled_by_template` в наших тулах) — это главная причина «писал, ничего не изменилось».
- Шаблон применим не к любому виду: `View.IsValidViewTemplate` проверяет совместимость типа.
  Несовместимый → `invalid_template`.
- Шаблоны расписаний управляют полями/фильтрами/сортировкой/форматом через
  `SCHEDULE_*_PARAM`, а не через V/G.

### Инварианты
- Перед записью в вид проверьте `hasTemplate`/`controlledParameters` (`get_view_settings`).
- `apply_view_template` с `templateId=0|null` — отвязка, не «шаблон по умолчанию».
- `set_view_template_inclusion` требует существующий шаблон (`no_view_template`) и параметр в нём
  (`template_parameter_not_found`).

### Рецепты
1. «Почему не меняется подрезка?»: `get_view_settings(fields=[templateName,controlledParameters])` →
   `set_view_template_inclusion(templateId, parameters=[""crop""], included=false)`.
2. «Повесить шаблон на все планы»: `list_view_templates` → `apply_view_template(select:{viewType:""FloorPlan""}, templateId=...)`.
3. «Сделать шаблон, который не управляет видимостью»: `set_view_template_inclusion(parameters=[""visibility""], included=false)`.
4. «Шаблон всем планам раздела АР»: `add_project_shared_parameter` («Раздел», text, категории views) →
   `set_parameters` проставить разделы → `apply_view_template(select:{viewType:""FloorPlan"",section:""АР""}, templateId=…)`.

### Что нельзя
- Пытаться включить в шаблон параметр, которого там нет (`parameter_not_in_template` — это ответ Revit, а не баг).
- Отвязывать шаблон, чтобы «просто поправить» настройку, без предупреждения пользователя: отвязка меняет поведение вида.
- Считать, что шаблон управляет подрезкой: у многих шаблонов её нет в наборе включённых параметров.
";
}