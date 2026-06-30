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
    FamilyImportSource? Source = null)
{
    public FamilyBatchImportAction Action { get; set; }
    public string? TargetCategoryId { get; set; }
    public string? TargetCategoryName { get; set; }
}
```

- `FilePath` — для UC-1/UC-2 (импорт с диска или активного `.rfa`) это реальный путь к файлу. Для UC-3/UC-4 (импорт активного проекта / выделенных элементов) это placeholder `"system://..."` или `"loadable://..."` до подтверждения пользователем, после чего `ProcessProjectImportAsync` перезаписывает это поле на managed-путь.
- `Source` (v2.0.0) — payload для пост-диалогового staging flow (UC-3/UC-4). `null` для UC-1/UC-2 (файл уже на диске). `SystemSource` / `LoadableSource` — sealed record-union, см. [FamilyImportSource](#familyimportsource).
- v2.0.0 breaking change: поля `Sha256` и `FileSizeBytes` удалены — SHA-256 dedup и показ размера файла больше не используются (см. ADR-035).

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
См. ADR-041.

**Файл:** `SetActiveVersionResult.cs`

```csharp
public sealed record SetActiveVersionResult(
    bool Success,
    string CatalogItemId,
    string VersionLabel,
    string? PreviousVersionLabel,
    DateTimeOffset ActivatedAtUtc,
    bool ContentHashSynced,
    string? ErrorMessage = null);
```

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
Catalog sidecar рядом с оригиналом при подготовке managed `.rfa`
(ADR-033).

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
    string ParentFamilyName,
    int IndexInBatch = 1,
    int TotalInBatch = 1,
    SharedFamilyNameSource NameSource = SharedFamilyNameSource.RevitApi);
```

| Поле | Назначение |
|---|---|
| `SharedFamilyName` | Имя конфликтующего shared nested. В Revit 2024.3+ — из Revit API. В более ранних (REVIT-198137) — из каталога SmartCon через `SharedFamilyNameResolver` (см. ADR-034) |
| `IsFamilyInUse` | Размещены ли экземпляры в проекте (влияет на текст предупреждения) |
| `ParentFamilyName` | Имя родительского семейства для caption диалога |
| `IndexInBatch` | 1-based индекс текущего вызова в серии `OnSharedFamilyFound` (ADR-034) |
| `TotalInBatch` | Размер списка `nestedSharedNames` из БД (0 = legacy-каталог, прогресс скрыт) |
| `NameSource` | Откуда взято `SharedFamilyName` (см. ниже) |

## SharedFamilyNameSource

Источник имени, отображаемого в диалоге. Используется UI для прозрачности —
если имя пришло не из Revit API, показывается индикатор «имя из каталога».

**Файл:** `SharedFamilyNameSource.cs`

```csharp
public enum SharedFamilyNameSource
{
    RevitApi = 0,            // Нормальный путь: Revit 2024.3+ / 2025+
    CatalogDb = 1,           // Fallback: REVIT-198137 + каталог SmartCon
    FallbackPlaceholder = 2  // Крайний случай: ни Revit, ни БД (legacy-каталог)
}
```

## SharedFamilyNameResolver

Pure-C# helper, выбирающий лучшее доступное имя для shared nested conflict.
Извлечён из `RevitFamilyLoadOptions` чтобы логика counter + fallback была
unit-тестируемой без Revit API (sealed native тип `Autodesk.Revit.DB.Family`).

**Файл:** `SharedFamilyNameResolver.cs`

```csharp
public sealed class SharedFamilyNameResolver
{
    public SharedFamilyNameResolver(IReadOnlyList<string>? nestedSharedNames = null);
    public int NextInvocationIndex();
    public int TotalInBatch { get; }
    public (string Name, SharedFamilyNameSource Source) Resolve(
        string? revitApiName, int invocationIndex);
}
```

Цепочка: `RevitApi` (если не null/whitespace) → `CatalogDb` (по индексу
вызова) → `FallbackPlaceholder` (с индексом). Thread-safe counter через
`Interlocked.Increment`. См. ADR-034.

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

## FamilyTypeCatalogBakingResult

Результат запекания Type Catalog (.txt) в .rfa при импорте (ADR-033).

**Файл:** `FamilyTypeCatalogBakingResult.cs`

