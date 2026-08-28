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
- `EmbeddedMarkerCatalogItemId` / `EmbeddedMarkerVersionLabel` (#209, в сигнатуре записи) — ES-маркер вложенного семейства, прочитанный при подготовке; `IsMarkerResolvedVersion` (#180, 2026-08-12) — matched-версия взята из верифицированного маркера вопреки identity-матчу; batch-диалог помечает такие строки «— маркер» (при FHV10 противоречие структурно невозможно — индикатор краевых случаев).
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
    public const int CurrentVersion = 10;
    public const int RecalculationSkipped = -1;
    public const int RecalculationMissing = -2;
}
```

- `HexString` — SHA-256 hex string (uppercase, no dashes).
- `FormatVersion` — algorithm version, bumped when canonical-string format changes so old hashes do not produce false duplicate matches against newly computed hashes.
- `SourceKind` — `"loadable"` or `"system"`. Used to enforce cross-source separation (system hashes never match loadable hashes and vice versa).
- `FamilyContentHashFormat.CurrentVersion` — **= 10 (FHV10, решение владельца 2026-08-12)**. История: v1 — loadable canonical string включал имя семейства (`FHV1|LOADABLE|{name}|...`), переименованные файлы давали другой хэш; v2 — имя исключено (`FHV2|LOADABLE|{cat}|...`), system-строки мигрированы дешёвым UPDATE флага; v3 — категория стала локале-инвариантным ordinal, добавлены секции FACTS/FLAGS/CONN (loadable) и STRUCT/ROUTING (system), геометрия расширена (bbox/surface/длины кривых), значения экранируются; обе source мигрируются полным пересчётом из файла (задача `hash-v3`); v4–v7 — system-only покрытие (FAMKEY/STRUCT+/SEGMENTS/SUBTYPES/RAILING, WIRE, детерминированный порядок TYPES, Shape-дискриминатор duct; задачи `hash-v5`/`hash-v6`/`hash-v7`); v8 — loadable-only: секция NESTEDHASH (композитные хэши прямых shared-nested детей), задача `hash-v8`; **v9 — loadable-only: секция PHANTOM (значения phantom-типа безтиповых семейств), задача `hash-v9`, floor `DbCompatibility` bump → 2.0.1-beta.9**; **v10 — loadable-only: группа параметра исключена из PARAMS (единственное поле, непереносимое merge'ем — зонд-доказано), единый хэш для identity и embedded-верификации, задача `hash-v10`, floor выровнен под первую релизную бету с FHV8+ → 2.0.1-beta.8**.
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

**Canonical string layout (FHV11, #238):**
- `FHV11|LOADABLE|{catOrdinal}|PARAMS(без группы)|...|TYPES|...|PHANTOM|...|GEOM(+surface,bbox)|GEOM2D(+lengths)|NESTED|NONSHARED|NESTEDHASH|FACTS|FLAGS|CONN|...|LOOKUP|...` (loadable)
- `FHV7|SYSTEM|{catId}|TYPES|{typeName}|{params}|FAMKEY|STRUCT|...|ROUTING|...|SEGMENTS|SUBTYPES|RAILING|WIRE|...` (system)

**FHV8 (композитный хэш вложенных, #209, ADR-066):**
- Секция `NESTEDHASH` — отсортированные пары `(имя, композитный хэш)` ПРЯМЫХ shared-nested детей. Изменение контента глубоко в цепочке (болт → фланец → кран) транзитивно сдвигает хэши всех предков → переимпорт родителя даёт новую версию, а не ложный «Дубликат».
- Прямые рёбра выводятся вычитанием из плоских subtree-сканов (`Direct(f) = Subtree(f) \ ⋃ Subtree(g)`), композиция снизу вверх — `CompositeFamilyHashComposer` (Core, pure C#).
- Нечитаемый ребёнок → константный маркер `UNREADABLE` (детерминизм; падения нет). Циклы в Revit невозможны (LoadFamily отклоняет) — защитный guard с тем же маркером.
- Тем же бампом: безымянный дефолтный ТИП больше не извлекается как `<default>` — Revit синтезирует его при ЗАГРУЗКЕ безтипового семейства в документ (raw .rfa: `Types.Size=0`, EditFamily-копия: `Size=1`), из-за чего хэш зависел от контекста извлечения (поймано интеграционным FHV8-пробом). UI безтиповых семейств не меняется: виртуальный узел «как семейство» создаётся и при 0 type-строк (`AttachTypesToNodes`). Значения phantom-типа — см. FHV9 ниже (утверждение «покрыты транзитивно через GEOM» оказалось ложным для не-геометрических параметров — стресс-тест 2026-08-12).
- Non-shared вложенные остаются name-only (секция NONSHARED) — #217 трекает их будущее как «Компоненты» каталога.
- Тесты: `CompositeFamilyHashComposerTests` (цепочки/ромб/детерминизм/эквивалентность), интеграционные `CompositeFamilyHashTests` (транзитивность на реальном контенте + standalone≡nested эквивалентность).

**FHV9 (значения phantom-типа, #209 стресс-тест 2026-08-12):**
- Секция `PHANTOM` (только identity-грейд) — значения параметров **безтипового** семейства (нет именованных типов). FHV8 пропускал phantom целиком, и значения typeless-семейств выпали из хэша: правка любого не-геометрического значения (встроенная «Модель» и т.п.) не меняла хэш → ложный «Дубликат» при импорте, невозможно создать новую версию.
- Экстракция контекстно-стабильна (`RevitFamilySnapshotExtractor.ExtractPhantomTypeValues`): редактор/EditFamily-копия — значения текущего безымянного типа (Size=1); raw-открытие (Size=0, нет CurrentType) — синтез временного типа `NewType` в транзакции с **RollBack** (файл не меняется; зонд-доказано 2026-08-12). Оба контекста читают одни и те же дефолтные значения → равенство «файл ↔ вложенная копия» сохраняется.
- Раскатка: критическая задача `hash-v9` пересчитывает все не-current строки (PHANTOM меняет хэши typeless-семейств и их предков через NESTEDHASH); `DbCompatibility.CurrentMinPluginVersion` bump → `2.0.1-beta.9` (breaking data format, прецедент FHV8).

**FHV10 (группы параметров уходят из хэша — единый хэш на всё, решение владельца 2026-08-12):**
- Поле `ParameterGroup` исключено из секции PARAMS. Обоснование: группа — единственное контентное поле, которое reload-merge физически не переносит (зонд-доказано дважды: UI/plain-merge 2026-08-11 на реальной библиотеке, poke + doc-to-doc 2026-08-12 в `GroupPropagationProbeTests` — merge приземляет контент, параметр даже пересоздаётся, группа остаётся хостовой). Двойная система хэшей (identity с группами + verification без) давала противоречия «маркер против хэша».
- Гипотеза «хост загрязняет embedded-документ» (объёмы/габариты/phantom) **опровергнута зондом** `DrivenEmbeddedPollutionProbeTests`: ассоциации живут на экземплярах в хосте, EditFamily-документ хранит авторское состояние байт-в-байт — поэтому метрики и phantom остаются в хэше, и отдельный verification-грейд (FHV8V/FHV9V) упразднён: `ComputeForEmbeddedVerification` удалён, везде `ComputeForLoadable`.
- Продуктовый трейдофф (принят владельцем): правка ТОЛЬКО группировки параметров больше не порождает новую версию (импорт скажет «Дубликат») — embedded-группы всё равно нельзя обновить. Пары версий «только группа» в существующих базах после пересчёта получают одинаковый хэш (детерминировано, см. ADR-068 addendum).
- Раскатка: критическая задача `hash-v10`; floor `DbCompatibility` = `2.0.1-beta.8` — первая бета, выпускающая форматы FHV8+ (v9-хэши не выпускались в релизных сборках). Константа не должна превышать версию, в которой шипится.
- Тесты: юнит `FamilyContentHasherTests` (`UnifiedHash_*` — group-инвариантность, метрики/phantom детектируются, golden FHV10), интеграционные `PhantomTypeValueHashTests`, `GroupPropagationProbeTests`, `DrivenEmbeddedPollutionProbeTests`, `NestedUpdateMatrixTests`.

**FHV11 (lookup tables в хэше, Issue #238, ADR-069):**
- Секция `LOOKUP` — raw CSV-содержимое встроенных в семейство таблиц поиска (`FamilySizeTable`), нормализованное (CRLF→LF, trim). До FHV11 правка только значений lookup-таблицы не сдвигала хэш → ложный «Дубликат» при загрузке новой версии.
- Источник контента — `ExportSizeTable` (машинный CSV с `##spec##unit` заголовками, локале-инвариантен), НЕ `AsValueString` (display-форматирование — риск локализации). Экстракция — в той же сессии открытия семейства (`RevitFamilySnapshotExtractor.ExtractLookupTables`), без дополнительных `OpenDocumentFile`.
- Секция **опускается** для семейств без таблиц (их хэш меняется только префиксом); таблицы сортируются по имени Ordinal (в семействе может быть несколько таблиц). Только loadable-путь — системные семейства таблиц не имеют.
- Merge-safe (зонд 2026-08-23): reload-merge переносит содержимое таблиц в embedded-копию и в проекте (`LoadFamilySymbol`), и при doc-to-doc загрузке — в отличие от групп параметров (FHV10). Единый хэш сохраняется, post-verify stale-update не зацикливается.
- Раскатка: критическая задача `hash-v11` пересчитывает все не-current строки; floor `DbCompatibility.CurrentMinPluginVersion` = `2.0.1-beta.9` — первая бета, выпускающая FHV11 (FHV10 в релизных сборках не выпускался; тег на момент решения: v2.0.1-beta.8).
- Тесты: юнит `FamilyContentHasherTests` (сдвиг хэша от правки CSV, опуск секции, Ordinal-детерминизм порядка таблиц, экранирование `|` в имени таблицы, golden FHV11), интеграционные `LookupTableHashTests` (правка CSV сдвигает хэш на реальном семействе; embedded == file hash после reload — приёмка совместимости с post-verify).

