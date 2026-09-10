# ADR-056: Content Hash v3 — PartType, коннекторы, behavior-флаги, CompoundStructure, RoutingPreferences, локале-инвариантная категория

**Date:** 2026-07-23
**Status:** accepted
**Related:** Issue #159, ADR-049 (hash v2), ADR-054 (движок актуализации — задача `hash-v3`), ADR-055 (family facts — теперь В хэше), ADR-039 (snapshot-driven commit), I-09 (Core без runtime-Revit)

## Context

FHV2 (ADR-049) покрывает параметры, типы и базовую геометрию, но пропускает класс реальных
изменений семейства — **ложные дубликаты** при дедупликации:

### Loadable (.rfa)
1. **PartType** (`FAMILY_CONTENT_PART_TYPE`): «Отвод» → «Тройник» не меняет хэш.
   ADR-055 сознательно исключил факты из хэша («косметические») — продуктовая переоценка:
   для фитингов Part Type **определяет** функцию семейства, это контент.
2. **Коннекторы полностью вне хэша**: domain, профиль, размеры (W/H/Radius),
   `SystemClassification` (ХВС→ГВС!), позиции, linked-связи. Коннектор не является ни
   параметром, ни `GenericForm` — оба существующих контура его не видят.
3. **Behavior-флаги** (`FAMILY_SHARED`, `FAMILY_WORK_PLANE_BASED`, `FAMILY_ALWAYS_VERTICAL`,
   `FAMILY_ALLOW_CUT_WITH_VOIDS`) — параметры на `OwnerFamily`, не входят в
   `FamilyManager.GetParameters()`.
4. **Категория = display name** — локалезависима: одно семейство в RU и EN Revit даёт
   **разные** хэши → дубли в смешанных командах.
5. **Геометрия**: сдвиг формы с сохранением объёма и числа граней пропускается
   (нет BoundingBox/SurfaceArea); 2D — только счётчики; non-shared nested не входят.
6. Известные ограничения ADR-049 (разделитель `|` не экранируется; blank-маркеры
   `INVALID`/`UNSUPPORTED`/`READERROR` коллидируют с пользовательскими строками) —
   отложенные «до v3», наступило.

### System (14 категорий `SystemCategoryRegistry`)
7. **CompoundStructure** (слои стен/перекрытий/крыш/потолков: функция, толщина, материал)
   — не параметр, вне хэша. Замена материала слоя с той же суммарной толщиной невидима.
8. **RoutingPreferences** (`MEPCurveType.RoutingPreferenceManager` — PipeType, DuctType,
   CableTrayType, ConduitType; подтверждено revitapidocs 2025: менеджер наследуется
   всеми четырьмя): правила трассировки (Segments/Elbows/Junctions/…) + PreferredJunctionType
   — не параметры, полностью вне хэша.
9. **CategoryName** в system-каноне — та же локалезависимость.

## Decision

### 1. Формат FHV3

```
Loadable: FHV3|LOADABLE|{categoryOrdinal}|PARAMS|…|TYPES|…|GEOM|…|GEOM2D|…|NESTED|…|FACTS|…|FLAGS|…|CONN|…
System:   FHV3|SYSTEM|{categoryId}|TYPES|…|STRUCT|…|ROUTING|…
```

`FamilyContentHashFormat.CurrentVersion = 3`.

### 2. Локале-инвариантная категория

В обеих канонических строках display name категории заменён на **ordinal**
(`BuiltInCategory` как int — вечная константа, см. ADR-055 §1). Loadable: ordinal из
`FamilySnapshot.CategoryId` (fallback на display name при `null`). System: `CategoryName`
из канона убран, остаётся `CategoryId`. Результат: одно семейство даёт одинаковый хэш
в RU и EN Revit.

### 3. Loadable — новые секции

| Секция | Содержимое | Источник |
|---|---|---|
| `FACTS` | `factKey\|valueKey` для всех фактов из `FamilySnapshot.Facts` (Part Type первым), sorted by key | Реестр ADR-055, уже извлекается |
| `FLAGS` | 4 behavior-флага с `OwnerFamily`: Shared, WorkPlaneBased, AlwaysVertical, CutWithVoids (`1`/`0`, absent → `-`) | `get_Parameter(BuiltInParameter.*)` на Family |
| `CONN` | Per `ConnectorElement`: Domain(ord), Shape(ord), SystemClassification(ord), IsPrimary, W/H/Radius (`0.######`), Origin XYZ (**округление 1e-4 ft**), LinkedIndex (индекс linked-коннектора в отсортированном списке, `-` если нет). Сортировка: (Domain, Shape, SystemClassification, Origin) | `FilteredElementCollector(familyDoc).OfCategory(OST_ConnectorElem)` |
| `GEOM` (расш.) | + BoundingBox Min/Max (`0.####`), + SurfaceArea (`0.######`) per form | тот же `Solid` в `ExtractFormMetrics` — дёшево |
| `GEOM2D` (расш.) | + суммарные длины Model/Detail/Symbolic кривых | `Curve.Length` |
| `NESTED` (расш.) | + имена **non-shared** nested семейств (отдельным подсписком после shared) | `Family` элементы с `GetFamilySymbolIds` |

