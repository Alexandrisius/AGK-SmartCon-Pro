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