**v3 rules:**
- Категория — локале-инвариантный ordinal (display name — только fallback при unknown ordinal).
- Все строковые значения экранируются (`%` → `%25`, `|` → `%7C`) — инъекция полей невозможна.
- Коннекторные координаты и bounding box округляются до 1e-4 ft.
- Blank values excluded (`IsBlankValue(hasValue, text, storageType)`): HasValue=false, empty string, `INVALID` (только ElementId storage), `UNSUPPORTED` (только неизвестные storage), `READERROR`. Numeric zero is NOT blank. Пользовательская строка "INVALID"/"UNSUPPORTED" в текстовом параметре — контент, участвует в хэше.
- Auto-generated parameters excluded (IsAutoGeneratedParameter(name)): anything containing IfcGUID or IFC GUID (case-insensitive). Revit regenerates these on every .rvt save — including them would break cross-document stability.
- Тесты: `src/SmartCon.Tests/FamilyManager/Services/FamilyContentHasherTests.cs` — blank-value, ноль, IfcGUID, порядок, cross-source, плюс FHV3-набор: ordinal-категория (locale-invariance), PartType, коннекторы (размер/система/Origin/rounding/linked), behavior-флаги, bbox/surface, длины кривых, non-shared nested, экранирование, слои (материал/порядок), routing (part/порядок/критерий/junction/null-part); golden-тесты FHV7 (system) и FHV10 (loadable, с NESTEDHASH + PHANTOM, без групп).

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