```csharp
public sealed record FamilyTypeCatalogBakingResult(
    bool Success,
    string? OutputRfaPath,
    int BakedTypeCount,
    string? ErrorMessage);
```

---

## TypeCatalogParseResult

Результат парсинга Type Catalog (.txt) семейства Revit.

**Файл:** `TypeCatalogParseResult.cs`

```csharp
public sealed record TypeCatalogParseResult(
    IReadOnlyList<TypeCatalogColumn> Columns,
    IReadOnlyList<TypeCatalogEntry> Entries)
{
    public IReadOnlyList<string> ParameterNames => Columns.Select(c => c.Name).ToList();
    public bool HasEntries => Entries.Count > 0;
    public TypeCatalogColumn? FindColumn(string parameterName);
}
```

`Columns` хранит header колонок вместе с `##TYPE##UNITS` annotation из Revit Type Catalog
specification. `ParameterNames` остался как backward-compatible helper для мест,
где нужен только список имён (formula-variable lookup). `FindColumn` используется
baker'ом для unit conversion перед `FamilyManager.Set`. См. ADR-033 BAKE-006.

---

## TypeCatalogColumn

Один столбец из header Type Catalog (.txt). Хранит имя параметра и опциональные
annotation `##TYPE##UNITS` (например `LENGTH##MILLIMETERS`), которые говорят Revit
о единицах измерения значений в колонке.

**Файл:** `TypeCatalogColumn.cs`

```csharp
public sealed record TypeCatalogColumn(
    string Name,
    string? TypeAnnotation,
    string? UnitAnnotation)
{
    public bool HasUnitAnnotation => !string.IsNullOrEmpty(UnitAnnotation);
}
```

Используется baker'ом через `RevitUnitsCompat.CatalogCellToInternalUnits` для
конвертации raw значения в Revit internal units перед записью в параметр семейства.
См. ADR-033 BAKE-006..009.

---

## TypeCatalogUnitAlias

Pure C# маппинг unit annotation из Type Catalog header в canonical key.
Вынесен из `RevitUnitsCompat.ResolveSourceUnitTypeId` для unit-тестирования
без Revit API (см. `docs/testing/unit-conversion-coverage-gaps.md`).
Canonical key (lowercase plural) мапится в `UnitTypeId` (R21+) или
`DisplayUnitType` (R19-R20) на стороне Revit-слоя.

**Файл:** `TypeCatalogUnitAlias.cs`

```csharp
public static class TypeCatalogUnitAlias
{
    public static string? Normalize(string? rawAnnotation);
    public static IReadOnlyCollection<string> SupportedAliases { get; }
}
```

`Normalize` принимает произвольный вход (null/whitespace возвращают `null`),
trim'ит, lower-case'ит, и резолвит в canonical key:

- **Length**: `millimeters`/`millimeter`/`milimeters` (Autodesk typo)/`mm`, `centimeters`/`cm`, `decimeters`/`dm`, `meters`/`m`, `inches`/`in`, `feet`/`ft`
- **Angle**: `degrees`/`degree`/`decimal_degrees`/`deg`, `radians`/`radian`/`rad`, `grads`/`grad` (R19-R20 only)
- **Area**: `square_millimeters`/`sq_mm`, ..., `square_feet`/`sq_ft`
- **Volume**: `cubic_millimeters`/`cu_mm`, ..., `cubic_feet`/`cu_ft`
- **Power**: `watts`/`w`, `kilowatts`/`kw`
- **Electrical**: `amperes`/`amp`/`amps`/`a`, `volts`/`v`

15 unit-тестов в `TypeCatalogUnitAliasTests.cs` покрывают все aliases + edge cases
(null/empty/whitespace/unknown).

См. ADR-033 BAKE-007, §Sources + §Risks (unit conversion testing gap).

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
    string? ExtractionRunId = null,
    string? UniqueId = null);