### 4. System — новые секции (per type)

| Секция | Содержимое | Источник |
|---|---|---|
| `STRUCT` | Per layer: Function(ord), Width (`0.######`), resolved имя материала, Variable(1/0); + число shell-слоёв exterior/interior. Только когда тип — `HostObjAttributes` с не-null CompoundStructure | `HostObjAttributes.GetCompoundStructure()` (Wall/Floor/Roof/Ceiling) |
| `ROUTING` | PreferredJunctionType(ord); per rule group (порядок enum): per rule: resolved имя MEPPart, Description, per criterion (type name + value). Только когда тип — `MEPCurveType` | `MEPCurveType.RoutingPreferenceManager` |

Resolved-имена (материалов, фитингов, сегментов) — тот же паттерн, что и для ElementId
параметров (ADR-039): имя стабильно между документами, id — нет.

### 5. Экранирование и blank-маркеры

- Все строковые значения в каноне экранируются: `%` → `%25`, затем `|` → `%7C`
  (имена параметров/типов/значений, resolved-имена, Description).
- Blank-маркеры экстрактора получают непечатный префикс `\u0001`
  (`\u0001INVALID` и т.д.) — коллизия с пользовательской строкой «INVALID» устранена.

### 6. Миграция: задача `hash-v3` (critical), заменяет `hash-v2`

- `HashFormatActualizationTask` переписан под v3: `Id = "hash-v3"`, `Order = 10`,
  `IsCritical = true`. Детект: `hash_format_version IS NULL OR NOT IN (3, -1, -2)`
  — покрывает строки v1/v2.
- **`RunFileFreePassAsync` убран** (возвращает 0): system-канон изменился структурно,
  дешёвый re-flag невозможен — system-группы пересчитываются из managed `.rvt`
  открытием файла, как loadable. `ExtractSystemCategoryAsync` расширен извлечением
  STRUCT/ROUTING из мини-проекта (типы скопированы туда с зависимостями — см. Risks).
- Терминальные маркеры `-1`/`-2` и их семантика сохранены (детект их исключает).
- Apply: хэш пишется во все Revit-варианты label; ресинк `catalog_items.content_hash`
  только на active label — как в v2.
- SQL-фильтр дедупа `hash_format_version = @fmt` (ADR-049 §Consequences) автоматически
  изолирует старые v2-строки от v3-поиска: ложных cross-version дублей нет,
  cross-name дедуп для них включается после миграции.

### 7. Тесты

Hasher: смена категории (ordinal), PartType, размер/система/Origin коннектора, flags,
bbox при равном объёме, surface area, слой (материал/толщина/порядок), routing rule
(имя/порядок/критерий), локале-инвариантность (name вне канона), экранирование `|`,
коллизия blank-маркера со строкой «INVALID», пустые списки детерминированы.
Миграция: детект v2→v3, apply гасит детект (re-count = 0), маркеры -1/-2, ресинк active,
system-группа проходит файловый путь.

## Отклонённые альтернативы

- **StackedWall members в STRUCT** — отложено: редкий кейс, реестр членов стены требует
  отдельного resolved-name контура; добавится записью в v4 без переделки остального.
- **Origin коннектора без округления / исключён** — отклонено: позиции, управляемые
  параметрами, и так в хэше через значения; Origin ловит сдвиги «руками» по геометрии.
  Округление 1e-4 ft (~0.03 мм) — компромисс с дрейфом при Revit upgrade
  (ложный `Existing` вместо `Duplicate` — безопасно, ADR-049 §Consequences).
- **Critical=false для `hash-v3`** — отклонено по таблице ADR-054: запись в старую базу
  с v2-хэшами плодит дубли, невидимые v3-дедупу (SQL-фильтр по версии).
- **Display name И ordinal в каноне** — отклонено: display name не добавляет энтропии
  (ordinal строго сильнее) и ломает кросс-локаль.

## Risks

1. **Мини-проект и routing-зависимости**: `CreateCleanProjectWithTypesAndInstances`
   копирует типы MEPCurve в isolated `.rvt`. Routing-правила ссылаются на фитинги/сегменты;
   если зависимости не копируются, resolved-имена при миграции станут INVALID и хэш
   миграции разойдётся с хэшем импорта из проекта. При реализации — проверить и при
   необходимости расширить набор копируемых элементов (все `MEPPartId` из правил +
   материалы слоёв CompoundStructure).
2. **Скорость миграции**: system-строки теряют мгновенный re-flag — разовый прогон
   с прогрессом; для больших баз минуты. Принято: честность важнее.
3. **Перфоманс bbox/surface**: вычисляются в той же сессии, что и Volume/Faces
   (`Solid` уже получен) — маржинальная стоимость.

## Consequences

- Ложные дубликаты классов 1–9 устранены; кросс-локальный дедуп работает.
- Цена миграции — один прогон «Обновить базу» (critical гейт — как v2).
- Канон v3 заморожен после миграции; новые классы контента — v4 тем же паттерном
  (`docs/architecture/database-migrations.md`).
