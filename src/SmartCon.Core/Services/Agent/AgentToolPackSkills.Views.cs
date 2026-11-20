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

Обзор, поиск, переименование и удаление видов; активный вид.

### Карта тулов
- `list_views` — список видов: `types`, `levelIds`, `nameContains`, `folders`, `fields`, `limit` (≤500).
- `get_current_view_info` — активный вид (id, тип, шаблон, масштаб) — начните с него, если пользователь говорит «этот вид».
- `rename_views` — пакетно `items[{viewId,newName}]`, `atomic`.
- `delete_views` — `viewIds` (≤50), `includeTemplates` (по умолчанию false).

### Кухня Revit
- Имя вида уникально **в пределах своего вида/семейства видов**: «Этаж 1» может существовать
  и как план этажа, и как план потолка, и как план конструкции — Revit сам создаёт такие
  пары. Наш `rename_views` сравнивает имена по всему документу (консервативно): отказ
  `duplicate_name` при свободном в вашем типе имени — это ограничение тула, а не Revit.
- Шаблон вида — тоже `View`, но `IsTemplate=true`. Он не размещается на листах и не считается
  обычным видом: rename/delete отказывают (`view_is_template`) без явного флага.
- Лист (`ViewSheet`) — не вид. `delete_views` на листе даёт `use_delete_sheets`; удаление
  листа — пак `sheets`.
- Удаление вида каскадно: уходят его `Viewport`, аннотации вида и **зависимые виды**. Так же
  удаление уровня уносит его план, удаление камеры — 3D-вид. Предупреждайте о каскаде.
- `list_views` отдаёт браузерную папку (`folder`), номер листа (`sheet`) и `isOnSheet` —
  используйте это вместо догадок о том, где живёт вид.

### Аргументы
- `fields` сокращает ответ: `viewId,name,type,level,template,sheet,isOnSheet,folder`. Не запрашивайте всё, если нужен только список имён.
- `summaryOnly=true` — только количество и типы, без карточек.

### Инварианты
- Перед переименованием получите `viewId` через `list_views` (имена не уникальны между семействами — по имени можно попасть не в тот вид).
- Имя: trim, ≤250 символов, без `\ : { } [ ] | ; < > ? ' ~`. Листы и браузерные виды через
  `rename_views` не переименовываются (`unsupported_view_type` в `failed[]`).
- `atomic=true` в `rename_views` откатывает весь пакет, если хоть одно имя занято (`duplicate_name`). Без него применяются только успешные элементы.
- `delete_views` необратим (`undoSupported=false`, change set не пишется): только `dryRun=true`
  перед применением.
- Все write-тулы по умолчанию `dryRun=true`: сначала план, затем повтор с `dryRun=false`.

### Рецепты
1. «Переименуй все планы по шаблону»: `list_views(types=[""FloorPlan""])` → сформировать имена → `rename_views(items, atomic=true)`.
2. «Какой вид открыт?»: `get_current_view_info` → при необходимости `get_view_settings(viewId)`.
3. «Удали лишние виды»: `list_views(types=[...], fields=[viewId,name])` → `delete_views(dryRun=true)` → подтверждение → `dryRun=false`.

### Что нельзя
- Удалять виды, размещённые на листах, без предупреждения: они пропадут с листов.
- Переименовывать шаблоны через `rename_views` — тул откажет; шаблоны ведутся паком `templates`.
- Считать, что имя вида уникально в документе: оно уникально только в своём ViewFamily.
";

    private const string ViewSettings = @"## Пак view_settings: настройки вида

Чтение и запись диапазона, подрезки, видимости категорий/рабочих наборов и параметров вида.

### Карта тулов
- `get_view_settings` — снимок: `viewRange`, `crop`, `hiddenCategories`, `controlledParameters`, `scale`, `templateName`. Поддерживает `viewIds` (пакетно, ≤200) и `fields`.
- `set_view_range` — `items[{viewId, viewRange:{top,cutPlane,bottom,viewDepth:{levelId,offsetMm}}}]`.
- `set_view_crop` — `crop:{active,visible,minMm,maxMm,copyFromViewId}`.
- `set_view_visibility` — `visibility:{categories:[{category|categoryId,hidden}],worksets:[{name|worksetId,hidden}]}`.
- `set_view_parameters` — `parameters:[{name|parameterId,value,unit}]` для общих свойств вида.

### Кухня Revit
- Любой write-тул отказывает на **workshared-документе** (`workshared_not_supported`): запись в
  совместные модели пока не поддерживается. Читать такие виды можно.
- Диапазон вида существует **только у планов** (`ViewPlan`) и областей плана: для разрезов/3D
  тул вернёт `unsupported_view_type`. Меняются только переданные плоскости, остальные
  сохраняются.
- Диапазон — четыре плоскости: `top`, `cutPlane`, `bottom`, `viewDepth`. Каждая = уровень +
  смещение. Revit валидирует порядок: для плана этажа Top > Cut > Bottom, View Depth ниже
  Bottom; для плана потолка картина зеркальная (`PlanViewRangeError`). Невалидный набор
  отклоняется целиком.
