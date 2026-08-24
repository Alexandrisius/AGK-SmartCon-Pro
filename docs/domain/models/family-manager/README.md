---
module: family-manager-index
---
# Модели FamilyManager — индекс

> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.
> Модуль разбит на тематические файлы (правило разбиения: см. `docs/domain/README.md`).

Все модели — immutable records. Идентификаторы — `string` (GUID), даты — `DateTimeOffset`.

## Файлы по темам

| Файл | Что внутри |
|---|---|
| [`catalog.md`](catalog.md) | Подключения и провайдеры (`DatabaseConnection`, `BaseType`, `CatalogProviderKind`), статусы контента, каталог (`FamilyCatalogItem`, `FamilyCatalogVersion`, `FamilyAsset`), запросы (`FamilyCatalogQuery`), метаданные и типы (`FamilyTypeDescriptor`, `FamilyParameterDescriptor`) |
| [`attributes.md`](attributes.md) | Атрибуты (`AttributeDefinition`, `CategoryAttributeBinding`), shared parameters, дерево категорий (`CategoryNode`, `CategoryTree`), пресеты (`AttributePreset`), извлечённые значения |
| [`import.md`](import.md) | Импорт: запросы/результаты (`FamilyImportRequest`, `FamilyImportResult`), batch-строки и статусы, источники (`FamilyImportSource`, `SystemFamilyPendingImport`, `PrecomputedImportTriple`), Batch Import UI state (Issue #127) |
| [`load-placement.md`](load-placement.md) | Загрузка в проект (`FamilyLoadOptions`, `FamilyLoadResult`), shared/nested решения (`SharedFamilyDecisionRequest`), размещение (`FamilyPlacementDragData`) |
| [`type-catalog.md`](type-catalog.md) | Type Catalog: `TypeCatalogEntry`, `TypeCatalogColumn`, `TypeCatalogParseResult`, `TypeCatalogUnitAlias`, bake-in результат |
| [`stale-detection.md`](stale-detection.md) | Stale Detection v2: `FamilyVersion`, `StaleCheckResult`, `FamilyStaleSnapshot`, `StaleSnapshotLogic`, `LoadableMarkerLogic` |
| [`content-hash.md`](content-hash.md) | Content Hash и дедупликация: `FamilyContentHash`, `ContentHashMatch`, `ContentHashDedupResult`, `PreparedFamilyItem`, `CategoryProvenance`, `FamilyContentHasher` |
| [`actualization.md`](actualization.md) | Миграции и актуализация БД (ADR-054): `DatabaseMigrationProgress`, `ActualizationVariant`, `FamilyActualizationContext`, `FamilyMigrationExtractResult` |
| [`geometry.md`](geometry.md) | Снапшоты (`FamilySnapshot`, `SystemFamilySnapshot`, `ConnectorSnapshot`, `CompoundStructureSnapshot`, `RoutingPreferencesSnapshot`), геометрия и 3D-превью (`GeometryMetrics`, `MeshData`, `FamilyGeometryPreview`) |
| [`family-facts.md`](family-facts.md) | Family Facts подсистема (ADR-055): `FamilyFact`, `FamilyFactsData`, `FamilyFactRule`, `FamilyFactRuleSet`, `PartTypeLabelMap` |
| [`validation.md`](validation.md) | Import Validation Gate (ADR-059): `ValidationRule`, `ValidationRuleOperator`, health-check (`FamilyHealthReport`), гейт-статусы (`FamilyRowGateStatus`), нормализованные входы (`FamilyValidationInput`), отчёты (`FamilyValidationReport`, `RuleViolation`), `FamilyValidationEngine`, `DisplayValueParser` |
| [`dependencies.md`](dependencies.md) | Зависимости parent→child (ADR-066, V29): `FamilyDependencyKind`, `FamilyDependencyInfo`, `FamilyDependencyDescriptor`, `FamilyDependencyLink` |
| [`assignment-rules.md`](assignment-rules.md) | Автоназначение категории (#241, ADR-070, V31): `AssignmentRuleGroup`, `AssignmentCondition`, `AssignmentOperatorPolicy`, `CategoryAutoAssignInput/Result/Preloaded`, `RevitCategoryLabel`, `CategoryProvenance.AutoRule` |

## См. также

- [`../family-manager-rbac.md`](../family-manager-rbac.md) — RBAC модели
- [`../family-manager-loadable.md`](../family-manager-loadable.md) — Loadable Family Import (Phase 22)
- [`../system-families.md`](../system-families.md) — System Families Import
- [`../../interfaces/family-manager/`](../../interfaces/family-manager/README.md) — интерфейсы модуля
