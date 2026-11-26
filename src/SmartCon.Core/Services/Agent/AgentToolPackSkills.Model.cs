namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> ModelSkills() =>
        new[]
        {
            ("model", Model),
            ("levels", Levels),
            ("elements", Elements),
            ("params", Params),
            ("changes", Changes)
        };

    private const string Model = @"## Пак model: обзор проекта

### Карта тулов
- `get_project_overview` — паспорт модели: единицы, категории с количеством, уровни, счётчики видов/листов.
- `list_categories` — категории: `usedOnly` (по умолчанию true), `limit/offset`.
- `list_system_types` — системные типы категории (стены, трубы, воздуховоды):
  `category`, поля `typeId/name/instanceCount/canBeDuplicated`.
- `get_selected_elements` — что выделено в Revit сейчас (id, категория, тип).

### Кухня Revit
- Внутренние единицы фиксированы (длина — футы); `Document.GetUnits()` меняет только отображение, не хранение.
- Категории сравниваются по id/`OST_`-имени, а не по `Category.Name` (он локализуется); наши тулы принимают оба и `categoryIds`.
- `Element.Category` бывает null (материалы, виды, служебные объекты).
- Фазы только читаются (создать через API нельзя); новый элемент берёт фазу активного вида, в headless — последнюю.
- Данные проекта (`ProjectInfo`) — built-in параметры; не ищите их по локализованному имени.
- Типы бывают **загружаемые** (FamilySymbol, `list_family_types`) и **системные**
  (WallType/..., `list_system_types`); `duplicate/rename/replace_family_type` (пак `families`)
  принимают оба, для системных — `typeName + categoryName`; `instanceCount` — сколько
  элементов стоит на типе.

### Аргументы
- `usedOnly=false` в `list_categories` — показать и пустые категории.
- `get_selected_elements` — быстрый путь вместо угадывания по имени.

### Инварианты
- Идентификаторы обзора — для последующих `query_elements`/`summarize_elements`, не для записи.
- Количество в обзоре — это счётчик, а не список: за деталями идите в `query_elements`.

### Рецепты
1. Первый ход в незнакомой модели: `get_project_overview` → `list_levels` → `list_categories(usedOnly=true)`.
2. «Что я выделил?»: `get_selected_elements` → `get_element_details(elementIds)`.
3. «Есть ли в проекте двери?»: `list_categories(usedOnly=true)` и поиск по имени/`OST_Doors`.
4. «Какие типы стен есть?»: `list_system_types(category:""OST_Walls"")`.

### Что нельзя
- Полагаться на английские имена категорий в русской Revit.
- Считать, что в модели есть все категории, которые бывают в Revit.
- Изменять проект этим паком: он только читает.
";

    private const string Levels = @"## Пак levels: уровни

### Карта тулов
- `list_levels` — все уровни: имя, отметка, id.
- `create_level` — новый уровень: `name`, `elevationMm` (отрицательная — ниже базы проекта);
  guard `level_name_taken`. План вида НЕ создаётся — следом `create_floor_plans`.
- `rename_levels` — `items[{levelId,newName}]`, `atomic`.

### Кухня Revit
- `Level.Elevation` считается от базы, указанной параметром типа `Elevation Base`: 0 = Project (от начала проекта), 1 = Shared (от shared origin). Для сравнения и сортировки уровней используйте `ProjectElevation` — он всегда от начала проекта.
- Z координат элементов отсчитывается от внутреннего нуля Revit, а не от отметки уровня. Разница между «отметкой уровня» и Z элемента — это Project Base Point; не сопоставляйте их напрямую.
- Имена уровней должны быть уникальны. Установка занятого имени бросает `ArgumentException` — в пакетном переименовании это отказ по элементу (`duplicate_name`).
- Циклическое переименование (1→2, 2→1) в одной транзакции невозможно без временных имён: второй шаг увидит старое имя занятым. `rename_levels(atomic=true)` откатит весь пакет — планируйте двухфазно.
- Создание уровня не создаёт план этажа: `ViewPlan` создаётся отдельно. У вида `View.GenLevel` есть только у планов; у разрезов/3D его нет.
- У разных классов элементов уровень хранится в разных параметрах (`LevelId`, `ReferenceLevel`, «Schedule Level»); у части MEP-элементов `LevelId` может быть пустым.

### Инварианты
- Перед переименованием получите id через `list_levels`; имена локализованы, а id стабильны
  в пределах сессии.
