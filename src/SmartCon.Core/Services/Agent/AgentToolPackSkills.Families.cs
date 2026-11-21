namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> FamiliesSkills() =>
        new[] { ("families", Families) };

    private const string Families = @"## Пак families: семейства, типы и параметры

Самое опасное семейство операций: правка идёт в отдельном документе семейства и
возвращается в проект загрузкой.

### Карта тулов
- Чтение: `list_families`, `list_family_types`, `get_family_parameters`,
  `find_families_with_parameter` (instance-параметры — `includeInstanceParameters=true`),
  `list_shared_parameters` (нужен активный ФОП, иначе `shared_parameter_file_missing`).
- Запись в документ семейства: `rename_family_parameter`, `add_family_parameter`,
  `set_family_parameter_values`, `set_family_formula`, `map_family_parameters`,
  `remove_family_parameter`, `replace_family_parameter`.
- Типы проекта и файлы: `duplicate_family_type`, `rename_family_type`,
  `replace_family_type` (bulk), `load_family` (.rfa по абсолютному пути),
  `purge_families` (destructive, confirmNames), `delete_family_types`.
- Каждый write семейства возвращает `blastRadius` — покажите пользователю, сколько
  экземпляров затронет загрузка.

### Три уровня, которые нельзя путать
- **Семейство** (`Family`) — контейнер: `familyId`, `familyName`, `familyUniqueId`.
- **Тип** (`FamilySymbol`) — конкретный размер/марка; параметры типа живут здесь.
- **Экземпляр** (`FamilyInstance`) — размещённый элемент; `scope:""instance""` пишет
  экземплярное значение, `scope:""type""` — на тип и значит «для всех его экземпляров».

### Кухня Revit
- `Document.EditFamily(family)` открывает **независимую копию** семейства без редактора;
  запрещено в открытой транзакции, для системных/in-place семейств и при открытом Family
  Editor (ошибки «document is modifiable» / «Family is not editable»).
- Документ семейства — отдельный `Document` со своим стеком транзакций (I-03b): транзакции
  открываются на нём, а не на проекте; проектные `ITransactionService` к нему не применяются.
- Правится только `FamilyManager.CurrentType`. Если у семейства нет ни одного типа, значения
  задавать некуда: сначала создайте тип. Имена типов уникальны.
- После правки семейство **загружается обратно** в проект (`LoadFamily` с `IFamilyLoadOptions`).
  Revit **удаляет и создаёт семейство заново**: `familyId`/`familyUniqueId` меняются; ответ
  содержит `previousFamilyId`/`previousFamilyUniqueId`. Для следующих вызовов — `familyName`.
- Перезагрузка **не удаляет** и **не переименовывает** типы проекта: переименованный в
  семействе тип появится как **дубль**. Удалять: в проекте — `delete_family_types`, в
  семействе — до загрузки. Поэтому для переименования типа используйте `rename_family_type`
  (прямо в проекте), а не правку семейства.
- `Document.Delete(FamilySymbol)` в проекте удаляет тип **и все его размещённые экземпляры**
  без подтверждения — это и есть `force=true` в `delete_family_types`.
- Типы проекта: `duplicate_family_type` — независимая копия (правка копии не трогает
  исходный тип); `rename_family_type` не трогает экземпляры; `replace_family_type` — bulk
  одним статическим `ChangeTypeId` (одна регенерация; несовместимые — per-item
  `type_not_valid_for_element`).
- `duplicate/rename/replace_family_type` работают и с **системными типами** (#441: стены,
  трубы): резолв `typeName + categoryName` или id из `list_system_types`; `fromTypeIds`
  переводит все элементы типа.
- `load_family` грузит .rfa по абсолютному пути: дубль имени → `family_already_loaded`;
  `overwrite=true` перезагружает файл поверх, но **не откатывается** `undo_last`.
- `purge_families`: кандидат = загружаемое семейство, ни один тип которого не имеет
  размещённых экземпляров; in-place помечены `isInPlace=true` — согласуйте отдельно.
- Значения `FamilyManager.Set` пишутся во внутренних единицах (футы, радианы); `SetValueString`
  парсит строку в единицах документа. `Set` отказывает, если параметр управляется формулой.
- `FamilyParameter.Formula` — **только чтение**; ставится `SetFormula`, `null` снимает.
  Ошибка на цикле — «invalid formula string».
- Диалект формул Revit: имена параметров регистрозависимы, `<=`/`>=` **не поддерживаются**
  (пишите `NOT(a > b)`), условие — `IF(cond, true, false)`, округление — `round/roundup/
  rounddown`; формулы размерные: складывать только одинаковые единицы, округление делит
  на `1 mm`, текст — только через `IF`.
- Добавление параметра: обычный (`name` + `storageType`), **shared** (по `guid`;
  `createDefinition=true` создаёт определение в активном ФОП). Определение shared фиксируется
  в проекте при первой загрузке (правка `.rfa` его не обновит).
- `createDefinition=true` пишет **вне** транзакции семейства: если следующая операция упадёт,
  документ семейства откатится, а определение в файле общих параметров останется.
