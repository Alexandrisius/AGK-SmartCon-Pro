# ADR-038: Nested Sticky Category Headers для TreeView категорий

## Статус

accepted

## Дата

2026-06-26

## Контекст

В модуле FamilyManager категории семейств организованы в иерархию без ограничения
глубины вложенности (как папки в проводнике Windows). Когда категорий становится
много (50–200) и пользователь разворачивает несколько уровней, вертикальный
скролл TreeView выходит за пределы viewport dockable pane — пользователь теряет
контекст: текущая Category-строка уезжает за верх viewport вместе с Family/Type-нодами.

Популярные паттерны решения:

- **iOS contact list** — заголовки секций прилипают к верху и вытесняют друг друга
- **Excel freeze panes** — несколько строк замораживаются сверху
- **DevExpress `AllowFixedGroups`** — оставляет родительские группы видимыми

Целевая глубина вложенности — **3–5 уровней**. Каждая прилипшая строка ≈ 22 px,
стек из 5 заголовков ≈ 110 px — приемлемо.

## Решение

### 1. Overlay-архитектура (не `TranslateTransform`)

Семь попыток реализации через `TranslateTransform` на `TreeViewItem.HeaderRow`
были отклонены: WPF рисует `TreeViewItem` внутри `ContentPresenter`, и
`Panel.ZIndex` на ContentPresenter не поднимает Z-порядок относительно sibling
TreeViewItem. Альтернатива — развернуть `HeaderRow` всех Category одновременно,
что невозможно при каскаде.

**Финальный подход:** отдельный `Border` overlay (panel `ZIndex=1000`) поверх
TreeView, в который на каждый `ScrollChanged` / `LayoutUpdated` добавляются
прилипшие Border-ы для каждой Category в стеке. Overlay рисуется всегда поверх
TreeView.

### 2. Каскадное закрепление (`StickyCascadingStackBuilder`)

Чтобы категория прилипала в момент, когда она **скрывается за уже закреплёнными
строками** (а не только когда достигает верхней границы viewport), boundary
вычисляется итеративно:

```
occupiedTop = 0
while occupiedTop < viewportHeight:
    candidate = категория с max(top) среди тех, у которых top < occupiedTop
    if candidate == -1: break
    добавить путь от candidate до root в стек
    occupiedTop += sum of heights newly added
```

Это даёт «каскад» — следующий sticky-header прилипает к низу предыдущего, как в
iOS section headers.

### 3. Чистые pure-функции для тестирования

Алгоритм вынесен в `StickyCascadingStackBuilder.BuildCascadingStack(tops, heights,
parents, viewportHeight)` — pure C#, без WPF-зависимостей, покрыт 8 unit-тестами
в `StickyCascadingStackBuilderTests.cs`.

### 4. VM-иерархия через `Parent`-ссылку

В `CatalogTreeNodeViewModel` добавлено свойство `Parent` (auto-set через кастомный
`Children` setter и подписку на `CollectionChanged`). Это исключает необходимость
вручную проставлять Parent в 8 production-местах построения дерева.

Реальный стек строится через индексный массив `parentIndicesList` =
`allCategoryVMs.IndexOf(vm.Parent)` — не требует обхода VM-объектов.

### 5. Hit-test в overlay

`StickyOverlay` Border имеет `IsHitTestVisible="True"` и `Background="Transparent"`,
чтобы вся область sticky перехватывала клики (а не только отдельные строки).
Без этого дочерние overlay-Border'ы не получали hit-test (Microsoft Learn:
если parent `IsHitTestVisible=False`, effective value у children тоже false).

`PreviewMouseWheel` обрабатывается в Behavior и перенаправляется в
`ScrollViewer`, чтобы скролл колесом работал над sticky-областью.

### 6. Клик по sticky-строке

При клике вызывается `ScrollCategoryBelowStickyBar`, который вычисляет target
scroll offset с учётом высоты sticky-bar:
```
targetOffset = scrollOffset + topInScp - occupiedTop
```
Так категория появляется прямо **под** sticky-bar, а не прячется за ним.

### 7. Отступы как в дереве

