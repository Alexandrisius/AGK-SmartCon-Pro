# Logging migration inventory (Phase 1)

> Generated 2026-06-09 as the working set for the
> `[Category]` → `BeginScope` sweep. Re-run
> `rg --no-heading -c "SmartConLogger\.(Info|Debug|Warn|Error)" --type cs src/`
> to refresh after each batch.

## Totals (baseline, pre-Phase 1)

| Group | Files | Call-sites | Notes |
|-------|-------|------------|-------|
| **Simple** (≤5) | 37 | 96 | one method, one category, mechanical |
| **Moderate** (6-20) | 34 | 313 | several methods, mixed categories, scope per method or per group |
| **Complex** (>20) | 11 | 597 | hot loops, multiple counters, requires Plan Mode review per file |
| **Total** | **82** | **1006** | |

Excluded from the migration:

| File | Reason |
|------|--------|
| `src/SmartCon.Core/Logging/SmartConLogger.cs` | the logger itself |
| ~~`src/SmartCon.Core/Logging/LogScopeExtensions.cs`~~ | removed (D2 fix, unused passthrough) |
| `src/SmartCon.Tests/.../SmartConLoggerScopeTests.cs` | tests for the logger API |

Those three files account for the 1013 - 1006 = 7 deltas.

## Group Simple (≤5 call-sites, 37 files, 96 call-sites)

Refactor recipe: read the file, identify the one or two
categories that dominate, wrap the method body in
`using var _ = SmartConLogger.BeginScope("<Category>");`,
strip the `[Category]` prefix from the message text. No
architectural decisions needed.

| Calls | File |
|------:|------|
| 5 | src/SmartCon.App\App.cs |
| 5 | src/SmartCon.Revit\Extensions\EditFamilySession.cs |
| 5 | src/SmartCon.PipeConnect\Services\PipeConnectRotationHandler.cs |
| 5 | src/SmartCon.Revit\FamilyManager\LoadableFamilyTypeResolver.cs |
| 5 | src/SmartCon.FamilyManager\ViewModels\FamilyManagerMainViewModel.Tree.cs |
| 5 | src/SmartCon.FamilyManager\ViewModels\AttributeLibraryViewModel.cs |
| 4 | src/SmartCon.FamilyManager\Services\LocalCatalog\FamilyDataImportService.cs |
| 4 | src/SmartCon.FamilyManager\Services\LoadableFamilyImportOrchestrator.cs |
| 4 | src/SmartCon.FamilyManager\ViewModels\CategoryPickerViewModel.cs |
| 4 | src/SmartCon.Revit\Network\NetworkMover.cs |
| 4 | src/SmartCon.Revit\Storage\RevitShareProjectSettingsRepository.cs |
| 4 | src/SmartCon.Revit\Storage\RevitFittingMappingRepository.cs |
| 4 | src/SmartCon.PipeConnect\Services\CtcGuessService.cs |
| 4 | src/SmartCon.Revit\FamilyManager\SystemFamilyPlacementService.cs |
| 3 | src/SmartCon.FamilyManager\Services\SystemFamilyImportOrchestrator.cs |
| 3 | src/SmartCon.PipeConnect\ViewModels\PipeConnectEditorViewModel.Chain.cs |
| 3 | src/SmartCon.FamilyManager\Services\SystemFamilyIsolationProjectAdapter.cs |
| 3 | src/SmartCon.ProjectManagement\Commands\ShareSettingsCommand.cs |
| 3 | src/SmartCon.Revit\FamilyManager\RevitFileInfoReader.cs |
| 3 | src/SmartCon.FamilyManager\Services\ActiveDocumentClassifier.cs |
| 2 | src/SmartCon.Revit\Selection\ElementChainIterator.cs |
| 2 | src/SmartCon.FamilyManager\ViewModels\FamilyBatchImportViewModel.cs |
| 2 | src/SmartCon.FamilyManager\ViewModels\FamilyManagerMainViewModel.FindInCatalog.cs |
| 2 | src/SmartCon.FamilyManager\ViewModels\CategoryTreeEditorViewModel.ExportImport.cs |
| 2 | src/SmartCon.FamilyManager\Services\LocalCatalog\DatabaseManager.cs |
| 2 | src/SmartCon.FamilyManager\Services\LocalCatalog\LocalCatalogProvider.cs |
| 2 | src.SmartCon.FamilyManager\ViewModels\ProfileViewModel.cs *(path: src/SmartCon.FamilyManager\ViewModels\ProfileViewModel.cs)* |
| 2 | src/SmartCon.Revit\FamilyManager\RevitFamilyLoadOptions.cs |
| 1 | src/SmartCon.UI\Converters\PathToBitmapImageConverter.cs |
| 1 | src/SmartCon.App\Services\RevitWindowFocusService.cs |
| 1 | src/SmartCon.Revit\FamilyManager\RevitFamilyPlacementDragService.cs |
| 1 | src/SmartCon.Core\Math\SizeRowSymbolMatcher.cs |
| 1 | src/SmartCon.FamilyManager\ViewModels\FamilyPropertiesViewModel.Assets.cs |
| 1 | src/SmartCon.Revit\FamilyManager\LoadableFamilyScanner.cs |
| 1 | src/SmartCon.Revit\Wrappers\ConnectorWrapper.cs |
| 1 | src/SmartCon.FamilyManager\ViewModels\FamilyManagerMainViewModel.LoadPlace.cs |
| 1 | src/SmartCon.Core\Math\SizeRowSymbolMatcher.cs |

