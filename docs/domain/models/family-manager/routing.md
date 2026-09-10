---
module: FamilyManager
topic: Routing as catalog data (ADR-072, V34/V35)
---

# Routing как данные каталога (#254, ADR-072; FHV21 — ADR-073)

Правила трассировки системных MEPCurve-типов версионируются в БД каталога,
а не в Revit-форме мини-проекта (slim мини не несёт фитингов). Два механизма
хранения в Revit унифицированы в одну модель: manager-группы (pipe/duct,
`RoutingPreferenceRuleGroupType`) и param-группы (flex/conduit/cable tray —
менеджера нет, выбор фитингов живёт в VISIBLE built-in параметрах
`RBS_CURVETYPE_*`, FHV19).

**Разделение владения (FHV21, ADR-073, решение владельца 2026-09-01):**
фитинги + их критерии + preferred junction — item-level связи каталога
(V37, вне хэша, World B, редактируются вкладкой «Трассировка»); сегментная
конфигурация трубы (набор сегментов + диапазоны правил + порядок) —
версионный контент мини-проекта (входит в хэш, per-version таблица V38
`family_segment_rules`; вкладка показывает её read-only).

## RoutingGroupKeys

Строковая идентичность группы правил (формат колонки
`family_routing_rules.group_key`): manager-группа — имя enum
(`"Segments"` … `"Caps"`, ordinal-маппа захардкожена — Core не ссылается на
Revit enum, I-09); param-группа — `"Param:<BuiltInParameter-name>"`.
`ParamGroupType = -1` — значение `RoutingRuleSnapshot.GroupType` для
param-групп (невалидный manager-ordinal).

**Файл:** `SmartCon.Core/Models/FamilyManager/RoutingGroupKeys.cs`

## FamilyRoutingRuleInfo / FamilyRoutingTypeSettings

Хранимые записи V34. `FamilyRoutingRuleInfo`: (TypeName, FamilyKey,
GroupKey, RuleOrder, PartName?, Description, Criteria) — критерии
произвольных видов выживают roundtrip через JSON-колонку. PartName=null —
no-part правило («Нет», легальный контент). `FamilyRoutingTypeSettings`:
per-type скаляр PreferredJunctionType (manager) / PREFERRED_BRANCH (flex);
наличие settings-строки — маркер «routing версии хранится как данные»
(дискриминатор legacy-fallback; легитимно пустой routing хранит settings).

**Файл:** `SmartCon.Core/Models/FamilyManager/FamilyRoutingRuleInfo.cs`

## RoutingRuleRecordMapper

Двусторонний маппинг снапшот ↔ V34-записи: `ToRecords(SystemTypeSnapshot)`
(per-group zero-based order), `ToSnapshot(typeName, familyKey, rules,
settings)` — канонический порядок (manager-группы по ordinal, param-группы
после, по ключу). Единая точка преобразования для импорта, sync, backfill
и будущего редактора (Ф3).

**Файл:** `SmartCon.Core/Services/Implementation/RoutingRuleRecordMapper.cs`

## RoutingSectionParser / ParsedTypeRouting

File-free backfill (Ф2b): парсинг канонических секций ROUTING/FAMKEY из
`catalog_versions.section_strings` (V33) обратно в
`RoutingPreferencesSnapshot` — manager int-токены и `"Param:<BIP>"`-ключи,
unescape `%7C`/`%25`, маркер `NOPART` → null. Малформированный хвост
останавливает парсинг (analytics-вход, никогда не доказательство).

**Файл:** `SmartCon.Core/Services/Implementation/RoutingSectionParser.cs`

## RoutingDrivingParameters (SmartCon.Revit)

Кандидатный набор routing-driving built-in параметров (16 ElementId
фитинг-параметров `RBS_CURVETYPE_DEFAULT_*`/`MULTISHAPE_*` +
`RBS_CURVETYPE_PREFERRED_BRANCH_PARAM`). Membership в `.Parameters` —
авторитет (per-класс-типа: CableTray without Fittings прячет TEE/CROSS);
на pipe/duct все routing-bip'ы hidden → фильтр no-op. Используется
экстрактором (исключение из VALUES → ROUTING), sync (dispatch) и
slimming-сервисом.

