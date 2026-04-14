# Changelog

All notable changes to SmartCon will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

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