---

## ContentSectionHash

Одна секция контент-хэша семейства (#249, FHV12+, ADR-071): ключ секции, её каноническая подстрока и SHA-256. Каноническая строка семейства = конкатенация секций в фиксированном порядке; секции хранятся в `catalog_versions.section_hashes`/`section_strings` (V33) и дают диф «что изменилось» без открытия файла.

**Файл:** `Models/FamilyManager/ContentSectionHash.cs`

```csharp
public sealed record ContentSectionHash(string SectionName, string CanonicalString, string HashHex)
{
    public string Key => TypeName is null ? SectionName : SectionName + "|" + TypeName;
}
```

- `Key` — плоский ключ хранения/диффа (`"STRUCT"` или `"SECTION|TypeName"` для per-type секций system-семейств).

---

## FamilyContentSectionNames

Константы имён секций контент-хэша (#249): loadable (META, PARAMS, TYPES, PHANTOM, DEF, GEOM, GEOM2D, NESTED, NONSHARED, NESTEDHASH, FACTS, FLAGS, CONN, LOOKUP, FAMKEY) и system (STRUCT, ROUTING, SEGMENTS, SUBTYPES, RAILING, WIRE, VALUES). Используются билдерами секций, классификатором, сериализатором и маппером отображаемых имён diff-окна.

**Файл:** `Models/FamilyManager/FamilyContentSectionNames.cs`

---

## ContentSectionJsonSerializer

Сериализация секций в/из JSON-колонок `section_hashes` (ключ → хэш) и `section_strings` (ключ → каноническая подстрока) (#249, Phase 4). Детерминированный порядок ключей; `Deserialize` возвращает null для пустого входа (analytics pending).

**Файл:** `Services/Implementation/ContentSectionJsonSerializer.cs`

---

## ContentChangeClass

Класс изменения содержимого между версией кандидата и активной версией (#249, Phase 4): `None` (идентично), `Trivial` (косметика — можно «Перезаписать текущую»), `Minor` (значения типов/параметры/вложения), `Major` (геометрия/привязки/коннекторы — рекомендуется новая версия). Отображается в diff-окне batch-диалога.

**Файл:** `Models/FamilyManager/ContentVersionDiff.cs`

---

## ContentVersionDiff

Результат диффа двух версий: класс изменения (`ContentChangeClass`), список изменённых секций (ключи `FamilyContentSectionNames`) и per-type списки (изменённые/добавленные/удалённые типы). Строится из секционных хэшей и per-type хэшей двух версий без открытия файлов.

**Файл:** `Models/FamilyManager/ContentVersionDiff.cs`

---

## ContentVersionDiffComputer

Чистый вычислитель `ContentVersionDiff` (#249, Phase 4): секции кандидата vs хэши активной версии → изменённые секции; per-type хэши → изменённые/добавленные/удалённые типы; класс через `ContentChangeClassifier`. Вход «нет аналитики активной стороны» обрабатывается вызывающим (пустые входы), чтобы не показывать фейковый Major.

**Файл:** `Services/Implementation/ContentVersionDiffComputer.cs`

---

## ContentChangeClassifier

Правила отображения набора изменённых секций на `ContentChangeClass` (#249): major = DEF/GEOM/CONN/STRUCT/ROUTING; minor = TYPES/VALUES/PHANTOM/NESTED*/LOOKUP/FLAGS/PARAMS; trivial = остальное (GEOM2D, FACTS, META и пр.). Берётся максимум по тяжести.

**Файл:** `Services/Implementation/ContentChangeClassifier.cs`

---

## FamilyTypeHashEntry

Строка per-type хэша для таблицы `family_type_hashes` (V32, #249 Phase 2): `TypeIdentityKey` (UPPER(имя) для loadable; `SystemTypeIdentityKey` для system), отображаемое `TypeName`, `HashHex` — SHA-256 канонической подстроки типа из TYPES. Считается при Prepare и едет через цепочку импорта без повторного открытия файла.

**Файл:** `Models/FamilyManager/FamilyTypeHashEntry.cs`

```csharp
public sealed record FamilyTypeHashEntry(string TypeIdentityKey, string TypeName, string HashHex);
```

---

## SystemTypeContentHash

Per-type хэш system-семейства (#249): `IdentityKey` (канонический `SystemTypeIdentityKey.Build` — «TOKEN|NAME», FHV6: одноимённые типы разных system-семейств не пересекаются), `TypeName`, `HashHex`. Фундамент для #179 (system per-type stale detection).

**Файл:** `Models/FamilyManager/SystemTypeContentHash.cs`

---

## DefinitionMetrics

DEF-секция снапшота (#249, FHV12): привязки определения семейства — формы **только с биндингами** (видимость/материал/offset, FHV14), размеры **только с метками** (FHV13), опорные плоскости (имя + DefinesOrigin). Тип-независимая «проводка» семейства.

**Файл:** `Models/FamilyManager/DefinitionMetrics.cs`

Содержит `FormDefinitionSnapshot`, `DimensionDefinitionSnapshot` (label может быть null — unlabeled), `ReferencePlaneDefinitionSnapshot`.

---

## LoadableVerificationResult

Результат контентной верификации loadable-семейства (#249 Phase 2): `Verdict` (true — embedded == файлу текущей версии; false — отличается; null — indeterminate: guard/файл недоступен) и `PerTypeStale` — карта drift по типам (имя → bool) или null при недоступном per-type proof. Используется `StaleDetector` для per-type оранжевых точек.

**Файл:** `Models/FamilyManager/LoadableVerificationResult.cs`

---

## FamilyContentHasher.LoadableSections

Partial-часть `FamilyContentHasher` с билдерами loadable-секций (#249): META (префикс FHVnn|LOADABLE|catOrdinal), PARAMS, TYPES (+per-type подстроки), PHANTOM, DEF (FHV14: bound-only формы, labeled-only размеры), GEOM (FHV12-усиленная), GEOM2D (FHV14: чистая 2D-графика), NESTED*/NESTEDHASH, FACTS, FLAGS, CONN, LOOKUP. `FormatCoord` (общий с VIEW3D) канонизирует `-0` → `0` (FHV16).

**Файл:** `Services/Implementation/FamilyContentHasher.LoadableSections.cs`

---

## FamilyContentHasher.SystemSections

Partial-часть `FamilyContentHasher` с билдерами system-секций (#249): META (FHVn|SYSTEM), FAMKEY, STRUCT, ROUTING, SEGMENTS, SUBTYPES, RAILING, WIRE, VALUES (+per-type подстроки для `family_type_hashes`).

**Файл:** `Services/Implementation/FamilyContentHasher.SystemSections.cs`