- Подрезка (`crop`) имеет **две независимые галочки**: `active` (подрезка включена) и
  `visible` (рамка показана). Форму подрезки (`ViewCropRegionShapeManager`) можно задать
  только у вида, который её поддерживает: у чертёжных видов подрезки нет вовсе, у 3D форма
  не задаётся, только `CropBox`.
- `minMm`/`maxMm` передаются только парой; при `copyFromViewId` остальные поля crop
  игнорируются; явные границы заменяют существующий контур (с warning).
- Если на виде включён **Scope Box**, границы подрезки задаёт он: `Crop View` в UI серый,
  запись `CropBox` не даёт эффекта. Сначала снимите Scope Box.
- Аннотационная подрезка — отдельная сущность (`VIEWER_ANNOTATION_CROP_ACTIVE` и
  `*AnnotationCropOffset`), не `CropBox`. Через `set_view_crop` она не меняется.
- Скрытие категории на виде (`View.SetCategoryHidden`) — настройка **вида**, не глобальная
  видимость категории в проекте. `GetCategoryHidden=false` **не значит «видно»**: фильтры,
  настройки классов и поэлементное скрытие действуют поверх. Для «виден ли элемент» —
  `Element.IsHidden(view)` / `query_elements(viewId=...)`.
- `set_view_visibility`: неизвестная или нередактируемая категория — это **warning**, а не
  `failed`; скрытие рабочих наборов (`worksets`) тул не поддерживает (всегда warning).
- Если настройку контролирует шаблон, запись в вид **молча не работает** — Revit не бросает
  исключение. Поэтому тулы возвращают `view_controlled_by_template` и рецепт отвязки.

### Инварианты
- `get_view_settings` для обычного вида отдаёт `writable{visibility,crop,viewRange,filters}` и
  `templateControls{...}` — именно по ним видно, что шаблон контролирует. Списки
  `controlledParameters`/`nonControlledParameters` заполняются только для самого шаблона.
- `view_controlled_by_template` → не повторяйте вызов: либо `set_view_template_inclusion(parameters=[""viewRange""|""visibility""], included=false)` (для crop это невозможно), либо правьте сам шаблон.
- `set_view_parameters`: только instance-параметры вида (`scope_mismatch` для типовых), GUID не
  поддерживается, `value` — строка, для `Double` нужен `unit`; флага `atomic` нет.
- `set_view_parameters` не откатывается `undo_last` (`undoSupported=false`): откат — повторный
  вызов с прежними значениями из `changed[].oldValue`.
- После записи делайте read-back: итоговое значение читается из модели, а не из отчёта.

### Рецепты
1. Поднять/опустить диапазон: `get_view_settings(viewId, fields=[viewRange])` → `set_view_range` с новыми `offsetMm` → read-back.
2. Скрыть категорию: `set_view_visibility(visibility.categories=[{category:""OST_Furniture"",hidden:true}])`.
3. Подрезка по границам: `crop:{active:true,visible:true,minMm:[x,y],maxMm:[x,y]}`; скопировать форму — `copyFromViewId`.
4. «Скрыть раздел на плане» — не `set_view_visibility`, а фильтр вида (пак `filters`), если нужно правило по параметру.

### Что нельзя
- Править диапазон у не-планов и ждать успеха.
- Считать `visible=false` выключенной подрезкой: `active` и `visible` — разные галочки.
- Писать в настройку, управляемую шаблоном, и потом «чинить» повторной записью.
";

    private const string ViewEdit = @"## Пак view_edit: композитная настройка и копирование

### Карта тулов
- `configure_views` — один вызов на много видов: переименование, шаблон, диапазон, подрезка, видимость. Два режима: `items[]` (явные виды) или `select` + `settings` (выбор по критериям; `type` — точное имя `ViewType`).
- `copy_view_settings` — `sourceViewId` → `targetViewIds`, секции `crop|viewRange|visibility|template|all` (по умолчанию только `crop`).

### Кухня Revit
- Копирование настроек между видами разного типа бессмысленно: диапазон скопируется только в
  план, подрезка — в виды с подрезкой. Тул отчитывается по секциям, а не «успех целиком».
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
- `configure_views` и `copy_view_settings` по умолчанию `dryRun=true` и возвращают `changed/skipped/failed` по каждому виду — читайте `skipped` с причинами, а не только `success`.
- При `atomic=true` первый отказ откатывает весь пакет (`atomic_aborted`).
- После применения проверяйте результат `get_view_settings` по каждому изменённому виду.

### Рецепты
1. «Одинаковый диапазон на все планы этажа»: `list_views(levelIds=[...], types=[""FloorPlan""])` → `configure_views(select:..., settings:{viewRange:...})`.
2. «Скопируй настройки с эталонного вида»: `copy_view_settings(sourceViewId, targetViewIds, sections=[""all""])`, затем read-back на 1–2 целях.
3. «Переименуй и назначь шаблон одной операцией»: `configure_views(items:[{viewId,newName,templateId}])`.