- `map_family_parameters`: source/target одного scope и размерности (`scope_mismatch`/
  `spec_mismatch`), значения **без конверсии единиц**; `values` снимает формулу target,
  `formula` переносит её текст.
- `rename_family_parameter` не переименовывает **shared**-параметр (`shared_parameter_not_renameable`):
  имя приходит из определения. Built-in параметры не переименовываются и не удаляются.
- `remove_family_parameter` удаляет family **и shared** (по `guid`) параметр; формулы с ним
  Revit удаляет сам (warning в ответе). Built-in — нельзя (`builtin_not_removable`).
- Перед реальной правкой создаётся backup `.rfa`; `undo_last` восстанавливает семейство из
  него. `delete_family_types` и `purge_families` — исключения: `undoSupported=false`.

### Инварианты
- Все write-тулы: `dryRun=true` по умолчанию, затем apply. Ошибка чтения/записи в документе
  семейства откатывает транзакцию целиком (`transaction_rolled_back`).
- Единицы: length/angle/area/volume требуют `unit` (иначе `unit_required`); `number` — без
  единицы; `text`/`yesno`/`integer`/`material` единицу не принимают. Десятичная запятая
  допустима.
- `parameter` ищется по имени в рамках `scope`; при неоднозначности используйте `guid`
  (shared) — имена экземплярного и типового параметра могут совпадать.
- `set_family_parameter_values`: `typeId` — это `FamilySymbol` **проекта** того же семейства;
  `typeName` — имя типа в документе семейства.
- Встроенный read-back проверяет только **типовые** параметры
  (`readBack.notVerifiableReasons`); instance-значения проверяйте отдельно.
- `delete_family_types`, `remove_family_parameter`, `replace_family_parameter` и
  `purge_families` не откатываются `undo_last` (`undoSupported=false`); у первых трёх есть
  `backupPath`. `duplicate/rename/replace_family_type` и `load_family` (без overwrite)
  откатываются.
- Перед `delete_family_types` прочитайте `list_family_types(includePlaced=true)`: число
  размещённых экземпляров — это радиус поражения (`type_in_use` без `force=true`).
- Перед `remove_family_parameter` прочитайте `get_family_parameters`: тул покажет формулы,
  которые будут удалены.
- После успешной записи — read-back (`get_family_parameters(includeValues=true)`).

### Форма get_family_parameters
- `parameters[]` — плоский массив: `id`, `name`, `scope` (`instance`/`type`), `isShared`,
  `guid`, `storageType`, `formula`, `canSetValue`.
- `valuesByType[]` — `typeName`, `value` (внутренние единицы), `displayValue` (с единицей),
  `unit`, `hasValue`.
- В `execute_plan.readBackExpectations` адресуйте параметр как
  `parameters.byName[""Ширина""].valuesByType.byName[""Тип 1""].displayValue`; `byName`
  сравнивает без учёта регистра.
- Family write-тулы сами делают read-back (`readBack.verified`/`mismatched`).

### Рецепты
1. «Добавь общий параметр ADSK_Наименование»: `list_shared_parameters(nameContains:""ADSK"")`
   → если нет, `add_family_parameter(name, storageType:""text"", isShared:true,
   createDefinition:true, group:""PG_IDENTITY_DATA"")`.
2. «Перенеси значения из A в B»: `get_family_parameters(includeValues=true)` →
   `map_family_parameters(mappings:[{source:{name:A}, target:{name:B}}], mode:""values"")`.
3. «Поставь формулу длины»: `set_family_parameter_values` для входных параметров →
   `set_family_formula(parameter:""Длина"", formula:""Ширина + 20 mm * 2"")` → read-back.
   Константам в формуле всегда указывайте единицы.
4. «Убери лишний тип»: `list_family_types(includePlaced=true)` → `delete_family_types(dryRun=true)`
   → решение о `force` → apply.
5. «Независимый вариант типа»: `duplicate_family_type` → `set_family_parameter_values` на
   новый тип (исходный не меняется).
6. «Переведи все элементы на новый тип»: `duplicate_family_type` (если тип новый) →
   `replace_family_type(fromTypeIds:[старый], targetTypeId:новый)` — bulk одним вызовом.
7. «Загрузи семейство из файла»: `load_family(path, dryRun=true)` → apply; дубль имени —
   решить с пользователем: `overwrite=true` или отказаться.
8. «Почисти неиспользуемые семейства»: `purge_families(dryRun=true)` → показать
   candidates[]/names[] (in-place — отдельно) → `purge_families(confirmNames:names[])`.
9. «Массовая правка по десяткам/сотням семейств» — пакетный путь
   `start_job(action:""map_family_parameters"")` (до 200 family units за джобу);
   поштучный тул — только для 1–5 семейств.

### Что нельзя
- Считать `familyId` стабильным после записи: загрузка пересоздаёт семейство.
- Править семейство через `Parameter.Set` без `FamilyManager` — значения не запишутся.
- Ставить значение параметру с формулой, не сняв формулу.
- Удалять типы с экземплярами «на всякий случай» с `force=true` без явного согласия.
- Загружать .rfa из непроверенных источников без явного указания пользователя.
";
}
