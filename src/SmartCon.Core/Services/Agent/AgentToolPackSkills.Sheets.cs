namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> SheetsSkills() =>
        new[] { ("sheets", Sheets) };

    private const string Sheets = @"## Пак sheets: листы и размещение видов

### Карта тулов
- `list_sheets` — листы: `numberContains`, `nameContains`, `excludePlaceholders`, `fields`.
  Возвращает `widthMm/heightMm`, `viewCount`, `scheduleCount` и `visualDuplicates[]` —
  номера, которые выглядят одинаково, но различаются невидимыми символами.
- `get_sheet_content` — состав листа: виды (координаты вьюпорта в мм) и расписания
  (`instanceId`, координаты, фактические `widthMm/heightMm` по bbox экземпляра);
  `sheetIds` — пакетно, ≤50.
- `create_sheet` — создать лист: `number` (уникален), `name?`, `titleBlockTypeId?`.
- `create_viewports` — разместить виды на листе пакетно (≤100, одна транзакция):
  `sheetId` + `items[{viewId, pointMmX, pointMmY}]`.
- `move_viewport` — переместить видовое окно: `viewportId` + новый центр `pointMmX/pointMmY`.
- `move_viewports` — пакетное перемещение окон (≤100, одна транзакция):
  `items[{viewportId, pointMmX, pointMmY}]` или `shiftMm {dx,dy}` — сдвиг каждого окна
  от текущего центра (координаты в items игнорируются).
- `remove_viewports` — снять виды с листа (≤100, одна транзакция): `viewportIds[]` или
  `sheetId`+`viewIds[]`. Удаляются только Viewport — виды остаются (`viewsKept`); undo
  восстанавливает размещения по записанным координатам.
- `set_sheet_number` / `rename_sheet` — номер и имя одного листа (лист по
  `sheetId`/номеру/имени).
- `renumber_sheets` — перенумеровать блок листов (≤50, одна транзакция): `sheetIds[]` в
  целевом порядке, номера `numbers[]` по порядку или `startNumber` («ОВ-5» → ОВ-5,
  ОВ-6...). Перед вызовом ОБЯЗАТЕЛЬНО `get_browser_organization(target:""sheets"")`:
  группировка альбомов решает, где нумерация продолжается сквозной, а где номер несёт
  невидимый символ на стыке. Через временные номера: вставка в середину без коллизий;
  undo возвращает все номера.
- `sanitize_sheet_numbers` — убрать невидимые символы из номеров (`sheetIds?` — все).
- `delete_sheets` — удаление листов, `sheetIds` ≤50, `confirmNames`.
- `get_titleblock_options` — типы титульников и их редактируемые параметры (кратность
  размера / формат листа); `sizeMap[]` — «кратность → размер» по листам альбома (чего в
  альбоме нет — неизвестно, проверяйте черновым листом; та же оговорка в `sizeMapNote`);
  по `sheetId` — параметры экземпляра; плюс `existingSheets[]` и `nextNumberHint`.
- `get_view_placement_preview` — бумажный размер видов (≤20): размещённый — box вьюпорта;
  план/разрез с кропом — кроп/масштаб (estimated=true); 3D без кропа — неизвестен.
- `build_system_sheets` — сборщик листов (≤20 items, одна транзакция): лист (номер задан или
  продолжение альбома) + вид в левом верхнем углу РАМКИ листа, целиком внутри границ (размер
  окна узнаётся размещением: place → Regenerate → GetBoxOutline → SetBoxCenter → проверка;
  торчит → warning `overflow=true`, пачка не откатывается) + справа от вида спека по
  `scheduleId` ЛИБО блок `explication {sourceScheduleId, keepFields[], filterField,
  filterValue}` — тул клонирует общую спецификацию в узкую таблицу: только keepFields
  (отсутствующая → warning + skip), фильтр equals, поле фильтра скрыто; клон
  «Экспликация <имя листа>» (два пути сразу — invalid_arguments) + `titleBlockParameters`
  (кратность); `explicationRows` — строки клона (0 под фильтром → warning с ближайшими
  значениями). НЕ переданные параметры титульника наследуются с листов альбома того же типа
  (`status:""inherited""`; `kept` — явные параметры изменили значение, альбомное не
  перезаписано) — кратность не сбрасывается в 0. undo_last удаляет пачку.

### Кухня Revit
- Лист (`ViewSheet`) — вид, но не размещается на листах. `ViewSheet.Create` требует тип рамки
  (`FamilySymbol` категории `OST_TitleBlocks`); нет ни одной — `title_block_missing`
  (загрузите титблок).
- Размер листа — в параметрах экземпляра рамки `SHEET_WIDTH`/`SHEET_HEIGHT` (id —
  `ViewSheet.SheetTitleBlockId`).
- Обычные виды — `Viewport.Create`, расписания — `ScheduleSheetInstance.Create` (у
  расписания нет вьюпорта).
- `Viewport.Create` принимает координаты листа в футах; после создания — `Regenerate`, затем
  `GetBoxOutline`/`SetBoxCenter`.
- Один вид нельзя разместить на двух листах (`Viewport.CanAddViewToSheet`): второй лист —
  дубль вида (`View.Duplicate`, WithDetailing/AsDependent); исключения — легенды и расписания.
- «Снять вид с листа» = удалить `Viewport` (вид остаётся); удаление вида удаляет размещения.
- `ViewSheet.GetAllViewports()` **не показывает расписания** — их надо собирать отдельно
  (`ScheduleSheetInstance`). «Лист пуст» по одному GetAllViewports — неверный вывод.
- `SheetNumber` уникален (в 2026 — в пределах коллекции листов); дубль бросает исключение.
- Номер: запрещены символы `{ } [ ] | ; < > ? ~`; пустой — тоже ошибка. Дубль «по
  визуальному написанию» Revit НЕ ловит (U+202A/U+202C/U+200B невидимы) — сравнение через
  очистку невидимых символов.