```

**v2.0.0 (ADR-036):** `UniqueId` (Revit `Element.UniqueId` типа) добавлен для устранения коллизий
при merge. Используется в `IFamilyTypeRepository.SyncTypesAsync` для сохранения
через `type_unique_id` колонку.

---

## IFamilyTypeRepository

**v2.0.0 (ADR-036):** старые методы `SaveTypesAsync` (DELETE+INSERT, project case) и
`SaveTypesForRunAsync` (UPSERT без DELETE, имел bug #1) **удалены** и заменены
единым `SyncTypesAsync` с семантикой **DELETE+INSERT в одной транзакции**.

**Файл:** `src/SmartCon.Core/Services/Interfaces/IFamilyTypeRepository.cs`

```csharp
public interface IFamilyTypeRepository
{
    Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemAsync(string catalogItemId, CancellationToken ct = default);
    Task<IReadOnlyList<FamilyTypeDescriptor>> GetTypesForItemVersionAsync(string catalogItemId, string? versionId, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, IReadOnlyList<FamilyTypeDescriptor>>> GetAllTypesBatchAsync(IEnumerable<string> catalogItemIds, CancellationToken ct = default);

    /// <summary>
    /// Synchronises the type list for a given (catalogItemId, versionId, fileId) triple
    /// inside a single transaction. Atomically removes types missing from the new list
    /// and upserts the supplied types. Returns {typeName → typeId} for downstream
    /// attribute-value persistence.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> SyncTypesAsync(
        string catalogItemId,
        string? versionId,
        string? fileId,
        string runId,
        IReadOnlyList<FamilyTypeDescriptor> types,
        CancellationToken ct = default);

    Task<bool> HasTypesAsync(string catalogItemId, CancellationToken ct = default);
}
```

**Call sites:**

| Caller | versionId / fileId | runId | Семантика |
|---|---|---|---|
| `FamilyDataImportService` (импорт активного .rfa) | `versionId`, `fileId` из extraction result | `runId` извлечения | Multi-version safe |
| `LocalFamilyImportService.TypeCatalog` (.txt bake-in) | `versionId`, `null` | `runId` извлечения | Multi-version safe |
| `LoadableFamilyImportOrchestrator` (project case) | `null`, `null` | `"no-run"` | Заменяет все типы catalog item |
| `SystemFamilyImportOrchestrator` (project case) | `null`, `null` | `"no-run"` | Заменяет все типы catalog item |

**Schema requirement:** `extracted_attribute_values.type_id` имеет FOREIGN KEY
→ `family_types(id) ON DELETE CASCADE` (V15, ADR-036). Orphan attribute values
автоматически удаляются при `SyncTypesAsync` с пустым `types` списком.

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

---

## LoadableMarkerLogic

Issue #84 / Phase 24 (ADR-030): pure static helper, который после успешного импорта loadable-семейства из активного проекта в каталог (через «Импорт активного файла» / «Импорт выделенных») переписывает `SmartCon_FamilyVersion_v1` ExtensibleStorage маркер на Family-элементе в активном проекте. Без этого шага следующий «Проверить» сразу помечает каждое свеже-импортированное loadable как `StaleReason.NoEntityStorage`, хотя это семейство в проекте и есть авторитетный источник vN+1 для новой строки каталога.

**Файл:** `LoadableMarkerLogic.cs` (в `SmartCon.Core/Services/Interfaces/`)

```csharp
public static class LoadableMarkerLogic
{
    public static Task<MarkerWriteSummary> WriteMarkersForImportedLoadablesAsync(
        IReadOnlyList<FamilyBatchImportItem> loadableItems,
        IReadOnlyList<LoadableFamilyAttributeTask> attributeTasks,
        IFamilyVersionWriter versionWriter,
        int targetRevit,
        CancellationToken ct);

