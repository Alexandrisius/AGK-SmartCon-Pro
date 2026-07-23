---
module: family-manager
---
# Модели FamilyManager — Миграции и актуализация БД

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

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
