namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> FamiliesSkills() =>
        new[] { ("families", Families) };

    private const string Families = @"## Пак families: семейства, типы и параметры

Самое опасное семейство операций: правка идёт в отдельном документе семейства и
возвращается в проект загрузкой.

### Уровень правки: сначала семейство
- Параметры заказчика (ФОП), формулы и таблицы поиска по умолчанию живут В СЕМЕЙСТВЕ
  (`add_family_parameter` → `set_family_formula`/`map_family_parameters`): переживают
  перезагрузку и новый типовой каталог, формулы продолжают считать.
- Проектные привязки (`add_project_shared_parameter`) — только по явному запросу: параметр
  проекта замораживает значения и не участвует в формулах семейства.
- Оба уровня — два коротких вопроса (где ФОП, семейство или проект), без молчаливого выбора.

### Карта тулов
- Чтение: `list_families`, `list_family_types`, `get_family_parameters`,
  `find_families_with_parameter` (`includeInstanceParameters=true` — instance-параметры),
  `list_shared_parameters` (нужен активный ФОП; иначе `shared_parameter_file_missing` →
  `set_shared_parameter_file` пака params).
- `category` (строка/массив, OST_*/имя/id; иначе `unknown_category`), `summary`,
  `placedOnly`, `fields`/`parameterNames`. Системных семейств в списке нет — их типы даёт `list_system_types`.
- Запись: `rename_family_parameter`, `add_family_parameter` (один параметр или `items[]` до 50 —
  одна транзакция и ОДНА перезагрузка), `set_family_parameter_values`, `set_family_formula`,
  `map_family_parameters`, `remove_family_parameter`, `replace_family_parameter`.
- Не общий параметр семейства **никогда не бывает полем спецификации** (`field_not_available`):
  для ведомости нужен общий (семейный — по умолчанию) или встроенный типовой параметр.
- Типы и файлы: `duplicate_family_type`, `rename_family_type`, `replace_family_type` (bulk),
  `load_family` (.rfa), `purge_families` (destructive, confirmNames), `delete_family_types`.
- `blastRadius` в каждом write — показывайте радиус загрузки.

### Три уровня, которые нельзя путать
- **Семейство** (`familyId`/`familyName`/`familyUniqueId`) → **тип** (`FamilySymbol`: размеры,
  параметры типа) → **экземпляр** (`FamilyInstance`: `scope:instance`; `scope:type` — весь тип).

### Кухня Revit
- `Document.EditFamily` открывает **независимую копию** без редактора; нельзя внутри транзакции,
  для системных/in-place и при открытом Family Editor.
- Документ семейства — отдельный `Document` (I-03b): транзакции на нём, не на проекте.
- Правится только `FamilyManager.CurrentType`; без типов значения задавать некуда (имена
  уникальны).
- После правки семейство **загружается обратно** и создаётся заново: `familyId`/`familyUniqueId`
  меняются (`previousFamilyId` в ответе), дальше адресуйте по `familyName`.
- Перезагрузка не переименовывает типы проекта: переименованный в семействе тип — дубль
  (`rename_family_type`; дубль удаляет `delete_family_types`).
- `Document.Delete(FamilySymbol)` удаляет тип **и все экземпляры** — это `force=true`.
- `duplicate_family_type` — независимая копия; `rename_family_type` не трогает экземпляры;
  `replace_family_type` — bulk одним `ChangeTypeId` (несовместимые — `type_not_valid_for_element`).
