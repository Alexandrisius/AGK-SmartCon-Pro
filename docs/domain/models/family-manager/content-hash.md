---
module: family-manager
---
# Модели FamilyManager — Content Hash и дедупликация

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

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
    public const int CurrentVersion = 8;
    public const int RecalculationSkipped = -1;
    public const int RecalculationMissing = -2;
}
```

- `HexString` — SHA-256 hex string (uppercase, no dashes).
- `FormatVersion` — algorithm version, bumped when canonical-string format changes so old hashes do not produce false duplicate matches against newly computed hashes.
- `SourceKind` — `"loadable"` or `"system"`. Used to enforce cross-source separation (system hashes never match loadable hashes and vice versa).
- `FamilyContentHashFormat.CurrentVersion` — **= 8 (FHV8, #209, ADR-066)**. История: v1 — loadable canonical string включал имя семейства (`FHV1|LOADABLE|{name}|...`), переименованные файлы давали другой хэш; v2 — имя исключено (`FHV2|LOADABLE|{cat}|...`), system-строки мигрированы дешёвым UPDATE флага; v3 — категория стала локале-инвариантным ordinal, добавлены секции FACTS/FLAGS/CONN (loadable) и STRUCT/ROUTING (system), геометрия расширена (bbox/surface/длины кривых), значения экранируются; обе source мигрируются полным пересчётом из файла (задача `hash-v3`); v4–v7 — system-only покрытие (FAMKEY/STRUCT+/SEGMENTS/SUBTYPES/RAILING, WIRE, детерминированный порядок TYPES, Shape-дискриминатор duct; задачи `hash-v5`/`hash-v6`/`hash-v7`); **v8 — loadable-only: секция NESTEDHASH (композитные хэши прямых shared-nested детей), задача `hash-v8`**.
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

## FamilyContentHasher

Pure-C# implementation of IFamilyContentHasher. No Revit API calls — entirely deterministic. Computes SHA-256 of a sorted canonical string built from the snapshot data. Lives in SmartCon.Core/Services/Implementation/; the IFamilyContentHasher contract is in docs/domain/interfaces/family-manager/extraction.md.

**Файл:** Services/Implementation/FamilyContentHasher.cs

`csharp
public sealed class FamilyContentHasher : IFamilyContentHasher
{
    public FamilyContentHash? ComputeForLoadable(FamilySnapshot snapshot);
    public FamilyContentHash? ComputeForSystem(SystemFamilySnapshot snapshot);
}
`

**Canonical string layout (FHV8, #209):**
- `FHV8|LOADABLE|{catOrdinal}|PARAMS|...|TYPES|...|GEOM(+surface,bbox)|GEOM2D(+lengths)|NESTED|NONSHARED|NESTEDHASH|FACTS|FLAGS|CONN|...` (loadable)
- `FHV7|SYSTEM|{catId}|TYPES|{typeName}|{params}|FAMKEY|STRUCT|...|ROUTING|...|SEGMENTS|SUBTYPES|RAILING|WIRE|...` (system)

**FHV8 (композитный хэш вложенных, #209, ADR-066):**
- Секция `NESTEDHASH` — отсортированные пары `(имя, композитный хэш)` ПРЯМЫХ shared-nested детей. Изменение контента глубоко в цепочке (болт → фланец → кран) транзитивно сдвигает хэши всех предков → переимпорт родителя даёт новую версию, а не ложный «Дубликат».
- Прямые рёбра выводятся вычитанием из плоских subtree-сканов (`Direct(f) = Subtree(f) \ ⋃ Subtree(g)`), композиция снизу вверх — `CompositeFamilyHashComposer` (Core, pure C#).
- Нечитаемый ребёнок → константный маркер `UNREADABLE` (детерминизм; падения нет). Циклы в Revit невозможны (LoadFamily отклоняет) — защитный guard с тем же маркером.
- Тем же бампом: безымянный дефолтный ТИП больше не извлекается как `<default>` — Revit синтезирует его при ЗАГРУЗКЕ безтипового семейства в документ (raw .rfa: `Types.Size=0`, EditFamily-копия: `Size=1`), из-за чего хэш зависел от контекста извлечения (поймано интеграционным FHV8-пробом). UI безтиповых семейств не меняется: виртуальный узел «как семейство» создаётся и при 0 type-строк (`AttachTypesToNodes`). Значения фантомного типа транзитивно покрыты секцией GEOM.
- Non-shared вложенные остаются name-only (секция NONSHARED) — #217 трекает их будущее как «Компоненты» каталога.
- Тесты: `CompositeFamilyHashComposerTests` (цепочки/ромб/детерминизм/эквивалентность), интеграционные `CompositeFamilyHashTests` (транзитивность на реальном контенте + standalone≡nested эквивалентность).

**v3 rules:**
- Категория — локале-инвариантный ordinal (display name — только fallback при unknown ordinal).
- Все строковые значения экранируются (`%` → `%25`, `|` → `%7C`) — инъекция полей невозможна.
- Коннекторные координаты и bounding box округляются до 1e-4 ft.
- Blank values excluded (`IsBlankValue(hasValue, text, storageType)`): HasValue=false, empty string, `INVALID` (только ElementId storage), `UNSUPPORTED` (только неизвестные storage), `READERROR`. Numeric zero is NOT blank. Пользовательская строка "INVALID"/"UNSUPPORTED" в текстовом параметре — контент, участвует в хэше.
- Auto-generated parameters excluded (IsAutoGeneratedParameter(name)): anything containing IfcGUID or IFC GUID (case-insensitive). Revit regenerates these on every .rvt save — including them would break cross-document stability.
- Тесты: `src/SmartCon.Tests/FamilyManager/Services/FamilyContentHasherTests.cs` — blank-value, ноль, IfcGUID, порядок, cross-source, плюс FHV3-набор: ordinal-категория (locale-invariance), PartType, коннекторы (размер/система/Origin/rounding/linked), behavior-флаги, bbox/surface, длины кривых, non-shared nested, экранирование, слои (материал/порядок), routing (part/порядок/критерий/junction/null-part); golden-тесты FHV7 (system) и FHV8 (loadable, с NESTEDHASH).

---

## NestedContentHash

FHV8 (#209): одна запись прямого shared-nested ребёнка в композитном хэше — имя семейства + его композитный хэш. Заполняется композитором непосредственно перед хэшированием (`FamilySnapshot.SharedNestedContentHashes`).

**Файл:** `Models/FamilyManager/FamilySnapshot.cs`

```csharp
public sealed record NestedContentHash(string FamilyName, string HashHex);
```

---

## CompositeFamilyHashComposer

FHV8 (#209, ADR-066): композитные хэши замкнутого набора loadable-семейств (родитель + вся shared-nested кложура). Pure C# в SmartCon.Core. Прямые рёбра выводятся вычитанием из плоских per-document subtree-сканов; композиция снизу вверх (мемоизированный DFS, Ordinal-порядок — полная детерминированность). Нечитаемый/отсутствующий ребёнок → маркер `UnreadableChildHash` (`"UNREADABLE"`). Используется и в import-time пайплайне (`FamilyImportPreparationService.FinalizeLoadableHashesAsync`), и в задаче `hash-v8` (self-contained кложура из managed .rfa).

**Файл:** Services/Implementation/CompositeFamilyHashComposer.cs

```csharp
public sealed class CompositeFamilyHashComposer
{
    public const string UnreadableChildHash = "UNREADABLE";
    public IReadOnlyDictionary<string, FamilyContentHash?> Compose(
        IReadOnlyDictionary<string, FamilySnapshot> snapshots,
        IReadOnlyDictionary<string, IReadOnlyList<string>> flatSubtrees);
}
```
