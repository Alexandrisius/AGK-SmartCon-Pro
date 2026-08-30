---
module: FamilyManager
topic: Routing as catalog data (ADR-072, V34/V35)
---

# Routing как данные каталога (#254, ADR-072)

Правила трассировки системных MEPCurve-типов версионируются в БД каталога,
а не в Revit-форме мини-проекта (slim мини не несёт фитингов). Два механизма
хранения в Revit унифицированы в одну модель: manager-группы (pipe/duct,
`RoutingPreferenceRuleGroupType`) и param-группы (flex/conduit/cable tray —
менеджера нет, выбор фитингов живёт в VISIBLE built-in параметрах
`RBS_CURVETYPE_*`, FHV19).

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