### Что нельзя
- Использовать `settings` без `select`: нужен хотя бы один критерий, иначе тул откажет.
- Ожидать, что `copy_view_settings` скопирует фильтры вида: секции ограничены `crop/viewRange/visibility/template`.
- Игнорировать `skipped[]`: это и есть ответ, почему «не применилось».
";

    private const string Templates = @"## Пак templates: шаблоны видов

### Карта тулов
- `list_view_templates` — список шаблонов с типом вида и включёнными настройками.
- `get_view_template_details` — карта шаблона: `includesViewRange`, `includesCrop`, `includesVisibility`, `settings`.
- `apply_view_template` — назначить/снять шаблон: `items[{viewId,templateId}]`, `templateId:0|null` снимает.
- `set_view_template_inclusion` — галочка «Include in template»: `parameters:[""crop""|""visibility""|""viewRange""|""scale""|""detailLevel""|""discipline""|""filters""|""worksets""]`, `included`.

### Кухня Revit
- Шаблон — это `View` с `IsTemplate=true`; создаётся из настроенного вида (`CreateViewTemplate`),
  не отображает элементы и не назначается другому шаблону.
- **Назначить ≠ применить.** `appliedViewTemplate` (Assign) — постоянная связь: правки
  шаблона приходят в вид, а контролируемые параметры нельзя менять на виде. Разовая
  «Apply Template Properties» — `View.ApplyViewTemplateParameters`, связь при этом не
  возникает. Не путайте эти два сценария в ответе пользователю.
- Включённые галочки — `GetTemplateParameterIds()` (полный whitelist для этого типа вида) и
  `GetNonControlledTemplateParameterIds()` (снятые). Набор меняется read-modify-write; id вне
  whitelist игнорируются. Сопоставляйте по `BuiltInParameter`, не по локализованному имени.
- Масштаб — это **три** параметра (`VIEW_SCALE` + два `*_PULLDOWN_*`): включение только
  `VIEW_SCALE` оставляет шаблон в несогласованном состоянии.
- `apply_view_template` требует не только совместимого семейства, но и **точного `ViewType`**:
  иначе `template_view_type_mismatch`. Если у вида уже другой шаблон и `overwriteTemplate=false`,
  это skip, а не ошибка.
- `set_view_template_inclusion` меняет **сам шаблон**, поэтому затрагивает все виды с ним
  (`affectedViewCount`); `affectedViews` показывает первые 50.
- Подрезку (`crop`) открепить через include-list нельзя: Revit не отдаёт её параметр в
  `GetTemplateParameterIds`, тул вернёт `parameter_not_in_template`. Если шаблон задаёт
  подрезку — правьте её в шаблоне или снимайте шаблон с вида.
- Диапазон вида включает, но у шаблона уровни только относительные (Current / Level Above /
  Level Below / Unlimited); шаблон с жёстким уровнем «перекинет» вид на чужой уровень.
- Снятие шаблона **не возвращает** прежние значения: на виде остаётся то, что шаблон
  последним задал. Снятие галочки Include тоже не откатывает уже применённое значение.
- Пока шаблон назначен и параметр включён, запись того же параметра в вид игнорируется Revit
  (`view_controlled_by_template` в наших тулах) — это главная причина «писал, ничего не изменилось».
- Шаблон применим не к любому виду: `View.IsValidViewTemplate` проверяет совместимость типа.
  Несовместимый → `invalid_template`.
- Шаблоны расписаний управляют полями/фильтрами/сортировкой/форматом через
  `SCHEDULE_*_PARAM`, а не через V/G.

### Инварианты
- Прежде чем писать в настройку вида, проверьте `hasTemplate` и `controlledParameters` (`get_view_settings`).
- `apply_view_template` с `templateId=0|null` — это отвязка, а не «шаблон по умолчанию».
- `set_view_template_inclusion` требует, чтобы у вида уже был шаблон (`no_view_template`) и чтобы параметр существовал в шаблоне (`template_parameter_not_found`).

### Рецепты
1. «Почему не меняется подрезка на виде?»: `get_view_settings(viewId, fields=[templateName,controlledParameters])` → если параметр контролируется, `set_view_template_inclusion(templateId, parameters=[""crop""], included=false)`.
2. «Повесить шаблон на все планы»: `list_view_templates` → `apply_view_template(select:{viewType:""FloorPlan""}, templateId=...)`.
3. «Сделать шаблон, который не управляет видимостью»: `set_view_template_inclusion(parameters=[""visibility""], included=false)`.

### Что нельзя
- Пытаться включить в шаблон параметр, которого там нет (`parameter_not_in_template` — это ответ Revit, а не баг).
- Отвязывать шаблон, чтобы «просто поправить» настройку, без предупреждения пользователя: отвязка меняет поведение вида.
- Считать, что шаблон управляет подрезкой: у многих шаблонов её нет в наборе включённых параметров.
";
}