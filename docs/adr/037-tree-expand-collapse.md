# ADR-037: Tree expand/collapse в FamilyManager (Issue #86)

**Status:** accepted
**Date:** 2026-06-25
**Phase:** 27
**Issue:** [#86](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/86)
**Supersedes:** Issue #86 в части «per-folder две кнопки ⏷⏶ в header категории»

## Context

Issue #86 запрашивает функционал «Развернуть всё / Свернуть всё» в дереве категорий FamilyManager (300–500 узлов, 3–4 уровня вложенности). Прямое раскрытие через одинарный chevron expander на каждом узле — 30+ кликов для типичного дерева, неудобно.

В issue был предложен паттерн «per-folder: две маленькие кнопки ⏷⏶ в header каждой категории». **Два предыдущих агента пытались реализовать это буквально** — результат был визуально неприемлем: двойные огромные fill-стрелки поверх встроенного expander-а `ModernTreeViewItem`, плюс конфликт с drag-and-drop. Причина: агенты пытались нарисовать ⏷⏶ как заполненные фигуры (Material Icons Unicode), хотя стиль иконок проекта — тонкие stroke-only 1.5px.

## Decision

**Принят смешанный паттерн:**

1. **Hover-reveal toggle-кнопка в header каждой категории** (одна кнопка, не две):
   - Появляется только при наведении мыши на `TreeViewItem` (`Opacity: 0` → `1`, `IsHitTestVisible: False` → `True`).
   - Клик вызывает `ToggleSubtreeCommand` — если хоть что-то в поддереве свёрнуто → `ExpandSubtree`; иначе → `CollapseSubtree`.
   - **Иконка динамически меняется** через DataTrigger на `IsAnyDescendantCollapsed` (computed property в `CategoryNodeViewModel`):
     - Если поддерево свёрнуто → `UnfoldMoreGeometry` (Material `unfold_more` — X-pattern, "развернуть").
     - Если поддерево развёрнуто → `UnfoldLessGeometry` (Material `unfold_less` — hourglass, "свернуть").
   - Filled style (12×12 px) — соответствует стандарту enterprise (Material Design, Microsoft Fluent UI, VS Code).

2. **Глобальные кнопки в статус-баре** (`ExpandAllTreeCommand` / `CollapseAllTreeCommand`):
   - Расположены справа в нижней панели, перед счётчиком загруженных семейств.
   - Стиль — `Background=Transparent`, `BorderThickness=0`, filled иконка (12×12 px):
     - `ExpandAllTree` → `UnfoldMoreGeometry` (X-pattern).
     - `CollapseAllTree` → `UnfoldLessGeometry` (hourglass).
   - Покрывают сценарий «развернуть вообще всё дерево одним кликом».

3. **Счётчик загруженных семейств** (`TotalItemCount`) перенесён из VM-only в статус-бар:
   - Без текста, только цифра (например `1379`) с tooltip «Загружено семейств: 1379» (локализация `FM_TotalFamiliesTooltip`).
   - Самый правый угол статус-бара.

4. **Контекстное меню (ПКМ) НЕ дополняется** — в нём только команды для категорий (Import / Update / Check), новые пункты не нужны.

## Иконки

Используются **filled PathGeometry** в стиле Material Design (стандарт enterprise UI):

- `UnfoldMoreGeometry` — X-pattern: верхний Λ + нижний V. Семантика: "расширение во все стороны" (expand all).
- `UnfoldLessGeometry` — hourglass: верхний V + нижний Λ. Семантика: "сжатие к центру" (collapse all).

Геометрии взяты из официальных Material Icons (Google, Apache 2.0 license), viewbox 0 0 24 24, масштабированы через `<Viewbox Width="12" Height="12">`.

**Альтернативы, рассмотренные и отвергнутые:**
- Двойной chevron в одну сторону (⏷⏶ Unicode) — НЕ интуитивно: одна стрелка не сигнализирует двунаправленное действие.
- Stroke-only chevron-ы (как встроенный expander) — слишком тонкие при 12px, теряются на фоне текста.
- Текстовые кнопки "Развернуть всё" — занимают много места в статус-баре.
- Material `unfold_more` / `unfold_less` (filled) — **выбран**: семантически правильно, стандартно в VS Code / Fluent UI / Material Design приложениях.

## Почему не per-folder (issue #86 буквально)

- Встроенный expander в `ModernTreeViewItem` (`Generic.xaml:627-658`) уже рисует одинарный chevron 16×16 слева от текста категории. Добавление ЕЩЁ двух кнопок ⏷⏶ в header даёт **3 chevron-а рядом** — визуальный шум.
- Drag-and-drop из header-а конфликтует с Button-ом — нужно либо `e.Handled = true` в code-behind, либо hover-reveal подход (кнопка невидима пока пользователь не наведёт → drag из header-а работает по умолчанию).
- Hover-reveal с одной кнопкой (а не двумя) — стандартный паттерн VS Solution Explorer: «Expand All» / «Collapse All» для поддерева делается одной кнопкой с toggle-логикой.

## Реализация

**Новые файлы:**
- `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.TreeExpand.cs` — pure logic + `[RelayCommand]` обёртки.
- `src/SmartCon.UI/Converters/TotalItemCountToTooltipConverter.cs` — локализованный tooltip для счётчика.
- `src/SmartCon.Tests/FamilyManager/ViewModels/FamilyManagerMainExpandCollapseTests.cs` — 10 unit-тестов.

**Изменено:**
- `src/SmartCon.UI/Generic.xaml:595-600` — добавлены `ChevronDoubleDownGeometry` и `ChevronDoubleUpGeometry`.
- `src/SmartCon.FamilyManager/Views/FamilyManagerPaneControl.xaml:624-680` — `HierarchicalDataTemplate` для `CategoryNodeViewModel` превращён из `<StackPanel>` в `<Grid>` с двумя колонками; добавлена hover-reveal кнопка `ToggleSubtreeCommand`.
- `src/SmartCon.FamilyManager/Views/FamilyManagerPaneControl.xaml:692-740` — статус-бар расширен до `<Grid>` с тремя колонками: StatusMessage | кнопки Expand/Collapse All | счётчик.
- `src/SmartCon.Core/Services/LocalizationService.Keys.FamilyManager.cs:344-355` — 4 новых ключа (`FM_ToggleSubtreeTooltip`, `FM_ExpandAllTooltip`, `FM_CollapseAllTooltip`, `FM_TotalFamiliesTooltip`).

## Альтернативы (рассмотренные)

- **Per-folder две кнопки ⏷⏶** — отвергнуто (см. выше).
- **Глобальные кнопки БЕЗ hover-reveal** — отвергнуто: пользователь явно попросил оба варианта.
- **ПКМ-команды** — отвергнуто: контекстное меню категории уже использует 3 команды (Import / Update / Check); новые пункты были бы лишними и шумными.
- **Кастомный Expander вместо TreeViewItem** — отвергнуто: ломает ModernTreeViewItem, drag-and-drop, multi-select (Issue #70).

## Edge cases

- **Drag-and-drop:** hover-reveal кнопка невидима по умолчанию → drag из header-а работает без изменений. При hover кнопка становится `IsHitTestVisible=True`, но мышь обычно не начинает drag во время наведения (drag стартует с `MouseDown` + минимальная задержка).
- **net48 совместимость:** `DataTrigger` с `RelativeSource AncestorType=TreeViewItem` внутри `HierarchicalDataTemplate` протестирован в R24 (net48) и R25 (net8). Обе сборки работают.
- **UI virtualization:** не используется для каталога (300–500 узлов), все контейнеры гарантированно существуют к моменту клика.
- **Активный поиск:** при `SearchText != null` дерево уже раскрыто полностью через `expandAll` в `LoadTreeAsync` — дополнительных действий не требуется.
- **Hover-reveal через DataTrigger:** если в будущем добавится UI virtualization, может потребоваться переход на `ItemContainerStyle`-based подход с attached property — отметим как tech debt.

## Verification

- Build R25: 0 warnings / 0 errors
- Build R24: 0 warnings / 0 errors
- Tests: 10/10 новых + все существующие зелёные
- Manual test: открыть проект с 50+ категориями → нажать ⏶ на корневой → всё свернулось; ⏷ → всё развернулось; hover на категорию → появилась toggle-кнопка справа → клик развернул/свернул поддерево
- DnD: drag из header-а папки (не с кнопки) → семейство перетаскивается между категориями

## Out of scope

- Хоткеи (Ctrl+Shift+E, Ctrl+Shift+C) — отдельный issue.
- Запоминание состояния раскрытости между сессиями — отдельный issue.
- Анимация раскрытия — WPF по умолчанию.