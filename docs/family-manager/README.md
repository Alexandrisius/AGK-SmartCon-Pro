# FamilyManager Unified Documentation Pack

## Назначение

Этот пакет объединяет стратегические и MVP-документы FamilyManager в единую систему для разработки модуля BIM content management внутри smartCon.

Пакет рассчитан на поэтапную разработку: сначала стратегия и стек, затем продуктовая рамка MVP, доменная модель, схема данных, provider contract, архитектурные принципы, риски и финальный технический MVP plan.

## Порядок чтения

1. `00-strategy/00-familymanager-concept-roadmap.md`
2. `00-strategy/01-familymanager-technical-stack.md`
3. `01-mvp/00-index.pplx.md`
4. `01-mvp/01-mvp-prd.pplx.md`
5. `01-mvp/02-mvp-scope-matrix.pplx.md`
6. `01-mvp/03-personas-jtbd.pplx.md`
7. `01-mvp/04-domain-model.pplx.md`
8. `01-mvp/05-metadata-schema.pplx.md`
9. `01-mvp/06-user-flows.pplx.md`
10. `01-mvp/07-ux-ia.pplx.md`
11. `01-mvp/08-architecture-principles.pplx.md`
12. `01-mvp/09-provider-contract.pplx.md`
13. `01-mvp/10-security-data-ownership.pplx.md`
14. `01-mvp/11-nfr-qa-strategy.pplx.md`
15. `01-mvp/12-risk-register-adr-backlog.pplx.md`
16. `01-mvp/13-technical-mvp-plan.pplx.md`

## Источники истины

| Тема | Канонический документ |
| --- | --- |
| Стратегия продукта и фазирование | `00-familymanager-concept-roadmap.md` |
| Библиотеки, runtime constraints, зависимости | `01-familymanager-technical-stack.md` |
| MVP scope и acceptance | `01-mvp-prd.pplx.md` |
| Границы MVP / post-MVP / enterprise | `02-mvp-scope-matrix.pplx.md` |
| Термины и доменные сущности | `04-domain-model.pplx.md` |
| SQLite/schema/file-cache split | `05-metadata-schema.pplx.md` |
| UX и dockable panel IA | `07-ux-ia.pplx.md` |
| Архитектурные инварианты smartCon | `08-architecture-principles.pplx.md` |
| Provider abstraction | `09-provider-contract.pplx.md` |
| Security/data ownership | `10-security-data-ownership.pplx.md` |
| Test/NFR strategy | `11-nfr-qa-strategy.pplx.md` |
| ADR backlog и риски | `12-risk-register-adr-backlog.pplx.md` |
| Последовательность реализации MVP | `13-technical-mvp-plan.pplx.md` |

## Зафиксированные решения

