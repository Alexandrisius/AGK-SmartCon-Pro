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
  `find_families_with_parameter` (ищет по параметрам, видимым на **типах**; значения размещённых
  экземпляров не сканирует), `list_shared_parameters` (нужен активный файл общих параметров,
  иначе `shared_parameter_file_missing`).
- Запись: `rename_family_parameter`, `add_family_parameter`, `set_family_parameter_values`,
  `set_family_formula`, `map_family_parameters`, `delete_family_types`,
  `remove_family_parameter`.
- Каждый write возвращает `blastRadius{affectedTypes,affectedInstances,sampleInstanceIds}` —
  покажите пользователю, сколько экземпляров затронет загрузка.

### Три уровня, которые нельзя путать
- **Семейство** (`Family`) — контейнер, у него есть id и имя (`familyId`, `familyName`, `familyUniqueId`).
- **Тип** (`FamilySymbol`) — конкретный размер/марка; параметры типа живут здесь.
- **Экземпляр** (`FamilyInstance`) — размещённый в модели элемент; `scope:""instance""` пишет
  экземплярное значение, `scope:""type""` — на тип и значит «для всех его экземпляров».

### Кухня Revit
- `Document.EditFamily(family)` открывает **независимую копию** семейства без окна редактора.
  Запрещено: при открытой транзакции/read-only документе проекта, для системных и in-place
  семейств (`Family.IsEditable=false`), а также если это семейство уже редактируется или
  открыт Family Editor. Отсюда ошибки «The document is currently modifiable» и «Family is
  not editable» — тул возвращает их текстом с рецептом.
- Документ семейства — отдельный `Document` со своим стеком транзакций (I-03b): транзакции
  открываются на нём, а не на проекте; проектные `ITransactionService` к нему не применяются.
- Правится только `FamilyManager.CurrentType`. Если у семейства нет ни одного типа, значения
  задавать некуда: сначала создайте тип. Имена типов уникальны.
- После правки семейство **загружается обратно** в проект (`LoadFamily` с `IFamilyLoadOptions`).
  При этом Revit **удаляет и создаёт семейство заново**: `familyId`/`familyUniqueId` меняются;
  ответ содержит `previousFamilyId`/`previousFamilyUniqueId` и `familyIdNote`. Для следующих
  вызовов — `familyName` или новые id.
- Перезагрузка **не удаляет** из проекта типы, которых больше нет в семействе, и **не
  переименовывает** типы: переименованный в семействе тип появится в проекте как **дубль**.
  Убирать тип надо двумя разными операциями: в проекте — `delete_family_types`, в семействе —
  `remove`/`DeleteCurrentType` до загрузки.
- `Document.Delete(FamilySymbol)` в проекте удаляет тип **и все его размещённые экземпляры**
  без подтверждения — это и есть `force=true` в `delete_family_types`.
- Значения `FamilyManager.Set` пишутся во внутренних единицах (футы, радианы); `SetValueString`
  парсит строку в единицах документа. `Set` отказывает, если параметр управляется формулой.
- `FamilyParameter.Formula` — **только чтение**; формула ставится `FamilyManager.SetFormula`,
  `null` её снимает. Ошибка на цикле или недопустимом параметре — «invalid formula string».
- Диалект формул Revit: имена параметров регистрозависимы, `<=`/`>=` **не поддерживаются**
  (пишите `NOT(a > b)`), условие — `IF(cond, true, false)`, округление — `round/roundup/
  rounddown`; формулы размерные: складывать можно только одинаковые единицы, для округления
  делят на `1 mm`, текстовые значения — только через `IF`.
- Добавление параметра: обычный (`name` + `storageType`), **shared** (по `guid`;
  `createDefinition=true` создаёт определение в активном файле общих параметров) и параметр
  типа семейства. Определение shared-параметра фиксируется в проекте при первой загрузке:
  смена группы/описания в `.rfa` проект не обновит.