Padding overlay-строки `left = 24 + 20 * depth` — чтобы текст категории в
sticky-строке совпадал по горизонтали с текстом в обычном дереве
(Border.Padding 4 + ToggleButton 16 + margin 4 + FolderIcon 14 + margin 6 = 44,
плюс 20 px за уровень вложенности).

## Рассмотренные альтернативы

### A. Один overlay-header (TextBlock поверх TreeView)

Один TextBlock-оверлей с именем текущей Category.

**Отклонено:** не показывает стек родителей.

### B. Single breadcrumb (одна строка с "A > B > C")

Один sticky-header с путём через `>`.

**Отклонено:** строка не кликабельна по сегментам.

### C. Готовая библиотека (Telerik / DevExpress / Xceed)

Все три поддерживают sticky group headers, но **только для Grid** — не TreeView.
Замена TreeView ломает DnD, контекстные меню, иерархическую навигацию.

### D. `TranslateTransform` на `TreeViewItem.HeaderRow`

**Отклонено** после 7 итераций: `Panel.ZIndex` на ContentPresenter не поднимает
Z-порядок между sibling TreeViewItem. Альтернативный подход с развернутыми
header'ами всех Category одновременно не работает при каскаде.

### E. `ElementCompositionPreview` (Composition API)

Работает только в Windows 10+ через `System.Windows.Media.Composition`. Не
доступен в net48 WPF (R19-R21).

## Архитектура

### `StickyCascadingStackBuilder` (pure C#)

```csharp
public static class StickyCascadingStackBuilder
{
    public static IReadOnlyList<int> BuildCascadingStack(
        IReadOnlyList<double> tops,
        IReadOnlyList<double> heights,
        IReadOnlyList<int> parentIndices,
        double viewportHeight);
}
```

Возвращает индексы в порядке root → current. Учитывает `PositiveInfinity` для
collapsed категорий (пропускает их).

### `StickyCategoryHeaderBehavior` (attached behavior)

```csharp
public static class StickyCategoryHeaderBehavior
{
    public static readonly DependencyProperty EnabledProperty = ...;
    public static readonly DependencyProperty OverlayProperty = ...;
}
```

Подключается через XAML:
```xml
<TreeView fmbehaviors:StickyCategoryHeaderBehavior.Enabled="True"
          fmbehaviors:StickyCategoryHeaderBehavior.Overlay="{Binding ElementName=StickyStack}" />
```

Алгоритм `RecalculateSticky`:

1. Собрать все materialised `CategoryTreeViewItem` из visual tree (на каждый вызов — без кэша).
2. Собрать все `CategoryNodeViewModel` через `vm.Children` (pre-order).
3. Построить `tops[]` (через `TransformToAncestor(ScrollContentPresenter)`) и `parentIndices[]` (через `vm.Parent`).
4. Вызвать `StickyCascadingStackBuilder.BuildCascadingStack`.
5. Перестроить overlay-StackPanel: для каждого индекса в стеке — `Border` с иконкой, именем, отступом по depth и кликом-прокси.

Триггеры пересчёта:
- `ScrollViewer.ScrollChanged` — основной
- `TreeView.LayoutUpdated` — expand/collapse/resize
- `OverlayBorder.PreviewMouseWheel` — форвардинг скролла

### VM изменения

```csharp
public abstract partial class CatalogTreeNodeViewModel : ObservableObject
{
    public CatalogTreeNodeViewModel? Parent { get; internal set; }

    private ObservableCollection<CatalogTreeNodeViewModel>? _children;
    public ObservableCollection<CatalogTreeNodeViewModel> Children
    {
        get { /* lazy init + подписка на CollectionChanged */ }
        set { /* подписка + присвоение Parent для существующих */ }
    }
}
```

Кастомный setter `Children` подписывается на `CollectionChanged` и **автоматически
проставляет Parent** для добавляемых детей (и снимает для удаляемых). Это
исключает необходимость менять 8 production-мест построения дерева.

### XAML

`ModernTreeViewItem` в `Generic.xaml` имеет `x:Name="HeaderRow"` — оставлен для
совместимости, но **не используется** behavior'ом (overlay рисует собственные
Border'ы).

