---
module: family-manager
---
# Модели FamilyManager

> Загружать: при работе с модулем FamilyManager (каталог семейств, импорт, атрибуты, метаданные).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

Все модели — immutable records, живут в `SmartCon.Core/Models/FamilyManager/`.
Идентификаторы — `string` (GUID), даты — `DateTimeOffset`.

## CatalogProviderKind

Тип провайдера каталога (локальный, сетевой, облачный).

**Файл:** `CatalogProviderKind.cs`

```csharp
public enum CatalogProviderKind
{
    Local,
    Network,
    Cloud
}
```

---

## DatabaseConnection

Подключение к базе данных каталога по пути. Папка содержит `catalog.db` (SQLite) + `files/` (managed storage).

**Файл:** `DatabaseConnection.cs`

```csharp
public sealed record DatabaseConnection(
    string Id,
    string Name,
    string Path,
    DateTimeOffset CreatedAtUtc);
```

---

## DatabaseConnectionRegistry

Реестр подключений. Сохраняется как `registry.json` в `%APPDATA%\SmartCon\FamilyManager\`.

**Файл:** `DatabaseConnectionRegistry.cs`

```csharp
public sealed record DatabaseConnectionRegistry(
    string? ActiveConnectionId,
    IReadOnlyList<DatabaseConnection> Connections);