- `rename_levels`: до 200 пар за вызов; имя ≤250 символов, без `\ : { } [ ] | ; < > ? ' ~`;
  уникальность проверяется без учёта регистра, а «изменения нет» — с учётом.
- `atomic=true` — для полного откката при конфликте имён; без него применяются успешные.
- После переименования уровней проверьте виды: имена видов, построенные от уровня, не меняются автоматически. Заодно проверьте, не переименовал ли пользователь уровни так, что сломалась их схема «Этаж N».

### Рецепты
1. «Переименуй уровни по шаблону»: `list_levels` → сформировать имена → `rename_levels(atomic=true)`.
2. «Какой у меня перепад этажей?»: `list_levels` → разница `ProjectElevation` между соседними (не `Elevation`: база может быть Shared).
3. «Элементы на уровне N»: `query_elements(levelIds=[id])`.
4. «Добавь этаж»: `create_level(name, elevationMm)` → в плане `execute_plan` связка через
   `{{""$step"":""s1"",""path"":""created.levelId""}}` внутри `levels:[…]` → `create_floor_plans(levels, templateId, namePattern)`.

### Что нельзя
- Сравнивать `Elevation` уровней с разной `Elevation Base`.
- Переименовывать в два прохода без учёта занятых имён — используйте временные имена или `atomic`.
- Считать, что у уровня ровно один план этажа.
";

    private const string Elements = @"## Пак elements: поиск и чтение элементов

### Карта тулов
- `query_elements` — выборка: `categories`, `levelIds`, `typeIds`, `viewId`, `roomId`, `conditions[{parameter,op,value,unit}]`, `logic`, `includeTypeParameters`, `fields`, `limit` ≤200, `offset`.
  `parameter` в условии — имя параметра, BuiltInParameter id или структурное поле карточки:
  `name` (имя элемента; для экземпляра семейства — также имя семейства или типа; для помещения —
  имя ROOM_NAME и номер, а не склеенный `Room.Name`), `family`,
  `type`, `category`, `level`. Категория принимается как enum-имя (`OST_Walls`),
  display-имя и псевдоним вида `OST_Pipes` = display «Pipes». Структурные поля — зарезервированные
  слова, для них только
  строковые операторы; числовой оператор с ними → `invalid_query`.
- `summarize_elements` — сводка: `groupBy: category|level|type|family|parameter|typeId`, `sumParameter`, `topN`, `includeZeroCount`; группы типов несут `family`, `typeId`, `category`.
- `get_element_details` — детали ≤10 элементов: `parameterClasses: system|shared|family`, `includeTypeParameters`, `parameterNames`, `maxParametersPerElement` ≤200.
- `get_element_bounds` — координаты и габариты ≤50 элементов в мм (координаты модели): `location`
  (точка вставки либо start/end/mid/lengthMm кривой размещения), `boundingBox` (min/max/size/center);
  `viewId` — боксы в контексте вида. Типы — `applicable=false` (`element_type`).
- `group_families_by_shared_parameters` — группы по общим параметрам
  (`scope: type|instance|both`) + отдельный `unplacedFamilies`. `groupBy:""signature""` —
  точный полный набор GUID (может дать десятки групп); `groupBy:""prefix""` — бакеты по
  префиксу имени, практическая оценка «сколько ФОП / сколько портировать» (Revit не хранит
  путь источника). `compact:true` — `parameterNames[]`/`familyCount`/`familyIds[]`;
  `familiesWithoutSharedParameters`; точный аудит по префиксу —
  `find_families_with_parameter(match:""prefix"", summary:true)`.
- `audit_fill_rate` — батч-аудит заполненности: обязательный набор = поля спек НА ЛИСТАХ
  (или явные `scheduleIds`), сводка за один проход по модели (cap 20000 элементов):
  filled / emptyNoValue / emptyString / missingParameter + виновники (семейство/тип/уровень);
  черновые спеки вне листов — в `offSheetSchedules`, НЕ аудируются.
- `list_type_parameter_candidates` — встроенные параметры типоразмера категории
  (обязателен `categories`/`categoryIds`) с заполненностью по всем типам;
  `recommended` = пустые записываемые String (группа `PG_IDENTITY_DATA` сверху) — выбор
  слота под код/ключ сортировки без хардкода.
- `delete_elements` — удаление элементов по `elementIds` ≤200, `confirmNames`. Dry-run
  показывает `names[]`, категорию и `dependentsCount`; удаление каскадно уносит зависимых
  (`dependentsDeleted` в ответе). Необратимо (`undoSupported=false`).