- Координаты `pointMmX/pointMmY` — это ЦЕНТР видового окна (не угол!), в мм в СИСТЕМЕ
  ЛИСТА: начало — точка вставки/plot origin титульника, у ADSK она смещена, поэтому
  координаты часто отрицательные. Реальную рамку листа даёт `get_sheet_content.sheetOutlineMm`
  (min/max в той же системе) — считайте отступы от неё, а не от нуля.
- Бумажный размер 3D-вида без кропа до размещения неизвестен (API не даёт extents) —
  сборщик размещает сначала, меряет потом. Размер спеки на листе —
  `instance.get_BoundingBox(sheet)` (включает рамку выделения ~2,1 мм вокруг таблицы).

### Инварианты
- Перед удалением листа — `get_sheet_content`: виды останутся (удаляются только размещения),
  пользователь должен это знать.
- `delete_sheets` необратим: всегда `dryRun=true`, затем apply с `confirmNames` = `names[]`
  СВЕЖЕГО dry-run (формат «номер - имя»); `confirm_names_required`/`confirm_names_mismatch` →
  повтори dry-run и передай свежий names[].
- Для «поставь вид на лист» проверяйте `isOnSheet` (`list_views`): занятый вид на второй
  лист не встанет.
- Write-тулы: сначала `dryRun=true` (по умолчанию), покажите план, потом `dryRun=false`.
  Координаты для create_viewports/move_viewport(s) берите из `get_sheet_content`
  (`centerXMm/centerYMm` — в системе листа, см. `sheetOutlineMm`).
- Коллизию после `sanitize_sheet_numbers` тул не перезаписывает: `sheet_number_taken`,
  решение — вручную.

### Рецепты
1. «Что на листе А-101?»: `list_sheets(numberContains:""А-101"")` → `get_sheet_content(sheetId)`.
2. «Найди листы без видов»: `list_sheets(fields=[sheetId,number,viewCount,scheduleCount])`
   → кандидаты с нулями → уточнить `get_sheet_content`.
3. «Создать лист и разложить планы»: `create_sheet(number:""ОВ-01"", name:""План 1 этажа"",
   dryRun=false)` → `list_views` (ids свободных планов) → `create_viewports(sheetId,
   items=[{viewId, pointMmX, pointMmY}], dryRun=false)` → `get_sheet_content` — проверка.
4. «Сдвинуть все виды на листе вправо на 100 мм»: `move_viewports(items=[{viewportId}...],
   shiftMm={""dx"":100,""dy"":0}, dryRun=false)` — центры берутся текущие.
5. «Снять виды с листа» (виды оставить): `get_sheet_content` →
   `remove_viewports(viewportIds=[...], dryRun=false)` → `viewsKept` — виды живы.
6. «Вставить номер в середину» (ОВ-1,ОВ-2 → ОВ-1,ОВ-3,ОВ-2):
   `get_browser_organization(target:""sheets"")` (альбомы!) →
   `renumber_sheets(sheetIds=[порядок], numbers=[""ОВ-1"",""ОВ-3"",""ОВ-2""],
   dryRun=false)`; сквозная последовательность — `startNumber:""ОВ-5""`.
7. «Перенумеровать один лист»: `set_sheet_number(sheetNumber:""АР-1"", newNumber:""АР-101"")`.
8. «Номер выглядит нормально, но Revit говорит „занят“»: `list_sheets` →
   `hasInvisibleCharacters`/`numberCodePoints` → `sanitize_sheet_numbers(dryRun=false)`.
9. «Удали пустые листы»: `list_sheets` (viewCount/scheduleCount) → `get_sheet_content`
   каждого кандидата → `delete_sheets(dryRun=true)` → подтвердить список →
   `delete_sheets(dryRun=false, confirmNames=names[])`.
10. «Листы для 3D-видов систем» (вид слева вверху + экспликация справа, номера продолжают
   альбом): `get_titleblock_options` (редактируемый параметр кратности размера,
   nextNumberHint) → `get_view_placement_preview(viewIds)` (крупный вид → большее значение
   кратности; у 3D размер узнаётся только размещением) → `build_system_sheets(items=[{viewId,
   sheetName, explication:{sourceScheduleId, keepFields:[""Наименование"",""Единица
   измерения"",""Количество""], filterField:""АДСК_ИмяСистемы"", filterValue:имя_системы},
   titleBlockParameters:{кратность:N}}], dryRun=false)` — экспликация соберётся сама (узкий
   клон + фильтр equals + скрытая колонка фильтра). Готовый клон из `clone_schedule` — только
   когда нужна нестандартная обработка (маппинг полей ФОП): тогда вместо explication
   передай `scheduleId`.

### Что нельзя
- Считать `viewCount=0` доказательством пустого листа: расписания считаются отдельно.
- Удалять вид, чтобы освободить его для другого листа: снимайте вьюпорт
  (`remove_viewports`) — вид остаётся в проекте.
- Ставить один вид на два листа без дублирования.
- Двигать десятки окон по одному (`move_viewport` в цикле) — для этого
  `move_viewports` (одна транзакция, один change set); сдвиг — `shiftMm`,
  абсолютные центры — `pointMmX/pointMmY`.
- Чистить невидимые символы в номерах, не проверив альбомы: сквозная нумерация по
  альбомам держится именно на них. Сначала `get_browser_organization(target:""sheets"")` и
  вопрос пользователю, потом `sanitize_sheet_numbers`.
- Перенумеровывать цепочку листов циклом `set_sheet_number` (промежуточные
  коллизии) — для этого `renumber_sheets` (временные номера внутри).
";
}
