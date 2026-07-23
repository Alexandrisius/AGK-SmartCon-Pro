# FamilyManager

> **Status:** Active | Модуль управления библиотекой семейств Revit (dockable panel + SQLite catalog).
>
> SSOT: `src/SmartCon.FamilyManager/` (интерфейсы и модели — в `src/SmartCon.Core/`).

## Структура модуля

```
docs/family-manager/
├── README.md                       # этот файл
├── 00-strategy/
│   └── 00-familymanager-concept-roadmap.md   # продуктовая стратегия
│   └── 01-familymanager-technical-stack.md     # технический стек
└── 02-plans/
    └── phase-24-stale-detection-v2.md          # детальный план Phase 24
```

## Зафиксированные решения

- Основной target: Revit 2025+ / `net8.0-windows`.
- Legacy target: Revit 2019–2024 / `net48`.
- Модуль: `SmartCon.FamilyManager`.
- `SmartCon.FamilyManager` зависит только от `SmartCon.Core` и `SmartCon.UI`.
- Revit API вызывается только через `SmartCon.Revit` (интерфейсы Core, реализации Revit).
- Хранилище: SQLite + managed `.rfa` storage.
- Локальный root: `%APPDATA%\SmartCon\FamilyManager\`.
- База: `%APPDATA%\SmartCon\FamilyManager\databases\{id}\catalog.db` (multi-DB pattern).
- MVP provider: `LocalCatalogProvider`.
- Future providers: `RemoteCatalogProvider`, `CorporateCatalogProvider`, `PublicReadOnlyProvider`, `CompositeCatalogProvider`.

## Жёсткий запрет

FamilyManager не хранит каталог, `.rfa`, metadata, теги, preview, search index, usage history или избранное в ExtensibleStorage.

ExtensibleStorage остаётся паттерном существующих модулей smartCon, но не является data plane FamilyManager.

### Исключение: `SmartCon_FamilyVersion_v1` (Phase 24)

**Единственное исключение** — маркер версии на `Family` элементе в проекте Revit, введённый в Phase 24 (см. [ADR-030](../adr/030-phase-24-stale-detection-v2.md)). Хранит **только** метаданные момента загрузки (`CatalogItemId`, `VersionLabel`, `LoadedAtUtc`, `SourceRevitVersion`). Не пишется в `.rfa`.

**Что остаётся запрещено:**
- Каталог (`catalog_items`, `catalog_versions`, `family_files`, `family_assets`) — в SQLite
- Метаданные (manufacturer, tags, description, preview) — в SQLite/managed storage
- История загрузок, избранное — в SQLite
- Любые новые ES Schema для FamilyManager (кроме `SmartCon_FamilyVersion_v1`)

## Статус по фазам

| Phase | Название | ADR | Завершена | Примечание |
|---|---|---|---|---|
| 12 | FamilyManager MVP | [ADR-014](../adr/014-familymanager-mvp-architecture.md) | 2026-04-28 | Superseded by ADR-015 |
| 13 | Published Storage | [ADR-015](../adr/015-familymanager-published-storage.md), [ADR-016](../adr/016-familymanager-readonly-files.md) | 2026-05-01 | schema v2 |
| 14 | Attribute Extraction | [ADR-017](../adr/017-familymanager-attribute-extraction.md) | 2026-05-06 | schema v6 |
| 18 | Refactoring | [ADR-018](../adr/018-familymanager-refactoring.md) | 2026-05-07 | DI, async safety, perf |
| 20 | RBAC | [ADR-022](../adr/022-familymanager-rbac.md) | 2026-05-16 | schema v7 |
| 21 | Active Import Refactor | [ADR-024](../adr/024-active-family-import-preparer.md) | 2026-06-05 | sidecar preservation |
| 22 | Placed Families v2 | [ADR-027](../adr/027-placed-families-v2.md) | 2026-06-07 | system + loadable families |
| 24 | Stale Detection v2 | [ADR-030](../adr/030-phase-24-stale-detection-v2.md) | 2026-06-18 | schema v12, breaking 2.0.0 |
| 25 | Type Catalog Simulation | [ADR-032](../adr/032-type-catalog-simulation.md) | 2026-06-21 | **Superseded by ADR-033** |
| 26 | Type Catalog Bake-in | [ADR-033](../adr/033-bakein-type-catalog.md) | 2026-06-22 | replaces simulation |
| 27 | v2.0.0 Cleanup | [ADR-034](../adr/034-shared-nested-persist-fallback.md), [ADR-035](../adr/035-remove-temp-logic-v2.md), [ADR-036](../adr/036-active-family-type-sync.md), [ADR-037](../adr/037-tree-expand-collapse.md), [ADR-038](../adr/038-sticky-category-headers.md), [ADR-039](../adr/039-snapshot-driven-commit.md) | 2026-06-29 | schema v14 |
| 28 | Active Version Management | [ADR-040](../adr/040-overwritecurrent-semantics.md), [ADR-041](../adr/041-active-version-management.md) | 2026-06-30 | schema v17-v19 |
| 29 | 3D Preview | [ADR-042](../adr/042-familymanager-3d-preview.md) | 2026-07-01 | SharpGLTF + HelixToolkit |
| 30 | Avatar Crop | [ADR-047](../adr/047-avatar-crop-derived-file.md) | 2026-07-16 | Issue #131: диалог кадрирования, производный `avatar.png` 560×420, единое превью 280×210 для свойств и tooltip, инвалидация при смене primary |
| 32 | Content Hash v2 + Migration | [ADR-049](../adr/049-content-hash-v2-rename-invariant.md), [ADR-050](../adr/050-hash-recalculation-migration.md) | 2026-07-17 | Issue #126: rename-invariant дедуп (hash-first), cross-name ⚠, миграция хэшей v1→v2 с прогресс-диалогом, breaking 3.0.0 (без DDL-миграции) |
| 33 | Content Hash v3 | [ADR-056](../adr/056-content-hash-v3.md) | 2026-07-23 | Issue #159: FHV3 — PartType/факты, коннекторы, behavior-флаги, bbox+surface геометрия, CompoundStructure, RoutingPreferences, локале-инвариантная категория (ordinal), экранирование; критическая задача `hash-v3` заменила `hash-v2` |

## Миграции SQLite V1..V23

| V | Изменение | Связанный ADR |
|---|---|---|
| 1 | Initial schema: 8 таблиц (`database_meta`, `schema_info`, `catalog_items`, `catalog_versions`, `family_files`, `family_assets`, `catalog_tags`, `project_usage`) | ADR-014 |
| 2 | Published Storage: multi-DB, version → Revit-version | ADR-015 |
| 3 | `category_id` column в `catalog_items` | ADR-015 |
| 4 | `family_types` table + indexes | ADR-017 |
| 5 | `is_primary` column в `family_types` | ADR-017 |
| 6 | `version_id` columns в `family_types` | ADR-017 |
| 7 | RBAC: `db_users` table, `owner_identity` в `database_meta` | ADR-022 |
| 8 | Recreate `extracted_attribute_values` (NOT NULL constraint) | ADR-017 |
| 9 | `loaded_version_label` в `project_usage` | ADR-024 |
| 10 | Index на `family_types(type_name)` | ADR-023 |
| 11 | System families: `family_source`, `revit_category`, `type_unique_id` | ADR-027 |
| 12 | DROP `project_usage` table + index | ADR-030 |
| 13 | `family_nested_shared_families` table | ADR-034 |
| 14 | DROP `sha256`/`size_bytes` columns | ADR-035 |
| 15 | FK (`type_id`) ON DELETE CASCADE на `extracted_attribute_values` | ADR-036 |
| 16 | `content_hash` / `hash_format_version` columns | ADR-039 |
| 17 | FK (`version_id`) ON DELETE CASCADE на `family_types` и `extracted_attribute_values` | ADR-041 |
| 18 | UNIQUE (`catalog_item_id`, `version_id`, `type_name`) для per-version types | ADR-041 |
| 19 | `published_by` column в `catalog_versions` | ADR-041 |
| 20 | `base_type` column в `database_meta` (General=0 default) | ADR-045 |
| 21 | `project_binding_json` column в `database_meta` (binding переживает reconnect) | #119 |
| 22 | `revit_category_id` в `catalog_items` + таблица `family_facts` (family-facts подсистема) | ADR-055 |
| 23 | `glb_state` в `catalog_versions` — терминальный маркер «нет 3D-геометрии» для glb-v1 | #157 |

## Ключевые интерфейсы и модели

- Доменные модели: `docs/domain/models/family-manager.md`
- Интерфейсы: `docs/domain/interfaces/family-manager.md`
- Глоссарий: `docs/domain/glossary.md`

## Связанные документы

- [ADR-014](../adr/014-familymanager-mvp-architecture.md) — MVP Architecture (superseded by ADR-015)
- [ADR-015](../adr/015-familymanager-published-storage.md) — Published Storage
- [ADR-030](../adr/030-phase-24-stale-detection-v2.md) — Stale Detection v2 + ES exception
- [ADR-033](../adr/033-bakein-type-catalog.md) — Type Catalog Bake-in
- [ADR-041](../adr/041-active-version-management.md) — Active Version Management
- [docs/domain/models/family-manager.md](../domain/models/family-manager.md)
- [docs/domain/interfaces/family-manager.md](../domain/interfaces/family-manager.md)