### Кухня Revit
- Быстрые фильтры (`OfCategory`, `WhereElementIsNotElementType`) работают по заголовку элемента и не раскрывают его; медленные (параметрические, геометрические) раскрывают каждый элемент. Поэтому сначала категория/класс, потом параметры — иначе на большой модели массовое раскрытие.
- Любая фильтрация в LINQ **после** коллектора маршалит данные в managed-память и стоит кратно дороже; в бенчмарке Tammik — до 1000× медленнее встроенного фильтра. Наши тулы сначала сужают выборку коллектором по категориям, затем вычисляют `conditions` по каждому оставшемуся элементу в памяти — параметрический фильтр не транслируется в `ElementParameterFilter`. Для больших моделей сначала сужайте категории/уровни, иначе проход будет долгим.
- Коллектор ленивый: повторный `Count()` + `ToElements()` — два прохода. Материализуйте один раз (внутри тула это уже сделано).
- Параметры типа живут на `ElementType`, а не на экземпляре: `get_Parameter(BuiltInParameter)` на экземпляре для типового параметра обычно возвращает null. `includeTypeParameters=true` переключает чтение на тип.
- Исключения из этого правила есть (`UNIFORMAT_CODE`, `IFC_TYPE_GUID` доступны и с экземпляра), поэтому определять владельца параметра надёжнее по `Parameter.Element`.
- `AsDouble()` возвращает внутренние единицы (футы), `AsValueString()` — пользовательскую строку с единицами и округлением. Для вычислений — первое, для показа — второе.
- `ElementId` стабилен только в пределах документа и текущей сессии: при worksharing-синхронизации он может быть переназначен. `UniqueId` переживает sync и апгрейд, но копия файла сохраняет те же UniqueId.
- `Element.Parameters` перечисление дорого: каждый параметр маршалится. Не перебирайте все параметры ради одного значения.
- `Element.IsValidObject` — единственная надёжная проверка, что обёртка ещё жива; после удаления/отката любой вызов даёт `InvalidObjectException`.

### Инварианты
- Условия: `op` из `eq|neq|gt|gte|lt|lte|contains|startsWith|endsWith|in|notIn|exists|notExists`;
  `unit` обязателен для числовых сравнений в мм/м/м²/м³ (`raw` — внутренние единицы).
- `notExists` находит элементы, **где параметр есть, но пуст**; элемент, у которого параметра
  нет вовсе, под это условие не попадёт. Для «без параметра» проверяйте `exists` и берите
  остаток. Значения типовых параметров видны и без `includeTypeParameters`, когда имя —
  enum-id типового built-in (`ALL_MODEL_TYPE_COMMENTS`) или экземпляр отдаёт пустой вид
  типового параметра (Assembly Code); прочие имена типовых параметров ищутся по типу
  только с флагом.
- Ноль совпадений по параметру, который привязан к типам, сопровождается подсказкой
  `typeBoundParameter` — типы смотрят `find_families_with_parameter` / `list_family_types`.
- Параметр, которого нет ни у одного элемента выборки, — это `unknown_parameter` с подсказками
  (ближайшие имена), а не пустой список: исправьте имя или ищите по структурному полю.
  Пустой список при успешном ответе означает «таких элементов нет».
- Без категорий/уровней/типов/условий/вида запрос с `limit > 50` отклоняется
  (`query_too_broad`): сначала сузьте выборку или снимите лимит до 50.
- `limit` ≤200, `offset` — пагинация; `total` считается по всем совпадениям. Для обзора —
  `summarize_elements` (скан до 20 000 элементов, `truncated` показывает усечение), а не
  тысячи строк `query_elements`.
- `fields` проецирует карточку элемента: `elementId`, `uniqueId`, `category`, `family`, `type`,
  `name`, `levelName`, `room`, `parameters`. Нужны только идентификаторы — не тяните параметры:
  ответ упирается в бюджет и приходит с `truncated`.
- `get_element_details` — не более 10 id; по умолчанию отдаёт `system` и `shared` параметры,
  family-параметры только считает (`familyParametersOmitted`). Числовое `value` — внутренние
  единицы, `displayValue` — проектные. `parameterNames` принимает и enum-id `BuiltInParameter`
  (например UNIFORMAT_CODE), а не только отображаемое имя; `typeId` в `elementIds` возвращает
  собственные параметры типа и без `includeTypeParameters`; каждый тип — один раз в `types[]`
  со своими параметрами.
