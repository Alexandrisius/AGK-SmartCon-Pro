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