    public sealed record MarkerWriteSummary(
        int SuccessCount,
        int SkippedCount,
        int FailedCount,
        int Total);
}
```

**Семантика:**

- Для каждого `LoadableFamilyAttributeTask` (успешно импортированное loadable) ищет соответствующий `FamilyBatchImportItem` по `PrecomputedCatalogItemId` и вызывает `IFamilyVersionWriter.WriteVersionMarkerAsync(catalogItemId, item.FileName, item.PrecomputedVersionLabel, targetRevit, ct)`. Версия маркера = та же, что попала в каталог (`v1` для нового, `vN+1` для ре-импорта).
- Само содержимое `Family` в проекте **не меняется** — меняется только метаданные маркера.
- Per-family try/catch: исключение на одном семействе **не прерывает** батч — пишется `Warn` с `[Action: ...]` и счётчик `FailedCount++`. Каталог уже принял запись, откатывать импорт нельзя.
- `SkippedCount` растёт если `task.CatalogItemId` пуст или не нашлось matching batch item (теоретический edge case, в orchestrator'е такого не бывает).
- System families (`FamilySource == "system"`) **не появляются** на входе — `ProcessProjectImportAsync` фильтрует их upstream. По дизайну (ADR-030 §Out of Scope) у них нет in-project `Family` элемента в смысле Revit API (`OST_PipeCurves` и т.п. — это `MEPCurve` / `Wall`).
- Aggregate `MarkerWriteSummary`: `SuccessCount + SkippedCount + FailedCount == Total`.

**Используется в:**
- `FamilyManagerMainViewModel.FamilyEdit.cs:WriteVersionMarkersForImportedLoadablesAsync` — тонкая обёртка, делегирующая в этот helper. Вызывается из `ProcessProjectImportAsync` после `LoadableFamilyImportOrchestrator.ImportAndPersistTypesAsync` и перед `_staleDetector.InvalidateCache()`.

**Pure logic** — никакого Revit API в сигнатуре (только `IFamilyVersionWriter`). Позволяет unit-тестировать через hand-written fake `IFamilyVersionWriter` без поднятия Revit (`src/SmartCon.Tests/FamilyManager/Stale/LoadableMarkerLogicTests.cs` — 8 тестов на empty batch / single / multiple / no-match skip / full failure / partial failure isolation / targetRevit passthrough / null-args throws).

---

## FamilySourceTypeInfo

v2.0.0: Core-level DTO для типов системных семейств, используемый в публичных API batch dialog (см. `FamilyBatchImportItem.SourceTypes` и `SystemFamilyPendingImport.Types`). Создан чтобы Core record не тянул `Autodesk.Revit.DB.BuiltInCategory` через `SelectedSystemType` — иначе нарушается I-09 (Core не должен зависеть от Revit API в публичных сигнатурах) и тесты без runtime Revit падают.

**Файл:** `Models/FamilyManager/FamilySourceTypeInfo.cs`

```csharp
public sealed record FamilySourceTypeInfo(
    string UniqueId,
    string Name,
    string CategoryName,
    int CategoryId);
```

- `UniqueId` — Revit unique id элемента типа. Extractor в managed `.rvt` ищет этот id.
- `Name` — отображаемое имя типа.
- `CategoryName` — отображаемое имя родительской категории (например `"OST_PipeFitting"`).
- `CategoryId` — ordinal `BuiltInCategory`, переданный через границу FamilyManager→Core как plain `int`. Orchestrator и extractor никогда не видят enum напрямую.

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
        IReadOnlyList<string> TypeNames) : FamilyImportSource;

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

## FamilyContentHash

Semantic content fingerprint of a family. Stable across SaveAs, rename, Revit upgrade. Changes when any parameter, type, value, geometry or formula changes. v2.0.0 dedup core.

**Файл:** Models/FamilyManager/FamilyContentHash.cs

`csharp
public sealed record FamilyContentHash(
    string HexString,
    int FormatVersion,
    string SourceKind);

public static class FamilyContentHashFormat
{
    public const int CurrentVersion = 1;
}
`

- HexString — SHA-256 hex string (uppercase, no dashes).
- FormatVersion — algorithm version, bumped when canonical-string format changes so old hashes do not produce false duplicate matches against new ones.
- SourceKind — "loadable" or "system". Used to enforce cross-source separation (system hashes never match loadable hashes and vice versa).
- FamilyContentHashFormat.CurrentVersion — current format version (= 1). Old rows with a lower FormatVersion will not produce false duplicate matches against newly computed hashes.

---

## FamilySnapshot

Structured snapshot of a loadable family (.rfa) used to compute a FamilyContentHash. Extracted in-memory from an open family document — never from file bytes — so it is stable across SaveAs, rename, and Revit upgrade.

**Файл:** Models/FamilyManager/FamilySnapshot.cs

`csharp
public sealed record FamilySnapshot(
    string FamilyName,
    string Category,
    IReadOnlyList<FamilyParameterInfo> Parameters,
    IReadOnlyList<FamilyTypeSnapshot> Types,
    GeometryMetrics Geometry,
    IReadOnlyList<string> SharedNestedFamilyNames);

