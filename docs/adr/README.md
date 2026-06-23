# Architecture Decision Records (ADR)

> Индекс архитектурных решений проекта SmartCon.
> Загружать: при вопросах «почему так сделано?»

## Формат

Каждый ADR — отдельный файл с номером. Статус: `accepted`, `superseded`, `deprecated`.

## Индекс

| # | Решение | Статус | Дата |
|---|---------|--------|------|
| [001](001-clean-architecture.md) | Clean Architecture: Core без зависимостей от Revit/WPF | accepted | 2026-03-25 |
| [002](002-connector-description-type-code.md) | Хранение типа соединения в Connector.Description | accepted | 2026-03-25 |
| [003](003-transaction-group-pattern.md) | TransactionGroup + Assimilate для PipeConnect | accepted | 2026-03-25 |
| [004](004-json-mapping-storage.md) | JSON в AppData для хранения маппинга фитингов | superseded by 012 | 2026-03-25 |
| [005](005-formula-solver-ast.md) | AST-парсер для формул Revit (универсальный модуль) | accepted | 2026-03-25 |
| [006](006-external-event-pattern.md) | IExternalEventHandler для WPF -> Revit API | accepted | 2026-03-25 |
| [007](007-communitytoolkit-mvvm.md) | CommunityToolkit.Mvvm вместо ручной MVVM-инфраструктуры | accepted | 2026-03-25 |
| [008](008-external-event-action-queue.md) | Action Queue паттерн для ExternalEvent dispatch | accepted | 2026-03-25 |
| [009](009-vec3-for-core-math.md) | Vec3 вместо XYZ для чистой математики в Core | accepted | 2026-03-26 |
| [010](010-fitting-chain-resolver.md) | FittingChainResolver — единая система подбора цепочек фитингов | accepted | 2026-04-17 |
| [011](011-dn-symbol-name-in-dropdown.md) | Отображение имени типоразмера в выпадающем списке DN | accepted | 2026-04-17 |
| [012](012-per-project-extensible-storage.md) | Per-project ExtensibleStorage для маппинга фитингов | accepted | 2026-04-19 |
| [013](013-project-management-module.md) | Модуль ProjectManagement — Share Project (ISO 19650) | accepted | 2026-04-23 |
| [014](014-familymanager-mvp-architecture.md) | FamilyManager MVP Architecture | superseded by 015 | 2026-04-28 |
| [015](015-familymanager-published-storage.md) | FamilyManager Published Storage (ISO 19650) | accepted | 2026-04-30 |
| [016](016-familymanager-readonly-files.md) | ReadOnly-флаг для managed-файлов семейств | accepted | 2026-04-30 |
| [017](017-familymanager-attribute-extraction.md) | FamilyManager Attribute Extraction Foundation | accepted | 2026-05-06 |
| [018](018-familymanager-refactoring.md) | FamilyManager Refactoring — DI Patterns, Async Safety, and Performance | accepted | 2026-05-07 |
| [019](019-drag-drop-visual-feedback.md) | Drag & Drop Visual Feedback — Adorner-based DnD | accepted | 2026-05-09 |
| [020](020-localization-architecture.md) | Binding-based локализация через LocExtension + TranslationSource | accepted | 2026-05-14 |
| [021](021-prerelease-versioning.md) | Pre-release Versioning и Beta Release Strategy | accepted | 2026-05-17 |
| [022](022-familymanager-rbac.md) | FamilyManager RBAC — Role-Based Access Control для локальных каталогов | accepted | 2026-05-16 |
| [023](023-familymanager-type-centric-workflow.md) | FamilyManager Type-Centric Workflow — Type Catalog, virtual types, per-type loading | accepted | 2026-06-04 |
| [024](024-active-family-import-preparer.md) | Active Family Import Preparer — Sidecar (.txt) preservation + new preparer/locator/classifier/cleanup services | accepted | 2026-06-05 |
| [025](025-refactoring-migration-backlog.md) | Refactoring Migration Backlog | accepted | 2026-06-09 |
| [026](026-logging-migration.md) | Logging Migration Plan (Phase 0 + Phase 1) | accepted | 2026-06-09 |
| [027](027-placed-families-v2.md) | Placed Families v2 | accepted | 2026-06-12 |
| [028](028-di-readiness.md) | DI Readiness | accepted | 2026-06-13 |
| [029](029-shared-nested-load-dialog.md) | Shared nested families — user dialog for load mode (Issue #67) | accepted | 2026-06-17 |
| [030](030-phase-24-stale-detection-v2.md) | Stale Detection v2 — On-Demand Family Version Marker via ExtensibleStorage on Family element in project (Issue #69) | accepted | 2026-06-18 |
| [031](031-fireandforget-ui-marshalling.md) | FireAndForget — обязательный UI-marshalling (net48 freeze post-mortem) | accepted | 2026-06-19 |
| [032](032-type-catalog-simulation.md) | Type Catalog Simulation — вычисление формул для типов из .txt через Document.Regenerate (Issue #66) | superseded by 033 | 2026-06-21 |
| [033](033-bakein-type-catalog.md) | Bake-in Type Catalog в .rfa при импорте (Issue #74) | accepted | 2026-06-22 |
| [034](034-shared-nested-persist-fallback.md) | Persist shared nested family names at import time (REVIT-198137 в Revit 2023/2024.2, Issue #77) | accepted | 2026-06-23 |
