---
module: family-manager
---
# Модели FamilyManager — Импорт

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

## FamilyDataImportStatus

Статус импорта данных семейства.

**Файл:** `FamilyDataImportStatus.cs`

```csharp
public enum FamilyDataImportStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Skipped
}
```

---

## FamilyDataImportRun

Запись о запуске импорта данных семейств (извлечение атрибутов из `.rfa`).

**Файл:** `FamilyDataImportRun.cs`

```csharp
public sealed record FamilyDataImportRun(
    string Id,
    string CatalogItemId,
    string? VersionId,
    FamilyDataImportStatus Status,
    string? ErrorMessage,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? CompletedAtUtc);
```

---

## FamilyImportRequest

Запрос на импорт одного файла `.rfa` в каталог. Файл копируется в managed storage.

**Файл:** `FamilyImportRequest.cs`

```csharp
public sealed record FamilyImportRequest(
    string FilePath,
    int RevitMajorVersion,
    string? Category,
    IReadOnlyList<string>? Tags,
    string? Description,
    string? CategoryId = null,
    string FamilySource = "loadable",
    string? RevitCategory = null,
    string? FileName = null,
    string? OriginalSourcePath = null);
```

`OriginalSourcePath` — путь к исходному `.rfa` до его копирования в
temp staging folder (используется пайплайном «Импорт активного
файла»). Передаётся в `PrepareManagedRfaAsync` для поиска
Type Catalog sidecar (.txt) рядом с оригиналом, когда рядом с temp
копией его нет. См. ADR-024 и ADR-033.

---

## FamilyImportResult

Результат импорта одного файла — содержит ID созданных сущностей или флаг дубликата.

**Файл:** `FamilyImportResult.cs`

```csharp
public sealed record FamilyImportResult(
    bool Success,
    string? CatalogItemId,
    string? VersionId,
    string? FileId,
    string? FileName,
    string? VersionLabel,
    string? ErrorMessage,
    bool WasSkipped = false,
    bool WasNewVersion = false);
```

---

## FamilyBatchImportItem

Одна строка (файл) в диалоге пакетного импорта. Содержит метаданные файла, статус и выбранное пользователем действие.

**Файл:** `FamilyBatchImportItem.cs`

```csharp
public sealed record FamilyBatchImportItem(
    string FilePath,
    string FileName,
    int RevitMajorVersion,
    FamilyBatchImportStatus Status,
    string? ExistingCatalogItemId = null,
    string? ExistingVersionLabel = null,
    string? TargetCategoryId = null,
    string? TargetCategoryName = null,
    string FamilySource = "loadable",
    int? TypeCount = null,
    string? RevitCategory = null,
    string? OriginalSourcePath = null,
    IReadOnlyList<FamilySourceTypeInfo>? SourceTypes = null,
    FamilyImportSource? Source = null,
    string? PrecomputedCatalogItemId = null,
    string? PrecomputedVersionLabel = null,
    string? PrecomputedManagedPath = null,
    string? ContentHash = null,
    int? HashFormatVersion = null,
    string? MatchedVersionLabel = null,
    FamilySnapshot? LoadableSnapshot = null,
    SystemFamilySnapshot? SystemSnapshot = null,
    string? PublishedBy = null,
    IReadOnlyList<FamilyGeometryPerType>? GeometryPerType = null,
    bool IsCrossNameDuplicate = false,
    string? MatchedItemName = null,
    string? ExistingCategoryId = null,
    string? ExistingCategoryPath = null)
{
    public FamilyBatchImportAction Action { get; set; }
    public string? TargetCategoryId { get; set; }
    public string? TargetCategoryName { get; set; }
    public string? PublishedByUser { get; set; }
}
```

