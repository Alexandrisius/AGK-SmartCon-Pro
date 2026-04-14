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

### Changed
- Unified `GuessCtcForFitting` and `GuessCtcForReducer` into single `GuessCtcForElement`
- Unified `InsertFittingSilent` and `InsertFittingSilentNoDynamicAdjust` (single method with parameter)
- `FamilySizeFormatter` now uses `Units.FeetToMm` instead of local constant
- `RevitDynamicSizeResolver.TryGetLookupTableSizes` uses `EditFamilySession` (53→12 lines)
- Both Revit services use `FamilyParameterSnapshot.Build()` instead of inline pre-cache

### Changed
- **Phase 2: ViewModel decomposition** — split 3,570-line `PipeConnectEditorViewModel.cs` into 5 partial files:
  - `PipeConnectEditorViewModel.cs` (872 lines) — core fields, Init, rotation, size logic
  - `PipeConnectEditorViewModel.Chain.cs` (735 lines) — Increment/Decrement ChainDepth, ConnectAllChain
  - `PipeConnectEditorViewModel.Ctc.cs` (640 lines) — Virtual CTC helpers + Fitting CTC setup
  - `PipeConnectEditorViewModel.Insert.cs` (319 lines) — InsertFitting, InsertReducer, Reassign CTC
  - `PipeConnectEditorViewModel.Connect.cs` (586 lines) — Connect, ValidateAndFixBeforeConnect

### Code Quality
- **Phase 5: Exception handling** — documented all 23 catch blocks:
  - Empty catches now have `// Intentional: ...` comments
  - Silent failures now log via `SmartConLogger.Info` for diagnostics
- **Phase 7: XML documentation** — added `<summary>` to all 18 undocumented public types
- **Phase 8: Tests** — added 17 new tests:
  - `BestSizeMatcherTests` (9 tests) — weighted matching, tie-breaking, empty candidates
  - `ConstantsTests` (8 tests) — unit conversion round-trips, tolerance values

### Removed
- Duplicate size-matching code in ViewModel (3 copy-paste instances → 1 shared method)
- Duplicate CTC guessing code (2 methods → 1 unified method)
- Duplicate fitting insert code (2 methods → 1 with parameter)