public sealed record FamilyParameterInfo(
    string Name,
    string StorageType,
    string ParameterGroup,
    bool IsInstance,
    bool IsShared,
    string? Formula,
    bool IsDeterminedByFormula,
    bool IsReporting,
    string? SharedParamGuid,
    string? BuiltInParameterId);

public sealed record FamilyTypeSnapshot(
    string Name,
    IReadOnlyList<FamilyParameterValue> Values);

public sealed record FamilyParameterValue(
    string ParameterName,
    string StorageType,
    bool HasValue,
    string? ValueText,
    double? ValueNumber,
    string? ResolvedElementName);
`

- FamilyName — from FamilyManager or family document title.
- Category — display name (e.g. "Pipe Fittings"). Changes to it shift the hash.
- Parameters — all schema-level parameters, sorted by name. Includes SharedParamGuid for shared params and BuiltInParameterId enum name for built-ins (null for user/shared).
- Types — all family types with their values. Unnamed types skipped.
- Geometry — aggregated GeometryMetrics from all GenericForm elements.
- SharedNestedFamilyNames — names of shared nested families (ADR-034), sorted.
- FamilyParameterValue.HasValue distinguishes "no value" (alse) from "value is zero" (	rue, ValueNumber=0) — hash treats them differently.

---

## SystemFamilySnapshot

Structured snapshot of a system family (category + types) inside a project (.rvt). Used to compute a FamilyContentHash for system families. Extracted in-memory from the active project document.

**Файл:** Models/FamilyManager/SystemFamilySnapshot.cs

`csharp
public sealed record SystemFamilySnapshot(
    string CategoryName,
    int CategoryId,
    IReadOnlyList<SystemTypeSnapshot> Types);

public sealed record SystemTypeSnapshot(
    string Name,
    IReadOnlyList<SystemParameterValue> Values);

public sealed record SystemParameterValue(
    string ParameterName,
    string StorageType,
    bool HasValue,
    string? ValueText,
    double? ValueNumber,
    string? ResolvedElementName);
`

- CategoryName — display name (e.g. "Трубы", "Воздуховоды").
- CategoryId — numeric BuiltInCategory ordinal carried as int so Core does not depend on Autodesk.Revit.DB (I-09).
- Types — selected system types with values, sorted by type name.
- SystemParameterValue — same semantics as FamilyParameterValue — distinguishes "no value" from "zero".

**v2.0.0 hash stability:** the hasher skips blank values (HasValue=false, empty string, INVALID, UNSUPPORTED, READERROR) so the empty ADSK_Завод-изготовитель parameter does not contribute to the hash. The hasher also skips the auto-generated Код IfcGUID parameter (different per .rvt save).

---

## GeometryMetrics

Aggregated geometry metrics for a loadable family document. Used as part of the content fingerprint so that adding/removing a form, or changing an extrusion depth, shifts the hash.

**Файл:** Models/FamilyManager/GeometryMetrics.cs

`csharp
public sealed record GeometryMetrics(
    int TotalFormCount,
    IReadOnlyList<FormMetrics> Forms);

public sealed record FormMetrics(
    string FormKind,
    bool IsSolid,
    double Volume,
    int FaceCount,
    int EdgeCount,
    string? SubcategoryName);
`

- TotalFormCount — number of GenericForm elements (extrusions, sweeps, revolutions, blends, free-form).
- Forms — per-form metrics sorted by (FormKind, IsSolid, Volume) for deterministic output.
- Volume — total volume of all solids in Revit internal units (cubic feet), 6-decimal precision so a 1 mm change shifts the value.
- FaceCount / EdgeCount — totals across all solids, or 0 if geometry could not be extracted (known bug for shared nested families).
- FormKind — "Extrusion", "Sweep", "Revolution", "Blend", "SweptBlend", or "GenericForm" for free-form.

---

## ContentHashMatch

Result of a cross-version content-hash search. Returned when a content hash matches a version (current or archived) of a catalog item. Used to display "Дубликат (vN)" in the batch dialog.

**Файл:** Models/FamilyManager/ContentHashMatch.cs

`csharp
public sealed record ContentHashMatch(
    string CatalogItemId,
    string MatchedVersionLabel,
    bool IsCurrentVersion);
