---
module: family-manager
---
# Модели FamilyManager — Каталог и ассеты

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

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
    DateTimeOffset CreatedAtUtc,
    DbUserRole? CurrentUserRole = null,
    string? OwnerIdentity = null,
    BaseType Kind = BaseType.General,
    ProjectBaseBinding? ProjectBinding = null);
```

Подключение может быть общим (`Kind = General`) или проектным (`Kind = Project`).
Проектное подключение хранит `ProjectBinding` — шаблон имени файла + библиотеку полей,
по которым определяется, подходит ли активный Revit-документ для этой базы.
См. `BaseType`, `ProjectBaseBinding` и ADR-045.

---

## BaseType

Тип базы FamilyManager: общая или привязанная к проекту.

**Файл:** `BaseType.cs`

```csharp
public enum BaseType
{
    General = 0,
    Project = 1
}
```

`General` — база доступна для любого проекта. `Project` — база активируется
автоматически, только если имя файла текущего Revit-документа подходит под шаблон
`ProjectBinding`. См. ADR-045.

---

## ProjectBaseBinding

Шаблон привязки проектной базы к имени файла Revit. Состоит из блочного парсера
`FileNameTemplate` и библиотеки полей `FieldDefinition` для валидации распарсенных
значений. Живёт в Core, используется FamilyManager без зависимости от ProjectManagement.

**Файл:** `ProjectBaseBinding.cs`

```csharp
public sealed record ProjectBaseBinding(
    FileNameTemplate Template,
    IReadOnlyList<FieldDefinition> FieldLibrary)
{
    public static ProjectBaseBinding Empty => new(FileNameTemplate.Empty, []);
}
```

---

## ProjectBaseBindingEvaluator

Реализация `IProjectBaseBindingEvaluator` в Core. Делегирует парсинг имени файла
в общий `IFileNameParser` (тот же экземпляр, что использует ProjectManagement
для ShareProject), чтобы семантика block-parsing и валидации полей была
единообразной между модулями без зависимости FamilyManager → ProjectManagement.

**Файл:** `Services/Implementation/ProjectBaseBindingEvaluator.cs`
**Интерфейс:** [`IProjectBaseBindingEvaluator`](../../interfaces/family-manager/database.md#iprojectbasebindingevaluator)

```csharp
public sealed class ProjectBaseBindingEvaluator : IProjectBaseBindingEvaluator
{
    public ProjectBaseBindingEvaluator(IFileNameParser parser);
    public ProjectBaseMatch Evaluate(ProjectBaseBinding? binding, string filePath);
}
```

---

## ProjectBaseActivator

Реализация `IProjectBaseActivator` в Core. Выбирает наиболее специфичную базу
для активного Revit-документа: сначала ищет подходящую проектную базу,
иначе fallback на первую общую базу. Если ни одной базы нет — активная база
не меняется (UI покажет mismatch-замок на текущей проектной базе).

**Файл:** `Services/Implementation/ProjectBaseActivator.cs`
**Интерфейс:** [`IProjectBaseActivator`](../../interfaces/family-manager/database.md#iprojectbaseactivator)

```csharp
public sealed class ProjectBaseActivator : IProjectBaseActivator
{
    public ProjectBaseActivator(IDatabaseManager dbManager, IProjectBaseBindingEvaluator evaluator);
    public Task<string?> ActivateForDocumentAsync(string currentFilePath, CancellationToken ct = default);
}
```

---

## DatabaseConnectionRegistry

Реестр подключений. Сохраняется как `registry.json` в `%APPDATA%\SmartCon\FamilyManager\`.

**Файл:** `DatabaseConnectionRegistry.cs`

```csharp
public sealed record DatabaseConnectionRegistry(
    string? ActiveConnectionId,
    IReadOnlyList<DatabaseConnection> Connections,
    int SchemaVersion = 0);
