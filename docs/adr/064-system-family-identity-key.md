# ADR-064: Locale-invariant идентичность системной семьи — family_key (Issue #190, #191)

**Date:** 2026-08-03
**Status:** accepted
**Related:** Issue #183 (идентичность (FamilyName, TypeName, category)), #190, #191, ADR-056 (locale-invariant категорий), ADR-061 (sync системных типов), ADR-027 (реестр категорий)

## Context

ADR-061/#183 ввели идентичность системного типа = (FamilyName, TypeName, category).
Проблема: `ElementType.FamilyName` — **локализованная** строка (revitapidocs:
"This value is a localized string describing the family"). На общей сетевой базе
эталон, импортированный из EN-Revit (`"Conduit with Fittings"`), никогда не
сматчится по имени семьи в RU-Revit («Короб с фитингами») — sync пропускает типы
с `NotFoundInSource`/`FamilyNotFound`, а per-type presence/stale-точки (#187)
не срабатывают. Стабильного ordinal-эквивалента у системной семьи в Revit API
нет (системная семья — не элемент; ADR-056 решил это для категорий ordinal'ом).

Попутно (#191): атрибутный пайплайн остался name-keyed —
`FamilyDataImportService` группировал дескрипторы по `t.Name` (`GroupBy(...).First()`),
поэтому два легальных одноимённых типа разных семей переиспользовали один
descriptor Id дважды → PK violation → откат сохранения атрибутов целиком.

## Decision

### 1. family_key — per-category discriminator, вычисляемый при extraction

Новое поле `family_key` (схема **V27**, `family_types.family_key TEXT NOT NULL DEFAULT ''`,
plain ADD COLUMN — ключ не входит в UNIQUE, т.к. внутри одной локали family_name
уже различает семьи; ключ нужен для КРОСС-локальной идентичности).

Ключ вычисляется из типа элемента через discriminator API (все проверены на
revitapidocs 2021–2027, версионный гейт не нужен; enum-имена culture-invariant
по определению CLR):

| Категория | Класс типа | Discriminator | Ключ |
|---|---|---|---|
| OST_Conduit | `ConduitType` | `IsWithFitting` (bool, Since 2015) | `Conduit.WithFittings` / `Conduit.WithoutFittings` |
| OST_CableTray | `CableTrayType` | `IsWithFitting` | `CableTray.WithFittings` / `CableTray.WithoutFittings` |
| OST_Walls | `WallType` | `Kind` (`WallKind`) | `Wall.Basic` / `Wall.Curtain` / `Wall.Stacked` / `Wall.Unknown` |
| OST_Stairs | `StairsType` | `ConstructionMethod` (`StairsConstructionMethod`, Since 2013) | `Stairs.Assembled` / `Stairs.CastInPlace` / `Stairs.Precast` |
| Остальные 10 категорий | — | одна системная семья на категорию | `Single` |

Обоснование `Single`: OST_Floors — foundation slabs живут в OST_StructuralFoundation
(не в реестре); трубы/воздуховоды/флексы/изоляции/провода/потолки/крыши/ограждения
имеют ровно одну системную семью на категорию — дискриминатор не нужен.
Токены константами в `SmartCon.Core` (`SystemFamilyKeys`) — Core/FamilyManager
сравнивают ключи без ссылок на Revit API (I-09). Резолвер —
`SystemFamilyKeyResolver` (SmartCon.Revit), pattern-matching по классу типа.

Fallback: категория без discriminator'а получает `Single` — задокументированная
деградация, не костыль: для multi-family категории без API-ключа (не выявлено
таких среди поддерживаемых) поведение = pre-#190 (матчинг по FamilyName).

### 2. Key-first матчинг с legacy-fallback везде

- `ISystemTypeFinder.FindTypeByName(..., familyName, familyKey)`: `familyKey`
  непустой → сравнение `SystemFamilyKeyResolver.Resolve(candidate)`; иначе
  legacy-матчинг по `FamilyName` (#183).
- Sync: `SystemTypeSyncService` вычисляет `effectiveFamilyKey` из эталонного
  типа (ground truth из документа) — целевой тип и `Duplicate()`-прототип
  матчатся по ключу; stale (batch + single) и placement используют тот же
  key-first путь.
- Presence (#187): снимок проекта регистрирует ОБЕ формы токена
  ((FamilyKey, Name) и (FamilyName, Name)), каждую с префиксом
  `CategoryOrdinal` — токен `Single` одинаков у всех односемейных
  категорий, без категории снимок cross-match'ил бы одноимённые типы
  разных категорий (найдено adversarial review). V27-строки матчатся по
  ключу, legacy-строки (pre-V27, `family_key = ''`) — по имени. Двойная
  регистрация безопасна: это lookup-структура, не хранилище.
- Identity-ключ — единственное определение в Core:
  `SystemTypeIdentityKey.Build(familyKey, familyName, typeName)` =
  `"TOKEN|NAME"` (upper-invariant). Используется stale-детектором,
  репозиторием типов (карта результатов UPSERT), атрибутным пайплайном и
  деревом — все слои строят ОДИН ключ для ОДНОГО дескриптора.

### 3. #191: атрибутный пайплайн по (family, name)

- `FamilyExtractionTypeValues` += `FamilyName`/`FamilyKey` (заполняется
  `SnapshotExtractionMapper` из `SystemTypeSnapshot`).
- `FamilyDataImportService.SaveExtractionResultAsync`: переиспользование
  дескрипторов и привязка `extracted_attribute_values` — по
  `SystemTypeIdentityKey`, а не по имени.
- `IFamilyTypeRepository.SyncTypesAsync`: возвращаемая карта `{identityKey → typeId}`
  (для loadable/legacy без токена ключ деградирует к `"|NAME"` — контракт
  задокументирован в интерфейсе).
- `HashFormatActualizationTask.TrimToCatalogTypeNamesAsync`: trim staged-типов
  по идентичности (имя + согласованные токены; legacy-строки без обоих полей
  матчатся по имени) — иначе FHV3-пересчёт схлопывал одноимённые типы.

### 4. Переходный период (legacy-строки)

Строки `family_key = ''` (pre-V27) продолжают матчиться по `family_name` —
поведение идентично pre-#190. Backfill ключей происходит естественно при
реимпорте категории (extraction всегда вычисляет ключ); массовая актуализация —
отдельная задача #189 (вне скоупа, решение владельца).

## Consequences

- EN/RU-команды на общей базе: sync/stale/presence работают без деградации.
- Content hash (FHV3) НЕ тронут: FamilyKey, как и FamilyName, sync-only
  (вход в хэш — кандидат FHV4, Фаза 4 мастер-плана).
- UPSERT `family_types` при конфликте обновляет `family_key`, только если
  новое значение непустое (CASE-гвард) — legacy producer не затирает ключ.
- Юнит-тесты: миграция V27, roundtrip ключа, #191-коллапс атрибутов.
  Интеграционные: `SystemFamilyKeyTests` (resolver на реальных
  Conduit/Wall/Stairs типах, key-first finder).
