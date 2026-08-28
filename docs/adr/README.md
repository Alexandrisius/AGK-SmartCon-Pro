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
| [035](035-remove-temp-logic-v2.md) | Удаление temp-логики в импорте семейств (Issue #73) | accepted | 2026-06-24 |
| [036](036-active-family-type-sync.md) | Sync типов при импорте активного семейства — bug #1 (ghost types) + bug #2 (UI refresh) (Issue #85) | accepted | 2026-06-25 |
| [037](037-tree-expand-collapse.md) | Tree Expand/Collapse — UX tree behavior | accepted | 2026-06-12 |
| [038](038-sticky-category-headers.md) | Sticky Category Headers in catalog tree | accepted | 2026-06-13 |
| [039](039-snapshot-driven-commit.md) | Snapshot-driven Commit — content-hash from family snapshot, not re-open | accepted | 2026-06-17 |
| [040](040-overwritecurrent-semantics.md) | OverwriteCurrent — UPDATE catalog_versions in place + managed file rotation | accepted | 2026-06-29 |
| [041](041-active-version-management.md) | Active Version Management — SetActive/DeleteVersion + per-version types/attributes | accepted | 2026-06-30 |
| [042](042-familymanager-3d-preview.md) | FamilyManager 3D Geometry Preview — GLB extraction (SharpGLTF) + HelixToolkit.Wpf.SharpDX viewer (Issue #92) | accepted | 2026-07-01 |
| [043](043-pipeconnect-modal-justification.md) | PipeConnectEditor модальность — единственно возможное решение для live real-element preview + single-undo cancel (Exa-исследование ограничений Revit API) | accepted | 2026-07-08 |
| [044](044-familymanager-content-tab-redesign.md) | FamilyManager Content Tab Redesign — Dual-Pane Layout, attach-to-version toggle, unified add, auto-GLB filter, confirm delete | accepted | 2026-07-08 |
| [045](045-core-filenameparser-reuses-familymanager-basetype.md) | Core FileNameParser reused by FamilyManager BaseType — project-base binding without FamilyManager → ProjectManagement dependency | accepted | 2026-07-09 |
| [046](046-hybrid-wpf-icon-sourcing.md) | Hybrid WPF Icon Sourcing — PackIconMaterial in module BAML, PathGeometry in SmartCon.UI | accepted | 2026-07-10 |
| [047](047-avatar-crop-derived-file.md) | Avatar Crop — производный avatar.png 560×420, инвалидация при смене primary, единая миниатюра для аватарки и tooltip (Issue #131) | accepted | 2026-07-16 |
| [048](048-batch-import-modeless-progress.md) | Batch Import - modeless диалог с живым прогрессом, паузой Остановить/Продолжить/Закрыть, поэлементный pipeline (Issue #127) | accepted | 2026-07-16 |
| [049](049-content-hash-v2-rename-invariant.md) | Content Hash v2 — rename-invariant дедупликация (hash-first), cross-name дубликаты с ⚠, имя айтема следует за активной версией (Issue #126) | accepted | 2026-07-17 |
| [050](050-hash-recalculation-migration.md) | Hash Recalculation Migration — user-initiated data repair v1→v2 с прогресс-диалогом, маркировка -1, purge недоступных (Issue #126) | superseded by [054](054-database-actualization-engine.md) | 2026-07-17 |
| [051](051-dependency-isolation.md) | Dependency Isolation — ILRepack merge (net48) + Nice3point ALC (net8) + конвейер миграции (Updater/installer/self-healing .addin) (Issue #134) | accepted | 2026-07-17 |
| [052](052-pipe-length-displacement-absorption.md) | PipeConnect — гашение смещения сети длиной трубы на её собственном уровне (PipeLengthAbsorber, min 100 мм, поэлементная симметрия +/−) | accepted | 2026-07-19 |
| [053](053-dn-transition-compensation.md) | PipeConnect — DN-компенсация по уровням: TRANSITION→REDUCER→RESIZE, гейтинг AdjustRelatedFamilyConnectors (Issue #146) | accepted | 2026-07-20 |
| [054](054-database-actualization-engine.md) | Database Actualization Engine — единый сервис актуализации БД: задачи (hash/attributes/glb), один open на семью, два уровня critical/optional, один диалог (#151/#152/#153); superseded ADR-050 фреймворк | accepted | 2026-07-21 |
| [055](055-family-facts-subsystem.md) | Family Facts — category-driven факты семейства (Part Type первым): реестр правил в Core, EAV-таблица V22, registry-driven детект `family-facts-v1`, локализация значений через PartTypeLabelMap | accepted | 2026-07-23 |
| [056](056-content-hash-v3.md) | Content Hash v3 (FHV3) — PartType/факты, коннекторы, behavior-флаги, bbox+surface геометрия, CompoundStructure, RoutingPreferences, локале-инвариантная категория (ordinal), экранирование разделителей; критическая задача `hash-v3` (Issue #159) | accepted | 2026-07-23 |
| [057](057-connector-binding-mepinfo.md) | Привязка коннектор→параметр через MEPFamilyConnectorInfo — конец гео-матчинга project↔familyDoc; FamilyFormulaCache (EditFamily 6-7×→1×), CTC-матчинг IsPrimary+index-order (Issue #161) | accepted | 2026-07-27 |
| [058](058-plugin-database-forward-compatibility-gate.md) | Forward-compatibility gate — `database_meta.min_plugin_version` (V24 + ретро-гейт FHV3), баннер «обновите плагин» → About, read-only для устаревшего плагина, конвенция bump+backfill (Issue #173) | accepted | 2026-07-28 |
| [059](059-import-validation-gate.md) | Import Validation Gate - health-check (silent rollback, UC-1/UC-2) + правила на binding'ах (schema v25), display-units-first, единый жёсткий гейт (импорт/DnD/свойства), карантин "Без категории" для read-only ролей, пакет v3 | accepted | 2026-07-29 |
| [060](060-full-immediate-category-editor.md) | Full-immediate редактор категорий — отказ от draft-модели: все операции сразу в БД, импорт атомарный, кнопка "Закрыть" вместо OK/Отмена | accepted | 2026-07-29 |
| [061](061-system-family-sync.md) | System Family Sync (Issue #104) - синхронизация данных вместо CopyElements: create-or-update на месте, ES-маркер на ElementType, stale parity, MEP-трассировка (сегменты/материалы/правила/фитинги), compound structure, коллизия имён с шаблоном (rename, без delete), hash-neutral sync-поля | accepted | 2026-07-30 |
| [062](062-mini-project-marker-lifecycle.md) | Маркер эталонных мини-проектов - ES-маркер SmartCon_MiniProject_v1, безопасное закрытие без сохранения после реимпорта (двухфакторный guard, fork-защита), запрет автопереключения активной БД (Issues #186, #188, #189) | accepted | 2026-07-31 |
| [063](063-presence-indicators-command-model.md) | Индикаторы присутствия в проекте + per-type командная модель - трёхзначные точки (серая/зелёная/оранжевая), presence-снимок с кэшем, per-type stale, команды по состоянию точки, авто stale-проверка после импорта (Issues #187, #185) | accepted | 2026-07-31 |
| [064](064-system-family-identity-key.md) | Locale-invariant идентичность системной семьи — family_key (схема V27): IsWithFitting/WallType.Kind/StairsType.ConstructionMethod discriminators, key-first матчинг sync/stale/presence, атрибутный пайплайн по (family, name) (Issues #190, #191) | accepted | 2026-08-03 |
| [065](065-fhv4-extended-sync.md) | FHV4 — хэш идентичности системных типов (STRUCT-флаги, сегменты, подтипы лестниц, структура ограждений, family_key) + расширенный sync подтипов/ограждений; матрица настроек категорий и gaps (Issues #184, #179) | accepted | 2026-08-03 |
| [066](066-family-dependencies-model.md) | Зависимости семейств (routing-фитинги, nested shared) как полноценные элементы каталога — таблица family_dependencies (V29), авто-импорт + dedup-link, каскадная валидация по ADR-059, sync по связям + ES-маркер (EPIC #207) | accepted | 2026-08-06 |
| [067](067-dependency-guard.md) | Dependency Guard — жёсткий запрет удаления зависимости, на которую ссылается ЛЮБАЯ версия родителя (каждая версия всегда полноценна); освобождение через удаление версии/родителя (E5, #213) | accepted | 2026-08-06 |
| [068](068-e2-nested-shared-update-verification.md) | E2 nested shared — poke+doc-to-doc обновление в family-документе, верификация FHV8V, marker-first, single-open оркестрация, self-heal dependency links, FHV9 phantom-значения (#209) | accepted | 2026-08-12 |
| [069](069-fhv11-lookup-tables-in-hash.md) | FHV11 — lookup tables (таблицы поиска) в едином content hash: секция LOOKUP из raw CSV (ExportSizeTable, локале-инвариант), merge-transfer доказан зондом, задача hash-v11 + floor 2.0.1-beta.9 (#238) | accepted | 2026-08-23 |
| [070](070-category-auto-assignment-rules.md) | Автоназначение категории при batch-импорте — OR-группы AND-условий (schema v31), переиспользование FamilyValidationEngine, tri-state движок, 9 позитивных операторов, конфликт-иконка + рекомендательный пикер в колонке «Категория», metadata v4 (Issue #241) | accepted | 2026-08-24 |
| [071](071-content-hash-hierarchy.md) | Иерархия контент-хэша FamilyManager (#249) — 13 секций + per-type хэши + diff-окно с классификацией + CAS-пул превью; FHV12-16: автономность секций, sketch-isolation, reference-тип (rolled-back), канонизация -0; VersionMismatch per-type карта из БД, shared-секция = все типы stale, confirm только для ContentDrift | accepted | 2026-08-28 |
| [072](072-manual-miniproject-staging.md) | Ручной staging мини-проектов MEPCurve + routing как данные каталога (#254) — отказ от CopyElements в staging (дубли материалов Revit 2024+), мини без фитингов, таблица family_routing_rules, редактор трассировки из каталога; полная криминалистика #254 | proposed | 2026-08-28 |