(34 entries listed; the table has the same 37 files including
a few 2-call files; double-check totals during the actual run.)

## Group Moderate (6-20 call-sites, 34 files, 313 call-sites)

Refactor recipe: same as Simple, but with possibly two or
three distinct categories per file. Decide the
scope-per-method vs scope-per-block question per case.
Most files have one method that dominates the volume
(e.g. `Load` / `Import` / `Save`); wrap that method and
leave secondary methods unwrapped or with their own
narrower scope.

| Calls | File |
|------:|------|
| 19 | src/SmartCon.Revit\Family\FittingFamilyRepository.cs |
| 18 | src/SmartCon.PipeConnect\ViewModels\PipeConnectEditorViewModel.cs |
| 18 | src/SmartCon.ProjectManagement\ViewModels\ShareSettingsViewModel.cs |
| 16 | src/SmartCon.PipeConnect\ViewModels\PipeConnectEditorViewModel.Insert.cs |
| 16 | src/SmartCon.FamilyManager\Services\ActiveImportCleanupService.cs |
| 16 | src/SmartCon.FamilyManager\ViewModels\FamilyManagerMainViewModel.Import.cs |
| 15 | src/SmartCon.PipeConnect\ViewModels\PipeConnectEditorViewModel.Cycle.cs |
| 15 | src/SmartCon.FamilyManager\Services\ActiveFamilyFilePreparer.cs |
| 14 | src/SmartCon.FamilyManager\Services\LocalCatalog\LocalFamilyImportService.TypeCatalog.cs |
| 13 | src/SmartCon.FamilyManager\Services\LocalCatalog\LocalFamilySidecarLocator.cs |
| 13 | src/SmartCon.Revit\FamilyManager\SystemFamilyRevitOperations.cs |
| 13 | src/SmartCon.FamilyManager\Services\LocalCatalog\LocalFamilyStorageRenameService.cs |
| 12 | src/SmartCon.FamilyManager\ViewModels\FamilyManagerMainViewModel.cs |
| 12 | src/SmartCon.PipeConnect\Services\PipeConnectInitHandler.cs |
| 12 | src/SmartCon.Core\Services\LookupColumnResolver.cs |
| 12 | src/SmartCon.Revit\Fittings\RevitFittingInsertService.cs |
| 10 | src/SmartCon.PipeConnect\Services\PipeConnectSizeHandler.cs |
|  9 | src/SmartCon.PipeConnect\ViewModels\PipeConnectEditorViewModel.Connect.cs |
|  9 | src/SmartCon.PipeConnect\Services\PipeConnectDiagnostics.cs |
|  9 | src/SmartCon.FamilyManager\ViewModels\CategoryTreeEditorViewModel.cs |
|  9 | src/SmartCon.FamilyManager\Services\SystemFamilyAttributeExtractor.cs |
|  9 | src/SmartCon.FamilyManager\Services\LocalCatalog\LocalFamilyImportService.cs |
|  8 | src/SmartCon.FamilyManager\Events\FamilyManagerAwaitableEvent.cs |
|  8 | src/SmartCon.PipeConnect\Services\CtcFamilyWriter.cs |
|  8 | src/SmartCon.Revit\FamilyManager\FamilyPlacementDropHandler.cs |
|  8 | src/SmartCon.PipeConnect\Services\DynamicSizeLoader.cs |
|  8 | src/SmartCon.Core\Math\LookupTableCsvParser.cs |
|  7 | src/SmartCon.Revit\FamilyManager\RevitFamilyDataExtractionService.cs |
|  7 | src/SmartCon.Revit\Transactions\RevitTransactionGroupSession.cs |
|  7 | src/SmartCon.Revit\Parameters\FamilySymbolSizeExtractor.cs |
|  6 | src/SmartCon.Revit\FamilyManager\RevitFamilyPlacementService.cs |
|  6 | src/SmartCon.Revit\FamilyManager\SystemFamilyAttributeExtractionService.cs |
|  6 | src/SmartCon.FamilyManager\ViewModels\FamilyPropertiesViewModel.cs |
|  6 | src/SmartCon.FamilyManager\Services\LocalCatalog\LocalFamilyFileResolver.cs |