**Файл:** `SmartCon.Revit/FamilyManager/RoutingDrivingParameters.cs`

## MiniProjectSlimmingOutcome / MiniProjectSlimmingStatus

Результат slimming-прохода (Ф2b): статус (Slimmed / AlreadySlim / Missing /
Failed), pre-slim снапшот (источник backfill; null для AlreadySlim —
защита DB rules), счётчики (инстансы/семейства/орфаны/ренеймы/бэкапы).

**Файл:** `SmartCon.Core/Services/Interfaces/IMiniProjectRoutingSlimmingService.cs`

## RoutingGroupCatalog / RoutingGroupDescriptor / RoutingManagerGroup

Per-category модель редактора трассировки (Ф3): какие группы показывает
категория (pipe/duct — manager, multi-rule + критерии; flex/conduit/tray —
param-строки по одному значению; conduit/tray without Fittings прячут
TEE/CROSS по family_key), фильтры пикера детали (категория фитинга +
ordinals `part_type`), read-only Segments, preferred junction (pipe/duct/
flex). Ordinals категорий и PartType — замороженные API-константы
(revitapidocs + PartTypeLabelMap), Core не ссылается на Revit enum (I-09).
FHV21 (ADR-073): константы форм коннекторов `ShapeRound/ShapeRectangular/
ShapeOval` (битмаска факта `connector_shape`); дескриптор несёт
`IsSegmentRow` (read-only view per-version конфигурации мини),
`RequiredConnectorShapeMask` (строки переходов переменной формы требуют
ВСЕ биты — круг↔прямоуг. = 3, прямоуг.↔овал = 6, овал↔круг = 5) и
`ExcludeMultiShapeParts` (обычная строка «Переходы» — только одноформенные
детали, мультиформенные живут в своих строках).

**Файл:** `SmartCon.Core/Models/FamilyManager/RoutingGroupCatalog.cs`

## RoutingEditorData / RoutingEditorTypeSave / RoutingSaveResult / RoutingPartCandidate / SegmentSizeBounds

Вход/выход движка редактора (Ф3, World B): типы итема (с дискриминацией
WithFittings по family_key), правила/настройки (item-таблицы V37, fallback
V34 текущей версии для legacy), presence-флаги (MissingPartFamilies),
dropdown-номиналы размеров (SizeNominalsFeet) и собственные диапазоны
сегментов (SegmentSizeBounds — min/max nominal по таблице размеров: строка
сегмента показывает их read-only, как диалог Revit). Save — только
затронутые типы; результат — успех + ArchivedLockedParts (детали, убранные
из routing, но залоченные архивными версиями — UX-подсказка ADR-067).
Версия не создаётся, хэш не трогается.

**Файл:** `SmartCon.Core/Models/FamilyManager/RoutingEditorData.cs`

## RoutingFingerprint

Каноническая сериализация правил трассировки + SHA-256 (ADR-072 World B,
FHV20): трассировка вышла из content-хэша (это связь семейств каталога, не
состояние трубы), поэтому равенство трассировок сравнивается отдельным
отпечатком — sync/stale (`StaleReason.RoutingDrift`)/диалог размещения.
Строка байт-идентична дореформенной ROUTING-секции. `null` = тип без
трассировки (отсутствие — своё состояние).

**Файл:** `SmartCon.Core/Services/Implementation/RoutingFingerprint.cs`

## SegmentSizeRecord

Строка таблицы размеров сегмента версии каталога (V36, Ф3): имя сегмента +
диаметры (internal units) + флаги использования. Источник dropdown'ов
мин./макс. размера редактора трассировки — строго NominalDiameter, как в
диалоге трассировки Revit. Пишется при импорте (`SegmentSizeWriter`),
дозаполняется для legacy-версий задачей `segment-sizes-v1` (детект:
системные трубы без строк размеров; extraction из мини). Save редактора
размеры не трогает (это содержимое файла, не связи).