- id, полученные из `query_elements`, годятся для следующего вызова в этой же сессии; для
  сохранения между сессиями — `uniqueId`.
- `get_element_bounds`: все координаты в мм (`units:""mm""`), как originMm коннекторов и отметки
  `list_levels`. Бокс выровнен по осям модели и включает невидимую геометрию семейства (flip
  controls): для зазоров между элементами сравнивайте `location` (концы кривых, точки вставки),
  а не только боксы; точный зазор системы даёт `trace_system_chain` (пак relations).
- `delete_elements`: сначала `dryRun=true` (вернёт `names[]`), apply — с `confirmNames` =
  `names[]` СВЕЖЕГО dry-run. Отказ `confirm_names_required`/`confirm_names_mismatch` =
  модель изменилась: повтори dry-run и передай его свежий names[]. Виды, листы и
  спецификации удаляйте профильными тулами, а не этим.

### Рецепты
1. «Трубы диаметром больше 100 на уровне 2»: `query_elements(categories=[""OST_PipeCurves""], levelIds=[lvl], conditions:[{parameter:""Диаметр"",op:""gt"",value:100,unit:""mm""}])`.
2. «Сколько чего по категориям»: `summarize_elements(groupBy:""category"")`, детали — по конкретной категории.
3. «Найди элементы по имени / семейству»: `query_elements(categories=[...], conditions:[{parameter:""name"", op:""contains"", value:""<часть имени>""}])` — `family` или `type`, когда нужно точное поле; «сколько элементов у каждого семейства» — `summarize_elements(groupBy:""family"")`.
4. «Покажи параметры этого элемента»: `get_element_details(elementIds=[id], includeTypeParameters=true, onlyNonEmpty=true)`.
5. «Аудит модели перед выдачей»: `audit_fill_rate` (без аргументов — поля листовых спек) →
   по виновникам точечное `set_parameters`/джоба для безопасно-выводимого.
6. «Из каких ФОПов собрана модель / сколько портировать»:
   `group_families_by_shared_parameters(groupBy:""prefix"", compact:true)` → группы по
   префиксам имён, `familiesWithoutSharedParameters`, `unplacedCount`; точный состав GUID —
   `groupBy:""signature""`; карточки — `get_family_parameters(familyIds:[...])`. «Сколько
   семейств на PREFIX_/BP_/BIM_» — `find_families_with_parameter(match:""prefix"", summary:true)`.
7. «Где стоят элементы / как далеко друг от друга»: `query_elements` (id) →
   `get_element_bounds(elementIds=[...])` → точки вставки и концы кривых в мм.

### Что нельзя
- Фильтровать «в памяти» огромные выборки вместо условий запроса.
- Считать null от `get_Parameter` доказательством отсутствия параметра: проверьте тип.
- Использовать `AsValueString` для вычислений и записи.
- Искать элементы по координатам через `get_element_bounds`: тул читает координаты уже известных id.
";

    private const string Params = @"## Пак params: запись и перенос значений

### Карта тулов
- `set_parameters` — `items[{elementId, parameter|guid, value, scope: instance|type, unit}]` ≤500, `dryRun`, `atomic`.
- `remap_parameter_values` — перенос: `mapping[{source{name,guid,scope}, target{name,guid,scope}}]` ≤20, `filter{categories,levelIds,typeIds,limit}` ≤500.
- `list_project_parameters` — проектные биндинги: имя, isShared, GUID, bindingType, категории.
- `add_project_shared_parameter` — привязка определения из ФОП к категориям.
- `set_shared_parameter_file` — подключить существующий ФОП по абсолютному `path` или создать новый
  пустой файл (`createIfMissing:true`) и сделать его активным; `dryRun` по умолчанию.

