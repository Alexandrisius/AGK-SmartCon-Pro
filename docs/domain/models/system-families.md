---
module: system-families
---
# System Families модели

> Загружать: при работе с импортом системных семейств из активного проекта.
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/SystemFamilies/*.cs`.

## CategoryAnalysis

Результат анализа одной системной категории в активном проекте Revit.
Содержит **только** типы, реально размещённые в модели (`WhereElementIsNotElementType`).
`BuiltInCategory` — value-type carrier (допустим в Core по I-09).

**Файл:** `CategoryAnalysis.cs`

```csharp
public sealed record CategoryAnalysis(
    BuiltInCategory Category,
    string DisplayName,
    IReadOnlyList<SystemTypeInfo> Types)
{
    public int TypeCount => Types.Count;
}

public sealed record SystemTypeInfo(
    string Name,
    string UniqueId);
```

---

## SelectedSystemType

Иммутабельный снапшот одного выбранного пользователем типа системного семейства
(из picker flow в активном проекте). `UniqueId` используется для последующего
копирования через `ElementTransformUtils.CopyElements`. `Category` (BuiltInCategory)
нужен для выбора placement-handler'а в `SystemCategoryRegistry` и должен
соответствовать одному из значений в `SystemCategoryRegistry.SupportedCategories`.

**Файл:** `SelectedSystemType.cs`

```csharp
public sealed record SelectedSystemType(
    string UniqueId,
    string Name,
    string CategoryName,
    BuiltInCategory Category);
```

| Поле | Тип | Назначение |
|---|---|---|
| `UniqueId` | `string` | Стабильный идентификатор типа в source-документе (для `ElementTransformUtils.CopyElements`) |
| `Name` | `string` | Отображаемое имя типа (для UI и логов) |
| `CategoryName` | `string` | Локализованное имя категории ("Трубы", "Воздуховоды"). Используется в UI |
| `Category` | `BuiltInCategory` | Канонический enum-значение категории. **Может быть `BuiltInCategory.INVALID`** если категория — custom sub-category; staging копирует тип, но не размещает инстансы |

---

## CreateCleanProjectResult

Результат создания чистого .rvt-проекта с копиями системных типов **и** инстансами
на сетке 2×2 м. `FilePath` — абсолютный путь к сохранённому .rvt во временной папке
(`%TEMP%\SmartCon\SystemFamilyLoadFromProject\<GUID>\<safeName>.rvt`, путь — из
`SystemFamilyTempLayout`).

**Файл:** `CreateCleanProjectResult.cs`

```csharp
public sealed record CreateCleanProjectResult(
    bool Success,
    string? FilePath,
    string? Error,
    int CopiedElementsCount,
    string? CategoryName = null,
    int PlacedInstancesCount = 0);
```

| Поле | Тип | Назначение |
|---|---|---|
| `Success` | `bool` | `true` если .rvt создан, сохранён и закрыт без ошибок |
| `FilePath` | `string?` | Абсолютный путь к staged .rvt (для передачи в extraction) или `null` при неудаче |
| `Error` | `string?` | Текст ошибки при `Success == false` |
| `CopiedElementsCount` | `int` | Сколько типов успешно скопировано (может быть меньше запрошенного из-за коллизий имён) |
| `CategoryName` | `string?` | Локализованное имя категории (для UI / логов) |
| `PlacedInstancesCount` | `int` | Сколько инстансов реально размещено. `0` если у категории нет placement handler'а в `SystemCategoryRegistry` (например, Floors, Roofs, Ceilings) или если тип не прошёл cast (FlexPipe → PipeType) |

---

## SystemFamilyImportResult

Результат финального импорта системных семейств из подготовленных .rvt в managed storage.
`ExtractionTasks` — по одному на каждую импортированную категорию (атрибуты
извлекаются отдельным фоновым сервисом `ISystemFamilyAttributeExtractionService`).

**Файл:** `SystemFamilyImportResult.cs`

```csharp
public sealed record SystemFamilyImportResult(
    bool Success,
    string? Message,
    IReadOnlyList<SystemFamilyExtractionTask> ExtractionTasks,
    int TypesCount);
```

---

## SystemFamilyExtractionTask

Один extraction task для фонового извлечения атрибутов.

**Файл:** `SystemFamilyImportResult.cs`

```csharp
public sealed record SystemFamilyExtractionTask(
    string CatalogItemId,
    string TempRvtPath,
    IReadOnlyList<string> TypeNames,
    string? VersionId,
    string? FileId);
```

---

## SystemTypeSyncResult

Результат синхронизации одного системного типа (Issue #104, ADR-061). `NotConvergedCount` — остаточные расхождения зависимостей (нерезолвленные правила трассировки, неудалимые размеры сегментов), тип при этом считается синхронизированным.

**Файл:** `Models/FamilyManager/SystemTypeSyncResult.cs`

```csharp
public enum SystemTypeSyncStatus
{
    Created, Updated, NotFoundInSource, NoPrototypeType, Failed,
}

public sealed record SystemTypeSyncResult(
    string TypeName,
    SystemTypeSyncStatus Status,
    int ParametersWritten,
    int ParametersSkipped,
    string? ErrorMessage = null,
    int NotConvergedCount = 0)
{
    public bool IsSuccess { get; }
}

public sealed record SystemFamilySyncResult(
    string CatalogItemId,
    IReadOnlyList<SystemTypeSyncResult> TypeResults)
{
    public int SuccessCount { get; }
    public int FailedCount { get; }
    public bool AllSucceeded { get; }
    public int TotalNotConverged { get; }
}
```

---

## SystemTypeLocation

Локация системного типа (`ElementType`) в документе: имя + ordinal категории + ElementId. Используется batch-сбором `ISystemTypeFinder.CollectTypes` для матчинга stale-detection (ключ `(CategoryOrdinal, Name)` — одинаковые имена в разных категориях не конфликтуют).

**Файл:** `Models/FamilyManager/SystemTypeLocation.cs`

```csharp
public sealed record SystemTypeLocation(
    string TypeName,
    int CategoryOrdinal,
    ElementId TypeId);
```

---

## SegmentSnapshot / SegmentSizeSnapshot

Эталонные данные сегмента трубы/воздуховода из мини-проекта (Issue #104): имя + материал + спецификация + шероховатость + таблица размеров. Все диаметры — internal units (feet).

**Файл:** `Models/FamilyManager/SegmentSnapshot.cs`

```csharp
public sealed record SegmentSnapshot(
    string Name,
    string? MaterialName,
    string? ScheduleName,
    double Roughness,
    IReadOnlyList<SegmentSizeSnapshot> Sizes);

public sealed record SegmentSizeSnapshot(
    double NominalDiameter,
    double InnerDiameter,
    double OuterDiameter,
    bool UsedInSizeLists,
    bool UsedInSizing);
```

---

## SegmentSyncResult

Результат синхронизации одного сегмента: разрешённый ElementId + счётчики конвергенции таблицы размеров. `SizesNotConverged` — неудалимые/некорректируемые размеры (используются размещёнными трубами или последний) — попадает в пользовательский счётчик «не приведено к эталону».

**Файл:** `Models/FamilyManager/SegmentSyncResult.cs`

```csharp
public sealed record SegmentSyncResult(
    ElementId? SegmentId,
    int SizesAdded,
    int SizesRemoved,
    int SizesNotConverged);
```