- `FilePath` — для UC-1/UC-2 (импорт с диска или активного `.rfa`) это реальный путь к файлу. Для UC-3/UC-4 (импорт активного проекта / выделенных элементов) это placeholder `"system://..."` или `"loadable://..."` до подтверждения пользователем, после чего `ProcessProjectImportAsync` перезаписывает это поле на managed-путь.
- `Source` (v2.0.0) — payload для пост-диалогового staging flow (UC-3/UC-4). `null` для UC-1/UC-2 (файл уже на диске). `SystemSource` / `LoadableSource` — sealed record-union, см. [FamilyImportSource](#familyimportsource).
- v2.0.0 breaking change: поля `Sha256` и `FileSizeBytes` удалены — SHA-256 dedup и показ размера файла больше не используются (см. ADR-035).
- `ExistingCategoryId` / `ExistingCategoryPath` (Issue #135) — реальная категория существующего айтема (`ExistingCatalogItemId`), независимо от `TargetCategoryId` (которая может быть перекрыта командой «Импорт в категорию» или пикером). Используется диалогом для предупреждения «семейство будет перемещено между категориями» (P2).

---

## FamilyBatchImportStatus

Статус файла в диалоге пакетного импорта. v2.0.0: `Duplicate` удалён — content-based dedup по SHA-256 больше не выполняется. Дубликаты по содержимому попадают как `Existing` (новая версия).

**Файл:** `FamilyBatchImportStatus.cs`

```csharp
public enum FamilyBatchImportStatus
{
    New,        // Новое семейство, отсутствует в каталоге
    Existing,   // Семейство с таким нормализованным именем уже в каталоге
    Error       // Ошибка чтения файла / невалидный .rfa
}
```

---

## FamilyBatchImportAction

Действие, выбранное пользователем для файла в пакетном импорте.

**Файл:** `FamilyBatchImportAction.cs`

```csharp
public enum FamilyBatchImportAction
{
    IncrementVersion,  // Создать новую версию (vN+1), обновить current_version_label
    OverwriteCurrent,  // Заменить файл текущей версии без изменения current_version_label
    Skip,              // Пропустить файл
    MakeActive         // Переключить active version на найденный дубликат (без сохранения файла). Только для Status=Duplicate.
}
```

---

## SetActiveVersionResult

Результат переключения активной версии каталог-айтема на существующую версию.
Обновляет `catalog_items.current_version_label` и синхронизирует `content_hash`/`hash_format_version`
с активируемой версией (чтобы content-hash дедупликация оставалась консистентной).
Issue #126: имя айтема следует за именем файла АКТИВНОЙ версии (`name` + `normalized_name`).
См. ADR-041, ADR-049.

**Файл:** `SetActiveVersionResult.cs`

```csharp
public sealed record SetActiveVersionResult(
    bool Success,
    string CatalogItemId,
    string VersionLabel,
    string? PreviousVersionLabel,
    DateTimeOffset ActivatedAtUtc,
    bool ContentHashSynced,
    string? ErrorMessage = null,
    bool NameChanged = false,
    string? PreviousName = null,
    string? NewName = null);
```

- `NameChanged` — Issue #126: имя айтема обновлено до имени файла активированной версии.
- `PreviousName` / `NewName` — имя до/после переключения (NewName = имя файла версии без расширения).

---

## DeleteVersionResult

Результат удаления неактивной версии каталог-айтема (hard delete). Удаляются:
- Строки `catalog_versions` (каскадно через FK: `family_files`, `family_types`, `extracted_attribute_values`, `family_nested_shared_families`).
- Строки `family_assets` явно (привязка по `(catalog_item_id, version_label)`, не FK к versions).
- Физические файлы в `{dbRoot}/files/{catalogItemId}/{versionLabel}/`.

См. ADR-041.

**Файл:** `DeleteVersionResult.cs`

```csharp
public sealed record DeleteVersionResult(
    bool Success,
    string CatalogItemId,
    string VersionLabel,
    int VersionsDeleted,
    int AssetsDeleted,
    bool FilesDeleted,
    string? PhysicalDirectoryPath,
    string? ErrorMessage = null);
```

---

## FamilyBatchImportResult

Агрегированный результат импорта нескольких файлов.

**Файл:** `FamilyBatchImportResult.cs`

```csharp
public sealed record FamilyBatchImportResult(
    IReadOnlyList<FamilyImportResult> Results,
    int TotalFiles,
    int SuccessCount,
    int SkippedCount,
    int ErrorCount);
```

---

## FamilyFolderImportRequest

Запрос на импорт всех `.rfa` файлов из папки (с возможностью рекурсивного обхода).

**Файл:** `FamilyFolderImportRequest.cs`

```csharp
public sealed record FamilyFolderImportRequest(
    string FolderPath,
    int RevitMajorVersion,
    bool Recursive,
    string? Category,
    IReadOnlyList<string>? Tags,
    string? Description,
    string? CategoryId = null);
```

---

## FamilyImportProgress

Прогресс пакетного импорта — передаётся в callback для обновления UI.

**Файл:** `FamilyImportProgress.cs`

```csharp
public sealed record FamilyImportProgress(
    int CurrentFileIndex,
    int TotalFiles,
    string CurrentFileName,
    int SuccessCount,
    int SkippedCount,
    int ErrorCount);
```

---

## FamilySourceTypeInfo

v2.0.0: Core-level DTO для типов системных семейств, используемый в публичных API batch dialog (см. `FamilyBatchImportItem.SourceTypes` и `SystemFamilyPendingImport.Types`). Создан чтобы Core record не тянул `Autodesk.Revit.DB.BuiltInCategory` через `SelectedSystemType` — иначе нарушается I-09 (Core не должен зависеть от Revit API в публичных сигнатурах) и тесты без runtime Revit падают.

**Файл:** `Models/FamilyManager/FamilySourceTypeInfo.cs`

```csharp
public sealed record FamilySourceTypeInfo(
    string UniqueId,
    string Name,
    string CategoryName,
    int CategoryId,
    string? FamilyName = null,
    string? FamilyKey = null);
```

- `UniqueId` — Revit unique id элемента типа. Extractor в managed `.rvt` ищет этот id.
- `Name` — отображаемое имя типа.
- `CategoryName` — отображаемое имя родительской категории (например `"OST_PipeFitting"`).
- `CategoryId` — ordinal `BuiltInCategory`, переданный через границу FamilyManager→Core как plain `int`. Orchestrator и extractor никогда не видят enum напрямую.
- `FamilyName` — системная семья типа (Issue #183) → `family_types.family_name`.
- `FamilyKey` — locale-invariant идентичность семьи (Issue #190, ADR-064) → `family_types.family_key` (схема V27).

Маппинг `SelectedSystemType → FamilySourceTypeInfo` выполняется на границе VM→Core в `FamilyManagerMainViewModel.Import.cs` (UC-3/UC-4 batch flow). В обратную сторону маппинг не нужен — extractor читает типы из managed `.rvt` по `UniqueId`/`Name`, ordinal ему не нужен.

---

## FamilyImportSource

v2.0.0: strongly-typed sealed record-union payload для `FamilyBatchImportItem.Source`. Несёт данные, необходимые пост-диалоговому staging flow (UC-3 / UC-4) для создания managed `.rvt` / `.rfa` уже **после** подтверждения пользователем. До введения этого типа batch dialog показывался через 20-30 сек для проекта с 50 семействами, потому что staging (`CreateCleanProjectWithTypesAndInstances` + `EditFamily + SaveAs`) выполнялся ДО диалога — и оставлял orphan-файлы при отмене.

**Файл:** `Models/FamilyManager/FamilyImportSource.cs`

```csharp
public abstract record FamilyImportSource
{
    private FamilyImportSource() { }

    public sealed record SystemSource(
        string DisplayName,
        int CategoryId,
        IReadOnlyList<string> TypeUniqueIds,
        IReadOnlyList<string> TypeNames,
        IReadOnlyList<string?>? TypeFamilyNames = null,
        IReadOnlyList<string?>? TypeFamilyKeys = null) : FamilyImportSource;

    public sealed record LoadableSource(
        string FamilyName,
        string FamilyUniqueId,
        string CategoryName) : FamilyImportSource;
}
```

- `SystemSource` — системное семейство (трубы, воздуховоды, и т.д.). `CategoryId` — ordinal `BuiltInCategory` (как в `FamilySourceTypeInfo.CategoryId`). `TypeUniqueIds` / `TypeNames` — параллельные списки Revit UniqueId и display name типов.
- `LoadableSource` — loadable семейство. `FamilyUniqueId` — Revit UniqueId `Family`-элемента в активном проекте (для `doc.GetElement(uid)` → `Family` → `EditFamily`).

Живёт в `SmartCon.Core` (не в `SmartCon.FamilyManager`) чтобы публичный API `FamilyBatchImportItem.Source` не тянул `Autodesk.Revit.DB.Document` (I-09).

Пост-диалоговый flow: `ProcessProjectImportAsync` → `StageSystemFamiliesFromMetadataAsync` / `StageLoadableFamiliesFromMetadataAsync` (в VM, через `IFamilyManagerAwaitableEvent`). Эти методы читают `Source`, вызывают Revit-API staging helper, и **перезаписывают `item.FilePath`** в managed-путь (через `with`-expression для record). После этого orchestrator'ы (`SystemFamilyImportOrchestrator`, `LoadableFamilyImportOrchestrator`) работают с реальными managed файлами и не требуют изменений.

---

## SystemFamilyPendingImport

v2.0.0: результат подготовки одной категории системного семейства к импорту. Один экземпляр на непустой `CategoryAnalysis` или на user-picked группу. Содержит managed-путь к мини-`.rvt` (создан `CreateCleanProjectWithTypesAndInstances` напрямую в managed storage) и список типов для последующего extraction.

**Файл:** `Models/FamilyManager/SystemFamilyPendingImport.cs`

```csharp
public sealed record SystemFamilyPendingImport(
    string CategoryName,
    IReadOnlyList<FamilySourceTypeInfo> Types,
    string ManagedRvtPath);
```

- `CategoryName` — отображаемое имя категории (используется как имя файла managed `.rvt`).
- `Types` — типы категории в виде Core DTO (см. `FamilySourceTypeInfo`).
- `ManagedRvtPath` — абсолютный путь к managed `.rvt` в `{dbRoot}/files/{catalogItemId}/v1/{name}.rvt`.

Используется только в VM как промежуточное значение между `StageSystemFromAnalysis` (создание managed `.rvt`) и `BuildSystemFamilyBatchRowAsync` (построение batch row для dialog).

---

## LegacyStageFolderCleaner

v2.0.0: one-shot helper для удаления legacy `files/_stage/` папок, оставшихся от SmartCon &lt; v2.0.0, где loadable families стейджились через `EditFamily + SaveAs` во временную подпапку `_stage/{guid}/`. После temp-removal sweep staging идёт напрямую в managed storage (`files/{catalogItemId}/v1/...`) и `_stage/` больше не создаётся — но папка может остаться на диске у пользователей, обновляющихся с предыдущей версии.

**Файл:** `Services/FamilyManager/LegacyStageFolderCleaner.cs`

```csharp
public static class LegacyStageFolderCleaner
{
    public static void Cleanup(string familyManagerRoot);
}
```

- `familyManagerRoot` — путь к `%APPDATA%\SmartCon\FamilyManager` (или другой catalog root). Передаётся из `App.OnStartup`.
- `Cleanup` идёт по каждой подпапке (catalog) и удаляет `files/_stage/` если существует. Идемпотентна: отсутствующая папка — no-op, повторный запуск — no-op.
- Все исключения логируются на уровне `Debug` и проглатываются (permissive): один заблокированный catalog не должен ломать startup.

Живёт в `SmartCon.Core` (а не в `SmartCon.App`) чтобы логика была тестируемой без Revit UIApplication. Реальный production entry point — `App.OnStartup` в `SmartCon.App`, который делегирует в `LegacyStageFolderCleaner.Cleanup(...)`.

---

## PrecomputedImportTriple

v2.0.0: каноническая тройка `(CatalogItemId, VersionLabel, ManagedPath)`, которую VM batch-диалога аллоцирует ДО показа диалога и пробрасывает через все стадии импорт-флоу (build → dialog → staging → import). Это единственная форма данных, которая гарантирует инвариант `family_files.relative_path = "{dbRoot}/files/<id>/<version>/<name>"`: `ManagedPath` — абсолютный путь на диске, `CatalogItemId` + `VersionLabel` — компоненты, которые вместе с именем файла образуют этот layout.

**Файл:** `Models/FamilyManager/PrecomputedImportTriple.cs`

```csharp
public sealed record PrecomputedImportTriple(
    string CatalogItemId,
    string VersionLabel,
    string ManagedPath);
```

Иммутабельный record: VM только заменяет тройку целиком (через `IFamilyImportPrecomputer.BuildPrecomputedTripleAsync`), никогда не мутирует поля по отдельности. Это исключает round-trip с полуобновлённым состоянием, когда `CatalogItemId` уже от нового имени, а `VersionLabel`/`ManagedPath` — от старого. Именно это состояние приводило к `UNIQUE constraint failed: catalog_items.id` в pre-v2.0.0: после rename строки в диалоге `ImportFileAsync` пытался `INSERT` новую запись с `id` от старого имени.

Используется в:
- `FamilyBatchImportItem.PrecomputedCatalogItemId/VersionLabel/ManagedPath` (Core) — DTO, передаваемое через batch dialog.
- `FamilyBatchImportRow.PrecomputedCatalogItemId/VersionLabel/ManagedPath` (`SmartCon.FamilyManager/ViewModels`) — бэкинг-поля row VM.
- `IFamilyImportPrecomputer.BuildPrecomputedTripleAsync` (Core) — контракт выделенного precomputer-сервиса, который является единым источником истины для вычисления этой тройки (как для initial dialog build, так и для dialog rename handler).

---

**Tree expand/collapse в UI дерева категорий** (Issue #86 / ADR-037).

Вся UI-логика разворачивания/сворачивания поддеревьев живёт в `SmartCon.FamilyManager/` (не в Core — это VM-уровень), но для reference описана здесь.

**Файлы:**

- `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.TreeExpand.cs` — pure logic + `[RelayCommand]` обёртки.
- `src/SmartCon.FamilyManager/ViewModels/CategoryNodeViewModel.cs` — `IsAnyDescendantCollapsed` computed property + `AttachCollapseTracking()`.
- `src/SmartCon.UI/Generic.xaml` — `UnfoldMoreGeometry` / `UnfoldLessGeometry` (Material Design filled, viewbox 0 0 24 24).

**Static helpers (pure logic, `internal static` для unit-тестирования):**

```csharp
internal static void ExpandSubtree(CatalogTreeNodeViewModel node);
internal static void CollapseSubtree(CatalogTreeNodeViewModel node);
internal static void ExpandAll(IEnumerable<CatalogTreeNodeViewModel> roots);
internal static void CollapseAll(IEnumerable<CatalogTreeNodeViewModel> roots);
```

Семантика:

- `ExpandSubtree(node)` — рекурсивно выставляет `IsExpanded = true` на всех `CategoryNodeViewModel` в поддереве (включая корень). `FamilyLeafNodeViewModel` пропускаются — их `IsExpanded` не имеет визуального эффекта.
- `CollapseSubtree(node)` — рекурсивно выставляет `IsExpanded = false` на всех `CategoryNodeViewModel` в поддереве **включая корень**. Поведение совпадает с VS Solution Explorer «Collapse All» — закрывает всё поддерево.

**RelayCommands (XAML bindings):**

```csharp
[RelayCommand] private void ExpandAllTree();         // → ExpandAll(TreeNodes)
[RelayCommand] private void CollapseAllTree();       // → CollapseAll(TreeNodes)
[RelayCommand] private void ToggleSubtree(CategoryNodeViewModel? category);
```

- `ExpandAllTreeCommand` / `CollapseAllTreeCommand` привязаны к двум кнопкам в статус-баре (`FamilyManagerPaneControl.xaml` Row 4).
- `ToggleSubtreeCommand` привязан к hover-reveal кнопке в header каждой категории — клик разворачивает всё поддерево если хоть что-то свёрнуто, иначе сворачивает всё.

**Иконки** (Material Design, filled `PathGeometry`):

- `UnfoldMoreGeometry` — X-pattern: верхний Λ + нижний V. Семантика: "расширение во все стороны" (expand).
- `UnfoldLessGeometry` — hourglass: верхний V + нижний Λ. Семантика: "сжатие к центру" (collapse).

Геометрии взяты из Material Icons (Google, Apache 2.0), viewbox 0 0 24 24, рендерятся через `<Viewbox Width="15" Height="15">` filled цветом `TextSecondaryBrush`. Hover-toggle-кнопка динамически меняет `Path.Data` через DataTrigger на `IsAnyDescendantCollapsed`.

**`CategoryNodeViewModel.IsAnyDescendantCollapsed`** — computed property для динамической смены иконки:

```csharp
[ObservableProperty] private bool _isAnyDescendantCollapsed = true;
```

- Подписывается на `PropertyChanged` самого узла и всех потомков `CategoryNodeViewModel` рекурсивно.
- Реагирует на изменения `IsExpanded` И `IsAnyDescendantCollapsed` в дочерних узлах (одного `IsExpanded` недостаточно — потомок может обновить только своё `IsAnyDescendantCollapsed` без изменения своего `IsExpanded`).
- Подписка настраивается через `AttachCollapseTracking()` (вызывается из `BuildCategoryNode` после построения поддерева).
- `DetachCollapseTracking()` для cleanup при удалении поддерева.

В XAML используется через DataTrigger для смены `Path.Data`:

```xaml
<Style.Triggers>
    <DataTrigger Binding="{Binding IsAnyDescendantCollapsed}" Value="False">
        <Setter Property="Data" Value="{StaticResource UnfoldLessGeometry}"/>
    </DataTrigger>
</Style.Triggers>
```

**UI binding:** `IsExpanded` через `ItemContainerStyle` уже привязан к VM в TwoWay (`FamilyTreeItemBaseStyle` в `FamilyManagerPaneControl.xaml:593`), так что прямое изменение свойства в VM **сразу** отражается на UI без дополнительного кода.

**Edge cases:**

- При активном поиске (`SearchText` непустой) дерево уже раскрыто полностью через `expandAll` в `LoadTreeAsync`, поэтому дополнительных действий не требуется.
- UI virtualization отключена для каталога (300–500 узлов), так что все `TreeViewItem` контейнеры гарантированно существуют к моменту клика. Команды корректно работают и для частично/полностью свёрнутых деревьев.
- Кнопка в header категории использует `Focusable="False"` чтобы не триггерить `TreeViewItem.IsSelected` при hover-click. Drag-and-drop из header-а папки (не с кнопки) по-прежнему работает через `TreeViewDragDropBehavior`.
- Кнопки статус-бара используют `Style="{StaticResource FlatIconButton}"` (Generic.xaml) — явный `ControlTemplate` с точно центрированным `ContentPresenter`. Иконка не смещается при hover (в отличие от дефолтного WPF Button).

**Используется в:**

- `FamilyManagerPaneControl.xaml:624-680` — `HierarchicalDataTemplate` для `CategoryNodeViewModel`, hover-reveal `ToggleSubtreeCommand` с динамической сменой иконки.
- `FamilyManagerPaneControl.xaml:738-797` — статус-бар с `ExpandAllTreeCommand` / `CollapseAllTreeCommand` + счётчик `TotalItemCount`.

Pure logic, ноль зависимостей от Revit API. Unit-тесты в `src/SmartCon.Tests/FamilyManager/ViewModels/FamilyManagerMainExpandCollapseTests.cs` (12 кейсов, включая реактивное обновление `IsAnyDescendantCollapsed`).

---

## FamilyBatchImportRowState (Issue #127)

Состояние строки batch-диалога во время импорта. Ставится executor'ом через `FamilyBatchImportProgress.ItemState` и маппится в иконку колонки статуса (Check/Close/TimerSand).

**Файл:** `FamilyBatchImportRowState.cs`

```csharp
public enum FamilyBatchImportRowState
{
    Pending,
    Running,
    Success,
    Skipped,
    Error
}
```

---

## FamilyBatchImportPhase (Issue #127)

Фаза обработки текущего элемента batch-импорта. Управляет текстом статуса в диалоге («Подготовка/Импорт/Обработка X из Y»).

**Файл:** `FamilyBatchImportPhase.cs`

```csharp
public enum FamilyBatchImportPhase
{
    Staging,
    Importing,
    Extracting,
    Finalizing,
    Paused
}
```

---

## FamilyBatchImportProgress (Issue #127)

Отчёт прогресса batch-импорта от `IFamilyBatchImportExecutor` к диалогу. `ItemState == null` — элемент начал обработку (строка → Running); иначе — элемент завершён с этим состоянием. `CurrentIndex` — индекс строки в `FamilyBatchImportViewModel.Items`.

**Файл:** `FamilyBatchImportProgress.cs`

```csharp
public sealed record FamilyBatchImportProgress(
    int CurrentIndex,
    int Total,
    string CurrentItemName,
    FamilyBatchImportPhase Phase,
    FamilyBatchImportRowState? ItemState,
    string? ItemError,
    int SuccessCount,
    int SkippedCount,
    int ErrorCount);
```

---

## FamilyBatchImportExecutionResult (Issue #127)

Итог выполнения batch-импорта. `WasStopped == true` — пользователь остановил импорт (Остановить → Закрыть); уже импортированные элементы остаются в каталоге.

**Файл:** `FamilyBatchImportExecutionResult.cs`

```csharp
public sealed record FamilyBatchImportExecutionResult(
    int SuccessCount,
    int SkippedCount,
    int ErrorCount,
    bool WasStopped);
```

---

## ProjectImportOutcome

Результат `FamilyManagerMainViewModel.ProcessProjectImportAsync` (batch-импорт
проекта: «Импорт активного файла» / «Импорт выделенных элементов»).
`ImportStarted == false` — пользователь отменил batch-диалог до старта:
вызывающий НЕ должен выполнять пост-импортные действия (закрытие
мини-проекта #186, авто stale-проверка #185).

**Файл:** `ProjectImportOutcome.cs`

```csharp
public sealed record ProjectImportOutcome(
    bool ImportStarted,
    int SuccessCount,
    IReadOnlyList<ImportedCatalogItem> ImportedItems)
{
    public static readonly ProjectImportOutcome NotStarted = new(false, 0, []);
}
```

---

## ImportedCatalogItem

Один затронутый импортом элемент каталога — минимум данных для пост-импортной
stale-проверки (#185) без повторного запроса к БД.

**Файл:** `ProjectImportOutcome.cs`

```csharp
public sealed record ImportedCatalogItem(
    string CatalogItemId,
    string DisplayName,
    string FamilySource);
```

---

## MiniProjectPathPattern

Чистая (pure) проверка пути по форме managed storage
(`{dbRoot}\files\{catalogItemId}\{versionLabel}\{name}.rvt`) — fallback
распознавания эталонного мини-проекта для файлов, созданных до появления
ES-маркера (Issue #188). Консервативна по дизайну: ложноположительное
распознавание скрыло бы реальный рабочий проект от автопереключения базы,
поэтому требуется полная форма пути. Решения о закрытии (#186) этот
fallback НЕ используют — только ES-маркер.

**Файл:** `MiniProjectPathPattern.cs` (`SmartCon.Core/Services/FamilyManager/`)

```csharp
public static class MiniProjectPathPattern
{
    public static bool IsMiniProjectPath(string? path);
}
```

---

## OpenDocumentInfo

Снимок одного открытого документа Revit для чистой логики выбора рабочего
проекта (`WorkProjectSelector`) — VM маппит `Document` на этот record, чтобы
логика оставалась юнит-тестируемой без Revit API.

**Файл:** `WorkProjectSelector.cs` (`SmartCon.Core/Services/FamilyManager/`)

```csharp
public sealed record OpenDocumentInfo(
    string PathName,
    bool IsFamilyDocument,
    bool IsLinked,
    bool IsMiniProject);
```

---

## WorkProjectSelector

Чистая логика выбора документа, которому передать фокус после закрытия
эталонного мини-проекта (#186). Рабочий проект — только реальный проект
пользователя: не семья, не link, не другой мини-проект, не закрываемый
эталон. `null` — подходящего документа нет (вызывающий создаёт пустой
проект: `Document.Close` запрещён на активном документе, а
`PostableCommand.Close` показал бы «Сохранить?» — риск перезаписи
read-only эталона v1).

**Файл:** `WorkProjectSelector.cs` (`SmartCon.Core/Services/FamilyManager/`)

```csharp
public static class WorkProjectSelector
{
    public static string? SelectWorkProjectPath(
        IReadOnlyList<OpenDocumentInfo> openDocuments,
        string? referencePath);
}
```
