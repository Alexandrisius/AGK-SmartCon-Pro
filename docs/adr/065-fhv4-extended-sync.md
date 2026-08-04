# ADR-065: FHV4 — хэш идентичности системных типов (подтипы, структура ограждений, сегменты) + расширенный sync (Issue #184, #179)

**Date:** 2026-08-03
**Status:** accepted
**Related:** Issue #104, #179, #184, #190, ADR-056 (FHV3), ADR-061 (sync), ADR-064 (family_key), ADR-054 (actualization)

## Context

FHV3 (ADR-056) хэширует: параметры типа, compound structure (слои: function/
width/material/variable + shell counts), routing preferences. Снапшот при этом
СОБИРАЕТ, но каноническая строка НЕ включает: `StructuralMaterialIndex`,
`EndCap`, `OpeningWrapping`, `LayerCapFlag`, `ParticipatesInWrapping`
(#179 — изменение этих настроек в эталоне не детектируется). Лестничные
подтипы (#184) и структура ограждений не покрыты ни хэшем, ни sync.
Таблицы размеров сегментов синхронизируются (#104), но не хэшируются.

## Матрица уникальных настроек категорий (исследование 2026-08-03, revitapidocs 2021–2027)

| Категория | Настройка | API | Версии | FHV4 (хэш) | Sync |
|---|---|---|---|---|---|
| OST_Stairs | RunType/LandingType/LeftSideSupportType/RightSideSupportType/MiddleSupportType | `StairsType.*` ElementId get/set | 2013+ | **имена подтипов** | **да** (вариант Б: по имени в той же транзакции, параметры подтипа тоже) |
| OST_Stairs | CutMarkType | параметр `STAIRSTYPE_CUTMARK_TYPE` (ElementId) | 2013+ | **имя** | **да** (параметр) |
| OST_PipeCurves/OST_DuctCurves/Conduit/CableTray | RoutingPreferenceManager | уже в FHV3 | — | уже | уже (#104) |
| OST_PipeCurves/OST_DuctCurves | Таблицы размеров сегментов | `Segment.GetSizes()` / `MEPSize` | все | **да** (имя, материал, шероховатость, все строки размеров) | уже (#104, live) |
| Walls/Floors/Roofs/Ceilings | StructuralMaterialIndex, EndCap, OpeningWrapping, LayerCapFlag, ParticipatesInWrapping | `CompoundStructure` | все | **да** (уже в снапшоте) | уже (#104) |
| OST_StairsRailing | RailStructure (имя/высота/offset/профиль/материал каждой направляющей), TopRail, Primary/SecondaryHandrail (тип/высота/offset/позиция) | `NonContinuousRailStructure`, `RailingType.*`. **Факты из компиляции:** `PrimaryHandrailHeight/LateralOffset` (и Secondary) на RailingType — READ-ONLY (следуют за типом поручня); handrail/top-rail типы (`HandRailType`/`TopRailType`) синхронизируются как referenced subtypes (со своими параметрами), на RailingType назначаются только reference + `HandRailPosition`. Support-типы лестниц не имеют класса API и живут в **`OST_StairsStringerCarriage`** (НЕ `OST_StairsSupports` — по ней не находится ни один элемент; RevitLookup/Autodesk forums, подтверждено ручным тестом 2026-08-04) | все | **да** (summary) | **да** (новая подсистема) |
| OST_StairsRailing | BalusterPlacement | `BalusterPattern`/`PostPattern`. **Факт:** `BalusterPattern.Length` read-only (вычисляется из списка балясин) — в хэше участвует как identity-сводка, в sync не пишется | все | **да** (scalars + имена балясин) | частично: scalars (justification/break/end space/excess spacing) + per-tread; per-baluster ElementId-назначения — gap (пересоздание паттерна — отдельная фича) |
| OST_Walls | Vertically compound walls | **не читается API** | — | gap (документирован) | gap (документирован) |
| OST_Floors | Deck-профиль | нет стабильного публичного API | — | gap (документирован) | gap (документирован) |
| Все | Свойства материалов (appearance/физика) | Material/PropertySetElement | все | gap (слишком глубоко для хэша; изменение материала детектируется по имени в слое/правиле) | уже (#104) |
| Все | Rename-invariance типа (#179, проблема 1) | — | — | **отложено**: имя типа остаётся в канонической строке — имя является идентичностью типа в каталоге (типы keyed by name во всех слоях: presence/stale/sync). Сортировка по UniqueId ломалась бы при рестейджинге мини-проекта (UniqueId меняются). | — |
| Все | Идентичность семьи | `SystemFamilyKeys` (ADR-064) | все | **да** (FamilyKey в канонической строке) | уже (#190) |
| OST_Wire | Материал/Температурный рейтинг/Изоляция/Макс. размер/Кабелепровод + нейтраль | **`WireType.*` — API-свойства поверх графа `ElectricalSetting` (WireMaterialType→TemperatureRatingType→InsulationType/WireSize, WireConduitType), НЕ параметры элемента** — generic-пайплайн их не видит (ручной тест 2026-08-04: смена материала не синхронизировалась). `WireMaterial` и др. — get/set в ≤2025; **Revit 2026 заменил граф на Conductor*-элементы** (сигнатуры изменились → MissingMethodException под 2025-бинарями; per-member try/catch → Warn + NotConverged) | ≤2025 | **да** (FHV5, секция WIRE) | **да** (find-by-name по графу target; материал create-from-base; остальное — только find, иначе Warn + NotConverged) |

## Decision

### 1. FHV4 — каноническая строка

`FamilyContentHashFormat.CurrentVersion = 4`. Формат:
`FHV4|SYSTEM|{catId}|TYPES|{name}|{params}|FAMKEY|{familyKey}|STRUCT|{...extras}|ROUTING|{...}|SEGMENTS|{...}|SUBTYPES|{...}|RAILING|{...}`

- `FAMKEY` — `SystemFamilyKeys`-токен (#190).
- `STRUCT` — + `StructuralMaterialIndex`, `EndCap`, `OpeningWrapping`; слой += `LayerCapFlag`, `ParticipatesInWrapping` (поля уже собираются в снапшот — только включаются в строку).
- `SEGMENTS` — для MEPCurve-типов: все сегменты из routing-правил (группа Segments): имя, материал, шероховатость, каждая строка размеров (ND/ID/OD/UsedInSizeLists/UsedInSizing).
- `SUBTYPES` — для лестниц: имена RunType/LandingType/Supports(L/R/Middle)/CutMarkType.
- `RAILING` — для ограждений: TopRail/Handrails (имена типов + высоты/offsets/позиции), все направляющие (имя/высота/offset/имя профиля/имя материала), baluster summary.
- Отсутствующие секции — маркер `-` (как STRUCT/ROUTING в FHV3).

### 2. Снапшот остаётся identity-level; sync читает живьём (ADR-061)

Новые данные в снапшоте — только identity-summary для хэша. Sync по-прежнему
читает эталон живьём из мини-проекта: подтипы лестницы — через
`StairsType.RunType` и т.д. source-элемента; структура ограждения — через
`RailingType` source-элемента. Дублирования глубоких данных в БД нет.

### 3. Актуализация (ADR-054)

`hash_format_version` 4: задача актуализации пересчитывает эталонные хэши
существующих баз (extractor читает новые поля из staged `.rvt` — они там есть,
т.к. staging копирует типы целиком). Детект изменений v3→v4 корректен: эталон
пересчитывается ДО первой проверки, ложного массового stale нет. Маркер
`min_plugin_version` — по конвенции ADR-058 (bump+backfill).

### 4. Sync подтипов лестниц (#184, вариант Б)

В той же транзакции после записи параметров основного типа: для каждой
ссылки (Run/Landing/Support L/R/Middle/CutMark) — найти подтип в эталоне,
найти-или-создать в проекте по (класс, имя) (Duplicate прототипа того же
класса; нет прототипа → Warn + skip — семья подтипов отсутствует в проекте),
записать параметры подтипа (тот же WriteParameters), назначить ElementId
основному типу.

### 5. Sync структуры ограждений

Новая подсистема в sync: TopRail/Handrails (тип по имени + скаляры),
RailStructure (очистить и пересоздать направляющие по эталону; профиль/
материал — по имени), BalusterPlacement (scalars + per-tread + имена
балясин best effort). Настройки без сеттера — Warn + NotConverged (существующий
канал отчётности), не молчаливый пропуск.

## Revision 2026-08-04 (ручной тест, раунд 2): FHV5 — WIRE

Ручной тест показал, что настройки провода (Материал и др.) не попадают ни в
`Element.Parameters`, ни в хэш, ни в sync — это API-свойства `WireType` поверх
объектного графа `ElectricalSetting`. Формат хэша поднят до **FHV5**
(`FHV5|SYSTEM|...|WIRE|{material}|{rating}|{insulation}|{maxSize}|{conduit}|{neutralMult}|{neutralReq}`),
`FamilyContentHashFormat.CurrentVersion = 5`, задача актуализации — `hash-v5`
(детект `NOT IN (5,-1,-2)`; FHV4 не выходил в публичных релизах, поэтому задача
переименована, а не добавлена вторая). Sync: `SystemTypeSyncService.SyncWireSettings`
— материал find-or-create (`ElectricalSetting.AddWireMaterialType(name, base)`),
rating/insulation/max-size резолвятся по имени ПОД уже назначенным материалом/
рейтингом (ownership chain по revitapidocs), conduit — из
`ElectricalSetting.WireConduitTypes`; недоступное — Warn + NotConverged.
Revit 2026: граф заменён на Conductor*-модель — до отдельного порта под новую
модель wire-секция там деградирует в Warn + NotConverged (per-member try/catch).

Дополнительно раунд 2:
- Активация sketch-категорий: PostCommand теперь откладывается в one-shot
  `Idling`-handler (IDropHandler.Execute — не командный контекст; команда,
  запощенная из дропа, Revit не запускает), а категория берётся из самого
  синхронизированного элемента, а не из каталожной строки (каталожный
  `RevitCategoryId` может быть null → молчаливый откат на no-op PostRequest).
- Sync подтипов лестниц покрыт read-back логированием (диагностика «не
  синхронизируется» при чистом прогоне).

## Consequences

- Изменение подтипа лестницы/структуры ограждения/таблицы размеров/
  STRUCT-флагов в эталоне теперь детектируется (хэш) И применяется (sync).
- Legacy-базы: FHV3-хэши инвалидируются один раз через актуализацию
  (пересчёт эталона в FHV4), не через ложный stale у пользователей.
- Gaps (vertically compound, deck, материалы в хэше, rename-invariance) —
  задокументированы в матрице выше, без костылей.
