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
  (`instanceId`, координаты); `sheetIds` — пакетно, ≤50.
- `create_sheet` — создать лист: `number` (уникален), `name?`, `titleBlockTypeId?`.
- `create_viewports` — разместить виды на листе пакетно (≤100, одна транзакция):
  `sheetId` + `items[{viewId, pointMmX, pointMmY}]`.
- `move_viewport` — переместить видовое окно: `viewportId` + новый центр `pointMmX/pointMmY`.
- `set_sheet_number` / `rename_sheet` — номер и имя листа (лист по `sheetId`/номеру/имени).
- `sanitize_sheet_numbers` — убрать невидимые символы из номеров (`sheetIds?` — все).
- `delete_sheets` — удаление листов, `sheetIds` ≤50.

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

### Инварианты
- Прежде чем удалять лист, прочитайте `get_sheet_content`: размещённые виды останутся в
  проекте (удаляются только размещения), но пользователь должен это знать.
- `delete_sheets` — необратимо (`undoSupported=false`); всегда сначала `dryRun=true`.
- Для «поставь вид на лист» проверяйте `isOnSheet` (`list_views`): занятый вид на второй
  лист не встанет.
- Write-тулы: сначала `dryRun=true` (по умолчанию), покажите план, потом `dryRun=false`.
  Координаты для create_viewports/move_viewport берите из `get_sheet_content`
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
4. «Перенумеровать лист»: `set_sheet_number(sheetNumber:""АР-1"", newNumber:""АР-101"")`.
5. «Номер выглядит нормально, но Revit говорит „занят“»: `list_sheets` →
   `hasInvisibleCharacters`/`numberCodePoints` → `sanitize_sheet_numbers(dryRun=false)`.
6. «Удали пустые листы»: `list_sheets` (viewCount/scheduleCount) → `get_sheet_content`
   каждого кандидата → `delete_sheets(dryRun=true)` → подтвердить список → `dryRun=false`.

### Что нельзя
- Считать `viewCount=0` доказательством пустого листа: расписания считаются отдельно.
- Удалять вид, чтобы освободить его для другого листа: удаляйте вьюпорт.
- Ставить один вид на два листа без дублирования.
- Сдвигать вьюпорт «на сколько-то мм» — только абсолютная точка (центр); посчитайте
  новую позицию сами по текущим координатам из `get_sheet_content`.
";
}