- `duplicate/rename/replace_family_type` работают и с **системными типами** (#441):
  `typeName + categoryName` или id из `list_system_types`.
- `load_family` — .rfa по пути: дубль имени → `family_already_loaded`; `overwrite=true` не
  откатывается.
- `purge_families`: кандидат — без размещённых экземпляров; in-place — отдельно.
- `FamilyManager.Set` пишет во внутренних единицах; `SetValueString` парсит строку в единицах
  документа; `Set` отказывает под формулой.
- `FamilyParameter.Formula` — только чтение; `SetFormula` ставит/снимает; цикл — invalid formula.
- Диалект формул: имена регистрозависимы, `<=`/`>=` нет (`NOT(a > b)`), условие — `IF(cond, t, f)`,
  округление — `round/roundup/rounddown`; единицы только одинаковые. Строковые параметры формулы
  держат: «текст — через IF» про условную сборку, не про запрет формул у текста.
- Добавление: обычный (`name` + `storageType`) и **shared** (по `guid`; `createDefinition=true`
  создаёт определение в ФОП); определение shared фиксируется при первой загрузке.
- Built-in имена («Код по классификатору») отклоняются (`parameter_name_taken`) — пишите built-in.
- `createDefinition=true` пишет вне транзакции семейства: при откате определение в ФОП останется.
- `map_family_parameters`: source/target одного scope и размерности (`scope_mismatch`/
  `spec_mismatch`), без конверсии; `values` снимает формулу target; `createTarget:true` — цель из ФОП.
- `rename_family_parameter` не переименовывает **shared** (`shared_parameter_not_renameable`; имя
  из определения); built-in не переименовываются и не удаляются.
- `remove_family_parameter` удаляет family и shared (по `guid`); built-in нельзя (`builtin_not_removable`).
- Перед правкой создаётся backup `.rfa`; `undo_last` восстанавливает семейство.

### Инварианты
- Все write-тулы: `dryRun=true`, затем apply; ошибка в документе семейства откатывает транзакцию
  (`transaction_rolled_back`).
- Единицы: length/angle/area/volume требуют `unit` (`unit_required`); `number` — без единицы;
  `text`/`yesno`/`integer`/`material` единицу не принимают.
- `parameter` ищется по имени в рамках `scope`; при неоднозначности — `guid` (имена экземплярного
  и типового могут совпадать).
- `set_family_parameter_values`: `typeId` — `FamilySymbol` **проекта** того же семейства;
  `typeName` — имя типа в документе семейства.
- Встроенный read-back проверяет только **типовые** параметры; instance — отдельно.
- `delete_family_types`, `remove/replace_family_parameter`, `purge_families` не откатываются
  (`undoSupported=false`; у первых трёх есть `backupPath`); `duplicate/rename/replace_family_type`
  и `load_family` (без overwrite) откатываются.
- Перед `delete_family_types` — `list_family_types(includePlaced=true)`: размещённые экземпляры
  это радиус поражения (`type_in_use` без `force=true`).
- После записи — read-back (`get_family_parameters(includeValues=true)`).

### Форма get_family_parameters
- Несколько семейств — одним вызовом `get_family_parameters(familyIds:[""…""])` (до 20):
  ответ — `families[]` с тем же payload; не перебирайте по одному.
- `parameters[]` — плоский массив: `id`, `name`, `scope`, `isShared`, `guid`, `storageType`,
  `formula`, `canSetValue`.
- `valuesByType[]` — `typeName`, `value` (внутренние единицы), `displayValue` (с единицей), `unit`,
  `hasValue`.
- Тул читает **документ семейства** (FamilyManager): Проектный общий параметр в ответе
  отсутствует — читайте его `list_family_types(familyId, parameterNames, valueScope:type)`.
- `execute_plan.readBackExpectations`:
  `parameters.byName[""Ширина""].valuesByType.byName[""Тип 1""].displayValue` (`byName` без учёта
  регистра).
- Family write-тулы сами делают read-back (`readBack.verified`/`mismatched`).

### Рецепты
1. «Добавь общий параметр <Имя> из ФОП»: `list_shared_parameters(nameContains:""<часть имени>"")` →
   если нет, `add_family_parameter(name, storageType:""text"", isShared:true,
   createDefinition:true)`; при `shared_parameter_file_missing` сначала
   `set_shared_parameter_file` (путь называет пользователь).
2. «Перенеси значения из A в B»: `get_family_parameters(includeValues=true)` →
   `map_family_parameters(mappings:[{source:{name:A}, target:{name:B}}], mode:""values"")`.
3. «Поставь формулу длины»: `set_family_formula(parameter:""Длина"", formula:""Ширина * 2"")`;
   константа без единиц = единицы параметра (длина: ""100"" = 30.5 м); суффиксы (""100 mm"")
   API отвергает; фикс-значение в мм — `set_family_parameter_values`.
4. «Убери лишний тип»: `list_family_types(includePlaced=true)` → `delete_family_types(dryRun=true)`
   → решение о `force` → apply.
5. «Независимый вариант типа»: `duplicate_family_type` → `set_family_parameter_values` на новый тип.
6. «Переведи все элементы на новый тип»: `duplicate_family_type` →
   `replace_family_type(fromTypeIds, targetTypeId)` — bulk одним вызовом.
7. «Загрузи семейство из файла»: `load_family(path, dryRun=true)` → apply; дубль имени — решить
   с пользователем (`overwrite=true` или отказаться).
8. «Почисти неиспользуемые семейства»: `purge_families(dryRun=true)` → candidates/names
   (in-place — отдельно) → `purge_families(confirmNames:names[])`.
9. «Массовая правка по десяткам/сотням семейств» — `start_job(action:""map_family_parameters"")`
   (до 200 family units); поштучный тул — для 1–5 семейств.
10. «Переведи семейство на ФОП заказчика» (семейный путь по умолчанию):
   `list_shared_parameters(nameContains:""<часть имени ФОП>"")` → `add_family_parameter(items:[{name,
   storageType, isShared:true, createDefinition:true, group:""PG_DATA""}, …])` ОДНИМ вызовом →
   `start_job(action:""map_family_parameters"")` с `createTarget:true` (mode formula для
   вычисляемых, mode values для текстовых) → read-back. Спецификация берёт поля-семейные
   параметры; проектные привязки не нужны.
11. «Сколько семейств на PREFIX_/BP_/BIM_»: `find_families_with_parameter(parameterName:""PREFIX_"",
   match:""prefix"", includeInstanceParameters:true, compact:true, summary:true)` → `byPrefix`,
   `familiesWithoutAnyShared`; перенос — `replace_family_parameter` (не `map_family_parameters`:
   тот копирует значения, а не перепривязывает определение).

### Что нельзя
- Править семейство через `Parameter.Set` без `FamilyManager` — значения не запишутся.
- Ставить значение параметру с формулой, не сняв формулу.
- Удалять типы с экземплярами `force=true` без явного согласия.
- Загружать .rfa из непроверенных источников без указания пользователя.
";
}
