# ADR-044: FamilyManager Content Tab Redesign — Dual-Pane Layout

**Date:** 2026-07-08  
**Status:** accepted  
**Related:** ADR-042 (3D Preview — auto-extracted GLB assets), Issue #109 (auto-GLB засорение Content tab)

## Context

Вкладка «Контент» в диалоге Properties семейства имела 7 Expander'ов (по одному на `FamilyAssetType`), все открытые одновременно (`IsExpanded="True"`). При типичном использовании 5–6 категорий пусты, но занимают ~380px вертикали только заголовками и кнопками «Добавить». Пользователь вынужден прокручивать вниз чтобы добраться до нужной категории (например, «3D Модели»).

Дополнительно обнаружены следующие проблемы:
1. **Auto-extracted GLB засоряет список «3D Модели»** — `LoadAssetsAsync` не фильтровал assets с `Description.StartsWith("auto-extracted-preview:")`, нарушая ADR-042:84 который предписывает раздельный показ.
2. **Нет empty state** — ключ `FM_Props_NoAssets` существовал в локализации, но не использовался в XAML.
3. **Нет thumbnail-превью** — в списках показывался только `FileName` текстом.
4. **7 отдельных кнопок «+ Добавить»** — нет автоопределения типа по расширению.
5. **Нет confirm delete** — файлы удалялись мгновенно без подтверждения.
6. **Нет привязки assets к конкретной версии** — все пользовательские assets добавлялись с `VersionLabel = null` (shared), без UI для per-version attach.

## Decision

### Dual-pane layout (sidebar + content)

Заменить 7 Expander'ов на dual-pane layout:
- **Sidebar (180px)** — вертикальный список категорий со счётчиками файлов (Все / Изображения 3 / Видео 0 / ...). Только одна категория видна в content area одновременно. Скролл только в content area, sidebar всегда виден целиком.
- **Content area** — `ListBox` с `VirtualizingStackPanel` для производительности при 50+ файлах. Каждая строка: thumbnail (32×32 для images) + FileName + SizeText + primary indicator (★) + actions (Set as primary / Open / Delete).
- **Empty state** — centered text «Нет файлов» + «Нажмите + Добавить файл» когда категория пуста.

**Best practice подкрепление:** dual-pane layout — universal pattern для asset browser. Все 6 DCC (Blender, Houdini, Unreal, Unity, Substance, Cinema 4D) используют его. Также shadcn "Gallery Categorized Tabs" — vertical sidebar с counts.

### Toggle «Привязать к версии»

В toolbar добавлен `ModernToggleSwitch` (новый стиль в `Generic.xaml`, ToggleButton-based, net48-safe):
- **По умолчанию выключен** — файлы добавляются как shared (`VersionLabel = null`), видны для всех версий.
- **Включён** — файлы добавляются с `VersionLabel` текущей версии, видны только для неё.
- Toggle виден только когда есть `VersionLabel` (через `IsAttachToVersionVisible`).
- Это escape-hatch для редких случаев когда per-version documentation нужна (например, PDF с инструкцией по миграции).

### Unified «Добавить файл»

Единая кнопка «+ Добавить файл» в toolbar заменяет 7 отдельных кнопок. `OpenFileDialog` с общим фильтром (все поддерживаемые расширения). Тип определяется через `FamilyAssetTypeExtensions.DetectFromExtension(path)`. После добавления sidebar переключается на категорию куда попал файл.

### Auto-GLB фильтр (bug fix)

`LoadAssetsAsync` теперь фильтрует assets с `Description.StartsWith("auto-extracted-preview:")` из `_allVisibleRows` и счётчиков категорий. Auto-extracted GLB управляются во вкладке «3D Просмотр» (там independent loading в `Preview3D.cs`). Это исправляет нарушение ADR-042:84.

### Confirm delete

`DeleteAsset` команда теперь вызывает `_dialogService.ShowConfirmation(title, body)` перед удалением, используя тот же паттерн что `DeleteVersion` (`Versions.cs:241`).

### Thumbnail-превью

`PathToBitmapImageConverter` расширен: `ConverterParameter` задаёт `DecodePixelWidth` (64px для thumbnail → отображается 32×32). `ContentAssetRow` wrapper pre-resolves absolute paths для Image assets в `PreResolveAssetPathsAsync`, чтобы XAML мог биндить синхронно без per-row async вызовов.

## Consequences

