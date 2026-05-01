# Changelog

All notable changes to SmartCon will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added — Phase 12-13: FamilyManager Module
- FamilyManager: dockable panel for managing Revit family libraries
- SQLite catalog with search, filtering, category tree
- Import .rfa files via drag-and-drop or file dialog (single/folder)
- Published Storage with Active/Deprecated/Retired statuses (ADR-015)
- Multi-database support: create, connect, switch, delete databases
- Asset management: attach auxiliary files (images, documents) to families
- Attribute presets: configurable parameter extraction per category
- Category tree: hierarchical organization with drag-and-drop reorder
- Family types: extract and cache type symbols for quick placement
- Load & Place: load family into Revit project and place instances
- Family update: re-import with version increment
- Auto-detect Revit version from .rfa via BasicFileInfo
- Read-only managed storage to prevent accidental modifications (ADR-016)
- ADR-014: FamilyManager MVP Architecture
- ADR-015: Published Storage Architecture
- ADR-016: Readonly Files Architecture

### Metrics
- Tests: 716 → 730
- Modules: +1 (FamilyManager)
- New models: 20+ (FamilyCatalogItem, FamilyCatalogVersion, FamilyFileRecord, etc.)
- New interfaces: 13 (IFamilyCatalogProvider, IFamilyImportService, etc.)

## [1.4.x] - 2026-04-25

### Added — Phase 11: ProjectManagement Module
- ShareProject module: ISO 19650 export workflow (sync → detach → purge → save to shared zone)
- ShareSettingsView: 4-tab settings (General, Purge, Views, Naming)
- Field Library: reusable field definitions with validation (AllowedValues, CharCount)
- FileNameParser: template-based file naming with validation and preview
- ExportNameDialog: manual field override when validation fails
- ParseRuleView: visual rule editor (5 parse modes)
- RevitModelPurgeService: 12-category model cleanup
- RevitShareProjectSettingsRepository: ExtensibleStorage persistence with JSON migration
- LocalizationService keys for all PM strings (RU/EN)

### Changed
- All ViewModels now use IDialogService for dialogs (no more MessageBox.Show from VM)
- All ViewModels now use IDialogPresenter for nested dialogs (no more new View(vm) from VM)
- MappingEditorView now inherits DialogWindowBase (consistent with other views)
- CommandHelper static class for shared IExternalCommand initialization pattern
- LEGACY FittingCtcSetup removed (auto-CTC via Reflect replaces it)
- IDialogService extended with ShowError, ShowQuestion, ShowFolderBrowser
- PurgeOptions mapping deduplicated (3 instances → 2 helper methods)
- I-09 expanded: Document explicitly allowed as opaque parameter carrier

### Metrics
- Tests: 676 → 716 (+40)
- Modules: +1 (ProjectManagement)
- LEGACY code removed: FittingCtcSetupView, FittingCtcSetupViewModel, EnsureFittingCtcForInsert, EnsureReducerCtcForInsert

## [1.3.0] - 2026-04-19

### Added — Phase 5: OSS Perfection (см. `.opencode/plans/oss-perfection-plan.md`)

#### Phase A — Elimination of Service Locator
- `IPipeConnectViewModelFactory`, `IAboutViewModelFactory`, `ISettingsViewModelFactory` + default implementations
- `WpfDialogPresenter` as unified `IDialogPresenter` — hides Window construction from services

#### Phase B — Clean Architecture
- `ConnectionGraphBuilder` split from immutable `ConnectionGraph` (CQS-style)
- `ElementIdEqualityComparer` extracted into its own file
- `IAlignmentService` + `RevitAlignmentService` (moved heavy math from `ConnectorAligner`)
- `Language` enum + `IObservableRequestClose` interface in Core

#### Phase C — MVVM Discipline
- `DialogWindowBase` — single WPF base class for the four modal windows
- `IDialogPresenter` abstraction (registered in DI)
- Named colour resources in XAML (no more magic hex literals in views)
- `IFittingCtcSetupItem` abstraction in Core; WPF-bindable `FittingCtcSetupItem` moved to `PipeConnect.ViewModels`

#### Phase D — XML Documentation
- Full XML-docs coverage on public API of `SmartCon.Core` and `SmartCon.PipeConnect`

#### Phase E — Multi-version CI
- GitHub Actions workflows aligned to shipping artifacts R19 / R21 / R24 / R25

#### Phase F — Test coverage
- 577 → **676 tests**, 0 regressions
- New suites: `FormulaEngineEdgeCaseTests`, `PipeConnectStateTests`

#### Phase H — Documentation sync
- Storage references updated to per-project ExtensibleStorage (ADR-012)
- `docs/future-work.md` — tracking TODOs (`[ChainV2]`, `[Phase 6B]`)
- ADR status headers standardised (`**Статус:** accepted`)
- `docs/README.md` updated: version 1.3.0, Revit 2019-2025, 676 tests