`

- CatalogItemId — ID of the catalog item whose version matched.
- MatchedVersionLabel — label of the matching version (e.g. "v2").
- IsCurrentVersion — 	rue if matched is current version; alse if archived (e.g. after rollback).

---

## ContentHashDedupResult

Result of the content-hash dedup check for a single batch-import row. Combines the name-based lookup with the cross-version hash search to produce the final FamilyBatchImportStatus.

**Файл:** Models/FamilyManager/ContentHashDedupResult.cs

`csharp
public sealed record ContentHashDedupResult(
    FamilyBatchImportStatus Status,
    string? ExistingCatalogItemId,
    string? ExistingVersionLabel,
    ContentHashMatch? HashMatch);
`

- Status — final status: New, Existing, Duplicate, or Error.
- ExistingCatalogItemId — ID of the existing item found by normalized name, or 
ull.
- ExistingVersionLabel — current version label of the existing item, or 
ull.
- HashMatch — cross-version hash match details if Status == Duplicate; otherwise 
ull.

---

## PreparedFamilyItem

Result of Phase 1 (Prepare) of the unified import flow. Contains everything the batch dialog needs to display a row and everything Phase 3 (Commit) needs to write to the catalog — extracted in a single pass from one opened document, without re-opening.

**Файл:** Models/FamilyManager/PreparedFamilyItem.cs

`csharp
public sealed record PreparedFamilyItem(
    string SourcePath,
    string DisplayName,
    int RevitMajorVersion,
    FamilyContentHash? ContentHash,
    FamilySnapshot? LoadableSnapshot,
    SystemFamilySnapshot? SystemSnapshot,
    string? ErrorMessage,
    FamilyImportSource? Source,
    IReadOnlyList<FamilySourceTypeInfo>? SourceTypes,
    string FamilySource,
    FamilyBatchImportStatus Status = FamilyBatchImportStatus.New,
    string? ExistingCatalogItemId = null,
    string? ExistingVersionLabel = null,
    string? MatchedVersionLabel = null);
`

- SourcePath — file path for UC-1, virtual placeholder for UC-3/UC-4 ("system://...", "loadable://...").
- ContentHash — computed hash, or 
ull if extraction failed (see ErrorMessage).
- LoadableSnapshot / SystemSnapshot — one is set, the other is 
ull depending on FamilySource.
- Source — v2.0.0 source payload for UC-3/UC-4 post-dialog staging; 
ull for UC-1/UC-2.
- MatchedVersionLabel — set when Status == Duplicate so the UI can show "Дубликат (v2)".
---

## FamilyContentHash

Semantic content fingerprint of a family. Stable across SaveAs, rename, Revit upgrade. Changes when any parameter, type, value, geometry or formula changes. v2.0.0 dedup core.

**Файл:** `Models/FamilyManager/FamilyContentHash.cs`

```csharp
public sealed record FamilyContentHash(
    string HexString,
    int FormatVersion,
    string SourceKind);

public static class FamilyContentHashFormat
{
    public const int CurrentVersion = 1;
}
```

- `HexString` — SHA-256 hex string (uppercase, no dashes).
- `FormatVersion` — algorithm version, bumped when canonical-string format changes so old hashes do not produce false duplicate matches against new ones.
- `SourceKind` — `"loadable"` or `"system"`. Used to enforce cross-source separation (system hashes never match loadable hashes and vice versa).
- `FamilyContentHashFormat.CurrentVersion` — current format version (= 1). Old rows with a lower `FormatVersion` will not produce false duplicate matches against newly computed hashes.

---

## FamilySnapshot

Structured snapshot of a loadable family (.rfa) used to compute a `FamilyContentHash`. Extracted in-memory from an open family document — never from file bytes — so it is stable across SaveAs, rename, and Revit upgrade.

**Файл:** `Models/FamilyManager/FamilySnapshot.cs`

```csharp
public sealed record FamilySnapshot(
    string FamilyName,
    string Category,
    IReadOnlyList<FamilyParameterInfo> Parameters,
    IReadOnlyList<FamilyTypeSnapshot> Types,
    GeometryMetrics Geometry,
    IReadOnlyList<string> SharedNestedFamilyNames);

public sealed record FamilyParameterInfo(
    string Name,
    string StorageType,
    string ParameterGroup,
    bool IsInstance,
    bool IsShared,
    string? Formula,
    bool IsDeterminedByFormula,
    bool IsReporting,
    string? SharedParamGuid,
    string? BuiltInParameterId);

