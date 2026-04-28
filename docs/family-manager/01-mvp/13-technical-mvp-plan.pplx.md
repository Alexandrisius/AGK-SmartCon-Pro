# FamilyManager Technical MVP Plan

## Цель документа

Это финальный технический MVP plan. Он опирается на PRD, scope, domain model, metadata schema, user flows, UX/IA, provider contract, security, NFR/QA и risk register.

## MVP Technical Goal

Построить `SmartCon.FamilyManager` как `net8-first` модуль smartCon для Revit 2025+, который предоставляет локальный каталог `.rfa` семейств с импортом, поиском, карточкой и загрузкой в активный Revit-проект, сохраняя legacy build compatibility для Revit 2019–2024.

## Phase 12A: Documentation and ADR Foundation

### Deliverables

- `docs/familymanager/README.md`
- `docs/familymanager/metadata-schema.md`
- `docs/familymanager/ui-spec.md`
- `docs/familymanager/algorithms.md`
- `docs/adr/014-familymanager-module.md`
- updates to `docs/domain/models.md`
- updates to `docs/domain/interfaces.md`
- updates to `docs/roadmap.md`

### Acceptance

- MVP scope approved.
- Storage split approved.
- Provider contract approved.
- SQLite schema versioning strategy approved.
- Dockable/modal decision made.

## Phase 12B: Core Domain and Contracts

### Add Core Models

Location: `SmartCon.Core/Models/FamilyManager`

Models:

- `FamilyCatalogItem`
- `FamilyCatalogVersion`
- `FamilyFileRecord`
- `FamilyTypeDescriptor`
- `FamilyParameterDescriptor`
- `FamilyCatalogQuery`
- `FamilyImportRequest`
- `FamilyImportResult`
- `FamilyBatchImportResult`
- `FamilyLoadResult`
- `ProjectFamilyUsage`

### Add Core Interfaces

Location: `SmartCon.Core/Services/Interfaces`

Interfaces:

- `IFamilyCatalogProvider`
- `IWritableFamilyCatalogProvider`
- `IFamilyImportService`
- `IFamilyFileResolver`
- `IFamilyLoadService`
- `IFamilyMetadataExtractionService`
- `IProjectFamilyUsageRepository`

### Acceptance

- Core compiles without `System.Windows`.
- Core does not call Revit API.
- Public APIs have XML-doc.
- Unit tests cover normalization, query model, status enum, result models.

## Phase 12C: Local SQLite Provider

### Add Package

Client package:

- `Microsoft.Data.Sqlite 8.x`

Optional:

- Dapper 2.x if manual ADO.NET becomes too verbose.

### Implement

Location candidates:

- `SmartCon.FamilyManager/Services/LocalCatalog`
- pure abstractions in Core;
- SQLite implementation can live in FamilyManager if it has no Revit API dependency.

Components:

- `LocalCatalogDatabase`
- `LocalCatalogMigrator`
- `LocalCatalogProvider`
- `LocalFamilyImportService`
- `LocalFamilyFileResolver`
- `FamilySearchNormalizer`
- `Sha256FileHasher`

### Acceptance

- Creates database.
- Runs migrations idempotently.
- Imports one `.rfa`.
- Imports folder.
- Stores hash, path, item, version, tags.
- Searches by name/status/category/tags.
- Handles duplicate hash.
- Tests use temporary SQLite DB.

## Phase 12D: File Cache and Import Pipeline

### Implement

- cache path resolution;
- storage mode: choose `Linked` or `Cached`;
- hash calculation;
- import result model;
- batch progress;
- cancellation;
- missing file detection.

### Two-Phase Indexing

Phase 1:

- file name;
- path;
- size;
- hash;
- timestamps;
- user-entered metadata.

Phase 2:

- Revit metadata extraction;
- family types;
- parameters;
- preview.

MVP can ship with Phase 1 plus minimal Revit extraction.

### Acceptance

- Folder import does not stop on one bad file.
- Import report lists success/skipped/errors.
- Missing file state is visible.
- File cache policy documented.

## Phase 12E: Revit Layer

### Implement

Location:

- `SmartCon.Revit/FamilyManager`

Classes:

- `RevitFamilyLoadService`
- `RevitFamilyMetadataExtractionService`
- `RevitFamilyDocumentService`

### Rules

- Load operations run through Revit-safe boundary.
- Usage history writes go to local SQLite DB in MVP.
- Family document operations follow I-03b.
- No live `Family`/`FamilySymbol` escapes service.

### Acceptance