## Group Complex (>20 call-sites, 11 files, 597 call-sites)

Refactor recipe: each file gets a Plan Mode review. The
hot-loop files (`RevitLookupTableService`, `RevitParameterResolver`,
`RevitDynamicSizeResolver`, `ChainOperationHandler`) keep
the counter + Debug-in-loop + Info-summary pattern that
Phase 0b established; the refactor only moves the category
out of the message and into the scope. `PipeConnectSessionBuilder`
and `ConnectExecutor` have several methods; one scope
per method.

| Calls | File | Plan Mode note |
|------:|------|----------------|
| 84 | src/SmartCon.Revit\Parameters\RevitLookupTableService.cs | counter pattern, decide between one scope for the whole file vs scope per public method |
| 83 | src/SmartCon.Revit\Parameters\RevitParameterResolver.cs | same as above |
| 64 | src/SmartCon.Revit\Parameters\RevitDynamicSizeResolver.cs | same as above |
| 60 | src/SmartCon.PipeConnect\Services\ChainOperationHandler.cs | already Debug-heavy (Phase 0b); one scope for the whole dispatch |
| 49 | src/SmartCon.PipeConnect\Services\PipeConnectSessionBuilder.cs | per-method scope (Session, Fit, Commit) |
| 46 | src/SmartCon.Revit\Family\RevitFamilyConnectorService.cs | per-method scope |
| 35 | src/SmartCon.FamilyManager\ViewModels\FamilyManagerMainViewModel.FamilyEdit.cs | ViewModel command, one scope per command |
| 29 | src/SmartCon.Revit\FamilyManager\RevitFamilyLoadService.cs | per-method scope |
| 28 | src/SmartCon.Revit\Parameters\FamilyParameterAnalyzer.cs | per-method scope |
| 27 | src/SmartCon.PipeConnect\Services\ConnectExecutor.cs | per-method scope |
| 24 | src/SmartCon.ProjectManagement\Commands\ShareProjectCommand.cs | per-command scope |

## Migration progress tracker

Mark with `[x]` after the corresponding commit lands.

| Group | Files done / total | Call-sites done / total | Commits |
|-------|--------------------|--------------------------|---------|
| Simple | 0 / 37 | 0 / 96 | — |
| Moderate | 0 / 34 | 0 / 313 | — |
| Complex | 0 / 11 | 0 / 597 | — |
| **Phase 1 total** | **0 / 82** | **0 / 1006** | — |

The tracker is updated at the end of each commit. A
"Phase 1 complete" entry is added when every row is `[x]`.