`StickyOverlayCategoryHeaderStyle` в `Generic.xaml` — стиль для overlay-Border'ов:
Background, BorderThickness="0,0,0,1", Padding="8,2", MinHeight="22",
`DropShadowEffect` для визуального акцента.

## Совместимость

| Компонент | Реакция |
|---|---|
| Drag-and-Drop | Overlay с `IsHitTestVisible=True` блокирует клики по sticky-области, но DnD за пределами sticky работает как обычно |
| Контекстные меню | ПКМ на оригинальной Category (не на overlay) — overlay не показывает ContextMenu |
| AutoScrollToSelectedItem | Не мешает |
| AutoExpand при DragHover (500ms) | Не мешает |
| `__no_category__` нода | Участвует как обычная Category |
| net48 / R19-R21 | `VisualTreeHelper`, `TransformToAncestor`, attached properties — всё доступно |
| Производительность | `CollectCategoryItems` обходит visual tree (без виртуализации) — при 200 категориях < 1 мс на ScrollChanged |

## Виртуализация TreeView

**НЕ включаем UI virtualization.** Обоснование:

1. WPF TreeView не поддерживает виртуализацию по умолчанию
2. Включение может сломать Drag-and-Drop (TreeViewDragDropBehavior использует контейнеры напрямую)
3. AutoExpand при DragHover требует, чтобы контейнеры всех развёрнутых категорий существовали в visual tree
4. Sticky header сам требует, чтобы все CategoryTreeViewItem были созданы
5. Реальный объём: 100–300 семейств, 50–200 категорий — работает приемлемо

При включении виртуализации в будущем нужно адаптировать `CollectCategoryItems`
(всё равно обходит visual tree, что совместимо с `ContainerFromIndex` для
виртуализированных TVI).

## План отката

Одна строка в `FamilyManagerPaneControl.xaml`:
```diff
- fmbehaviors:StickyCategoryHeaderBehavior.Enabled="True"
```

Полный откат без побочных эффектов. `Parent` в `CatalogTreeNodeViewModel`
остаётся (безвредна, удаляется отдельным коммитом).

## Файлы

**Создано:**
- `src/SmartCon.FamilyManager/Behaviors/StickyCategoryHeaderBehavior.cs` — главный attached behavior
- `src/SmartCon.FamilyManager/Behaviors/StickyCascadingStackBuilder.cs` — pure-функция каскада
- `src/SmartCon.Tests/FamilyManager/Behaviors/StickyCascadingStackBuilderTests.cs` — 8 unit-тестов
- `docs/adr/038-sticky-category-headers.md` — этот документ

**Изменено:**
- `src/SmartCon.FamilyManager/ViewModels/CatalogTreeNodeViewModel.cs` — добавлен `Parent`, кастомный `Children` setter с автопривязкой
- `src/SmartCon.UI/Generic.xaml` — `ModernTreeViewItem`: `x:Name="HeaderRow"`; добавлен `StickyOverlayCategoryHeaderStyle`
- `src/SmartCon.FamilyManager/Views/FamilyManagerPaneControl.xaml` — TreeView обёрнут в Grid, добавлен overlay Border + StackPanel

**Не затронуто:**
- Production-код построения дерева (8 мест) — Parent проставляется автоматически
- DnD behavior (`TreeViewDragDropBehavior`) — не конфликтует
- Контекстные меню — работают на оригинальных TVI
- AutoScrollToSelectedItem — не мешает
- `SmartCon.Core` — без изменений

## Метрики успеха

- При скролле вложенной Category пользователь видит весь путь до корня (каскад)
- Каждая следующая Category прилипает в момент, когда она скрывается за уже закреплёнными строками, а не только при достижении верхней границы viewport
- Клик на промежуточной Category в стеке скроллит TreeView так, чтобы категория оказалась ПОД sticky-bar
- Клики по sticky-строкам блокируются (не проходят к TreeViewItem под ними)
- Скролл колесом над sticky-областью работает (форвардинг в ScrollViewer)
- DnD семейств между категориями продолжает работать
- Контекстное меню (ПКМ) открывается на правильной Category
- Build R25, R24, R21, R19: 0 warnings / 0 errors
- Unit-тесты: 1578/1578 passed