- Valid `.rfa` loads into active Revit 2025 project.
- Project usage writes to local catalog DB, not ExtensibleStorage.
- Usage history repository writes to SQLite through provider/application service, not through Revit storage.
- Revit errors are logged and shown cleanly.

## Phase 12F: UI Module

### Add Project

Location:

- `SmartCon.FamilyManager`

Folders:

- `Commands`
- `ViewModels`
- `Views`
- `Services`
- `Events`

### Main UI

Views:

- `FamilyManagerView`
- `FamilyImportView`
- `FamilyImportProgressView`
- `FamilyDetailsView`
- `FamilyMetadataEditView`
- `FamilyManagerSettingsView`

ViewModels:

- `FamilyManagerViewModel`
- `FamilyImportViewModel`
- `FamilyDetailsViewModel`
- `FamilyMetadataEditViewModel`
- `FamilyManagerSettingsViewModel`

### Acceptance

- MVVM Toolkit used.
- Code-behind minimal.
- DataGrid headers set programmatically.
- RU/EN localization keys added.
- Empty/loading/error states implemented.

## Phase 12G: Dockable Panel and Ribbon Integration

### App Integration

Update:

- `SmartCon.App/Ribbon/RibbonBuilder.cs`
- `SmartCon.App/DI/ServiceRegistrar.cs`
- `SmartCon.App.csproj`

### Dockable Decision

If dockable panel is stable:

- register in `App.OnStartup`;
- singleton content;
- active document refresh.

If not stable for MVP:

- modal window fallback;
- same ViewModel/application services;
- ADR documents reason.

### Acceptance

- Ribbon button opens FamilyManager.
- DI resolves all services.
- No direct service locator in ViewModels.
- Panel/window can be opened repeatedly.

## Phase 12H: Tests

### Unit Tests

Areas:

- domain models;
- normalization;
- serializers;
- migration planning;
- search query builder;
- import result aggregation;
- ViewModel commands.

### Integration Tests

Areas:

- SQLite migration;
- repository CRUD;
- duplicate detection;
- search/filter;
- project usage repository.

### Smoke Tests

Manual:

- Revit 2025 open UI;
- import valid `.rfa`;
- load to project;
- close/reopen FamilyManager catalog and verify usage history in SQLite;
- legacy build/load.

### Acceptance

- Target: 60+ new tests; minimum acceptable: 50 tests.
- Existing baseline remains green.
- R19/R21/R24/R25/R26 builds pass.

## Phase 12I: Multi-Version Polish

### Work Items

- verify `net8.0-windows` R25/R26 behavior;
- verify `net48` R19/R21/R24 compilation;
- isolate `#if` in Revit/App/Compatibility only;
- disable unsupported legacy operations;
- confirm SQLite package loading in legacy builds.

### Acceptance

- No warnings.
- No dependency drift to `Microsoft.Extensions.*` 9+.
- No `Newtonsoft.Json`.
- No third-party WPF themes.
- No direct Revit references in FamilyManager module.

## Phase 12J: Release Readiness

### Required Updates

- `docs/familymanager/*`
- `docs/domain/models.md`
- `docs/domain/interfaces.md`
- `docs/roadmap.md`
- `docs/future-work.md`
- `CHANGELOG.md`

### Release Checklist

1. Code builds.
2. Tests pass.
3. Manual Revit 2025 smoke done.
4. Legacy builds verified.
5. Docs updated.
6. ADRs merged.
7. Risk register reviewed.
8. Package dependency review completed.

## MVP File Map

| Area | Files/Folders |
| --- | --- |
| Core models | `SmartCon.Core/Models/FamilyManager/*` |
| Core interfaces | `SmartCon.Core/Services/Interfaces/IFamily*.cs` |
| SQLite provider | `SmartCon.FamilyManager/Services/LocalCatalog/*` |
| UI | `SmartCon.FamilyManager/Views/*`, `ViewModels/*` |
| Revit load/extract | `SmartCon.Revit/FamilyManager/*` |
| Project usage | SQLite tables in local catalog; server DB later |
| DI | `SmartCon.App/DI/ServiceRegistrar.cs` |
| Ribbon | `SmartCon.App/Ribbon/RibbonBuilder.cs` |
| Tests | `SmartCon.Tests/FamilyManager/*` |

## Final Technical Recommendation

Start implementation with the smallest useful slice:

1. Core domain models.
2. SQLite schema/migrator.
3. Local import of one `.rfa`.
4. Main UI list/detail.
5. Load to Revit 2025 project.
6. Project usage in local DB.
7. Folder import and search.
8. Multi-version cleanup.

This order proves the full vertical architecture early: UI → provider → SQLite/file cache → Revit layer → usage history in DB → tests.