```

---

## ContentStatus

Статус опубликованного контента в каталоге. FM — Published-зона, всё импортированное = опубликовано.

**Файл:** `ContentStatus.cs`

```csharp
public enum ContentStatus
{
    Active = 0,       // доступно для загрузки в проекты
    Deprecated = 1,   // устарело, не рекомендуется для новых проектов
    Retired = 2       // снято с публикации, недоступно для загрузки
}
```

---

## FamilyAssetType

Тип вспомогательного ассета (изображение, документ и т.д.), прикреплённого к семейству.

**Файл:** `FamilyAssetType.cs`

```csharp
public enum FamilyAssetType
{
    Image = 0,
    Video = 1,
    Document = 2,
    Model3D = 3,
    LookupTable = 4,
    Other = 5,
    Spreadsheet = 6
}
```

---

## FamilyCatalogItem

Логическая запись каталога семейств — основная сущность, к которой привязаны версии и файлы.

**Файл:** `FamilyCatalogItem.cs`

```csharp
public sealed record FamilyCatalogItem(
    string Id,
    string Name,
    string NormalizedName,
    string? Description,
    string? CategoryPath,
    string? CategoryId,
    string? Manufacturer,
    ContentStatus ContentStatus,
    string? CurrentVersionLabel,
    IReadOnlyList<string> Tags,
    string? PublishedBy,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
```

---

## FamilyCatalogVersion

Версия записи каталога — связка с конкретным файлом `.rfa`. Один CatalogItem может иметь несколько версий.

**Файл:** `FamilyCatalogVersion.cs`

```csharp
public sealed record FamilyCatalogVersion(
    string Id,
    string CatalogItemId,
    string FileId,
    string VersionLabel,
    string Sha256,
    int RevitMajorVersion,
    int? TypesCount,
    int? ParametersCount,
    DateTimeOffset PublishedAtUtc);
```

---

## FamilyFileRecord

Физический файл семейства в managed storage. Путь: `{db-root}/files/{family-id}/{version}/r{revit}/{sha256}.rfa`.

**Файл:** `FamilyFileRecord.cs`

```csharp
public sealed record FamilyFileRecord(
    string Id,
    string RelativePath,
    string FileName,
    long SizeBytes,
    string Sha256,
    int RevitMajorVersion,
    DateTimeOffset ImportedAtUtc);
```

---

## FamilyAsset

Вспомогательный ассет (изображение, документ, lookup table), привязанный к семейству.
Файлы хранятся в `{db-root}/files/{family-id}/{version}/assets/{type}/`.

**Файл:** `FamilyAsset.cs`

```csharp
public sealed record FamilyAsset(
    string Id,
    string CatalogItemId,
    string? VersionLabel,
    FamilyAssetType AssetType,
    string FileName,
    string RelativePath,
    long SizeBytes,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    bool IsPrimary = false);
```

---

## AttributeScope

Область применения атрибута (тип/экземпляр).

**Файл:** `AttributeScope.cs`

```csharp
public enum AttributeScope
{
    Type,
    Instance
}
```

---

## AttributeDefinition

Определение атрибута (параметра) для извлечения из семейств. Заменяет устаревший `AttributePreset` (ADR-017).

**Файл:** `AttributeDefinition.cs`

```csharp
public sealed record AttributeDefinition(
    string Id,
    string Name,
    string? Group,
    bool IsActive,
    DateTimeOffset CreatedAtUtc);
```

---

## CategoryAttributeBinding

Связь категории с атрибутом — какие параметры извлекать для семейств данной категории.

**Файл:** `CategoryAttributeBinding.cs`

```csharp
public sealed record CategoryAttributeBinding(
    string Id,
    string CategoryId,
    string AttributeId,
    int SortOrder,
    bool IsEnabled);
```

---

## EffectiveCategoryAttribute

Эффективный атрибут категории с учётом наследования от родительских категорий.

**Файл:** `EffectiveCategoryAttribute.cs`

```csharp
public sealed record EffectiveCategoryAttribute(
    string AttributeId,
    string Name,
    string? Group,
    int SortOrder,
    bool IsEnabled,
    bool IsInherited,
    string? SourceCategoryId);
```

---

## CategoryNode

Узел дерева категорий. Формирует иерархию категорий каталога семейств.

**Файл:** `CategoryNode.cs`

```csharp
public sealed record CategoryNode(
    string Id,
    string Name,
    string? ParentId,
    int SortOrder,
    string FullPath,
    DateTimeOffset CreatedAtUtc);
```

---

## CategoryTree

Иммутабельное дерево категорий с быстрым поиском по ID и дочерним узлам.

**Файл:** `CategoryTree.cs`

```csharp
public sealed class CategoryTree
{
    public CategoryTree(IReadOnlyList<CategoryNode> nodes);
    public IReadOnlyList<CategoryNode> GetAllNodes();
    public CategoryNode? GetById(string id);
    public IReadOnlyList<CategoryNode> GetChildren(string? parentId);
    public IReadOnlyList<CategoryNode> GetRootNodes();
    public string GetFullPath(string id);
    public IReadOnlyList<string> GetDescendantIds(string categoryId);
    public string BuildFullPath(string id);
}
```

---

## AttributePreset

Набор параметров для извлечения из семейств, привязанный к категории. Дочерние категории наследуют параметры от родительских.

**Файл:** `AttributePreset.cs`

```csharp
public sealed record AttributePreset(
    string Id,
    string? CategoryId,
    IReadOnlyList<AttributePresetParameter> Parameters,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
```

---

## AttributePresetParameter

Описание одного параметра в пресете.

**Файл:** `AttributePresetParameter.cs`

```csharp
public sealed record AttributePresetParameter(
    string ParameterName,
    string? DisplayName,
    int SortOrder)
{
    public string DisplayText => DisplayName ?? ParameterName;
}
```

---

## AttributeValueStatus

Статус извлечённого значения.

**Файл:** `AttributeValueStatus.cs`

```csharp
public enum AttributeValueStatus
{
    Valid,
    Missing,
    Error
}
```

---

## ExtractedAttributeValue

Извлечённое значение атрибута из семейства.

**Файл:** `ExtractedAttributeValue.cs`

```csharp
public sealed record ExtractedAttributeValue(
    string Id,
    string CatalogItemId,
    string? VersionId,
    string AttributeId,
    string? TypeId,
    string? ValueText,
    AttributeValueStatus Status,
    DateTimeOffset ExtractedAtUtc);
```

---

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
файла»). Передаётся в `ImportTypeCatalogIfPresentAsync` для поиска
Type Catalog sidecar (.txt) рядом с оригиналом, когда рядом с temp
копией его нет. См. ADR-024.

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
    bool WasSkippedAsDuplicate = false,
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
    string Sha256,
    int RevitMajorVersion,
    long FileSizeBytes,
    FamilyBatchImportStatus Status,
    string? ExistingCatalogItemId = null,
    string? ExistingVersionLabel = null,
    string? TargetCategoryId = null,
    string? TargetCategoryName = null,
    string FamilySource = "loadable",
    int TypeCount = 0,
    string? RevitCategory = null,
    string? OriginalSourcePath = null)
{
    public FamilyBatchImportAction Action { get; set; }
    public string? TargetCategoryId { get; set; }
    public string? TargetCategoryName { get; set; }
}
```

---

## FamilyBatchImportStatus

Статус файла в диалоге пакетного импорта. Определяется на основе сравнения SHA256 с каталогом.

**Файл:** `FamilyBatchImportStatus.cs`

```csharp
public enum FamilyBatchImportStatus
{
    New,        // Новое семейство, отсутствует в каталоге
    Existing,   // Семейство есть в каталоге, но SHA256 отличается
    Duplicate,  // Точное совпадение SHA256 — пропускается автоматически
    Error       // Ошибка чтения файла
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
    Skip               // Пропустить файл
}
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

## FamilyUpdateRequest

Запрос на обновление файла семейства в каталоге.

**Файл:** `FamilyUpdateRequest.cs`

```csharp
public sealed record FamilyUpdateRequest(
    string CatalogItemId,
    string FilePath,
    int RevitMajorVersion,
    string? CategoryId = null,
    string? CategoryName = null,
    string? FileName = null,
    string? OriginalSourcePath = null);
```

`OriginalSourcePath` (см. ADR-024) — путь к исходному `.rfa` до
копирования в temp staging folder. Используется для поиска Type
Catalog sidecar рядом с оригиналом.

---

## ActiveFamilyPreparationResult

Результат подготовки активного Revit family-документа для импорта.
Возвращается `IActiveFamilyFilePreparer.PrepareActiveFamilyAsync`.

**Файл:** `ActiveFamilyPreparationResult.cs`

```csharp
public sealed record ActiveFamilyPreparationResult(
    string TempRfaPath,
    string? TempTxtPath,
    string? OriginalRfaPath,
    string? OriginalTxtPath);
```

| Поле | Описание |
|---|---|
| `TempRfaPath` | Absolute path к `.rfa`, сохранённому в temp (например `%TEMP%\SmartCon\FMLoad\{guid}\{name}.rfa`) |
| `TempTxtPath` | Absolute path к скопированному sidecar `.txt` рядом с `TempRfaPath`, или `null` если sidecar не найден |
| `OriginalRfaPath` | Absolute path к исходному `.rfa` (managed storage или рабочая папка пользователя); `null` для несохранённых документов |
| `OriginalTxtPath` | Absolute path к исходному sidecar `.txt` рядом с `OriginalRfaPath`, или `null` |

---

## FamilyLoadOptions

Параметры загрузки семейства в проект Revit.

**Файл:** `FamilyLoadOptions.cs`

```csharp
public sealed record FamilyLoadOptions(
    bool OverwriteExisting = false,
    bool UpdateFamilyIfChanged = false,
    string? PreferredName = null,
    bool OverwriteParameterValues = true)
{
    public static FamilyLoadOptions Default { get; } = new();
}
```

---

## SharedFamiliesLoadChoice

Решение пользователя о способе загрузки одного общего вложенного семейства (shared nested), которое уже есть в проекте, но в загружаемой версии `.rfa` оно изменено. Используется в диалоге `SharedFamiliesLoadModeDialogView` (issue #67).

**Файл:** `SharedFamiliesLoadChoice.cs`

```csharp
public enum SharedFamiliesLoadChoice
{
    UseProject = 0,            // FamilySource.Project — оставить проектную версию
    OverwriteParameters = 1,   // FamilySource.Family + overwrite=true — обновить параметры
    OverwriteAll = 2           // FamilySource.Family + overwrite=true — полная перезапись
}
```

Соответствие API Revit:

| Choice | `FamilySource` | `overwriteParameterValues` |
|---|---|---|
| `UseProject` | `FamilySource.Project` | `false` |
| `OverwriteParameters` | `FamilySource.Family` | `true` |
| `OverwriteAll` | `FamilySource.Family` | `true` |

---

## SharedFamilyDecisionRequest

Запрос на решение, передаваемый из `IFamilyLoadOptions.OnSharedFamilyFound` в UI-слой через `IFamilyManagerDialogService.ShowSharedFamiliesLoadModeDialog`.

**Файл:** `SharedFamilyDecisionRequest.cs`

```csharp
public sealed record SharedFamilyDecisionRequest(
    string SharedFamilyName,
    bool IsFamilyInUse,
    string ParentFamilyName);
```

| Поле | Назначение |
|---|---|
| `SharedFamilyName` | Имя конфликтующего shared nested (в Revit 2024.3+ это nested; в более ранних — parent, REVIT-198137) |
| `IsFamilyInUse` | Размещены ли экземпляры в проекте (влияет на текст предупреждения) |
| `ParentFamilyName` | Имя родительского семейства для caption диалога |

---

## FamilyLoadResult

Результат загрузки семейства в проект Revit.

**Файл:** `FamilyLoadResult.cs`

```csharp
public sealed record FamilyLoadResult(
    bool Success,
    string? FamilyName,
    string? Message,
    string? ErrorMessage);
```

---

## FamilyLoadStatus

Статус загрузки семейства в проект Revit.

**Файл:** `FamilyLoadStatus.cs`

```csharp
public enum FamilyLoadStatus
{
    Failed,
    Loaded,
    Updated,
    Current
}
```

---

## FamilyResolvedFile

Разрешённый путь к файлу `.rfa` — готов для загрузки в Revit.

**Файл:** `FamilyResolvedFile.cs`

```csharp
public sealed record FamilyResolvedFile(
    string AbsolutePath,
    string? CatalogItemId,
    string? VersionId,
    string? VersionLabel = null);
```

---

## FamilyPlacementDragData

Payload для drag-and-drop размещения типоразмера семейства из FamilyManager в canvas Revit.

**Файл:** `FamilyPlacementDragData.cs`

```csharp
public sealed record FamilyPlacementDragData(
    string CatalogItemId,
    string FamilyName,
    string TypeName,
    int TargetRevitVersion,
    bool IsVirtual = false);
```

---

## TypeCatalogEntry

Одна запись (тип) из Type Catalog (.txt) семейства Revit.

**Файл:** `TypeCatalogEntry.cs`

```csharp
public sealed record TypeCatalogEntry(
    string TypeName,
    IReadOnlyDictionary<string, string> ParameterValues);
```

---

## TypeCatalogParseResult

Результат парсинга Type Catalog (.txt) семейства Revit.

**Файл:** `TypeCatalogParseResult.cs`

```csharp
public sealed record TypeCatalogParseResult(
    IReadOnlyList<string> ParameterNames,
    IReadOnlyList<TypeCatalogEntry> Entries)
{
    public bool HasEntries => Entries.Count > 0;
}
```

---

## FamilyMetadataExtractionResult

Результат извлечения метаданных из `.rfa`. MVP — только файловые метаданные (имя, размер, хеш).
Post-MVP — глубокое извлечение (категория, типы, параметры).

**Файл:** `FamilyMetadataExtractionResult.cs`

```csharp
public sealed record FamilyMetadataExtractionResult(
    string FileName,
    long FileSizeBytes,
    string Sha256,
    DateTimeOffset? LastWriteTimeUtc,
    string? CategoryName,
    int? RevitMajorVersion,
    IReadOnlyList<FamilyTypeDescriptor>? Types,
    IReadOnlyList<FamilyParameterDescriptor>? Parameters);
```

---

## FamilyTypeDescriptor

Дескриптор типоразмера семейства (Post-MVP: заполняется при глубоком извлечении).

**Файл:** `FamilyTypeDescriptor.cs`

```csharp
public sealed record FamilyTypeDescriptor(
    string Id,
    string CatalogItemId,
    string Name,
    int SortOrder,
    string? VersionId = null,
    string? FileId = null,
    string? ExtractionRunId = null);
```

---

## FamilyParameterDescriptor

Дескриптор параметра семейства (Post-MVP: заполняется при глубоком извлечении).

**Файл:** `FamilyParameterDescriptor.cs`

```csharp
public sealed record FamilyParameterDescriptor(
    string Id,
    string VersionId,
    string? TypeId,
    string Name,
    string? StorageType,
    string? ValueText,
    bool? IsInstance,
    bool? IsReadonly,
    string? ForgeTypeId);
```

---

## FamilyCatalogQuery

Параметры запроса поиска по каталогу с пагинацией и фильтрацией.

**Файл:** `FamilyCatalogQuery.cs`

```csharp
public sealed record FamilyCatalogQuery(
    string? SearchText,
    string? CategoryFilter,
    ContentStatus? StatusFilter,
    IReadOnlyList<string>? Tags,
    string? ManufacturerFilter,
    FamilyCatalogSort Sort,
    int Offset,
    int Limit);
```

---

## FamilyCatalogSort

Порядок сортировки результатов поиска по каталогу.

**Файл:** `FamilyCatalogSort.cs`

```csharp
public enum FamilyCatalogSort
{
    NameAsc,
    NameDesc,
    UpdatedAtDesc,
    CreatedAtDesc
}
```

---

## FamilyCatalogCapabilities

Описание возможностей провайдера каталога — используется для адаптации UI.

**Файл:** `FamilyCatalogCapabilities.cs`

```csharp
public sealed record FamilyCatalogCapabilities(
    bool SupportsWrite,
    bool SupportsSearch,
    bool SupportsTags,
    bool SupportsBatchImport,
    bool SupportsVersionHistory,
    CatalogProviderKind ProviderKind);
```

---

## SelectedElementsAnalysis

Результат `ISystemFamilyRevitOperations.PickSelectedElements`:
- `SystemTypes` — выбранные системные элементы (после фильтра `AnyElementSelectionFilter`).
- `LoadableFamilies` — уникальные `Family` (через `GroupBy(fi.Symbol.Family)`), выбранные пользователем.

**Файл:** `SelectedElementsAnalysis.cs`

```csharp
public sealed record SelectedElementsAnalysis(
    IReadOnlyList<SelectedSystemType> SystemTypes,
    IReadOnlyList<LoadableFamilyInfo> LoadableFamilies)
{
    public int TotalCount => SystemTypes.Count + LoadableFamilies.Count;
    public bool IsEmpty => TotalCount == 0;
}
```

> **Note:** Модель `SelectedElementsAnalysis` живёт в `Core/Models/FamilyManager/`, но относится к flow пикера (объединяет system + loadable). Задокументирована здесь вместе с loadable.

---

## FamilyVersion

Per-family version marker, хранимый в ExtensibleStorage (Schema `SmartCon.FamilyVersion.v1`, ADR-030). Содержит ID записи каталога, метку загруженной версии, время загрузки в проект и версию Revit, которой загружали. `CurrentSchemaVersion` — номер текущей схемы payload (используется при будущих миграциях). `Empty` — sentinel для «маркер не прочитан».

**Файл:** `FamilyVersion.cs`

```csharp
public sealed record FamilyVersion(
    int SchemaVersion,
    string CatalogItemId,
    string VersionLabel,
    DateTimeOffset LoadedAtUtc,
    int SourceRevitVersion)
{
    public const int CurrentSchemaVersion = 1;

    public static FamilyVersion Empty { get; } =
        new(0, string.Empty, string.Empty, DateTimeOffset.MinValue, 0);
}
```

---

## StaleCheckResult

Результат проверки актуальности одного семейства (Issue #69, ADR-030). Содержит enum `StaleReason` (причина, по которой семейство считается устаревшим: `NoEntityStorage` для семейств без ES-маркера, `VersionMismatch`, `RevitVersionMismatch`, `NotInCatalog`) и record `StaleCheckResult` с версиями из каталога и из ES.

**Файл:** `StaleCheckResult.cs`

```csharp
public enum StaleReason
{
    None = 0,
    NoEntityStorage = 1,
    VersionMismatch = 2,
    RevitVersionMismatch = 3,
    NotInCatalog = 4
}

public sealed record StaleCheckResult(
    string CatalogItemId,
    string FamilyName,
    string? CurrentVersionLabel,
    string? LoadedVersionLabel,
    bool IsStale,
    StaleReason Reason);
```

---

## StaleUpdateRequest

Параметры операции обновления устаревших семейств (одиночного или пакетного). `OverwriteParameterValues` пробрасывается в `FamilyLoadOptions.OverwriteParameterValues`. `Recursive` зарезервирован для будущего использования (сейчас всегда `true`).

**Файл:** `StaleUpdateRequest.cs`

```csharp
public sealed record StaleUpdateRequest(
    IReadOnlyList<string> CatalogItemIds,
    bool OverwriteParameterValues,
    bool Recursive = true);
```

---

## StaleBatchUpdateResult

Агрегированный результат пакетного обновления (record `StaleBatchUpdateResult` со счётчиками и списком failed ID для retry) и payload прогресса (record `StaleBatchUpdateProgress`) для `IProgress<>` callback.

**Файл:** `StaleBatchUpdateResult.cs`

```csharp
public sealed record StaleBatchUpdateResult(
    int TotalRequested,
    int SuccessCount,
    int FailedCount,
    IReadOnlyList<string> FailedCatalogItemIds);

public sealed record StaleBatchUpdateProgress(
    int Completed,
    int Total,
    string CurrentFamilyName);
```

---

## FamilyStaleSnapshot

Сессионный снимок результатов проверки актуальности (ADR-030, D-06). Заполняется `IStaleDetector` и инвалидируется при Load/Update/Edit и смене БД. Используется VM для обновления индикаторов категорий без повторного чтения ES. `Empty` — sentinel для «снимок ещё не построен».

**Файл:** `FamilyStaleSnapshot.cs`

```csharp
public sealed record FamilyStaleSnapshot(
    IReadOnlyDictionary<string, StaleCheckResult> Results,
    DateTimeOffset CheckedAtUtc)
{
    public static FamilyStaleSnapshot Empty { get; } =
        new(new Dictionary<string, StaleCheckResult>(), DateTimeOffset.MinValue);
}
```

---

## CategoryStaleStats

Per-category roll-up статистика для stale detection (ADR-030 Phase 24). Возвращается из `IStaleCategoryAggregator.AggregateByCategory` и маппится на `CategoryNodeViewModel.HasStale` / `StaleCount`.

**Файл:** `CategoryStaleStats.cs` (в `SmartCon.Core/Services/Interfaces/`, рядом с `IStaleCategoryAggregator`)

```csharp
public sealed record CategoryStaleStats(bool HasStale, int StaleCount)
{
    public static CategoryStaleStats Empty { get; } = new(false, 0);
}
```

**Семантика:**
- `HasStale = true` если любое catalog item, привязанное к этой категории (с recursive parent expansion), stale.
- `StaleCount` — количество stale items **напрямую** в этой категории (не считая подкатегории — это encoded в `HasStale` родителя).
- `Empty` — дефолт для категории без stale items (используется в `AggregateByCategory`).

---

## StaleSnapshotLogic

Pure static helper для merge / prune snapshot (ADR-030 Phase 24, fix-merge-snapshot bug). Вынесен в Core чтобы unit-тесты могли проверить логику merge без поднятия Revit API.

**Файл:** `StaleSnapshotLogic.cs` (в `SmartCon.Core/Services/Interfaces/`)

```csharp
public static class StaleSnapshotLogic
{
    public static FamilyStaleSnapshot MergeInto(
        FamilyStaleSnapshot? existing,
        IReadOnlyList<StaleCheckResult> newResults,
        DateTimeOffset now);

    public static FamilyStaleSnapshot RemoveFrom(
        FamilyStaleSnapshot? existing,
        IReadOnlyCollection<string> catalogItemIds,
        DateTimeOffset now);
}
```

**Семантика (исправляет баг «Проверить на одной папке теряет stale на другой»):**

- `MergeInto`: **добавляет/перезаписывает** entries из `newResults` в существующий snapshot. Entries для **других** catalog item IDs (других категорий) сохраняются. Это значит что `CheckCategory(catA)` затем `CheckCategory(catB)` сохраняет stale маркеры обеих категорий.
- `RemoveFrom`: **удаляет** entries по `catalogItemIds`. Возвращает тот же snapshot instance если ничего не удалено (zero-allocation). Используется после успешного Update — stale маркер удаляется, остальные сохраняются.
- Both methods **не мутируют** входной snapshot — создаётся новый `FamilyStaleSnapshot`.