#### Phase I — OSS-ready
- `CONTRIBUTING.md` — corrected build instructions (named configurations, no `-p:RevitVersion`)
- `.github/ISSUE_TEMPLATE/bug_report.yml`, `feature_request.yml`
- `.github/PULL_REQUEST_TEMPLATE.md`
- `.github/CODEOWNERS`
- `.github/dependabot.yml`

### Changed
- `ExtractArtifactTag` in `GitHubUpdateService` uses `string.Contains(s, StringComparison)` on net8, `IndexOf` on net48 (CA2249)
- `FamilySelectorViewModel` now implements `IObservableRequestClose`
- `PipeConnectEditorViewModel` — consolidated duplicate `IsClosing` property

### Metrics
- Tests: 577 → 676 (+99)
- Revit shipping artifacts covered by automation: 4 (R19, R21, R24, R25)
- Invariants covered: I-01..I-13


### Added
- `.editorconfig` with C# 12 conventions and naming rules
- Code analysis with `AnalysisLevel=latest-recommended`
- `Constants.cs` — centralized unit conversion (`Units.FeetToMm`) and tolerances (`Tolerance.*`)
- `BestSizeMatcher` — deduplicated size-matching algorithm (was copy-pasted 3×)
- `EditFamilySession` helper — eliminates EditFamily boilerplate across Revit services
- `FamilyParameterSnapshot` — safe pre-caching of FamilyManager parameters
- Root `README.md` with project overview and architecture diagram
- MIT License
- `LocalizationService` — static localization service with RU/EN dictionaries and JSON persistence
- `LanguageManager` — ResourceDictionary swap for XAML DynamicResource bindings
- `DynamicSizeLoader` — dynamic family size loading and auto-selection
- `ConnectorCycleService` — free connector cycling with alignment
- `FittingCardBuilder` — builds fitting/reducer card items for UI
- `ConnectExecutor` — validates and executes final ConnectTo operation
- `ChainOperationHandler` — handles chain depth increment/decrement
- `PositionCorrector` — post-connect position correction
- `PipeConnectInitHandler`, `PipeConnectRotationHandler`, `PipeConnectSizeHandler` — decomposed handler services
- XML documentation for all public types in PipeConnect
- `.editorconfig` CA1863 suppression for localized format strings

### Changed
- **Phase 0: Project skeleton** — Solution + Ribbon + DI + .editorconfig + MIT License
- **Phase 1: Foundation** — 10 domain models, 11 interfaces, Vec3, VectorUtils, ConnectorWrapper
- **Phase 2: Basic connect** — FreeConnectorFilter, ElementSelectionService, ConnectorAligner, ExternalEvent pattern
- **Phase 3: Connector types** — MiniTypeSelector, MappingEditor, JSON persistence, EditFamily API
- **Phase 4: Parameter resolution** — MiniFormulaSolver → FormulaSolver (AST), FamilyParameterAnalyzer, RevitLookupTableService
- **Phase 5: Fitting system** — FittingMapper, IFittingInsertService, auto-insert by mapping, size filtering
- **Phase 6: FormulaSolver** — Full AST parser with Evaluate, SolveFor (algebraic + bisection), ParseSizeLookup
- **Phase 7: Chain operations** — ElementChainIterator (BFS), ConnectionGraph, NetworkMover, NetworkSnapshotStore
- **Phase 8: PostProcessing UI** — PipeConnectEditor (modeless), rotation, connector cycling, fitting list, Assimilate/RollBack
- **Phase 9: Refactoring** — ViewModel decomposition (631→384 lines, 5 partial files), 12 handler services extracted
- **Phase 10: Open-source quality** — Localization (RU/EN), XML documentation, log rotation, code quality fixes
- Unified `GuessCtcForFitting` and `GuessCtcForReducer` into single `GuessCtcForElement`
- Unified `InsertFittingSilent` and `InsertFittingSilentNoDynamicAdjust` (single method with parameter)
- `FamilySizeFormatter` now uses `Units.FeetToMm` instead of local constant
- `RevitDynamicSizeResolver.TryGetLookupTableSizes` uses `EditFamilySession` (53→12 lines)
- Both Revit services use `FamilyParameterSnapshot.Build()` instead of inline pre-cache

### Code Quality
- **Phase 5: Exception handling** — documented all 23 catch blocks:
  - Empty catches now have `// Intentional: ...` comments
  - Silent failures now log via `SmartConLogger.Info` for diagnostics
- **Phase 7: XML documentation** — added `<summary>` to all 18 undocumented public types
- **Phase 8: Tests** — added 17 new tests:
  - `BestSizeMatcherTests` (9 tests) — weighted matching, tie-breaking, empty candidates
  - `ConstantsTests` (8 tests) — unit conversion round-trips, tolerance values

### Metrics
- ViewModel: 631 → 384 lines (39% reduction)
- Tests: 545 → 577 (32 new, 0 regressions)
- Handler services extracted: 12
- Localization keys: ~120 (RU + EN)

### Removed
- Duplicate size-matching code in ViewModel (3 copy-paste instances → 1 shared method)
- Duplicate CTC guessing code (2 methods → 1 unified method)
- Duplicate fitting insert code (2 methods → 1 with parameter)
