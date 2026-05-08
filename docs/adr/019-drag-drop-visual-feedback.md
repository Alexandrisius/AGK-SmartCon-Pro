# ADR-019: Drag & Drop Visual Feedback Architecture

## Статус

accepted

## Дата

2026-05-09

## Контекст

В модуле FamilyManager реализован drag-and-drop для перемещения семейств между категориями в TreeView. Необходимо было решить проблему мерцания курсора при перетаскивании (cursor flickering), которая возникает из-за [известного бага WPF #111](https://github.com/dotnet/wpf/issues/111): `DragLeave` и `DragEnter` передают `Effects` по ссылке, что приводит к race condition между `PreviewDragOver` и `GiveFeedback`.

## Решение

### 1. Отказ от cursor-based feedback

Вместо управления системным drag cursor (Move/No) мы **полностью скрываем стандартный cursor** (`Mouse.OverrideCursor = Cursors.Arrow`) и предоставляем весь feedback через визуальные adorner'ы:

- **DragAdorner** — визуальный "призрак" перетаскиваемого элемента, следует за курсором
- **DropTargetAdorner** — подсветка категории, на которую можно бросить элемент

### 2. AdornerLayer вместо Popup

Первоначально был рассмотрен `Popup` (как в GongSolutions.WPF.DragDrop v4), но он оказался несовместим с Revit-hosted WPF: создание Popup перед `DoDragDrop` блокирует drag message loop. Решение: `AdornerLayer` на root visual.

```
TreeView Drag Start
  └── Create DragAdorner on AdornerLayer(rootVisual)
  └── GiveFeedback: update adorner position via Mouse.GetPosition()
  └── PreviewDragOver: add/remove DropTargetAdorner on target TreeViewItem
  └── Drop: remove both adorners, execute command
```

### 3. Timestamp workaround для cursor flickering

Для обратной совместимости (если adorner'ы не инициализируются) сохранён timestamp-based workaround:

```csharp
// PreviewDragOver: обновляем timestamp при каждом движении
state.LastDragOverTime = DateTimeOffset.UtcNow;
state.IsOverValidDropTarget = command?.CanExecute(dropInfo) == true;

// GiveFeedback: 50ms threshold даёт "инерцию" состоянию
var elapsed = DateTimeOffset.UtcNow - state.LastDragOverTime;
if (elapsed.TotalMilliseconds > 50)
    Mouse.SetCursor(Cursors.No);
else if (state.IsOverValidDropTarget)
    e.UseDefaultCursors = true;
```

### 4. Архитектурные контракты

- `IDragInfo` / `IDropInfo` — минимальные интерфейсы в `SmartCon.Core`
- `TreeViewDragInfo` / `TreeViewDropInfo` — реализации в `SmartCon.UI.Behaviors`
- `DataObject` с custom format `"SmartCon.TreeViewDrag"` — изоляция от системного DnD

## Последствия

### Положительные

- **Нет мерцания cursor** — всё управление через `IsOverValidDropTarget` + timestamp
- **Богатый visual feedback** — пользователь видит что тащит и куда можно бросить
- **MVVM-чистота** — behavior полностью отделяет UI-логику от ViewModel
- **Enterprise-grade** — соответствует подходам GongSolutions, Telerik, DevExpress

### Отрицательные

- **AdornerLayer dependency** — требует `PresentationSource.FromVisual` для получения root visual
- **DragAdorner статичен** — показывает только DisplayName, не VisualBrush копию элемента
- **Memory pressure** — создаётся Border + TextBlock для каждого drag (небольшой overhead)

## Альтернативы

### Альтернатива A: Popup-based (GongSolutions approach)

**Отклонена:** `Popup` создаёт Win32 окно, которое конфликтует с WPF drag message loop в Revit context. DnD не начинался вообще.

### Альтернатива B: Cursor-only (как было изначально)

**Отклонена:** Невозможно устранить мерцание без либо timestamp-костыля, либо полного отказа от cursor feedback. Timestamp-костыль считается workaround для WPF bug, не решением.

### Альтернатива C: VisualBrush в DragAdorner

**Отложена:** Можно отрисовывать точную копию dragged TreeViewItem через `VisualBrush`. Добавляет сложность при virtualized TreeView (item может быть не загружен). В текущей реализации Border + TextBlock достаточно информативен.

## Реализация

- `SmartCon.UI/DragDrop/DragAdorner.cs` — следует за курсором
- `SmartCon.UI/DragDrop/DropTargetAdorner.cs` — подсветка target
- `SmartCon.UI/Behaviors/TreeViewDragDropBehavior.cs` — attached behavior для TreeView
- `SmartCon.Core/Services/Interfaces/IDragInfo.cs` — контракт payload
- `SmartCon.Core/Services/Interfaces/IDropInfo.cs` — контракт target