### Кухня Revit
- У параметра четыре `StorageType`: String, Double, Integer, ElementId. `Parameter.Set` имеет типизированные перегрузки, и перегрузка не по тому типу просто не запишет значение.
- `Parameter.Set` возвращает bool: false = значение не изменилось. Read-only параметр бросает `InvalidOperationException`.
- `IsReadOnly` считается **для конкретного элемента**: вычисляемые параметры (длина/площадь стены), параметры под формулой, связанные с global parameter, часть вложенных семейств и design options — только для чтения. `UserModifiable` — про интерактивного пользователя, не про API: ориентируйтесь на `IsReadOnly`.
- Числовые значения пишутся во внутренних единицах (футы). Значение из UI/Excel в мм нужно переводить: `unit` в наших тулах делает это за вас.
- `SetValueString` парсит строку в проектных единицах и понимает `18""`/`2' 3""`, но применим только к value-type параметрам; для String-параметров он вернёт пустую строку.
- Типовой параметр пишется на `ElementType` (`scope:""type""`), и изменение действует на **все** экземпляры типа. Не проходитесь по экземплярам ради одного типового значения: ответ сам возвращает `blastRadius{affectedTypes,affectedInstances,sampleInstanceIds}` — покажите его пользователю.
- `LookupParameter(name)` берёт первый параметр с таким именем: shared/проектный может перекрыть built-in. Для built-in — `get_Parameter(BuiltInParameter)`, для shared — по `guid`.
- Запись возможна только внутри транзакции; в SmartCon — только через `ITransactionService`.
- `Set` вернул true — ещё не доказательство: читайте значение после коммита (наши тулы делают read-back).
- Файл общих параметров (ФОП) — настройка **пользователя** Revit (Управление > Общие параметры), а не документа; по умолчанию его нет. Без ФОП `list_shared_parameters` и `add_project_shared_parameter(createDefinition:true)` отвечают `shared_parameter_file_missing`.

### Инварианты
- `dryRun=true` по умолчанию. `atomic=true` откатывает весь пакет при первом отказе.
- `value` передаётся **строкой** — даже число. Для `Double` поле `unit` обязательно (в том числе
  `unit:""raw""` для безразмерных): без единицы тул вернёт `invalid_value`, а не подставит
  внутренние единицы.
- `add_project_shared_parameter`: `createDefinition=true` — только по явной договорённости:
  определение пишется в ФОП, откатывается лишь биндинг (`definitionCreated: true`).
  Тот же GUID — добавление недостающих категорий (повтор — `no_changes`); смена
  instance↔type — `binding_type_conflict`; чужое имя — `name_conflict`.
- После записи читайте `changed`/`failed` с причинами: `parameter_not_found`,
  `readonly_parameter`, `invalid_value`, `invalid_unit`, `scope_mismatch`,
  `parameter_not_supported`, `unsupported_storage_type`, `set_failed` — у каждого
  свой следующий шаг.
- В `remap_parameter_values` ошибки резолва **source/target** попадают в `failed[]` (если не разрешилась хотя бы одна из сторон, элемент не переносится); ошибки конверсии — в `warnings[]`. Если у всех элементов не разрешилась пара, ответ `success=false` с `parameter_not_found`, а не «нет изменений».
- Для переноса значений между параметрами разной размерности сверяйте единицы: `remap_parameter_values` копирует значения, а не конвертирует смысл.
- `set_shared_parameter_file`: `dryRun=true` ничего не меняет; `dryRun=false` — после подтверждения.
  Существующий файл не перезаписывается; новый создаётся только по `createIfMissing:true` и только
  по пути, который назвал пользователь. `undo_last` настройку не откатывает — прежний файл
  возвращается повторным вызовом с `revertPath` из ответа.

### Рецепты
1. Проставить «Этаж» 300 стенам: `query_elements` → `set_parameters(items, scope:""instance"")` → read-back по 2–3 элементам.
2. Изменить типовой параметр у типа трубы: `set_parameters([{elementId:typeId, parameter:""Диаметр"", value:""100"", unit:""mm"", scope:""type""}])`.
3. Перенести «Наименование» в «Марку»: `remap_parameter_values(mapping:[{source:{name:""Наименование""}, target:{name:""Марка""}}])`.
4. Новый общий параметр: `list_shared_parameters` → `add_project_shared_parameter` → `list_project_parameters`.
   Ответ `shared_parameter_file_missing` — ФОП не подключён: спросите у пользователя путь и вызовите
   `set_shared_parameter_file(path, dryRun:true)` → после подтверждения `dryRun:false` → повторите шаг.

### Что нельзя
- Писать числовое значение без единиц и удивляться масштабу.
- Считать `Set == true` доказательством записи без read-back.
- Писать типовой параметр по экземплярам в цикле.
- Подключать или создавать ФОП по пути, которого пользователь не называл.
";

    private const string Changes = @"## Пак changes: наборы изменений, откат, сохранение, аудит

### Карта тулов
- `list_change_sets` — список наборов текущего документа (новые сверху): id, операция,
  статус applied/rolledBack/discarded, itemCount; `limit/offset`.
