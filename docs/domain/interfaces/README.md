---
module: interfaces-index
---
# Интерфейсы и контракты

> **Загружать:** при реализации или вызове сервисов.
> **Правило:** Интерфейсы объявлены в `SmartCon.Core/Services/Interfaces/`. Реализации — в `SmartCon.Revit/` или `SmartCon.Core/Services/Implementation/`.

## Файлы по модулям

| Файл | Что внутри | Источник в коде |
|---|---|---|
| [`pipeconnect.md`](pipeconnect.md) | IRevitContext, ITransactionService, ITransformService, IFittingMapper, IAlignmentService, IFittingChainResolver | `SmartCon.Core/Services/Interfaces/` (root) |
| [`project-management.md`](project-management.md) | IShareProjectService, IModelPurgeService, IFileNameParser, IViewRepository | `SmartCon.Core/Services/Interfaces/` (root) |
| [`family-manager.md`](family-manager.md) | IFamilyCatalogProvider, IFamilyImportService, IFamilyLoadService, IFamilyAssetService, IAttributeDefinitionRepository | `SmartCon.Core/Services/Interfaces/` (root) |
| [`family-manager-rbac.md`](family-manager-rbac.md) | IDbUserRepository, IDbAccessControlService, IUserIdentityService | `SmartCon.Core/Services/Interfaces/` (root) |
| [`family-manager-loadable.md`](family-manager-loadable.md) | ILoadableFamilyScanner, ILoadableFamilyTypeResolver, ILoadableFamilyImportOrchestrator (Phase 22) | `SmartCon.Core/Services/Interfaces/` (root) |
| [`family-manager-system.md`](family-manager-system.md) | ISystemFamilyRevitOperations, ISystemFamilyIsolationProjectService, ISystemFamilyAttributeExtractor | `SmartCon.Core/Services/Interfaces/` (root) |
| [`drag-drop-contracts.md`](drag-drop-contracts.md) | IDragInfo, IDropInfo (минимальные контракты для WPF drag-drop) | `SmartCon.Core/Services/Interfaces/` (root) |
| [`ui-contracts.md`](ui-contracts.md) | IObservableRequestClose, ICloseAwareViewModel, ISaveableViewModel | `SmartCon.Core/Services/Interfaces/` (root) |
| [`cross-cutting.md`](cross-cutting.md) | IClock, IIdGenerator, IDispatcher, ILocalCatalogMigrator, Guard, JsonOptions, SqliteConnectionExtensions, ITypeCatalogValueApplier, StorageTypeCode | `SmartCon.Core/Services/Interfaces/` (root) + `Core/Compatibility/`, `Core/Data/` |