**Файл:** `SmartCon.Core/Models/FamilyManager/SegmentSizeRecord.cs`

## SegmentRuleRecord

Строка per-version сегментного правила (V38, FHV21, ADR-073): сегментная
конфигурация типа трубы — набор сегментов, их диапазоны Мин/Макс
(NULL = unrestricted) и порядок правил — версионный контент мини-проекта,
входит в хэш (SEGMENTS-секция, META FHV11). Таблица `family_segment_rules`
(каскадное удаление с версией) — откат версии восстанавливает СВОЮ
конфигурацию. Писатели: `SegmentRuleWriter` (импорт),
`RoutingBackfillActualizationTask` (все варианты, включая архивные),
задача `segment-rules-v1` (backfill из мини для legacy).

**Файл:** `SmartCon.Core/Models/FamilyManager/SegmentRuleRecord.cs`

## SegmentRuleComposition

Единая композиция читателей трассировки (FHV21, ADR-073): фитинг-группы —
из item-канала (World B), сегментная группа — из per-version таблицы
активной версии; legacy-fallback на stored Segments-строки, пока версия не
backfill'нута. `Compose` (читатели: редактор, sync, обе drift-пробы),
`ToRuleInfo`, `FromSnapshot` (писатели: `SegmentRuleWriter` — порядок
`order++` per type). Известный осознанный edge: «легитимно пустая» сегментная
конфигурация неотличима от «не backfill'нут» → fallback на legacy-строки
(практически недостижимо: Revit требует ≥1 сегментное правило на тип трубы).

**Файл:** `SmartCon.Core/Services/Implementation/SegmentRuleComposition.cs`

## ConnectorShapeLabelMap

Локализованные подписи факта `connector_shape` (ADR-055, ADR-073): битмаска
`FamilyFact.ValueKey` (Round=1, Rectangular=2, Oval=4) → RU/EN строка по
текущему языку («Круглый и Прямоугольный», никаких «Round+Rectangular»).
Маска 0 (семейство без коннекторов — факт вычислен, коннекторов нет) —
`NoConnectorsLabel` («Нет коннекторов»), детектор `IsZeroMask`. null для
неизвестных масок → fallback на extraction-time `ValueDisplay` (контракт
как у PartTypeLabelMap).

**Файл:** `SmartCon.Core/Models/FamilyManager/ConnectorShapeLabelMap.cs`

---

## RoutingPartReference

Сырая строка `item_routing_rules` с непустым `part_name` (вход детектора routing-фантомов #133; Segments-группа отфильтрована — это конфигурация сегментов, не ссылка на фитинг). Читается одним SQL по всей базе: `IFamilyRoutingRuleRepository.ReadAllPartReferencesAsync`.

**Файл:** `Models/FamilyManager/RoutingPhantomInfo.cs`

```csharp
public sealed record RoutingPartReference(
    string CatalogItemId,
    string FamilyKey,
    string TypeName,
    string PartName);
```

---

## RoutingPhantomInfo

Нерезолвированная ссылка правила трассировки (#133): правило хранит фитинг строкой `part_name` «Семейство:Тип» БЕЗ FK — после удаления семейства из каталога (напр. purge'ем недоступных) правило остаётся с мёртвой ссылкой. Детектор (`IRoutingEditorService.FindRoutingPhantomsAsync`, тот же резолв, что вкладка «Трассировка»: семейство-часть токена до `:`, поиск по нормализованному имени среди loadable) гоняется после каждой загрузки дерева: затронутые семейства получают значок-бейдж (SourceBranch), клик по которому открывает свойства сразу на вкладке «Трассировка» на затронутом типе.

**Файл:** `Models/FamilyManager/RoutingPhantomInfo.cs`

```csharp
public sealed record RoutingPhantomInfo(
    string CatalogItemId,
    string FamilyKey,
    string TypeName,
    string MissingPartFamilyName);
```