```

`SchemaVersion` = `0` для legacy-реестров, записанных до issue #119; `1` после миграции
`IRegistryMigrator`, когда появились поля `kind` и `projectBinding` у подключений.

---

## DbCompatibility

Константы forward-совместимости каталога (ADR-058, #173): минимальная версия
плагина, способная безопасно писать в базу с текущим breaking-форматом данных.

**Файл:** `DbCompatibility.cs`

```csharp
public static class DbCompatibility
{
    public const string CurrentMinPluginVersion = "2.0.1-beta.5";
}
```

`CurrentMinPluginVersion` — floor-версия, записываемая в
`database_meta.min_plugin_version` новых баз (`CreateDatabaseAsync`) и
backfill'ом миграций (прецедент V24: базы с FHV3-хэшами). Bump только при
breaking-изменении данных, делающем старый плагин вредным для базы (его дедуп
молча плодит дубликаты); аддитивные изменения floor не поднимают. Сравнение
с версией плагина — через `IDatabaseCompatibilityService` + `SemVersion`.

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

## ContentStatusParser

Парсер строки `content_status` из БД каталога в `ContentStatus`. Маппит legacy-значение `Retired` в `ContentStatus.Deprecated`, чтобы старые записи оставались читаемыми после перехода UI на двухстатусную модель Active/Deprecated. См. `docs/known-workarounds.md` и issue #109.

**Файл:** `ContentStatusParser.cs`

```csharp
public static class ContentStatusParser
{
    public static ContentStatus Parse(string? value);
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

## FamilyAssetTypeExtensions

Методы расширения для `FamilyAssetType`: автоопределение типа файла по расширению и общий фильтр для диалога выбора файлов.

**Файл:** `FamilyAssetTypeExtensions.cs`

```csharp
public static class FamilyAssetTypeExtensions
{
    public static FamilyAssetType DetectFromExtension(string filePath);
    public static string AllAssetFilters();
}
```

---

## FamilyCatalogItem

Логическая запись каталога семейств — основная сущность, к которой привязаны версии и файлы.

`ActiveRevitMajorVersion` — Revit-версия файла активной версии (`current_version_label`); заполняется только в `SearchAsync` (скалярный подзапрос `MIN(revit_major_version)` по активной метке), в остальных выборках `null`. `MinRevitMajorVersion` — минимальная Revit-версия среди всех версий айтема; используется деревом каталога для индикации недоступности (замок + серый текст) и tooltip-подсказки «есть совместимая версия».

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
    DateTimeOffset UpdatedAtUtc,
    string FamilySource = "loadable",
    string? RevitCategory = null,
    string? ContentHash = null,
    int? HashFormatVersion = null,
    int? ActiveRevitMajorVersion = null,
    int? MinRevitMajorVersion = null,
    int? RevitCategoryId = null);
```

`RevitCategoryId` (ADR-055, schema V22) — ординал BuiltInCategory для матчинга правил `FamilyFactRuleSet`; `null` у pre-V22 строк до актуализации. Display-текст остаётся в `RevitCategory` (локалезависим).

---

## FamilyUnavailableReason

Причина, по которой семейство недоступно для загрузки в текущий документ Revit. Управляет замком и серым текстом в дереве каталога, а также текстом tooltip (причина + способ восстановления доступа).

**Файл:** `FamilyUnavailableReason.cs`

```csharp
public enum FamilyUnavailableReason
{
    None = 0,
    Deprecated = 1,
    RevitVersion = 2,
    DeprecatedAndRevitVersion = 3,
}
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

## ImageCropRect

Прямоугольник кадрирования в пикселях исходного изображения. Производится диалогом
кадрирования аватарки (issue #131) и потребляется `IAvatarCropService` для рендера
производного `avatar.png`. См. ADR-047.

**Файл:** `ImageCropRect.cs`

```csharp
public sealed record ImageCropRect(double X, double Y, double Width, double Height);
```

---

## AvatarThumbnail

Константы единой миниатюры аватара (ADR-047): 560×420 PNG (4:3, 2× supersample
для превью 280×210). Один файл `avatar.png` используется и аватаркой в свойствах,
и превью в tooltip.

**Файл:** `AvatarThumbnail.cs`

```csharp
public static class AvatarThumbnail
{
    public const int Width = 560;
    public const int Height = 420;
}
```

---

## CropViewportMath

Чистая статическая математика вьюпорта диалога кадрирования (issue #131, ADR-047):
fit-scale изображения во вьюпорт, `MinZoom` (изображение всегда покрывает рамку),
клампы пана/рамки, зум вокруг точки, resize рамки произвольных пропорций за углы
(rev 2), маппинг рамки в `ImageCropRect` (пиксели исходника).
Не зависит от WPF — unit-тестируется в `CropViewportMathTests`.

**Файл:** `CropViewportMath.cs`

```csharp
public enum CropCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public static class CropViewportMath
{
    public static double FitScale(double viewWidth, double viewHeight, double imageWidth, double imageHeight);
    public static double MinZoom(double frameWidth, double frameHeight, double imageWidth, double imageHeight, double fitScale);
    public static (double Left, double Top) GetImageTopLeft(...);
    public static (double X, double Y) ClampOffset(...);
    public static (double X, double Y) ClampFrame(...);
    public static (double Zoom, double OffsetX, double OffsetY) ZoomAroundPoint(...);
    public static (double X, double Y, double Width, double Height) ResizeFrame(CropCorner corner, ...);
    public static ImageCropRect ToSourceRect(...);
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
