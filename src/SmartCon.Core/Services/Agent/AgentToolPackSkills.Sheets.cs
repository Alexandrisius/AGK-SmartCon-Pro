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
  ОВ-6...). Через временные номера: вставка в середину без промежуточных коллизий;
  undo возвращает все номера.
- `sanitize_sheet_numbers` — убрать невидимые символы из номеров (`sheetIds?` — все).
- `delete_sheets` — удаление листов, `sheetIds` ≤50, `confirmNames`.
- `get_titleblock_options` — типы титульников + их редактируемые параметры (кратность
  размера / формат листа), `sizeMap[]` — таблица «кратность → размер» по листам альбома
  (значения, которых в альбоме нет, неизвестны — проверяйте черновым листом), по
  `sheetId` — параметры экземпляра титульника листа; плюс `existingSheets[]` и
  `nextNumberHint` (продолжение альбома).
- `get_view_placement_preview` — бумажный размер видов (≤20): размещённый — фактический box
  вьюпорта; план/разрез с кропом — кроп/масштаб (estimated=true); 3D без кропа — размер до
  размещения неизвестен (API не даёт).
- `build_system_sheets` — сборщик листов (≤20 items, одна транзакция): лист (номер задан или
  продолжение альбома) + вид в левом верхнем углу ЦЕЛИКОМ внутри границ (размер окна узнаётся
  только размещением: place → Regenerate → GetBoxOutline → SetBoxCenter → проверка границ;
  торчит → warning `overflow=true`, пачка не откатывается) + справа от вида ЛИБО спека по
  `scheduleId` (готовый клон), ЛИБО блок `explication {sourceScheduleId, keepFields[],
  filterField, filterValue}` — тул сам клонирует общую спецификацию в узкую таблицу: видимые
  только keepFields-колонки (отсутствующая → warning + skip), фильтр equals filterField=
  filterValue, поле фильтра — скрытая колонка; клон «Экспликация <имя листа>» (два пути
  одновременно — invalid_arguments) + `titleBlockParameters` титульника (кратность);
  `explicationRows` — строки клона (0 строк под фильтром → warning с ближайшими
  значениями поля); НЕ переданные параметры титульника наследуются с листов альбома
  того же типа (status ""inherited"") — кратность не сбрасывается в 0.
  undo_last удаляет пачку (лист, вид, клон, выноску).

### Кухня Revit
- Лист (`ViewSheet`) — вид, но не размещается на листах. `ViewSheet.Create` требует тип
  рамки (`FamilySymbol` категории `OST_TitleBlocks`); нет ни одной — `title_block_missing`
  (сначала загрузите семейство титблока).
- Размер листа хранится **не на листе**, а в параметрах экземпляра рамки `SHEET_WIDTH`/
  `SHEET_HEIGHT`; id рамки — `ViewSheet.SheetTitleBlockId`.
- Обычные виды ставятся на лист через `Viewport.Create`, расписания — через
  `ScheduleSheetInstance.Create` (у расписания нет вьюпорта).
- `Viewport.Create` принимает координаты листа во внутренних единицах (футы), `[0,0]` —
  левый нижний угол; после создания нужен `Regenerate`, затем позиционирование
  `GetBoxOutline`/`SetBoxCenter`.
- Один вид нельзя разместить на двух листах (`Viewport.CanAddViewToSheet`): для второго
  листа вид дублируют (`View.Duplicate`: WithDetailing / AsDependent). Исключения — легенды
  и расписания.
- «Снять вид с листа» = удалить `Viewport`; сам вид остаётся в проекте. Обратное неверно:
  удаление вида удаляет его размещения.
- `ViewSheet.GetAllViewports()` **не показывает расписания** — их надо собирать отдельно
  (`ScheduleSheetInstance`). «Лист пуст» по одному GetAllViewports — неверный вывод.
- `SheetNumber` уникален (в 2026 — в пределах коллекции листов); дубль бросает исключение.
- Номер: запрещены символы `{ } [ ] | ; < > ? ~`; пустой — тоже ошибка. Дубль «по
  визуальному написанию» Revit НЕ ловит (U+202A/U+202C/U+200B невидимы) — сравнение через
  очистку невидимых символов.
- Координаты `pointMmX/pointMmY` — это ЦЕНТР видового окна (не угол!), в мм от левого
  нижнего угла листа; внутренние единицы Revit — футы, конвертация только на границе.
- Бумажный размер 3D-вида без кропа до размещения неизвестен (API не даёт extents) —
  сборщик размещает сначала, меряет потом. Размер спеки на листе —
  `instance.get_BoundingBox(sheet)` (включает рамку выделения ~2,1 мм вокруг таблицы).

### Инварианты
- Прежде чем удалять лист, прочитайте `get_sheet_content`: размещённые виды останутся в
  проекте (удаляются только размещения), но пользователь должен это знать.
- `delete_sheets` — необратимо (`undoSupported=false`); всегда сначала `dryRun=true`, а apply —
  с `confirmNames` = `names[]` СВЕЖЕГО dry-run (формат «номер - имя»). Отказ
  `confirm_names_required`/`confirm_names_mismatch` = модель изменилась: повтори dry-run и
  передай его свежий names[].
- Для «поставь вид на лист» проверяйте `isOnSheet` (`list_views`): занятый вид на второй
  лист не встанет.
- Write-тулы: сначала `dryRun=true` (по умолчанию), покажите план, потом `dryRun=false`.
  Координаты для create_viewports/move_viewport(s) берите из `get_sheet_content`
  (`centerXMm/centerYMm` — уже в мм от левого нижнего угла листа).
- Коллизию номера после очистки `sanitize_sheet_numbers` не перезаписывает молча — лист
  помечается `sheet_number_taken` и разрешается вручную.

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
- Перенумеровывать цепочку листов циклом `set_sheet_number` (промежуточные
  коллизии) — для этого `renumber_sheets` (временные номера внутри).
";
}
