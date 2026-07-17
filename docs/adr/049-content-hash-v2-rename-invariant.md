# ADR-049: Content Hash v2 — rename-invariant дедупликация (hash-first)

**Date:** 2026-07-17  
**Status:** accepted  
**Related:** Issue #126, ADR-039 (snapshot-driven commit), ADR-041 (active version management), ADR-050 (hash recalculation migration)

## Context

Content-hash дедупликация (Phase 27) включала имя семейства (`Document.Title`) в canonical string: `FHV1|LOADABLE|{name}|{cat}|...`. Следствия:

1. Переименованный `.rfa` давал другой хэш → дубликаты под другим именем не находились вообще.
2. Дедуп запускался ТОЛЬКО после совпадения нормализованного имени (`ContentHashDedupService` — name-first). Бизнес-цель «находить дубликаты даже если семейство переименовано» была недостижима.

Первоначальный план Issue #126 содержал технические ошибки, исправленные этим ADR (см. комментарий к Issue от 2026-07-17).

## Decision

### 1. Hash format v2: имя исключено из canonical string

`FamilyContentHashFormat.CurrentVersion = 2`. Loadable: `FHV2|LOADABLE|{cat}|PARAMS|...` — имя удалено. Контентная идентичность ≠ имя; имя — изменяемые метаданные (как git content-addressing).

System canonical string **не изменена** (`FHV1|SYSTEM|...`) — имени там никогда не было. Поэтому system-строки мигрируются дешёвым `UPDATE hash_format_version=2` без пересчёта (см. ADR-050): их v1-хэши уже rename-invariant, а пересчёт из isolated `.rvt` рисковал бы рассинхроном с хэшами, извлекаемыми при импорте из реального проекта (resolved-имена материалов).

### 2. Hash-first порядок дедупликации

`ContentHashDedupService.CheckAsync` теперь:

1. Хэш != null → `FindByContentHashAcrossVersionsAsync` по ВСЕМ версиям ВСЕХ айтемов (индекс `ix_catalog_versions_content_hash`, микросекунды на любых объёмах) — независимо от имени.
2. Найдено → `Duplicate`; айтем, найденный по хэшу — канонический «existing» для MakeActive/IncrementVersion. `IsCrossNameDuplicate = (matched.NormalizedName != row.NormalizedName)`.
3. Не найдено (или хэша нет) → name lookup → `Existing` / `New`.
4. Конфликт «хэш совпал с айтемом A, имя занято другим айтемом B» → приоритет контенту (Duplicate к A, default Skip, Warn в лог). Согласовано с владельцем продукта.

`ContentHashMatch` расширен: `CurrentVersionLabel`, `MatchedItemName`, `MatchedItemNormalizedName` (SELECT в провайдере добавил `ci.name`, `ci.normalized_name`, `ci.current_version_label`; индекс не изменился).

### 3. Имя айтема следует за именем файла АКТИВНОЙ версии

`SetActiveVersionAsync` читает `family_files.file_name` целевой версии (JOIN) и атомарно обновляет `catalog_items.current_version_label` + `content_hash` + `name` + `normalized_name`. Результат несёт `NameChanged`/`PreviousName`/`NewName`; properties-диалог обновляет `Name`, дерево — через существующий `LoadTreeAsync`. Согласовано с владельцем: имя в каталоге всегда отражает активный файл (консистентно с тем, как `UpdateFamilyAsync` уже переименовывает айтем при IncrementVersion).

### 4. Cross-name дубликат в UI

Batch-диалог: значок ⚠ рядом со статусом «Дубликат (vN)» (только при `IsCrossNameDuplicate`), tooltip объясняет последствия: «Сделать активной» — файл не импортируется, активируется найденная версия; «Новая версия» — семейство «{matched}» будет переименовано в имя файла (локализовано ru/en).

### 5. Precomputed triple целится в hash-matched айтем

`IFamilyImportPrecomputer.BuildPrecomputedTripleAsync` получил `forcedCatalogItemId`: когда дедуп сматчил строку по хэшу (возможно под другим именем), triple считается для ЭТОГО айтема (его `GetNextVersionLabelAsync` + managed path в его папке). Без этого IncrementVersion писал бы файл в сиротскую GUID-папку и падал бы на UNIQUE constraint со стейл `v1`.

Заодно исправлен латентный баг orphan SaveAs: staging-сервисы (`FileFamilyStagingService`, `ProjectFamilyStagingService`) теперь пропускают `Action == MakeActive` — MakeActive не импортирует файл, а staging сохранял held-документ в `files/{id}/vN+1/`, не регистрируемый в БД.

## Consequences

**Плюсы:**
- Дубликаты находятся по содержимому независимо от переименований — бизнес-цель Issue #126.
- Производительность дедупа не изменилась: один indexed lookup на строку (плюс name lookup только при промахе/конфликте).
- Переименование айтема/файла больше не ломает дедуп (v2 rename-invariant) — устранён целый класс рассинхронов.
- Старые v1-строки безопасно «невидимы» для hash-поиска (SQL-фильтр `hash_format_version = 2`) — ложных дублей нет; cross-name дедуп для них включается после миграции (ADR-050).

**Минусы / риски:**
- Два семейства с одинаковым контентом, но разными именами, импортированные под одним именем, теперь дадут `Duplicate` (раньше — `Existing`+IncrementVersion). Осознанное поведение: контентная идентичность важнее.
- Хэш — «best effort identity»: теоретический дрейф между мажорными версиями Revit (изменение built-in parameter ids/групп) возможен и существовал и в v1; в худшем случае даёт `Existing` вместо `Duplicate` — безопасно, без потери данных.
- `normalized_name` не UNIQUE: конфликтный IncrementVersion может оставить два айтема с одинаковым именем. Редкий кейс, принят владельцем; поиск по имени вернёт один из них.

**Tests:** hasher (same-content/different-names → same hash), dedup (cross-name, конфликт, hash-null fallback), SetActiveVersion (name adoption), precomputer (forced id). Полный прогон зелёный.