- **+8 ключей локализации** (FM_Props_CategoryAll, FM_Props_AttachToVersion, FM_Props_AttachToVersionHint, FM_Props_EmptyStateTitle, FM_Props_EmptyStateHint, FM_Props_AddFileUnified, FM_Props_ConfirmDeleteAssetTitle, FM_Props_ConfirmDeleteAssetBody)
- **+3 новых класса** (ContentCategory enum, CategoryNavItem, ContentAssetRow)
- **+1 extension class** (FamilyAssetTypeExtensions — DetectFromExtension + AllAssetFilters)
- **+1 новый стиль** (ModernToggleSwitch в Generic.xaml)
- **+8 PathGeometry ресурсов** (asset category icons в Generic.xaml)
- **PathToBitmapImageConverter** — добавлена поддержка `ConverterParameter` для `DecodePixelWidth`
- **Старые 7 коллекций** (ImageAssets, VideoAssets, ...) остаются в VM для совместимости с 3D Preview tab и net48 fallback
- **`AddAsset` команда** (старая, с CommandParameter) сохранена для совместимости; `AddFileUnified` — новый основной путь

## Verification

- Build R25: 0 warnings / 0 errors
- Build R24: 0 warnings / 0 errors
- Build R21: 0 warnings / 0 errors
- Build R19: 0 warnings / 0 errors
- Tests: 1743/1743 passed (38 new tests for FamilyAssetTypeExtensions)

## Notes

- Sidebar icons (PathGeometry per category) подготовлены в Generic.xaml, но пока не используются в sidebar item template. Текст + счётчик достаточны для MVP.
- `ModernToggleSwitch` — первый styled toggle switch в проекте. Обязательная проверка на net48 (Revit 2024) при ручном тестировании.
- Drag-and-drop файлов из проводника не включён в этот phase (nice-to-have, требует net48 compat тестирования).

## Revision #1 (2026-07-08) — UX fixes after manual testing

1. **Горизонтальный скролл sidebar** — добавлен `ScrollViewer.HorizontalScrollBarVisibility="Disabled"` на sidebar ListBox, ширина уменьшена с 180→170px.
2. **Per-file toggle вместо глобального** — глобальный toggle «Привязать к версии» удалён из toolbar. Вместо него каждая строка файла имеет свой `ModernToggleSwitch` (Column 2). Переключатель: включён = файл привязан к текущей версии (`VersionLabel != null`), выключен = shared. Для перемещения файла используется новый метод `IFamilyAssetService.SetAssetVersionBindingAsync(assetId, newVersionLabel)` который физически перемещает файл и обновляет БД. Toggle виден только когда есть `VersionLabel` (через `CanToggleVersionBinding`).
3. **Красная граница фокуса** — `FocusVisualStyle="{x:Null}"` добавлен на оба ListBox (sidebar + content) и в стиль `CompactListBoxItem` в `Generic.xaml`. Устраняет стандартную WPF focus rectangle.
4. **Категория «3D Модели» удалена** из sidebar. Auto-extracted GLB уже фильтруются (ADR-042 fix). Пользовательские Model3D файлы (если есть) показываются в «Все» и в «Прочие файлы» (Other category фильтрует по `Other OR Model3D`).
5. **Категории «Таблицы поиска (CSV)» и «Электронные таблицы (XLS)» объединены** в одну категорию «Таблицы» (`ContentCategory.Table`). Фильтрует по `FamilyAssetType.LookupTable OR FamilyAssetType.Spreadsheet`. Добавлен ключ локализации `FM_Props_Tables`.

### Новые изменения в ревизии #1

- `IFamilyAssetService.SetAssetVersionBindingAsync` — новый метод интерфейса (перемещение файла между shared/per-version)
- `LocalFamilyAssetService.SetAssetVersionBindingAsync` — реализация (File.Move + UPDATE БД + cleanup пустых директорий)
- `IFamilyAssetService.GetPrimaryImageAsync` — добавлен опциональный `versionLabel` параметр
- `ContentCategory` enum: убраны `Model3D`, `LookupTable`, `Spreadsheet`; добавлен `Table = 100`
- `ContentAssetRow`: добавлены `IsVersionBound`, `CanToggleVersionBinding`
- `FamilyPropertiesViewModel`: `ToggleAssetVersionBindingCommand` — per-file toggle
- Убран `AttachToVersion` свойство (глобальный toggle)
- `FocusVisualStyle="{x:Null}"` на CompactListBoxItem и обоих ListBox

## Verification (Revision #1)

- Build R25: 0 warnings / 0 errors
- Build R24: 0 warnings / 0 errors
- Build R21: 0 warnings / 0 errors
- Build R19: 0 warnings / 0 errors
- Tests: 1743/1743 passed
- Docs validation: PASSED