public sealed record FamilyTypeSnapshot(
    string Name,
    IReadOnlyList<FamilyParameterValue> Values);

public sealed record FamilyParameterValue(
    string ParameterName,
    string StorageType,
    bool HasValue,
    string? ValueText,
    double? ValueNumber,
    string? ResolvedElementName);
```

- `FamilyName` — from `FamilyManager` or family document title.
- `Category` — display name (e.g. "Pipe Fittings"). Changes to it shift the hash.
- `Parameters` — all schema-level parameters, sorted by name. Includes `SharedParamGuid` for shared params and `BuiltInParameterId` enum name for built-ins (null for user/shared).
- `Types` — all family types with their values. Unnamed types skipped.
- `Geometry` — aggregated `GeometryMetrics` from all `GenericForm` elements.
- `SharedNestedFamilyNames` — names of shared nested families (ADR-034), sorted.
- `FamilyParameterValue.HasValue` distinguishes "no value" (`false`) from "value is zero" (`true`, `ValueNumber=0`) — hash treats them differently.

---

## SystemFamilySnapshot

Structured snapshot of a system family (category + types) inside a project (.rvt). Used to compute a `FamilyContentHash` for system families. Extracted in-memory from the active project document.

**Файл:** `Models/FamilyManager/SystemFamilySnapshot.cs`

```csharp
public sealed record SystemFamilySnapshot(
    string CategoryName,
    int CategoryId,
    IReadOnlyList<SystemTypeSnapshot> Types);

public sealed record SystemTypeSnapshot(
    string Name,
    IReadOnlyList<SystemParameterValue> Values);

public sealed record SystemParameterValue(
    string ParameterName,
    string StorageType,
    bool HasValue,
    string? ValueText,
    double? ValueNumber,
    string? ResolvedElementName);
```

- `CategoryName` — display name (e.g. "Трубы", "Воздуховоды").
- `CategoryId` — numeric `BuiltInCategory` ordinal carried as `int` so Core does not depend on `Autodesk.Revit.DB` (I-09).
- `Types` — selected system types with values, sorted by type name.
- `SystemParameterValue` — same semantics as `FamilyParameterValue` — distinguishes "no value" from "zero".

**v2.0.0 hash stability:** the hasher skips blank values (`HasValue=false`, empty string, `INVALID`, `UNSUPPORTED`, `READERROR`) so the empty `ADSK_Завод-изготовитель` parameter does not contribute to the hash. The hasher also skips the auto-generated `Код IfcGUID` parameter (different per `.rvt` save).

---

## GeometryMetrics

Aggregated geometry metrics for a loadable family document. Used as part of the content fingerprint so that adding/removing a form, or changing an extrusion depth, shifts the hash.

**Файл:** `Models/FamilyManager/GeometryMetrics.cs`

```csharp
public sealed record GeometryMetrics(
    int TotalFormCount,
    IReadOnlyList<FormMetrics> Forms);

public sealed record FormMetrics(
    string FormKind,
    bool IsSolid,
    double Volume,
    int FaceCount,
    int EdgeCount,
    string? SubcategoryName);
```

- `TotalFormCount` — number of `GenericForm` elements (extrusions, sweeps, revolutions, blends, free-form).
- `Forms` — per-form metrics sorted by `(FormKind, IsSolid, Volume)` for deterministic output.
- `Volume` — total volume of all solids in Revit internal units (cubic feet), 6-decimal precision so a 1 mm change shifts the value.
- `FaceCount` / `EdgeCount` — totals across all solids, or 0 if geometry could not be extracted (known bug for shared nested families).
- `FormKind` — `"Extrusion"`, `"Sweep"`, `"Revolution"`, `"Blend"`, `"SweptBlend"`, or `"GenericForm"` for free-form.

---

## ContentHashMatch

Result of a cross-version content-hash search. Returned when a content hash matches a version (current or archived) of a catalog item. Used to display "Дубликат (vN)" in the batch dialog.

**Файл:** `Models/FamilyManager/ContentHashMatch.cs`

```csharp
public sealed record ContentHashMatch(
    string CatalogItemId,
    string MatchedVersionLabel,
    bool IsCurrentVersion);
