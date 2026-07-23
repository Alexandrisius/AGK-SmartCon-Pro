---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager — Импорт и staging

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## IFamilyImportService

Оркестрация импорта семейств: запись в managed storage, запись в БД каталога. v2.0.0: SHA-256 dedup и `.txt` sidecar copy убраны. Метод `ImportBatchAsync` принимает уже подготовленные `FamilyBatchImportItem` (с реальным `FilePath` в managed storage или с placeholder + `Source` payload, который `ProcessProjectImportAsync` резолвит ДО передачи).

**Файл:** `IFamilyImportService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.cs`

```csharp
public interface IFamilyImportService
{
    Task<FamilyImportResult> ImportFileAsync(FamilyImportRequest request, CancellationToken ct = default);
    Task<FamilyBatchImportResult> ImportFolderAsync(FamilyFolderImportRequest request, IProgress<FamilyImportProgress>? progress, CancellationToken ct = default);
    Task<FamilyImportResult> UpdateFamilyAsync(FamilyUpdateRequest request, CancellationToken ct = default);
    Task<FamilyBatchImportResult> ImportBatchAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        string? categoryId,
        IProgress<FamilyImportProgress>? progress,
        CancellationToken ct = default);
}
```

> **Архитектурное примечание (v2.0.0):** ранний план предлагал ввести отдельные методы `ImportActiveFamilyAsync(Document, ...)` и `ImportManagedFileAsync(string managedPath, ...)`. Реализация пошла по более простому пути: VM-слой сам делает `SaveAs(managedRfaPath)` в активном документе (UC-2) или `StageLoadableFamilyFromProject` / `CreateCleanProjectWithTypesAndInstances` (UC-3/UC-4), затем `item.FilePath` перезаписывается на managed-путь и orchestrator (`SystemFamilyImportOrchestrator` / `LoadableFamilyImportOrchestrator`) вызывает существующий `ImportBatchAsync`. Это сохраняет `IFamilyImportService` компактным (1 import-path для всех 4 use-case'ов) и убирает необходимость в `FamilyActiveImportRequest` record.

---

## IFamilyTypeCatalogBaker

Запекание Type Catalog (.txt) в .rfa при импорте (ADR-033). Реализация выполняет все Revit API вызовы на UI thread.

**Unit conversion (BAKE-006..009):** baker вызывает `RevitUnitsCompat.CatalogCellToInternalUnits(raw, annotation, param)` для каждой `StorageType.Double` колонки с `##TYPE##UNITS` annotation. Конвертация пропускается для: не-Double storage, dimensionless parameters, отсутствующей annotation (legacy behavior — raw value). На failure path (annotation не распознана) пишется `Warn` + skip parameter, агрегируется в `BakeStats.Failed` для Info summary. На success path значение конвертируется через `UnitUtils.ConvertToInternalUnits` после валидации `UnitUtils.IsValidUnit(targetSpec, sourceUnit)`. Pure normalization вынесен в `TypeCatalogUnitAlias.Normalize` (SmartCon.Core, fully unit-tested).

**Файл:** `IFamilyTypeCatalogBaker.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyTypeCatalogBaker.cs`

```csharp
public interface IFamilyTypeCatalogBaker
{
    Task<FamilyTypeCatalogBakingResult> BakeAsync(
        string sourceRfaPath,
        TypeCatalogParseResult catalog,
        string outputRfaPath,
        CancellationToken ct = default);

    Task<FamilyTypeCatalogBakingResult> BakeInExistingDocumentAsync(
        object familyDoc,
        TypeCatalogParseResult catalog,
        CancellationToken ct = default);
}
```

**Два метода:**
- `BakeAsync` — открывает .rfa по пути, bake, SaveAs, Close (Commit-фаза, ADR-033)
- `BakeInExistingDocumentAsync` (ADR-039) — bake в уже открытом family document
  (Prepare-фаза). `object familyDoc` — opaque Document (I-09). Без
  OpenDocumentFile/SaveAs/Close. Используется Phase 27B для bake в held-open
  документе до extraction snapshot.

**Зависимости:** `IFamilyManagerAwaitableEvent`, `ITransactionService`, `ITypeCatalogValueApplier`, `IFormulaSolver`. Все Revit API операции маршалятся через `IFamilyManagerAwaitableEvent` callback (I-01).

---

## IFamilyDataImportRunRepository

CRUD для запусков импорта данных семейств (FamilyDataImportRun).

**Файл:** `IFamilyDataImportRunRepository.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyDataImportRunRepository.cs`

```csharp
public interface IFamilyDataImportRunRepository
{
    Task<FamilyDataImportRun?> GetLatestRunAsync(string catalogItemId, CancellationToken ct = default);
    Task<IReadOnlyList<FamilyDataImportRun>> GetRunsForItemAsync(string catalogItemId, CancellationToken ct = default);
    Task<FamilyDataImportRun> CreateRunAsync(FamilyDataImportRun run, CancellationToken ct = default);
    Task<FamilyDataImportRun> UpdateRunAsync(string runId, FamilyDataImportStatus status, int typesCount, DateTimeOffset completedAtUtc, string? errorMessage, CancellationToken ct = default);
}
```

---

## IFamilyDataImportService

Оркестрация импорта данных семейств: подготовка, извлечение, сохранение значений атрибутов.

**Файл:** `IFamilyDataImportService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/FamilyDataImportService.cs`

```csharp
public sealed record FamilyDataImportResult(
    bool Success,
    string? RunId,
    int TypesCount,
    int AttributesFoundCount,
    int AttributesMissingCount,
    string? ErrorMessage);

public sealed record FamilyExtractionPrepareResult(
    bool Success,
    FamilyCatalogItem? Item,
    string? ResolvedFilePath,
    IReadOnlyList<string> ParameterNames,
    string? ErrorMessage);

public interface IFamilyDataImportService
{
    Task<FamilyDataImportResult> ImportDataAsync(string catalogItemId, CancellationToken ct = default);
    Task<FamilyExtractionPrepareResult> PrepareExtractionAsync(string catalogItemId, int targetRevitVersion, CancellationToken ct = default);
    Task<FamilyDataImportResult> SaveExtractionResultAsync(string catalogItemId, FamilyExtractionResult extractionResult, string? versionId, string? fileId, CancellationToken ct = default);
}
```

---

## IFamilyVersionWriter

Единая точка записи `FamilyVersion` маркера в ExtensibleStorage (ADR-030 Phase 24). Раньше дублировалось в `LoadPlace` (2 места) и `StaleFamilyUpdater` (1 место). Теперь — один helper.

**Файл:** `IFamilyVersionWriter.cs`

```csharp
public interface IFamilyVersionWriter
{
    Task WriteVersionMarkerAsync(
        string catalogItemId,
        string familyName,
        string? versionLabel,
        int targetRevit,
        CancellationToken ct);
}
```

**Реализация:** `FamilyVersionWriter` (в `SmartCon.FamilyManager/Services/Stale/`). Внутри — `RaiseAsyncTask` для `FindByName` + `WriteToLoadedFamily` на Revit main thread. No-op если семейство не загружено в проект (например, при `LoadFamilySymbol` без `LoadFamily`).

---

## IFamilyImportPrecomputer

v2.0.0: single-purpose сервис, аллоцирующий каноническую `PrecomputedImportTriple` для заданного `displayName` без файлового I/O. Выделен из `IFamilyImportService` для соблюдения SRP: импорт-pipeline (запись файлов + обновление БД) и trivial DB-lookup + path-математика, которую каждая строка batch dialog делает заранее, — это разные ответственности.

**Файл:** `IFamilyImportPrecomputer.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportPrecomputer.cs`

```csharp
public interface IFamilyImportPrecomputer
{
    Task<PrecomputedImportTriple?> BuildPrecomputedTripleAsync(
        string displayName,
        string extension,
        string? forcedCatalogItemId = null,
        CancellationToken ct = default);
}
```

Семантика:
- `forcedCatalogItemId` (Issue #126): когда дедуп-сервис сматчил строку к существующему айтему ПО ХЭШУ (возможно под другим именем), caller передаёт id этого айтема — triple целится в него (его next version label + managed path в ЕГО папке), а не в name-lookup. Иначе IncrementVersion писал бы файл в сиротскую GUID-папку и падал на UNIQUE constraint со стейл `v1`.
- Без `forcedCatalogItemId`: нормализует `displayName` через `FamilyNameNormalizer` и ищет existing item через `IFamilyCatalogProvider.FindByNormalizedNameAsync`.
- Если existing найден — возвращает `(existing.Id, ComputeNextVersionLabel(existing.Id), ComputeManagedFilePath(...))`: id сохраняется, версия инкрементируется (`vN → vN+1`).
- Если existing не найден — возвращает `(Guid.NewGuid() в формате "N", "v1", ComputeManagedFilePath(...))`.
- `extension` — расширение с ведущей точкой (`".rfa"` для loadable, `".rvt"` для system); `".rfa"` default для null/empty.
- Возвращает `null` когда активная БД не имеет `GetDatabaseRoot()` (caller обязан surfacing this как user error, не fallback на temp-папку — v2.0.0 не использует temp-папки, см. ADR-035).

Используется в **обоих** сценариях, где нужен precomputed triple:
- Initial dialog build (`BuildSystemFamilyBatchRowVirtualAsync` / `BuildLoadableFamilyBatchRowVirtualAsync` в `FamilyManagerMainViewModel.Import.cs`) — при первом построении списка строк.
- Dialog rename handler (`FamilyBatchImportViewModel.OnRowNameChanged`) — при переименовании пользователем строки в batch dialog.

Без единого источника истины эти два пути могут разойтись: `BuildSystem` использует `existingByName` из прямого lookup в `IFamilyCatalogProvider`, а `OnRowNameChanged` — `existing` из `IFamilyCatalogProvider` (тот же provider, но в другом контексте). Преcomputers заменяет оба на один вызов, гарантируя что `CatalogItemId`/`VersionLabel`/`ManagedPath` всегда согласованы и перевычисляются атомарно.

`OnRowNameChanged` дополнительно оборачивает вызов в `Task.Delay(250 ms)` debouncer, чтобы DB-lookup не срабатывал на каждое нажатие клавиши. Cancellation token отменяет предыдущий pending-вызов при следующем нажатии.

---

## IFamilyBatchImportExecutor (Issue #127)

Точка входа выполнения batch-импорта после подтверждения диалога. Реализации: `FileFamilyBatchImportExecutor` (UC-1, импорт .rfa) и `ProjectFamilyBatchImportExecutor` (UC-3/UC-4, импорт из проекта). Поэлементный цикл stage → import → extract с отчётами через `IProgress<FamilyBatchImportProgress>` и cooperative-паузой через `PauseGate`. Отмена — между элементами (текущий дорабатывает); `WasStopped` в результате.

**Файл:** `IFamilyBatchImportExecutor.cs`
**Реализации:** `SmartCon.FamilyManager/Services/Import/FileFamilyBatchImportExecutor.cs`, `ProjectFamilyBatchImportExecutor.cs`

```csharp
public interface IFamilyBatchImportExecutor
{
    Task<FamilyBatchImportExecutionResult> ExecuteAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        string? categoryId,
        IProgress<FamilyBatchImportProgress>? progress,
        PauseGate? pauseGate,
        CancellationToken ct);
}
```

---

## IFileFamilyStagingService (Issue #127)

Staging UC-1: `SaveAs` held-open документа (открытого в Phase 1 Prepare) в precomputed managed path. Весь Revit API — только внутри `IFamilyManagerAwaitableEvent.RaiseAsync`. Ошибка staging не фатальна — возвращается исходный item (fallback на copy/bake внутри импорта).

**Файл:** `IFileFamilyStagingService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Import/FileFamilyStagingService.cs`

```csharp
public interface IFileFamilyStagingService
{
    Task<FamilyBatchImportItem> StageAsync(FamilyBatchImportItem item, CancellationToken ct);
    Task CloseAllPreparedDocumentsAsync(CancellationToken ct);
}
```

---

## IProjectFamilyStagingService (Issue #127)

Staging UC-3/UC-4: system — создание mini-.rvt через `CreateCleanProjectWithTypesAndInstances`; loadable — `EditFamily` + `SaveAs` в managed storage (или `SaveAs` из held-open документа Prepare-фазы). `null` из Stage* — staging не удался, элемент помечается Error, цикл продолжается.

**Файл:** `IProjectFamilyStagingService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Import/ProjectFamilyStagingService.cs`

```csharp
public interface IProjectFamilyStagingService
{
    Task<FamilyBatchImportItem?> StageSystemAsync(FamilyBatchImportItem item, CancellationToken ct);
    Task<FamilyBatchImportItem?> StageLoadableAsync(FamilyBatchImportItem item, CancellationToken ct);
    Task CloseAllPreparedDocumentsAsync(CancellationToken ct);
}
```

---

## IFamilyImportPreparationService (Issue #127)

Тестовый шов (seam) над `FamilyImportPreparationService` (sealed) — только методы, нужные staging-сервисам: доступ к held-open документам и их закрытие. `Document` — opaque parameter (I-09), вызовы методов документа — только внутри `RaiseAsync` в реализациях staging.

**Файл:** `IFamilyImportPreparationService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/FamilyImportPreparationService.cs`

```csharp
public interface IFamilyImportPreparationService
{
    Task CloseAllPreparedDocumentsAsync(CancellationToken ct = default);
    Document? GetOpenedDocument(string sourcePath);
    void CloseAndRelease(string sourcePath);
}
```