- `get_change_set_details` — состав одного набора по changeSetId: элементы, старое→новое.
- `undo_last` — откат набора: `changeSetId` (по умолчанию последний), `force`, `dryRun`.
- `undo_until` — откат ВСЕХ applied наборов от последнего вниз до указанного включительно
  (`changeSetId` обязателен); по одному undo_last на набор, стоп на первом конфликте.
- `discard_change_set` — забыть набор без отката (журнал перестаёт предлагать откат).
- `save_document` — сохранить документ по текущему пути (Ctrl+S); не автосейв, change set
  не создаёт; несохранённый ранее документ — `document_not_saved_before` (Save As руками).
- `audit_model` — read-only проверки: дубли номеров листов, невидимые символы, виды без
  шаблона, пустые спецификации, неразмещённые семейства, pending change sets.

### Кухня Revit
- **Программного Undo в Revit API нет.** После `Commit` операция необратима; откат возможен
  только компенсацией — вернуть старые значения, удалить созданное.
- Undo строго LIFO: `undo_until` идёт от последнего набора вниз по одному и
  останавливается на первом конфликте.
- `Document.Save()` запрещён в открытой транзакции; `PathName` пуст, пока документ не
  сохранён ни разу.

### Наш контракт
- `undo_last`/`undo_until` — это не Revit Undo, а **компенсация по сохранённому change set**:
  тул повторно применяет старые значения из журнала.
- Откат поддерживают: переименования видов и уровней (`view_name`, `level_name`) с временными
  именами для swap/циклов; `set_parameters`/`remap_parameter_values` (`instance:…`/`type:…`);
  шаблон вида, диапазон, подрезка, видимость, включение параметров в шаблон; создание фильтра
  и его состояние; семейства (по плану операций или backup `.rfa`).
- `undoSupported=false` возвращают: `set_filter_rules` (правила не откатываются — только
  `previousRules`), все delete-тулы (`delete_views`, `delete_sheets`, `delete_elements`,
  `delete_schedules`, `delete_filter`, `delete_family_types` — apply требует `confirmNames`
  = `names[]` свежего dry-run).
- `set_view_parameters` откат **не** покрывает. Джобы откатываются только `rollback_job(jobId)`.
- `save_document` не попадает в change sets: сохранение не меняет модель.

### Инварианты
- Перед откатом — dryRun: показывает, что будет восстановлено и где конфликты.
- `force=true` нужен, если набор устарел относительно документа; без него тул откажет
  (`undo_conflict`).
- Результаты: `nothing_to_undo` (модель не менялась), `undo_partial` (часть восстановлена —
  набор остаётся applied для повторного/force-открата), `undo_conflict` (всё провалилось).
  `undo_until` при стопе в середине цепочки честно сообщает, сколько наборов откатилось.
- `discard_change_set` не меняет модель — закрывает запись журнала, чтобы следующий
  `undo_last` целился в предыдущий набор.
- Если между записью и откатом пользователь менял те же объекты, откат перезапишет его
  правки: предупредите.
- `audit_model` ничего не чинит: находки — задания для профильных тулов.

### Рецепты
1. «Отмени последнее»: `undo_last(dryRun=true)` → показать → `undo_last(dryRun=false)`.
2. «Верни как было до этой операции»: `list_change_sets` → `undo_until(changeSetId,
   dryRun=true)` → показать → `undo_until(dryRun=false)`.
3. «Сохрани»: `save_document(dryRun=true)` → `save_document(dryRun=false)`.
4. «Проверь модель»: `audit_model` → разбор находок профильными тулами.

### Что нельзя
- Обещать пользователю «Revit Undo вернёт как было»: это не так.
- Откатывать удаления — их нет в change set.
- Считать `discard_change_set` откатом; делать Save As через `save_document`.

### Классификация документа
- `load_classification_table(path)` подключает файл классификатора к документу — как Управление →
  Настройки классификации. Формат файла — экспорт Revit: строка = `код <TAB> название <TAB> уровень
  <TAB> категория` (четыре колонки, последняя может быть пустой); кириллица — файл в UTF-16
  («Юникод» в Блокноте), только латиница — ASCII или UTF-8 без BOM. После этого
  `list_classification_entries` (пак semantic) читает таблицу, а запись «Кода по классификатору»
  проверяется по ней. Изменение откатывается только Undo в Revit.
- Нужен только когда пользователь назвал файл и таблица ещё не загружена; путь не выдумывают.
";
}