- `createDefinition=true` пишет **вне** транзакции семейства: если следующая операция упадёт,
  документ семейства откатится, а определение в файле общих параметров останется.
- `map_family_parameters`: source и target должны быть одного scope (`scope_mismatch`) и одной
  размерности (`spec_mismatch`) — значения переносятся **без конверсии единиц**. В режиме
  `values` формула target сначала снимается; в режиме `formula` — переносится текст формулы.
- `rename_family_parameter` не переименовывает **shared**-параметр (`shared_parameter_not_renameable`):
  имя приходит из определения. Built-in параметры не переименовываются и не удаляются.
- `remove_family_parameter` удаляет family/shared параметр; формулы, ссылающиеся на него,
  Revit удаляет автоматически — ответ перечисляет их в warning.
- Перед реальной правкой создаётся backup `.rfa`; `undo_last` восстанавливает семейство из
  него. `delete_family_types` — исключение: `undoSupported=false`.
- `FamilyManager.Set` требует выставленного `CurrentType`; значение пишется во внутренних
  единицах. Формула параметра хранится строкой и пересчитывает значения типов.
- Параметр под формулой нельзя задавать вручную — сначала снимите формулу
  (`set_family_formula(formula=null)`).
- Удаление параметра, от которого зависят формулы, Revit выполняет, но формулы исчезают:
  ответ перечисляет их в warning. Built-in и shared-параметры удалять нельзя.
- Удаление типа с размещёнными экземплярами отклоняется (`force=false`); `force=true`
  удаляет и экземпляры из модели — это необратимо.
- Shared-параметр добавляется по `guid` из файла общих параметров; если определения нет,
  `createDefinition=true` создаёт его, иначе `definition_not_found`.

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
- Встроенный read-back проверяет только **типовые** параметры: instance-значения и ссылки на
  элементы он не подтверждает (`readBack.notVerifiableReasons`). Их проверяйте отдельно.
- `delete_family_types` и `remove_family_parameter` **не откатываются** `undo_last`
  (`undoSupported=false`): предупредите до apply. У удаления параметра в ответе есть
  `backupPath` — pre-edit `.rfa` для ручного восстановления.
- Перед `delete_family_types` прочитайте `list_family_types(includePlaced=true)`: число
  размещённых экземпляров — это радиус поражения (`type_in_use` без `force=true`).
- Перед `remove_family_parameter` прочитайте `get_family_parameters`: тул покажет формулы,
  которые будут удалены.
- После любой успешной записи — read-back (`get_family_parameters(includeValues=true)`);
  тул делает свою проверку, но итог читается из модели.

### Рецепты
1. «Добавь общий параметр ADSK_Наименование»: `list_shared_parameters(nameContains:""ADSK"")`
   → если нет, `add_family_parameter(name, storageType:""text"", isShared:true,
   createDefinition:true, group:""PG_IDENTITY_DATA"")`.
2. «Перенеси значения из A в B»: `get_family_parameters(includeValues=true)` →
   `map_family_parameters(mappings:[{source:{name:A}, target:{name:B}}], mode:""values"")`.
3. «Поставь формулу длины»: `set_family_parameter_values` для входных параметров →
   `set_family_formula(parameter:""Длина"", formula:""Ширина + 20 mm * 2"")` → read-back значений
   по типам. Константам в формуле всегда указывайте единицы.
4. «Убери лишний тип»: `list_family_types(includePlaced=true)` → `delete_family_types(dryRun=true)`
   → решение о `force` → apply.

### Что нельзя
- Считать `familyId` стабильным после записи: загрузка пересоздаёт семейство.
- Править семейство через `Parameter.Set` без `FamilyManager` — значения не запишутся.
- Ставить значение параметру с формулой, не сняв формулу.
- Удалять типы с экземплярами «на всякий случай» с `force=true` без явного согласия.
";
}