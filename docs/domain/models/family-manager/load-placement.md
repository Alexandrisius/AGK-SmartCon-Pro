---
module: family-manager
---
# Модели FamilyManager — Загрузка и размещение

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

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