- Основной target: Revit 2025+ / `net8.0-windows`.
- Legacy target: Revit 2019–2024 / `net48`.
- Новый модуль: `SmartCon.FamilyManager`.
- `SmartCon.FamilyManager` зависит только от `SmartCon.Core` и `SmartCon.UI`.
- Revit API вызывается только через `SmartCon.Revit`.
- MVP storage: SQLite + file cache.
- Канонический локальный root: `%APPDATA%\SmartCon\FamilyManager\`.
- База MVP: `%APPDATA%\SmartCon\FamilyManager\databases\{id}\catalog.db` (multi-DB pattern).
- Канонические таблицы (schema v2, ADR-015): `database_meta`, `schema_info`, `catalog_items`, `catalog_versions`, `family_files`, `family_assets`, `catalog_tags`, `project_usage` (8 таблиц).
- MVP provider: `LocalCatalogProvider`.
- Future providers: `RemoteCatalogProvider`, `CorporateCatalogProvider`, `PublicReadOnlyProvider`, `CompositeCatalogProvider`.
- Project usage в MVP хранится в локальной SQLite БД, в corporate phase — в серверной БД.

## Жёсткий запрет

FamilyManager не хранит каталог, `.rfa`, metadata, теги, preview, search index, usage history или избранное в ExtensibleStorage.

ExtensibleStorage остаётся паттерном существующих модулей smartCon, но не является data plane FamilyManager.

### Исключение: `SmartCon.FamilyVersion.v1` (Phase 24)

**Единственное исключение** из правила — маркер версии в самом `.rfa` файле, введённый в Phase 24 (см. [ADR-030](../adr/030-phase-24-stale-detection-v2.md)). Хранит **только** метаданные момента загрузки (CatalogItemId, VersionLabel, LoadedAtUtc, SourceRevitVersion), а не каталожные данные.

**Что остаётся запрещено:**
- Каталог (`catalog_items`, `catalog_versions`, `family_files`, `family_assets`) — в SQLite
- Метаданные (manufacturer, tags, description, preview) — в SQLite/managed storage
- История загрузок, избранное — в SQLite
- Любые новые ES Schema для FamilyManager (кроме `FamilyVersion.v1`)

**Обоснование исключения:** см. [ADR-030 §Решение](../adr/030-phase-24-stale-detection-v2.md).

## Перед стартом реализации

Перед написанием кода нужно утвердить ADR-FM-001, ADR-FM-003, ADR-FM-004, ADR-FM-006 и ADR-FM-007. Затем технический план MVP следует детализировать фазами из `13-technical-mvp-plan.pplx.md`.

## Статус

**Phase 12 (FamilyManager MVP) — COMPLETED (2026-04-28).**

- ADR-014 принят: `docs/adr/014-familymanager-mvp-architecture.md`
- Модели и интерфейсы добавлены в `docs/domain/models/family-manager.md` и `docs/domain/interfaces/family-manager.md`
- `SmartCon.FamilyManager` добавлен в `docs/architecture/solution-structure.md` и `docs/architecture/dependency-rule.md`

**Phase 13 (FamilyManager Published Storage) — COMPLETED (2026-05-01).**

- ADR-015 принят: `docs/adr/015-familymanager-published-storage.md` — Published Storage, configurable DB location, managed storage, version → Revit-version model, auxiliary assets, schema v2 (8 таблиц)
- ADR-016 принят: `docs/adr/016-familymanager-readonly-files.md` — ReadOnly-флаг для managed-файлов
- Схема БД обновлена до v2: `database_meta`, `schema_info`, `catalog_items`, `catalog_versions`, `family_files`, `family_assets`, `catalog_tags`, `project_usage` (8 таблиц)
- Asset management: изображения, видео, документы, FBX, lookup-таблицы
- Category tree для навигации по каталогу

**Phase 21 (FamilyManager Active Import Refactor) — COMPLETED (2026-06-05).**

- ADR-024 принят: `docs/adr/024-active-family-import-preparer.md`
- Устранена потеря Type Catalog (.txt) при импорте активного `.rfa`
- Новые сервисы: `IFamilySidecarLocator` (pure I/O, 13 unit-тестов), `IActiveFamilyFilePreparer`, `IActiveDocumentClassifier`, `IActiveImportCleanupService`
- `OriginalSourcePath` в `FamilyImportRequest`/`FamilyBatchImportItem`/`FamilyUpdateRequest`
- VM `ImportActiveFileAsync` упрощён через классификатор активного документа
- Удалён static `CleanupImportActiveTemp` — заменён `IActiveImportCleanupService`

**Phase 22 (FamilyManager Placed Families v2 — OfClass(Family) + EditFamily for extraction) — COMPLETED (2026-06-07).**

- ADR-027 принят: `docs/adr/027-placed-families-v2.md`
- "Импорт активного файла" импортирует **и** системные, **и** loadable families из активного проекта
- "Импорт системного семейства" переименован в **"Импорт выделенных элементов"**, принимает любые элементы (system + loadable)
- Ключевая идея: **Analyze = только метаданные (мгновенно)**, **Stage = по подтверждению**, **Extract = через существующий `IFamilyDataExtractionService`**
- Новые сервисы: `ILoadableFamilyScanner` (`OfClass(Family)`, O(F) — не O(N)), `ILoadableFamilyTypeResolver` (открывает `.rfa`, читает `FamilyManager.GetTypes()` с UniqueId), `ILoadableFamilyImportOrchestrator` (managed storage + type persist)
- Picker filter `SystemFamilySelectionFilter` заменён на `AnyElementSelectionFilter` (`FamilyInstance` + system categories)
- `ISystemFamilyRevitOperations.PickSystemTypes()` → `PickSelectedElements() → SelectedElementsAnalysis`
- `ProcessProjectImportAsync` принимает `IReadOnlyList<FamilyBatchImportItem>` и диспетчеризирует по `FamilySource` (system → `ISystemFamilyImportOrchestrator`, loadable → `ILoadableFamilyImportOrchestrator`)
- Атрибуты loadable извлекаются через `IFamilyDataExtractionService.Extract(managedRfaPath, [])` — **переиспользует** существующий сервис (без нового extractor'а для `.rfa`)
- 4 новых unit-теста для `LoadableFamilyInfo`. Тесты для `SelectedElementsAnalysis` невозможны (record содержит `BuiltInCategory` value-type, требует `RevitAPI.dll` в test bin)
- Всего: 1219/1219 тестов зелёные (1215 до + 4 новых)
- 19 новых тестов: 12 sidecar + 1 preparer + 5 TypeCatalog + 1 прочий

**Phase 24 (FamilyManager Stale Detection v2 — On-Demand) — PLANNED (2026-06-18).**

- ADR-030 принят: `docs/adr/030-phase-24-stale-detection-v2.md` — override ADR-014 §FM-007 (запрет ExtensibleStorage)
- Новая ES Schema `SmartCon.FamilyVersion.v1` на самом `.rfa` файле (per-family маркер версии)
- VendorId workaround: `AGKSMARTCON` (9 chars) + `AccessLevel.Public/Public` — как в `FittingMappingSchema`
- 4 простых поля: `SchemaVersion`, `CatalogItemId`, `VersionLabel`, `LoadedAtUtc`, `SourceRevitVersion`
- Семейство «несёт с собой» маркер версии — работает при cross-project, multi-user, backup
- 4 новых интерфейса в Core: `IFamilyVersionStore`, `IStaleDetector`, `IStaleFamilyUpdater`, `IStaleCategoryAggregator`
- 4 новые модели в Core: `FamilyVersion`, `StaleCheckResult` (+ `StaleReason` enum), `StaleUpdateRequest`, `FamilyStaleSnapshot`
- UI: ПКМ "Проверить" на категории (рекурсивно) и на семействе, ПКМ "Обновить" с подменю (с перезаписью/без/пакетное), roll-up `⚠` индикация на leaf + категориях
- On-demand модель: единственный триггер — ПКМ "Проверить". НЕТ push events (Phase 23 отвергнут)
- Refresh кнопка ↻ — **только каталог** (НЕ stale)
- Кеш `FamilyStaleSnapshot` на сессию, инвалидируется при Load/Update/Edit/смена БД
- Производительность: < 200 мс на 30 семейств (target Issue #69: < 500 мс)
- SQLite schema **v12**: `DROP TABLE project_usage` + `DROP INDEX ix_project_usage_lookup` (clean slate)
- Breaking change `2.0.0` — pre-release `2.0.0-beta.1` (ADR-021)
- Доступно всем ролям (это операция в активном проекте, не каталог)
- Детальный план: `docs/family-manager/02-plans/phase-24-stale-detection-v2.md`
