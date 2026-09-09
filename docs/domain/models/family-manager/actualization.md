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
    SystemFamilySnapshot? SystemSnapshot = null,
    IReadOnlyList<FamilySnapshot>? SharedNestedSnapshots = null,
    IReadOnlyList<SharedNestedSubtree>? SharedNestedSubtrees = null);
```

- `SystemSnapshot` (ADR-056) — заполнен только на system-пути (staged `.rvt`); у loadable-групп `null`.
- `SharedNestedSnapshots` / `SharedNestedSubtrees` (FHV8, #209) — кложура shared-nested (снапшоты каждого вложенного + плоские subtree-сканы по документам), извлечённая в той же сессии открытия managed `.rfa`; `null`, когда shared-nested нет. Источник для композитного хэша задачи `hash-v8`.

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

## MiniProjectMarkFileOutcome

Результат дозаписи ES-маркера мини-проекта в один staged .rvt (Issue #189,
задача `mini-project-marker-v1`) — первой операции движка, изменяющей
MANAGED-ФАЙЛ, а не БД. Задача маппит статус на маркер колонки V28
`es_marker_version` (Marked/AlreadyMarked → 1, Missing → -2, Failed → -1).

**Файл:** `Models/FamilyManager/MiniProjectMarkFileOutcome.cs`

```csharp
public sealed record MiniProjectMarkFileOutcome(
    MiniProjectMarkFileStatus Status,
    int BackupsDeleted,       // удалённые бэкапы Revit name.NNNN.rvt
    string? ErrorMessage = null);

public enum MiniProjectMarkFileStatus
{
    Marked = 0,         // маркер записан, файл сохранён на месте
    AlreadyMarked = 1,  // валидный маркер уже был — перезапись пропущена (идемпотентность)
    Missing = 2,        // managed-файл отсутствует (терминально, -2)
    Failed = 3,         // open/mark/save упал (терминально, -1)
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
    SystemFamilySnapshot? SystemSnapshot = null,
    IReadOnlyList<FamilySnapshot>? SharedNestedSnapshots = null,
    IReadOnlyList<SharedNestedSubtree>? SharedNestedSubtrees = null);
```

- `Geometry` (ADR-054) — per-type геометрия из той же open-сессии (catalog backfill); `null`, если геометрия не запрашивалась или упала (caller делает fallback на отдельный проход).
- `SystemSnapshot` (ADR-056) — системный snapshot из staged `.rvt` на system-пути (типы + параметры + STRUCT + ROUTING); на loadable-пути `null`.
- `SharedNestedSnapshots` / `SharedNestedSubtrees` (FHV8, #209) — shared-nested кложура, переоткрытая через EditFamily в той же сессии (probe P2/P3); `null`, когда shared-nested нет.

---

## SharedNestedSubtree

FHV8 (#209): плоский subtree-скан одного family-документа (probe P1 — все уровни вложенности видны плоско). Корневая семья и каждое открытое вложенное дают по одной записи; композитор выводит прямые рёбра вычитанием.

**Файл:** `Models/FamilyManager/FamilyMigrationExtractResult.cs`

```csharp
public sealed record SharedNestedSubtree(
    string OwnerFamilyName,
    IReadOnlyList<string> NestedFamilyNames);
```

- `OwnerFamilyName` — нормализованное имя семейства, чей документ сканировался.
- `NestedFamilyNames` — нормализованные имена ВСЕХ shared-nested, видимых в этом документе (плоско, все уровни).

---

## MissingRecordCandidate

Один кандидат инструмента «Очистить недоступные записи» (#133): версия каталога, чей managed-файл недоступен. Причина `MarkedMissing` — версия помечена миграцией хэша (`hash_format_version = -2`, `RecalculationMissing`); `FileMissing` — файл отсутствует физически, найдено сканом диска. Удаление — через `ICatalogActualizationService.PurgeMissingAsync`.

**Файл:** `Models/FamilyManager/MissingRecordCandidate.cs`

```csharp
public enum MissingRecordReason
{
    MarkedMissing = 0,
    FileMissing = 1
}

public sealed record MissingRecordCandidate(
    string CatalogItemId,
    string ItemName,
    string VersionLabel,
    string FileName,
    string RelativePath,
    int RevitVersion,
    MissingRecordReason Reason);
```

Лейбл становится кандидатом только если отсутствуют ВСЕ его варианты — единственный выживший вариант держит запись рабочей в своей версии Revit (`DeleteVersionAsync` удаляет лейбл целиком).

---

## MissingRecordScanProgress

Прогресс скана недоступных записей (#133) — зеркало `DatabaseMigrationProgress` диалога актуализации («Проверка X из Y — файл»). `Found` non-null, когда проверенная версия оказалась недоступной — диалог добавляет строку сразу, поэтому прерванный скан сохраняет частичный результат.

**Файл:** `Models/FamilyManager/MissingRecordCandidate.cs`

```csharp
public sealed record MissingRecordScanProgress(
    int Current,
    int Total,
    string CurrentFileName,
    MissingRecordCandidate? Found = null);
```

---

## PurgeMissingResult

Исход `ICatalogActualizationService.PurgeMissingAsync` — числа и СПИСКИ ИМЁН для сводок (диалог обновления БД и «Очистить недоступные записи», #133): пользователь видит, КАКИЕ семейства затронуты, а не только счётчики.

**Файл:** `Models/FamilyManager/PurgeMissingResult.cs`

```csharp
public sealed record ResetRoutingLinkInfo(string PurgedItemName, string ParentItemName);
public sealed record SwitchedActiveVersionInfo(string ItemName, string NewActiveVersionLabel);

public sealed record PurgeMissingResult(
    int DeletedItems,
    int DeletedVersions,
    int FailedDirectories,
    IReadOnlyList<ResetRoutingLinkInfo> ResetRoutingLinks,
    IReadOnlyList<SwitchedActiveVersionInfo> SwitchedActiveVersions)
{
    public static PurgeMissingResult Empty { get; }
}
```

- `ResetRoutingLinks` (#133, решение владельца 2026-09-09): сброшенные связи трассировки — `family_dependencies` FK CASCADE умирает вместе с удалённым фитингом; пары имён идут в буллет-список «• Родитель: фитинг 'X' удалён» + предупреждение о stale-трассировке в проекте. Заменил упразднённый guard E5 (`GuardedSkippedItems`).
- `SwitchedActiveVersions` — семейства, у которых удалённая версия была активной: active переключен на новейшую оставшуюся автоматически (удаление версии ≠ удаление семейства).
- `FailedDirectories` — недоступные папки: строки удалены DB-only, папки — на ручное удаление.
