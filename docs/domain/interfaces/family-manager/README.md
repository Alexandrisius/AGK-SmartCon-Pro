---
module: family-manager-interfaces-index
---
# Интерфейсы FamilyManager — индекс

> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.
> Модуль разбит на тематические файлы (правило разбиения: см. `docs/domain/README.md`).

## Файлы по темам

| Файл | Что внутри |
|---|---|
| [`catalog.md`](catalog.md) | Провайдеры каталога (`IFamilyCatalogProvider`, `IWritableFamilyCatalogProvider`), поиск и метаданные (`IFamilySearchService`, `IFamilyMetadataExtractionService`, `IFamilyDataExtractionService`, `IFamilyFinder`) |
| [`import.md`](import.md) | Импорт (`IFamilyImportService`, `IFamilyDataImportService`, `IFamilyTypeCatalogBaker`), precompute (`IFamilyImportPrecomputer`, `IFamilyVersionWriter`), batch staging (Issue #127) |
| [`documents-assets.md`](documents-assets.md) | Документы и файлы: `IActiveDocumentClassifier`, `IFamilyFileResolver`, `IFamilyAssetService`, `IAvatarCropService`, `IActiveDocumentChangeNotifier` |
| [`load-placement.md`](load-placement.md) | Загрузка и размещение: `IFamilyLoadService`, `ISharedNestedFamilyRepository`, `IFamilyPlacementService`, `IFamilyPlacementDragService` |
| [`database.md`](database.md) | База данных: `IDatabaseManager`, `IRegistryMigrator`, `IProjectBaseBindingEvaluator`, `IProjectBaseActivator` |
| [`ui-dialogs.md`](ui-dialogs.md) | `IFamilyManagerDialogService`, `IFamilyManagerAwaitableEvent` |
| [`attributes.md`](attributes.md) | Атрибуты и репозитории: `IAttributeDefinitionRepository`, `ICategoryRepository`, `IAttributePresetService`, `IFamilyTypeRepository`, `IFamilyFactRepository` |
| [`stale-detection.md`](stale-detection.md) | Stale Detection v2: `IFamilyVersionStore`, `IStaleDetector`, `IStaleFamilyUpdater`, `IStaleCategoryAggregator` |
| [`extraction.md`](extraction.md) | Снапшоты, хеширование, геометрия: `IFamilySnapshotExtractor`, `IFamilyContentHasher`, `IContentHashDedupService`, `IFamilyGeometryExtractor`, `IGlbWriter`, `IFamilyGeometryPipeline` |
| [`actualization.md`](actualization.md) | Актуализация БД (ADR-054): `ICatalogActualizationService`, `IDatabaseActualizationTask`, `IFamilyMigrationExtractor`, `IDatabaseUpdateStateService` |
| [`validation.md`](validation.md) | Import Validation Gate (ADR-059): `IValidationRuleRepository`, `IFamilyValidationEngine`, `IFamilyHealthChecker`, `IFamilyImportValidationService`, `ICategoryChangeGateService` |
| [`dependencies.md`](dependencies.md) | Зависимости parent→child (ADR-066, V29): `IFamilyDependencyRepository`, `IFamilyDependencyCollector` |

## См. также

- [`../family-manager-rbac.md`](../family-manager-rbac.md) — RBAC интерфейсы
- [`../family-manager-loadable.md`](../family-manager-loadable.md) — Loadable Family Import (Phase 22)
- [`../family-manager-system.md`](../family-manager-system.md) — System Families Import
- [`../../models/family-manager/`](../../models/family-manager/README.md) — модели модуля
