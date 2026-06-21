---
module: models-index
---
# Доменные модели

> **Загружать:** при работе с моделями данных.
> **Правило:** Не создавай новые доменные классы без добавления их в один из файлов ниже.

Все модели живут в `SmartCon.Core/Models/` и `SmartCon.Core/Math/`. Используют только типы из .NET и собственные типы Core.
Исключение: `ElementId`, `XYZ`, `Domain`, `BuiltInParameter`, `ForgeTypeId` — value-типы Revit, допустимые в Core через compile-time ссылку на API (без runtime-зависимости, I-09).

## Файлы по модулям

| Файл | Что внутри | Источник в коде |
|---|---|---|
| [`pipeconnect.md`](pipeconnect.md) | Модели флагманского модуля PipeConnect: ConnectorProxy, PipeConnectionSession, Fitting*, Chain* | `SmartCon.Core/Models/` (root) |
| [`project-management.md`](project-management.md) | Share Project, FileNameTemplate, FieldDefinition, PurgeOptions | `SmartCon.Core/Models/` (root) |
| [`family-manager.md`](family-manager.md) | Каталог семейств, атрибуты, импорт, батч-операции, метаданные | `SmartCon.Core/Models/FamilyManager/` |
| [`family-manager-rbac.md`](family-manager-rbac.md) | RBAC: DbUser, UserIdentity, DbAccessDeniedException | `SmartCon.Core/Models/FamilyManager/Rbac/` |
| [`family-manager-loadable.md`](family-manager-loadable.md) | Loadable Family Import (Phase 22): LoadableFamilyInfo, SelectedElementsAnalysis | `SmartCon.Core/Models/FamilyManager/` |
| [`system-families.md`](system-families.md) | System Families Import: CategoryAnalysis, SelectedSystemType, SystemFamilyImportResult | `SmartCon.Core/Models/FamilyManager/SystemFamilies/` |
| [`formula-engine.md`](formula-engine.md) | AST парсер формул Revit: Token, AstNode, Solver | `SmartCon.Core/Math/FormulaEngine/` |
| [`math-utilities.md`](math-utilities.md) | Vec3, ConnectorAligner, VectorUtils, BestSizeMatcher | `SmartCon.Core/Math/` |
| [`updates.md`](updates.md) | Автообновление через GitHub: PendingUpdate, SemVersion, UpdateInfo | `SmartCon.Core/Models/` (root) |
| [`cross-cutting.md`](cross-cutting.md) | CtcGuesser, ElementIdEqualityComparer, FamilyMetadataFormat, JsonOptions, ILocalCatalogMigrator, TypeCatalogValueApplier, TypeCatalogValueApplyResult, TypeCatalogValueApplyStatus | `SmartCon.Core/Models/` + `Core/Services/` |
