namespace SmartCon.Core.Services.Agent;

public static partial class AgentToolPackSkills
{
    private static IEnumerable<(string Pack, string Markdown)> SheetsSkills() =>
        new[] { ("sheets", Sheets) };

    private const string Sheets = @"## Пак sheets: листы и размещение видов

### Карта тулов
- `list_sheets` — листы: `numberContains`, `nameContains`, `excludePlaceholders`, `fields`;
  `widthMm/heightMm`, `viewCount`, `scheduleCount`, `visualDuplicates[]` (номера, различимые
  только невидимыми символами).
- `get_sheet_content` — состав листа: виды (координаты вьюпорта, мм) и расписания
  (`instanceId`, координаты, фактические `widthMm/heightMm`); `sheetIds` — пакетно, ≤50.
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
  целевом порядке, `numbers[]` или `startNumber`. Перед вызовом ОБЯЗАТЕЛЬНО
  `get_browser_organization(target:""sheets"")`: группировка альбомов решает, где нумерация
  сквозная, а где номер несёт невидимый символ. Через временные номера; undo возвращает всё.
- `sanitize_sheet_numbers` — убрать невидимые символы из номеров (`sheetIds?` — все).
- `delete_sheets` — удаление листов, `sheetIds` ≤50, `confirmNames`.
- `get_titleblock_options` — типы титульников и их редактируемые параметры; `sizeMap[]` —
  «значение параметра → размер» по альбому (чего в альбоме нет — проверяйте черновым листом; та же
  оговорка в `sizeMapNote`); по `sheetId` — параметры экземпляра; `existingSheets[]`,
  `nextNumberHint`.
- `get_view_placement_preview` — бумажный размер видов (≤20): размещённый — box вьюпорта;
  план/разрез с кропом — кроп/масштаб (estimated=true); 3D без кропа — неизвестен.
- `build_system_sheets` — сборщик листов (≤20 items, одна транзакция): лист (номер задан или
  продолжение альбома) + вид в левом верхнем углу РАМКИ листа, целиком внутри (размер окна
  узнаётся размещением; торчит → warning `overflow=true`, пачка не откатывается) + справа
  спека: `scheduleId` (готовый клон) ИЛИ `explication {sourceScheduleId | sourceScheduleIds,
  keepFields[], filterField, filterValue}` — узкая таблица: только keepFields (нет поля →
  warning + skip), фильтр equals, поле фильтра скрыто; `sourceScheduleIds` — МУЛЬТИКАТЕГОРИЙНАЯ
  таблица по всем категориям системы (поля/фильтр — только общие параметры); клон
  «Экспликация <имя листа>» (два пути сразу — invalid_arguments) + `titleBlockParameters`.
  `explicationRows` — строки (0 под фильтром → warning с ближайшими значениями).
  Не переданные параметры титульника наследуются с альбома (`inherited`; `kept` — явные
  параметры изменили значение, альбомное не перезаписано). undo_last удаляет пачку.

### Кухня Revit
- Лист — вид, не размещается на листах; `ViewSheet.Create` требует тип рамки
  (`OST_TitleBlocks`), иначе `title_block_missing` (загрузите титблок).
- Размер листа — в параметрах экземпляра рамки `SHEET_WIDTH`/`SHEET_HEIGHT` (id —
  `ViewSheet.SheetTitleBlockId`).
- Обычные виды — `Viewport.Create`, расписания — `ScheduleSheetInstance.Create` (у
  расписания нет вьюпорта).
- `Viewport.Create` принимает координаты листа в футах; после создания — `Regenerate`, затем
  `GetBoxOutline`/`SetBoxCenter`.
- Один вид нельзя разместить на двух листах (`Viewport.CanAddViewToSheet`): второй лист —
  дубль вида (`View.Duplicate`, WithDetailing/AsDependent); исключения — легенды и расписания.
- «Снять вид с листа» = удалить `Viewport` (вид остаётся); удаление вида удаляет размещения.
- `GetAllViewports()` не видит расписания — собирайте `ScheduleSheetInstance`; «лист пуст»
  по нему — неверный вывод.
- `SheetNumber` уникален (в 2026 — в пределах коллекции листов); дубль бросает исключение.
- Номер: запрещены символы `{ } [ ] | ; < > ? ~`; пустой — тоже ошибка. Дубль «по
  визуальному написанию» Revit НЕ ловит (U+202A/U+202C/U+200B невидимы) — сравнение через
  очистку невидимых символов.
- Координаты `pointMmX/pointMmY` — ЦЕНТР окна (не угол) в СИСТЕМЕ ЛИСТА: начало — plot
  origin титульника, у титульников с собственным origin он смещён, поэтому мм часто отрицательные. Рамку даёт
  `get_sheet_content.sheetOutlineMm` (min/max) — отступы считайте от неё, не от нуля.
- Размер 3D-вида без кропа до размещения неизвестен; спека на листе —
  `get_BoundingBox(sheet)` (включает ~2,1 мм рамки).

### Инварианты
- Перед удалением листа — `get_sheet_content`: виды останутся, пользователь должен знать.
- `delete_sheets` необратим: `dryRun=true`, затем apply с `confirmNames` = `names[]`
  СВЕЖЕГО dry-run; `confirm_names_required`/`confirm_names_mismatch` → повтори dry-run.
- «Поставь вид на лист»: `isOnSheet` (`list_views`) — занятый вид не встанет.
- Write-тулы: `dryRun=true`, затем apply; координаты — из `get_sheet_content`
  (`centerXMm/centerYMm`, см. `sheetOutlineMm`).
- Коллизию после `sanitize_sheet_numbers` тул не перезаписывает: `sheet_number_taken`.

### Рецепты
1. «Что на листе А-101?»: `list_sheets(numberContains:""А-101"")` → `get_sheet_content(sheetId)`.
2. «Найди листы без видов»: `list_sheets(fields=[sheetId,number,viewCount,scheduleCount])`
   → кандидаты с нулями → уточнить `get_sheet_content`.
3. «Создать лист и разложить планы»: `create_sheet(number:""ОВ-01"", name:""План на отм. 0,000"",
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
10. «Собери лист под задачу» — состав берётся из задачи, не из примера: какие виды, какая
   таблица (и нужна ли), по каким категориям, какой фильтр. Чего нет — спроси (два коротких
   вопроса: состав листа и таблицы). Дальше: `list_sheets`/`get_sheet_content` →
   `get_titleblock_options` → `get_view_placement_preview` → `build_system_sheets(items=
   [{viewId, sheetName, titleBlockParameters, scheduleId|explication}])`. Таблица справа:
   готовый клон — `scheduleId`; `explication.sourceScheduleId` — одна категория;
   `sourceScheduleIds` — ВСЕ категории системы одной мультикатегорийной таблицей (поля и
   фильтр — только общие параметры ФОП). Пустая таблица — warning.

### Контракт системного вида
Вид «системы X» показывает ТОЛЬКО элементы этой системы.
- Прочие MEP-категории (воздуховоды, короба, лотки, оборудование) скрывай категориями
  (`set_view_visibility`) или расширенным фильтром. Шаблон/образец переносит только СВОИ
  скрытые категории — «дублируй образец» не гарантирует изоляцию.
- Фильтр по «Имя системы» — только после `list_filterable_parameters`: у лотков и коробов
  параметра может не быть; общий параметр ФОП надёжнее.
- Обязательный read-back: `query_elements(viewId=..., categories=[...])` — что ещё видно.
  `build_system_sheets` отдаёт `hiddenCategories`, но не список видимого — проверяй сам.

### Что нельзя
- Считать `viewCount=0` доказательством пустого листа: расписания считаются отдельно.
- Считать, что шаблон/образец уже скрывает всё лишнее: проверяй видимое `query_elements`.
- Собирать таблицу «по всем категориям системы» клоном одной спецификации: одна
  категория не покажет арматуру, соединительные детали и гибкие трубы — нужен
  `sourceScheduleIds` (мультикатегорийная таблица).
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
