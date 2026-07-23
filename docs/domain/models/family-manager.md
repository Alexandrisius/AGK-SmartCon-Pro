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
**Интерфейс:** [`IProjectBaseBindingEvaluator`](../interfaces/family-manager.md#iprojectbasebindingevaluator)

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
**Интерфейс:** [`IProjectBaseActivator`](../interfaces/family-manager.md#iprojectbaseactivator)

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

## SharedParameterEntry

Одна запись параметра, распарсенная из файла общих параметров Revit (ФОП, .txt).
Чистый data carrier — парсер живёт в Core и не трогает Revit API.
Группа ФОП переносится только как отображаемое имя (`GroupName`) и не импортируется
в пользовательские группы атрибутов.

**Файл:** `SharedParameterEntry.cs`

```csharp
public sealed record SharedParameterEntry(
    Guid ParameterGuid,
    string Name,
    string DataType,
    string? DataCategory,
    string? GroupName,
    string? Description);
```

---

## FamilyManagerUserSettings

Пользовательские настройки FamilyManager уровня машины (не привязаны к конкретной базе).
Хранятся в `%APPDATA%\SmartCon\FamilyManager\user-settings.json`. Сейчас — кэш пути к ФОП.

**Файл:** `FamilyManagerUserSettings.cs`

```csharp
public sealed record FamilyManagerUserSettings(string? SharedParametersFilePath);
```

---

## SharedParameterFileParser

Реализация `ISharedParameterFileParser` — pure C# парсер ФОП (.txt): tab-delimited строки,
порядок колонок из заголовков `*GROUP`/`*PARAM` с фиксированным fallback,
BOM-детекция кодировки (UTF-8/UTF-16 LE/BE). Бросает `InvalidDataException`,
если секция `*PARAM` отсутствует.

**Файл:** `SmartCon.Core/Services/Implementation/SharedParameterFileParser.cs`

---

## JsonFamilyManagerUserSettingsRepository

JSON-репозиторий для `FamilyManagerUserSettings`. Файл: `%APPDATA%\SmartCon\FamilyManager\user-settings.json`.

**Файл:** `SmartCon.Core/Services/Implementation/JsonFamilyManagerUserSettingsRepository.cs`

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

## UnitSymbolFixup

Pure C# коррекция известных ошибок русской локализации Autodesk в символах
единиц, которые Revit возвращает из `AsValueString` / `UnitFormatUtils.Format`.
RU-таблица символов Revit рендерит единицу давления бар как «бары», но по
ГОСТ 8.417 «бар» несклоняем — корректное отображение «16 бар». Правила —
замены хвостового токена (ordinal), неизвестные строки проходят без изменений.
Вынесено в Core для unit-тестирования без Revit API; применяется в
`RevitUnitsCompat.FormatDisplayValue` и в fallback-точках `AsValueString`.

**Файл:** `Services/Implementation/UnitSymbolFixup.cs`

```csharp
public static class UnitSymbolFixup
{
    public static string? Correct(string? formatted);
}
```

9 unit-тестов в `UnitSymbolFixupTests.cs` покрывают замену «бары»→«бар»,
no-op для корректных/английских/прочих символов, null/empty и не-хвостовые
позиции.

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


## ContentHashMatch

Result of a cross-version content-hash search. Returned when a content hash matches a version (current or archived) of a catalog item. Used to display "Дубликат (vN)" in the batch dialog. Issue #126: расширен именем найденного айтема и его активной версией — с hash-first дедупликацией найденный по хэшу айтем является каноническим «existing» для MakeActive/IncrementVersion даже когда его имя отличается от имени файла.

**Файл:** Models/FamilyManager/ContentHashMatch.cs

`csharp
public sealed record ContentHashMatch(
    string CatalogItemId,
    string MatchedVersionLabel,
    bool IsCurrentVersion,
    string? CurrentVersionLabel,
    string MatchedItemName,
    string MatchedItemNormalizedName);
`

- CatalogItemId — ID of the catalog item whose version matched.
- MatchedVersionLabel — label of the matching version (e.g. "v2").
- IsCurrentVersion — 	rue if matched is current version; alse if archived (e.g. after rollback).
- CurrentVersionLabel — активная версия найденного айтема (Issue #126).
- MatchedItemName / MatchedItemNormalizedName — имя найденного айтема; отличается от имени файла при cross-name дубликате (Issue #126).

---

## ContentHashDedupResult

Result of the content-hash dedup check for a single batch-import row. Issue #126: hash-first порядок — поиск по хэшу по всем версиям каталога независимо от имени, имя — вторым шагом.

**Файл:** Models/FamilyManager/ContentHashDedupResult.cs

`csharp
public sealed record ContentHashDedupResult(
    FamilyBatchImportStatus Status,
    string? ExistingCatalogItemId,
    string? ExistingVersionLabel,
    ContentHashMatch? HashMatch,
    bool IsCrossNameDuplicate = false);
`

- Status — final status: New, Existing, Duplicate, or Error.
- ExistingCatalogItemId — для Duplicate: айтем, найденный ПО ХЭШУ (может иметь другое имя); для Existing: айтем по нормализованному имени; 
ull для New/Error.
- ExistingVersionLabel — current version label of the resolved item, or 
ull.
- HashMatch — cross-version hash match details if Status == Duplicate; otherwise 
ull.
- IsCrossNameDuplicate — Issue #126: хэш совпал с айтемом под другим именем (файл переименован); batch-диалог рисует ⚠ с tooltip.

---

## CategoryProvenance

Источник категории строки batch-диалога (Issue #135). Единственный источник правды для lock-семантики: `Command` и `Manual` — залочены (rename не сбрасывает категорию); `AutoName`, `AutoHash`, `None` — пересчитываются rename-хендлером.

**Файл:** Models/FamilyManager/CategoryProvenance.cs

`csharp
public enum CategoryProvenance
{
    None = 0,
    AutoName = 1,
    AutoHash = 2,
    Command = 3,
    Manual = 4,
}
`

- None — категория не назначена (плейсхолдер «Без категории»).
- AutoName — подтянута из каталога по совпадению нормализованного имени.
- AutoHash — подтянута из hash-matched дубликата (ADR-049: контент совпал, имя может отличаться).
- Command — предвыбор команды «Импорт в категорию»; rename в имя существующего семейства ПЕРЕМЕЩАЕТ его в эту категорию при импорте.
- Manual — явный выбор пользователя (пикер или multi-select batch apply).

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
    string? MatchedVersionLabel = null,
    IReadOnlyList<FamilyGeometryPerType>? GeometryPerType = null,
    bool IsCrossNameDuplicate = false,
    string? MatchedItemName = null);
`

- SourcePath — file path for UC-1, virtual placeholder for UC-3/UC-4 ("system://...", "loadable://...").
- ContentHash — computed hash, or 
ull if extraction failed (see ErrorMessage).
- LoadableSnapshot / SystemSnapshot — one is set, the other is 
ull depending on FamilySource.
- Source — v2.0.0 source payload for UC-3/UC-4 post-dialog staging; 
ull for UC-1/UC-2.
- MatchedVersionLabel — set when Status == Duplicate so the UI can show "Дубликат (v2)".
- IsCrossNameDuplicate / MatchedItemName — Issue #126: хэш совпал с айтемом под другим именем; прокидывается в batch-строку для ⚠-иконки и tooltip.
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
    public const int CurrentVersion = 3;
    public const int RecalculationSkipped = -1;
    public const int RecalculationMissing = -2;
}
```

- `HexString` — SHA-256 hex string (uppercase, no dashes).
- `FormatVersion` — algorithm version, bumped when canonical-string format changes so old hashes do not produce false duplicate matches against newly computed hashes.
- `SourceKind` — `"loadable"` or `"system"`. Used to enforce cross-source separation (system hashes never match loadable hashes and vice versa).
- `FamilyContentHashFormat.CurrentVersion` — **= 3 (Issue #159, ADR-056)**. История: v1 — loadable canonical string включал имя семейства (`FHV1|LOADABLE|{name}|...`), переименованные файлы давали другой хэш; v2 — имя исключено (`FHV2|LOADABLE|{cat}|...`), system-строки мигрированы дешёвым UPDATE флага; v3 — категория стала локале-инвариантным ordinal, добавлены секции FACTS/FLAGS/CONN (loadable) и STRUCT/ROUTING (system), геометрия расширена (bbox/surface/длины кривых), значения экранируются; обе source мигрируются полным пересчётом из файла (задача `hash-v3`).
- `FamilyContentHashFormat.RecalculationSkipped` — sentinel `-1`: миграция помечает версии, чей файл безвозвратно нечитаем; исключены из pending-числа и никогда не ретраятся.
- `FamilyContentHashFormat.RecalculationMissing` — sentinel `-2`: managed-файл отсутствует на диске; исключён из pending-числа (purge/restore — решение пользователя).

---

## DatabasePendingBreakdown

Разбивка pending-записей актуализации по уровням и открываемости (ADR-054 §3a). `Critical`/`Optional` — processable группы; `NewerOnlyCritical` — группы, требующие Revit новее запущенного (гейтят как processable critical — база read-only до идеальной миграции); `NewerOnlyOptional` — только янтарный индикатор; `NewerOnlyCriticalRequiredRevitVersion` — минимальный Revit для обновления всех newer-only critical групп за один раз.

**Файл:** `Models/FamilyManager/DatabasePendingBreakdown.cs`

```csharp
public sealed record DatabasePendingBreakdown(
    int Critical,
    int Optional,
    int NewerOnlyCritical,
    int NewerOnlyOptional,
    int NewerOnlyCriticalRequiredRevitVersion,
    int NewerOnlyOptionalRequiredRevitVersion)
{
    public static DatabasePendingBreakdown Empty { get; }
    public int TotalProcessable => Critical + Optional;
    public int TotalCritical => Critical + NewerOnlyCritical;   // условие гейта
}
```

- `NewerOnlyCriticalRequiredRevitVersion` — минимальный Revit для обновления всех newer-only critical групп за раз (тексты баннера/гейта).
- `NewerOnlyOptionalRequiredRevitVersion` — то же для optional групп (тултип янтарной точки).

---

## NewerOnlyPendingInfo

Newer-Revit-only pending одной задачи актуализации (ADR-054 §3a): число групп, чьи файловые варианты ВСЕ новее запущенного Revit, и минимальный Revit, в котором они все становятся processable за один проход (`MAX` по группам от `MIN(вариант Revit)` — группа открываема, когда запущенный Revit ≥ её самого старого варианта).

**Файл:** `Models/FamilyManager/NewerOnlyPendingInfo.cs`

```csharp
public sealed record NewerOnlyPendingInfo(int Count, int RequiredRevitVersion)
{
    public static NewerOnlyPendingInfo None { get; }
}
```

---

## DatabaseMigrationProgress

Item-прогресс одной задачи/движка актуализации (ADR-054) — общая форма для всех задач; единый диалог обновления рисует её напрямую. Репортится раз в обработанный файл.

**Файл:** `Models/FamilyManager/DatabaseMigrationProgress.cs`

```csharp
public sealed record DatabaseMigrationProgress(
    int Current,
    int Total,
    string CurrentFileName);
```

---

## DatabaseMigrationResult

Нормализованный исход прогона одной миграции БД (ADR-054). Каждая миграция маппит свой внутренний результат в эту форму, чтобы единый диалог показал общую сводку.

**Файл:** `Models/FamilyManager/DatabaseMigrationResult.cs`

```csharp
public sealed record DatabaseMigrationResult(
    int UpdatedCount,
    int NewerRevitCount,
    IReadOnlyList<HashRecalculationMissingFile> MissingFiles,
    IReadOnlyList<HashRecalculationFailedFile> FailedFiles,
    bool WasCancelled)
{
    public static DatabaseMigrationResult Cancelled { get; }
}
```

- `UpdatedCount` — успешно обработанные записи.
- `NewerRevitCount` — записи, оставленные pending: файловые варианты требуют Revit новее запущенного.
- `MissingFiles` — managed-файлы не найдены на диске.
- `FailedFiles` — файлы открылись, но извлечение упало.
- `WasCancelled` — прервано пользователем; закоммиченные пачки сохранены.

---

## ActualizationVariant

Одна Revit-вариация version label каталога (ADR-054, движок актуализации). Контент идентичен между вариантами одного label — движок открывает ОДИН вариант, задачи применяют результат ко ВСЕМ.

**Файл:** `Models/FamilyManager/ActualizationVariant.cs`

```csharp
public sealed record ActualizationVariant(
    string VersionId,
    string FileId,
    int RevitMajorVersion,
    string RelativePath,
    string FileName);
```

---

## ActualizationGroup

Рабочая единица движка актуализации (ADR-054): группа `(catalog_item, version_label)` со ВСЕМИ её Revit-вариантами. `Key` (`catalogItemId|versionLabel`) — общий с задачами ключ детекции.

**Файл:** `Models/FamilyManager/ActualizationGroup.cs`

```csharp
public sealed record ActualizationGroup(
    string CatalogItemId,
    string ItemName,
    string VersionLabel,
    bool IsActiveLabel,
    IReadOnlyList<ActualizationVariant> Variants)
{
    public string Key => CatalogItemId + "|" + VersionLabel;
}
```

---

## FamilyActualizationContext

Всё, что нужно задаче актуализации для записи своих артефактов по ОДНОЙ группе (ADR-054): группа, открытый вариант и продукты ЕДИНОЙ сессии открытия (snapshot + per-type геометрия; `Geometry` = `null` при её сбое — задачи делают fallback).

**Файл:** `Models/FamilyManager/FamilyActualizationContext.cs`

```csharp
public sealed record FamilyActualizationContext(
    ActualizationGroup Group,
    ActualizationVariant OpenedVariant,
    string AbsolutePath,
    FamilySnapshot Snapshot,
    IReadOnlyList<FamilyGeometryPerType>? Geometry,
    SystemFamilySnapshot? SystemSnapshot = null);
```

- `SystemSnapshot` (ADR-056) — заполнен только на system-пути (staged `.rvt`); у loadable-групп `null`.

---

## ActualizationFailureKind

Причина, по которой группа не смогла быть извлечена (ADR-054). Задача решает по виду сбоя, как пометить свой критерий (hash пишет терминальные -2/-1; attributes/glb остаются pending и ретраятся).

**Файл:** `Models/FamilyManager/ActualizationFailureKind.cs`

```csharp
public enum ActualizationFailureKind
{
    MissingFile,       // managed-файл не найден на диске
    ExtractionFailed,  // файл есть, но open/extract упал (повреждён, ошибка Revit)
}
```

---

## HashRecalculationMissingFile

Версия каталога, чей managed-файл не найден на диске при миграции (Issue #126). Показывается на summary-экране; пользователь решает — удалить записи из каталога или оставить (файл может быть на временно недоступном диске).

**Файл:** `Models/FamilyManager/HashRecalculationMissingFile.cs`

```csharp
public sealed record HashRecalculationMissingFile(
    string CatalogItemId,
    string ItemName,
    string VersionLabel,
    string FileName);
```

---

## HashRecalculationFailedFile

Версия каталога, чей файл существует, но не прочитался при миграции (повреждён, ошибка Revit API). Помечается `hash_format_version = -1` навсегда (Issue #126).

**Файл:** `Models/FamilyManager/HashRecalculationFailedFile.cs`

```csharp
public sealed record HashRecalculationFailedFile(
    string ItemName,
    string VersionLabel,
    string FileName,
    string ErrorMessage);
```

---

## FamilyMigrationExtractResult

Результат извлечения snapshot из одного файла для миграции (Issue #126). Никогда не бросает исключение через границу — ошибка в `ErrorMessage`, батч продолжается.

**Файл:** `Models/FamilyManager/FamilyMigrationExtractResult.cs`

```csharp
public sealed record FamilyMigrationExtractResult(
    bool Success,
    FamilySnapshot? LoadableSnapshot,
    string? ErrorMessage,
    IReadOnlyList<FamilyGeometryPerType>? Geometry = null,
    SystemFamilySnapshot? SystemSnapshot = null);
```

- `Geometry` (ADR-054) — per-type геометрия из той же open-сессии (catalog backfill); `null`, если геометрия не запрашивалась или упала (caller делает fallback на отдельный проход).
- `SystemSnapshot` (ADR-056) — системный snapshot из staged `.rvt` на system-пути (типы + параметры + STRUCT + ROUTING); на loadable-пути `null`.

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
    IReadOnlyList<string> SharedNestedFamilyNames,
    int? CategoryId = null,
    IReadOnlyList<FamilyFact>? Facts = null,
    IReadOnlyList<ConnectorSnapshot>? Connectors = null,
    FamilyBehaviorFlags? BehaviorFlags = null,
    IReadOnlyList<string>? NonSharedNestedFamilyNames = null);

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
    string? ResolvedElementName,
    string? ValueDisplay = null,
    string? SpecTypeId = null,
    string? UnitTypeId = null);
```

- `FamilyName` — from `FamilyManager` or family document title.
- `Category` — display name (e.g. "Pipe Fittings"). NOT hashed since FHV3 — the ordinal is (ADR-056).
- `CategoryId` — `BuiltInCategory` ordinal (ADR-055). Since FHV3 (ADR-056) it IS the hashed category identity — locale-invariant (RU/EN Revit produce the same hash); the display name is only a fallback when the ordinal is unknown.
- `Facts` — category-driven facts (Part Type; ADR-055). Part of the content hash since FHV3 (ADR-056): for fittings the Part Type defines the family function.
- `Connectors` — connector elements of the family (ADR-056), pre-sorted by the extractor with `LinkedIndex` computed against that order. Part of the content hash since FHV3.
- `BehaviorFlags` — Shared/WorkPlaneBased/AlwaysVertical/CutWithVoids from `OwnerFamily` built-in parameters (ADR-056). Part of the content hash since FHV3.
- `NonSharedNestedFamilyNames` — names of NON-shared nested families (ADR-056), sorted. Part of the content hash since FHV3.
- `Parameters` — all schema-level parameters, sorted by name. Includes `SharedParamGuid` for shared params and `BuiltInParameterId` enum name for built-ins (null for user/shared).
- `Types` — all family types with their values. The unnamed default type is extracted under the hash-stable synthetic name `<default>` so families without user-created types keep their attribute values. UI never shows the literal — `FamilyTypeSnapshot.ResolveDisplayName(typeName, familyName)` substitutes the family name (catalog tree, properties tabs, batch import tooltip).
- `Geometry` — aggregated `GeometryMetrics` from all `GenericForm` elements.
- `SharedNestedFamilyNames` — names of shared nested families (ADR-034), sorted.
- `FamilyParameterValue.HasValue` distinguishes "no value" (`false`) from "value is zero" (`true`, `ValueNumber=0`) — hash treats them differently.
- `FamilyParameterValue.ValueDisplay` — human-readable value formatted per the owning document's unit settings with the unit symbol (e.g. "300 мм", "16 бар"); `null` when not applicable. NOT part of the content hash — display metadata only. `SpecTypeId`/`UnitTypeId` carry the Forge TypeId strings (legacy enum names on R19-R20) and are likewise excluded from the hash.

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
    IReadOnlyList<SystemParameterValue> Values,
    CompoundStructureSnapshot? Structure = null,
    RoutingPreferencesSnapshot? Routing = null);

public sealed record SystemParameterValue(
    string ParameterName,
    string StorageType,
    bool HasValue,
    string? ValueText,
    double? ValueNumber,
    string? ResolvedElementName,
    string? ValueDisplay = null,
    string? SpecTypeId = null,
    string? UnitTypeId = null);
```

- `CategoryName` — display name (e.g. "Трубы", "Воздуховоды"). NOT hashed since FHV3 (ADR-056) — locale-dependent.
- `CategoryId` — numeric `BuiltInCategory` ordinal carried as `int` so Core does not depend on `Autodesk.Revit.DB` (I-09). The hashed category identity (FHV3).
- `Types` — selected system types with values, sorted by type name.
- `SystemTypeSnapshot.Structure` — compound structure (layer stack) for wall/floor/roof/ceiling types, `null` otherwise. Part of the content hash since FHV3 (ADR-056).
- `SystemTypeSnapshot.Routing` — routing preferences for MEP curve types (pipe/duct/cable tray/conduit), `null` otherwise. Part of the content hash since FHV3 (ADR-056).
- `SystemParameterValue` — same semantics as `FamilyParameterValue` — distinguishes "no value" from "zero".

**v2.0.0 hash stability:** the hasher skips blank values (`HasValue=false`, empty string, storage-scoped `INVALID`/`UNSUPPORTED`, `READERROR` — v3 narrows the first two by storage type so a user's literal text no longer collides, ADR-056) so the empty `ADSK_Завод-изготовитель` parameter does not contribute to the hash. The hasher also skips the auto-generated `Код IfcGUID` parameter (different per `.rvt` save).

---

## GeometryMetrics

Aggregated geometry metrics for a loadable family document. Used as part of the content fingerprint so that adding/removing a form, or changing an extrusion depth, shifts the hash.

**Файл:** `Models/FamilyManager/GeometryMetrics.cs`

```csharp
public sealed record GeometryMetrics(
    int TotalFormCount,
    IReadOnlyList<FormMetrics> Forms,
    int SymbolicCurveCount = 0,
    int DetailCurveCount = 0,
    int ModelCurveCount = 0,
    int TextNoteCount = 0,
    int ReferencePlaneCount = 0,
    int DimensionCount = 0,
    double TotalSymbolicCurveLength = 0,
    double TotalDetailCurveLength = 0,
    double TotalModelCurveLength = 0);

public sealed record FormMetrics(
    string FormKind,
    bool IsSolid,
    double Volume,
    int FaceCount,
    int EdgeCount,
    string? SubcategoryName,
    double SurfaceArea = 0,
    BoundingBoxSnapshot? Bounds = null);

public sealed record BoundingBoxSnapshot(
    double MinX, double MinY, double MinZ,
    double MaxX, double MaxY, double MaxZ);
```

- `TotalFormCount` — number of `GenericForm` elements (extrusions, sweeps, revolutions, blends, free-form).
- `Forms` — per-form metrics sorted by `(FormKind, IsSolid, Volume)` for deterministic output.
- `Volume` — total volume of all solids in Revit internal units (cubic feet), 6-decimal precision so a 1 mm change shifts the value.
- `FaceCount` / `EdgeCount` — totals across all solids, or 0 if geometry could not be extracted (known bug for shared nested families).
- `SurfaceArea` — summed face area (square feet), catches shape edits that preserve volume and face count (ADR-056).
- `Bounds` — view-independent bounding box, catches translations/proportion edits that preserve volume (ADR-056); hashed with 1e-4 ft rounding.
- `FormKind` — `"Extrusion"`, `"Sweep"`, `"Revolution"`, `"Blend"`, `"SweptBlend"`, or `"GenericForm"` for free-form.
- `TotalSymbolicCurveLength` / `TotalDetailCurveLength` / `TotalModelCurveLength` — summed 2D curve lengths (feet), catch redraws that keep element counts constant (ADR-056).

---

## ConnectorSnapshot (ADR-056)

Snapshot of a single `ConnectorElement` inside a family document — domain, profile, sizes, system classification, origin and intra-family linkage. Connectors carry MEP identity that parameters do not (changing a connector's system classification from ХВС to ГВС leaves every parameter untouched). All enum values are raw ordinals (I-09, same rule as ADR-055 facts). Part of the content hash since FHV3 (Issue #159).

**Файл:** `Models/FamilyManager/ConnectorSnapshot.cs`

```csharp
public sealed record ConnectorSnapshot(
    int Domain,
    int Shape,
    int SystemClassification,
    bool IsPrimary,
    double? Width,
    double? Height,
    double? Radius,
    double OriginX,
    double OriginY,
    double OriginZ,
    int LinkedIndex);
```

- `Domain` / `Shape` / `SystemClassification` — ordinals of `Domain`, `ConnectorProfileType`, `MEPSystemClassification`.
- `Width` / `Height` / `Radius` — connector sizes in feet; `null` when not applicable to the profile (e.g. radius on rectangular).
- `OriginX/Y/Z` — family-local origin in feet; hashed with 1e-4 ft rounding to absorb regen noise.
- `LinkedIndex` — index of the linked connector in the same sorted list (`-1` when unlinked) — captures intra-family connection topology.

---

## FamilyBehaviorFlags (ADR-056)

Behavior flags of a loadable family, read from built-in parameters on the `Family` element — invisible to `FamilyManager.GetParameters()`. `null` per flag = parameter absent in this Revit version/template. Part of the content hash since FHV3 (Issue #159).

**Файл:** `Models/FamilyManager/FamilyBehaviorFlags.cs`

```csharp
public sealed record FamilyBehaviorFlags(
    bool? IsShared,
    bool? IsWorkPlaneBased,
    bool? IsAlwaysVertical,
    bool? AllowsCutWithVoids);
```

---

## CompoundStructureSnapshot / CompoundLayerSnapshot (ADR-056)

Layer stack of a system host type (Basic walls, floors, roofs, ceilings). Not a parameter — changing a layer material/thickness leaves every type parameter untouched. `null` on the type = no compound structure (stacked/curtain walls, non-host categories). Layer order is content (never sorted). Part of the content hash since FHV3 (Issue #159).

**Файл:** `Models/FamilyManager/CompoundStructureSnapshot.cs`

```csharp
public sealed record CompoundStructureSnapshot(
    int ExteriorShellLayerCount,
    int InteriorShellLayerCount,
    IReadOnlyList<CompoundLayerSnapshot> Layers);

public sealed record CompoundLayerSnapshot(
    int Function,
    double Width,
    string? MaterialName,
    bool IsVariable);
```

- `Function` — `MaterialFunctionAssignment` ordinal.
- `MaterialName` — resolved material name (`null` = "By Category"); names are document content, not UI-localized.

---

## RoutingPreferencesSnapshot / RoutingRuleSnapshot / RoutingCriterionSnapshot (ADR-056)

Routing preferences of a MEP curve type (PipeType, DuctType, CableTrayType, ConduitType — all inherit `MEPCurveType.RoutingPreferenceManager`). Not parameters — editing routing rules leaves every type parameter untouched. `null` on the type = not a MEP curve type. Rule order is content (first matching rule wins — never sorted). Part of the content hash since FHV3 (Issue #159).

**Файл:** `Models/FamilyManager/RoutingPreferencesSnapshot.cs`

```csharp
public sealed record RoutingPreferencesSnapshot(
    int PreferredJunctionType,
    IReadOnlyList<RoutingRuleSnapshot> Rules);

public sealed record RoutingRuleSnapshot(
    int GroupType,
    string? PartName,
    string Description,
    IReadOnlyList<RoutingCriterionSnapshot> Criteria);

public sealed record RoutingCriterionSnapshot(
    string CriterionType,
    double MinimumSize,
    double MaximumSize);
```

- `PartName` — resolved part name: `"{Family}:{Type}"` for fitting symbols, element name for segments; `null` when the rule references `InvalidElementId` ("no part allowed" — real content).

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

**Canonical string layout (FHV3, ADR-056):**
- `FHV3|LOADABLE|{catOrdinal}|PARAMS|...|TYPES|...|GEOM(+surface,bbox)|GEOM2D(+lengths)|NESTED(+NONSHARED)|FACTS|FLAGS|CONN|...` (loadable)
- `FHV3|SYSTEM|{catId}|TYPES|{typeName}|{params}|STRUCT|...|ROUTING|...` (system)

**v3 rules:**
- Категория — локале-инвариантный ordinal (display name — только fallback при unknown ordinal).
- Все строковые значения экранируются (`%` → `%25`, `|` → `%7C`) — инъекция полей невозможна.
- Коннекторные координаты и bounding box округляются до 1e-4 ft.
- Blank values excluded (`IsBlankValue(hasValue, text, storageType)`): HasValue=false, empty string, `INVALID` (только ElementId storage), `UNSUPPORTED` (только неизвестные storage), `READERROR`. Numeric zero is NOT blank. Пользовательская строка "INVALID"/"UNSUPPORTED" в текстовом параметре — контент, участвует в хэше.
- Auto-generated parameters excluded (IsAutoGeneratedParameter(name)): anything containing IfcGUID or IFC GUID (case-insensitive). Revit regenerates these on every .rvt save — including them would break cross-document stability.
- Тесты: `src/SmartCon.Tests/FamilyManager/Services/FamilyContentHasherTests.cs` — blank-value, ноль, IfcGUID, порядок, cross-source, плюс FHV3-набор: ordinal-категория (locale-invariance), PartType, коннекторы (размер/система/Origin/rounding/linked), behavior-флаги, bbox/surface, длины кривых, non-shared nested, экранирование, слои (материал/порядок), routing (part/порядок/критерий/junction/null-part).

---

## MeshData

Tessellated mesh extracted from a single Revit `Solid` or `GeometryInstance`. Pure C# value-type structure — NO Revit API references (I-09). The Revit extractor immediately serializes into this shape so no `GeometryObject` is held between calls (I-05).

**Файл:** `Models/FamilyManager/MeshData.cs`

```csharp
public sealed record MeshData(
    float[] Positions,
    float[]? Normals,
    int[] Indices,
    Vector4 DiffuseColor,
    string NodeName)
{
    public int VertexCount => Positions.Length / 3;
    public int TriangleCount => Indices.Length / 3;
    public bool IsEmpty => Positions.Length < 3 || Indices.Length < 3;
}
```

**Поля:**
- `Positions` — vertex positions as a flat float array layout `[x0, y0, z0, x1, y1, z1, …]`. Length is always a multiple of 3.
- `Normals` — optional vertex normals in the same layout as `Positions`. `null` when the source Revit `Mesh` did not produce normals (the GLB writer emits positions-only, viewer computes flat normals).
- `Indices` — triangle indices into `Positions`. Length is always a multiple of 3 (counter-clockwise winding in Revit's right-handed coordinate system).
- `DiffuseColor` — `Vector4` RGBA diffuse color in 0..1 range. Resolved from the element's `Category.Material.Color` → fallback `Category.LineColor` → fallback gray.
- `NodeName` — human-readable node name used in the glTF scene tree (usually the source `GenericForm` type name + id, e.g. `"Extrusion_12345"`).

---

## FamilyGeometryPreview

Aggregated 3D geometry of a single family version — the result of `IFamilyGeometryExtractor.ExtractAsync`. Pure C# value (I-09): no Revit `GeometryObject` retained (I-05). Passed to `IGlbWriter` which serializes it to a GLB file.

**Файл:** `Models/FamilyManager/FamilyGeometryPreview.cs`

```csharp
public sealed record FamilyGeometryPreview(
    string CatalogItemId,
    string VersionLabel,
    string FamilyName,
    IReadOnlyList<MeshData> Meshes)
{
    public int TotalTriangleCount => Meshes.Sum(m => m.TriangleCount);
    public int TotalVertexCount => Meshes.Sum(m => m.VertexCount);
    public bool IsEmpty => Meshes.Count == 0 || Meshes.All(m => m.IsEmpty);
}
```

**Поля:**
- `CatalogItemId` — catalog item identifier (matches `catalog_items.id`).
- `VersionLabel` — version label this geometry belongs to (matches `catalog_versions.version_label`). Used as `family_assets.version_label` when the GLB is registered as an auto-extracted asset (ADR-042).
- `FamilyName` — family display name (without `.rfa` extension), used as `family_assets.file_name` for the GLB.
- `Meshes` — all non-empty `MeshData` entries extracted from the family document. May be empty when the family has no visible geometry (the pipeline skips writing).

---

## FamilyGeometryPerType

3D geometry for a single family type — the unit produced by `IFamilySnapshotExtractor.ExtractGeometryPerType` and consumed by `IFamilyGeometryPipeline`. One `FamilyGeometryPerType` entry is generated per `FamilyType` that produces non-empty geometry. Families with no types produce a single entry with `TypeName = ""`.

**Файл:** `Models/FamilyManager/FamilyGeometryPerType.cs`

```csharp
public sealed record FamilyGeometryPerType(
    string TypeName,
    string FamilyName,
    IReadOnlyList<MeshData> Meshes)
{
    public int TotalTriangleCount => Meshes.Sum(m => m.TriangleCount);
    public int TotalVertexCount => Meshes.Sum(m => m.VertexCount);
    public bool IsEmpty => Meshes.Count == 0 || Meshes.All(m => m.IsEmpty);
}
```

**Поля:**
- `TypeName` — `FamilyType.Name` from `FamilyManager`. Empty string for families with no types.
- `FamilyName` — family display name (without `.rfa` extension).
- `Meshes` — all non-empty `MeshData` entries extracted for this type. May be empty (the pipeline skips writing a GLB for empty types).


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

## FamilyFact / FamilyFactsData (ADR-055)

Один machine-readable «факт» о catalog item, извлечённый из файла при импорте/актуализации по правилам `FamilyFactRuleSet`. Item-level метаданные — в content hash НЕ входят.

**Файл:** `Models/FamilyManager/FamilyFact.cs`

```csharp
public sealed record FamilyFact(
    string FactKey,
    string ValueKey,
    string ValueDisplay);

public sealed record FamilyFactsData(
    int? RevitCategoryId,
    IReadOnlyList<FamilyFact> Facts);
```

- `FactKey` — стабильный машинный ключ факта (`"part_type"`).
- `ValueKey` — стабильное машинное значение (ординал enum строкой, напр. `"5"` = Elbow; для будущего расширенного поиска). Пустая строка — sentinel «факт вычислен, но параметр в семействе отсутствует»: детект миграции гаснет, UI скрывает строку.
- `ValueDisplay` — человекочитаемый fallback на момент извлечения (имя члена enum, напр. `"Elbow"`). UI предпочитает `PartTypeLabelMap` (следует за языком UI), fallback — на это поле.
- `FamilyFactsData` — read-модель окна свойств: ординал категории (`catalog_items.revit_category_id`; `null` = pre-V22 строка, не актуализирована) + все факты итема.

---

## FamilyFactRule (ADR-055)

Правило «для категории X извлекай built-in параметр P и храни под ключом K» (nested в `FamilyFactRuleSet.cs`).

**Файл:** `Models/FamilyManager/FamilyFactRuleSet.cs`

```csharp
public sealed record FamilyFactRule(
    int CategoryId,
    string FactKey,
    string LabelKey,
    int ParameterId);
```

- `CategoryId`/`ParameterId` — сырые int-ординалы BuiltInCategory/BuiltInParameter, НЕ enum: Core грузится тестами без RevitAPI (I-09, Nice3point runtime-excluded). Ординалы верифицированы по revitapidocs 2025/2026.

---

## FamilyFactRuleSet (ADR-055)

Статический реестр `FamilyFactRule`. Единая точка расширения подсистемы фактов: реестр управляет извлечением (SmartCon.Revit), детектом миграции (`family-facts-v1` генерирует SQL из реестра) и UI (label по `LabelKey`). Новый category-driven атрибут = одна строка в `Rules` — без DDL, новой задачи и правок UI.

**Файл:** `Models/FamilyManager/FamilyFactRuleSet.cs`

```csharp
public static class FamilyFactRuleSet
{
    public const string PartTypeFactKey = "part_type";
    public const string PartTypeLabelKey = "FM_Fact_PartType";
    public static IReadOnlyList<FamilyFactRule> Rules { get; }
    public static IReadOnlyCollection<int> CategoryIdsWithRules { get; }
    public static IReadOnlyList<FamilyFactRule> GetRulesForCategory(int categoryId);
    public static FamilyFactRule? FindRule(int categoryId, string factKey);
}
```

- Текущий scope: `part_type` для 4 MEP фитинговых категорий (OST_PipeFitting=-2008049, OST_DuctFitting=-2008010, OST_CableTrayFitting=-2008126, OST_ConduitFitting=-2008128; FAMILY_CONTENT_PART_TYPE=-1114206). Арматура (accessories) исключена продуктовым решением.

---

## PartTypeLabelMap (ADR-055)

Локализованные подписи значений Part Type: ординал → RU/EN по текущему языку UI (`LocalizationService.CurrentLanguage`). **RU-строки дословно повторяют официальную русскую локализацию Revit** (help.autodesk.com/cloudhelp/2023/RUS, таблицы GUID-54F9DD0A / GUID-4DA88E95 — «Мультипорт», «Соединение», «Механическое сочленение» и т.д.) — свои переводы не выдумываем. Неизвестные/будущие ординалы → `null` (caller fallback'ит на `FamilyFact.ValueDisplay`). Значения — по enum PartType Revit 2025 API.

**Файл:** `Models/FamilyManager/PartTypeLabelMap.cs`

```csharp
public static class PartTypeLabelMap
{
    public static string? TryGetLabel(string valueKey);
    public static string? TryGetLabel(int ordinal);
}
```