```

- `CatalogItemId` — ID of the catalog item whose version matched.
- `MatchedVersionLabel` — label of the matching version (e.g. `"v2"`).
- `IsCurrentVersion` — `true` if matched is current version; `false` if archived (e.g. after rollback).

---

## ContentHashDedupResult

Result of the content-hash dedup check for a single batch-import row. Combines the name-based lookup with the cross-version hash search to produce the final `FamilyBatchImportStatus`.

**Файл:** `Models/FamilyManager/ContentHashDedupResult.cs`

```csharp
public sealed record ContentHashDedupResult(
    FamilyBatchImportStatus Status,
    string? ExistingCatalogItemId,
    string? ExistingVersionLabel,
    ContentHashMatch? HashMatch);
```

- `Status` — final status: `New`, `Existing`, `Duplicate`, or `Error`.
- `ExistingCatalogItemId` — ID of the existing item found by normalized name, or `null`.
- `ExistingVersionLabel` — current version label of the existing item, or `null`.
- `HashMatch` — cross-version hash match details if `Status == Duplicate`; otherwise `null`.

---

## PreparedFamilyItem

Result of Phase 1 (Prepare) of the unified import flow. Contains everything the batch dialog needs to display a row and everything Phase 3 (Commit) needs to write to the catalog — extracted in a single pass from one opened document, without re-opening.

**Файл:** `Models/FamilyManager/PreparedFamilyItem.cs`

```csharp
public sealed record PreparedFamilyItem(
    string SourcePath,
    string DisplayName,
    int RevitMajorVersion,
    FamilyContentHash? ContentHash,
    FamilySnapshot? LoadableSnapshot,
    SystemFamilySnapshot? SystemSnapshot,
    string? ErrorMessage,
    FamilyImportSource? Source,
    IReadOnlyList<FamilySourceTypeInfo>? SourceTypes,
    string FamilySource,
    FamilyBatchImportStatus Status = FamilyBatchImportStatus.New,
    string? ExistingCatalogItemId = null,
    string? ExistingVersionLabel = null,
    string? MatchedVersionLabel = null);
```

- `SourcePath` — file path for UC-1, virtual placeholder for UC-3/UC-4 (`"system://..."`, `"loadable://..."`).
- `ContentHash` — computed hash, or `null` if extraction failed (see `ErrorMessage`).
- `LoadableSnapshot` / `SystemSnapshot` — one is set, the other is `null` depending on `FamilySource`.
- `Source` — v2.0.0 source payload for UC-3/UC-4 post-dialog staging; `null` for UC-1/UC-2.
- `MatchedVersionLabel` — set when `Status == Duplicate` so the UI can show "Дубликат (v2)".

---

## FamilyContentHasher

Pure-C# implementation of IFamilyContentHasher. No Revit API calls — entirely deterministic. Computes SHA-256 of a sorted canonical string built from the snapshot data. Lives in SmartCon.Core/Services/Implementation/; the IFamilyContentHasher contract is in docs/domain/interfaces/family-manager.md.

**Файл:** Services/Implementation/FamilyContentHasher.cs

`csharp
public sealed class FamilyContentHasher : IFamilyContentHasher
{
    public FamilyContentHash? ComputeForLoadable(FamilySnapshot snapshot);
    public FamilyContentHash? ComputeForSystem(SystemFamilySnapshot snapshot);
}
`

**Canonical string layout:**
- FHV1|LOADABLE|{name}|{cat}|PARAMS|...|TYPES|...|GEOM|...|NESTED|... (loadable)
- FHV1|SYSTEM|{catName}|{catId}|TYPES|... (system)

**v2.0.0 stability rules:**
- Blank values excluded (IsBlankValue(hasValue, text)): HasValue=false, empty string, INVALID (no element), UNSUPPORTED, READERROR. Numeric zero is NOT blank.
- Auto-generated parameters excluded (IsAutoGeneratedParameter(name)): anything containing IfcGUID or IFC GUID (case-insensitive). Revit regenerates these on every .rvt save — including them would break cross-document stability.
- 10 unit tests in src/SmartCon.Tests/FamilyManager/Services/FamilyContentHasherTests.cs cover blank-value exclusion, INVALID exclusion, numeric zero significance, IFC GUID exclusion, loadable-family blank values, parameter/type/geometry/SharedNested independence, and cross-source prefix separation.
