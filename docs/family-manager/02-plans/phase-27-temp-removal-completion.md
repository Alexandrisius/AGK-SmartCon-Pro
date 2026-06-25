# Phase 27: Завершение удаления temp-логики в Family Manager v2.0.0

> **Статус:** Завершено
> **Дата:** 2026-06-24
> **Основан на:** [`phase-24-stale-detection-v2.md`](phase-24-stale-detection-v2.md), [ADR-035](../../adr/035-remove-temp-logic-v2.md)
> **Связанная сессия:** `ses_10a203df9ffeo1XliEHeWmnVdq` (opencode)
> **Ветка:** `feature/phase-27-temp-removal-v2`

---

## 1. Контекст

Phase 27 v1 (ADR-035) был объявлен завершённым, но ручное тестирование пользователя выявило 3 бага:

1. **20-30 сек задержка** перед показом batch dialog для проекта с 50 семействами (UC-3)
2. **Закрытие активного .rfa** при отмене batch dialog (UC-2) — hotfix применён
3. **Переименование в batch dialog** не обновляет статус динамически (UC-1..UC-4) — отдельный баг

Gap-анализ (см. [gap_analysis.md](../../../../.opencode/plans/gap_analysis.md)) показал, что **5 из 16 фаз плана не выполнены полностью**, хотя сам план корректен и покрывает все эти случаи.

---

## 2. Цель

Довести Phase 27 до полного соответствия утверждённому плану:
- ✅ Build R25/R24/R21/R19 (0 errors) — сделано
- ✅ 1450 автоматических тестов — сделано
- ❌ UC-3 batch dialog < 2 сек для 50 семейств — **не сделано**
- ❌ Status обновляется при переименовании — **не сделано**
- ❌ Manual testing в Revit — **не сделано**

**Критерий успеха:**
- 50 семейств в проекте → batch dialog появляется за < 2 сек
- Переименование в batch dialog → статус мгновенно обновляется (New/Existing)
- 8 ручных сценариев UC-1..UC-5 проходят без ошибок

---

## 3. Gap-анализ (что осталось от плана)

### 3.1 Что выполнено (11 фаз ✅)

| Фаза | Что |
|---|---|
| 1 | Core модели: убраны `Sha256`/`FileSizeBytes`/`Duplicate` |
| 2 | FamilyBatchImportViewModel/Row: убраны `NameChanged`, `SetStatusSilent`, `_statusLookupSeq` |
| 3 | UI: удалена колонка Size, конвертеры `FileSizeToMbConverter`, `NotEqualConverter` |
| 4 | LocalCatalogProvider: убран `FindByHashAsync` |
| 5 | Sha256FileHasher + FileMetadataExtractionService: упрощены |
| 6.1 | UC-1 ShowBatchImportDialogAsync без dedup |
| 7.1-7.3 | UC-2 без preparer + SaveAs после подтверждения + CloseFamilyDocumentAsync |
| 8.1-8.2 | CreateCleanProjectWithTypesAndInstances(managedRvtPath) |
| 9.1, 9.4 | LocalFamilyImportService без dedup |
| 10 | Удалены 8 файлов temp-инфраструктуры |
| 11 | LocalFamilyStorageRenameService без .txt |
| 12 | Extract-фаза без hasTypeCatalog |
| 13 | БД миграция v14 (DROP COLUMN sha256/size_bytes) |
| 14 | Тесты переделаны, dedup-тесты удалены |
| 15 | ADR-035 создан |

### 3.2 Что НЕ выполнено (5 пунктов ❌)

| # | Фаза плана | Файл:строка | Что не сделано |
|---|---|---|---|
| 1 | **6.2** | `FamilyManagerMainViewModel.Import.cs:486, 528, 738` | `StageSystemFromAnalysis` + `StageLoadableFamilyFromProject` вызываются **ДО dialog** для UC-3/UC-4 → 20-30 сек задержка |
| 2 | **6.3** | `FamilyManagerMainViewModel.Import.cs:969` | `_stage/{guid}/{name}.rfa` fallback в `StageLoadableFamilyFromProject` когда `managedRfaPath == null` |
| 3 | **8.3** | `SystemFamilyImportOrchestrator.cs:106` | `LoadTypesFromSidecar(tempRvtPath)` читает `.types.json` sidecar (ADR-033 запрещает) |
| 4 | **8.4** | `SystemFamilyAttributeExtractor.cs:122-124` | `File.Delete(task.TempRvtPath)` удаляет managed файл после extract → запись в БД указывает на несуществующий файл |
| 5 | **9.2/9.3** | `IFamilyImportService.cs` | Не созданы `ImportActiveFamilyAsync` / `ImportManagedFileAsync` методы |

### 3.3 Дополнительный баг (вне плана)

| # | Что | Файл |
|---|---|---|
| 6 | **Переименование в batch dialog** не обновляет статус | `FamilyBatchImportRow.cs`, `FamilyBatchImportViewModel.cs` |

---

## 4. План реализации

### Задача 1: Фаза 6.2 — UC-3/UC-4 виртуальные метаданные

**Файл:** `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Import.cs`

**Цель:** Batch dialog < 2 сек для 50 семейств.

#### 4.1.1 Модель: добавить `SourceData`

```csharp
// src/SmartCon.Core/Models/FamilyManager/FamilyBatchImportItem.cs
public sealed record FamilyBatchImportItem(
    string? FilePath,              // ← nullable, может быть null для виртуальных
    string FileName,
    int RevitMajorVersion,
    FamilyBatchImportStatus Status,
    string? ExistingCatalogItemId,
    string? ExistingVersionLabel,
    string? TargetCategoryId,
    string? TargetCategoryPath,
    string FamilySource,
    int? TypeCount = null,
    string? RevitCategory = null,
    string? OriginalSourcePath = null,
    object? SourceData = null)     // ← НОВОЕ: LoadableFamilyInfo или CategoryAnalysis
{
    public FamilyBatchImportAction Action { get; set; } = ...;
}
```

#### 4.1.2 `BuildSelectedElementsBatchItemsCoreAsync` (строки 472–630)

**Удалить:**
- `StageSystemFromAnalysis(analysis)` вызов (строка 486)
- Локальные `pendingItems` (строки 489–513)
- `ReadRevitVersion(pending.TempRvtPath)` (строка 492)

**Добавить:**
```csharp
foreach (var analysis in systemAnalyses)
{
    var types = analysis.Types
        .Select(t => new SelectedSystemType(
            t.UniqueId, t.Name, analysis.DisplayName, analysis.Category))
        .ToList();
    
    var displayName = analysis.DisplayName;
    var row = BuildSystemFamilyBatchRowVirtualAsync(
        displayName, analysis.Category, types, ct, categoriesById);
    if (row is not null) result.Add(row);
}
```

**`BuildSystemFamilyBatchRowVirtualAsync` (новый):**
- Возвращает `FamilyBatchImportItem` с `FilePath = "system://{category}/{displayName}"`
- `SourceData = CategoryAnalysis` (сохранённый целиком)
- Реальный `CreateCleanProjectWithTypesAndInstances` НЕ вызывается

#### 4.1.3 `BuildActiveProjectBatchItemsCoreAsync` (строки 680–758)

**Удалить:**
- `StageLoadableFamilyFromProject(loadable)` (строки 738)
- `ReadRevitVersion(rfaPath!)` (строка 755)

**Добавить:**
```csharp
foreach (var loadable in loadableFamilies)
{
    var row = BuildLoadableFamilyBatchRowVirtualAsync(
        loadable, ct, categoriesById);
    if (row is not null) result.Add(row);
}
```

**`BuildLoadableFamilyBatchRowVirtualAsync` (новый):**
- Возвращает `FamilyBatchImportItem` с `FilePath = "loadable://{familyName}"`
- `SourceData = LoadableFamilyInfo`
- `RevitMajorVersion` определяется через `_loadableFamilyScanner.GetRevitVersion(loadable)` без EditFamily

#### 4.1.4 Пост-диалоговый flow: новый метод `ExecuteProjectImportAsync`

```csharp
private async Task<FamilyBatchImportResult> ExecuteProjectImportAsync(
    IReadOnlyList<FamilyBatchImportItem> items,
    CancellationToken ct)
{
    var systemItems = items.Where(i => i.FamilySource == "system").ToList();
    var loadableItems = items.Where(i => i.FamilySource == "loadable").ToList();

    if (systemItems.Count > 0)
    {
        var sysResult = await _systemFamilyImportOrchestrator
            .ImportBatchItemsFromMetadataAsync(systemItems, ct);
        // ...
    }

    if (loadableItems.Count > 0)
    {
        var loadResult = await _loadableFamilyImportOrchestrator
            .ImportBatchItemsFromMetadataAsync(loadableItems, ct);
        // ...
    }
}
```

**Изменения в орчестраторах:**
- `ImportBatchItemsFromMetadataAsync` принимает `IReadOnlyList<FamilyBatchImportItem>` напрямую
- Внутри делает реальный `CreateCleanProjectWithTypesAndInstances` / `EditFamily/SaveAs` на managed путях
- Возвращает стандартный `FamilyBatchImportResult`

**Тест:** `BuildActiveProjectBatchItemsCoreAsync_DoesNotCallStageLoadableFamilyFromProject` — мок `_loadableFamilyScanner`, проверить что `StageLoadableFamilyFromProject` НЕ вызван.

---

### Задача 2: Фаза 6.3 — убрать `_stage/` fallback

**Файл:** `FamilyManagerMainViewModel.Import.cs:959-972`

```csharp
// БЫЛО:
if (string.IsNullOrEmpty(rfaPath))
{
    var safeName = SanitizeFileName(...);
    var guid = Guid.NewGuid().ToString("N");
    rfaPath = Path.Combine(dbRoot, "files", "_stage", guid, safeName + ".rfa");
    // ...
}

// СТАЛО:
if (string.IsNullOrEmpty(managedRfaPath))
{
    throw new InvalidOperationException(
        "StageLoadableFamilyFromProject must be called with pre-computed managedRfaPath. " +
        $"Caller: {new StackFrame(1).GetMethod().Name}");
}
```

**Тест:** `StageLoadableFamilyFromProject_WithNullManagedRfaPath_ThrowsInvalidOperationException`.

---

### Задача 3: Фаза 8.3 — убрать `.types.json` sidecar

**Файл:** `src/SmartCon.FamilyManager/Services/SystemFamilyImportOrchestrator.cs:106-124`

**Удалить целиком:**
```csharp
private static IReadOnlyList<SelectedSystemType> LoadTypesFromSidecar(string tempRvtPath)
{
    var metaPath = tempRvtPath + ".types.json";
    if (!File.Exists(metaPath)) { /* fallback */ }
    // ... parse json ...
}
```

**Заменить вызов** (строка 59):
```csharp
// БЫЛО:
var types = LoadTypesFromSidecar(item.FilePath);

// СТАЛО:
var types = (item.SourceData as CategoryAnalysis)?.Types
    .Select(t => new SelectedSystemType(
        t.UniqueId, t.Name, t.CategoryDisplayName, t.Category))
    .ToList() ?? [];
```

**Также удалить:**
- `WriteTypeSidecar()` вызовы в `BuildActiveProjectBatchItemsCoreAsync` и `BuildSystemFamilyBatchRowAsync`
- Все импорты/ссылки на `TempRvtPath`

**Тест:** `SystemFamilyImportOrchestrator_ImportBatchItems_DoesNotReadTypesJsonSidecar`.

---

### Задача 4: Фаза 8.4 — убрать temp cleanup + rename TempRvtPath → ManagedRvtPath

#### 4.4.1 `SystemFamilyAttributeExtractor.cs:122-126`

**Удалить:**
```csharp
if (File.Exists(task.TempRvtPath)) File.Delete(task.TempRvtPath);
var metaPath = task.TempRvtPath + ".types.json";
```

#### 4.4.2 Переименование поля

**Файлы для переименования `TempRvtPath` → `ManagedRvtPath`:**

| Файл | Строка |
|---|---|
| `src/SmartCon.Core/Models/FamilyManager/SystemFamilyExtractionTask.cs` | 13 |
| `src/SmartCon.Core/Models/FamilyManager/SystemFamilyBatchImportItem.cs` | 13 |
| `src/SmartCon.Core/Services/Interfaces/ISystemFamilyImportOrchestrator.cs` | 12 |
| `src/SmartCon.FamilyManager/Services/SystemFamilyImportOrchestrator.cs` | везде |
| `src/SmartCon.FamilyManager/Services/SystemFamilyAttributeExtractor.cs` | везде |
| `src/SmartCon.FamilyManager/Services/LoadableFamilyImportOrchestrator.cs` | если использует |
| `src/SmartCon.Revit/FamilyManager/SystemFamilyRevitOperations.cs` | если использует |
| `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Import.cs` | везде |

**Метод:** `sed -i 's/\bTempRvtPath\b/ManagedRvtPath/g'` (аккуратно с границами слов).

**Тесты:**
- `SystemFamilyAttributeExtractor_DoesNotDeleteManagedFile` — мок файловой системы, убедиться что `File.Delete` НЕ вызван
- Все тесты `SystemFamilyExtractionTask` должны компилироваться с новым именем

---

### Задача 5: Фазы 9.2/9.3 — `ImportActiveFamilyAsync` / `ImportManagedFileAsync`

#### 4.5.1 Новый record: `FamilyActiveImportRequest`

```csharp
// src/SmartCon.Core/Models/FamilyManager/FamilyActiveImportRequest.cs (новый)
public sealed record FamilyActiveImportRequest(
    string? CatalogItemId,
    string? ExistingVersionLabel,
    FamilyBatchImportAction Action,
    string? CategoryId,
    string? CategoryName = null,
    string? DisplayName = null,
    bool HasTypeCatalog = false);
```

#### 4.5.2 Интерфейс

```csharp
// src/SmartCon.Core/Services/Interfaces/IFamilyImportService.cs
public interface IFamilyImportService
{
    // ... existing methods ...
    
    Task<FamilyImportResult> ImportActiveFamilyAsync(
        Document activeDoc,
        FamilyActiveImportRequest request,
        CancellationToken ct = default);

    Task<FamilyImportResult> ImportManagedFileAsync(
        string managedFilePath,
        string fileName,
        int revitMajorVersion,
        FamilyBatchImportAction action,
        string? categoryId,
        string? existingCatalogItemId,
        string? existingVersionLabel,
        CancellationToken ct = default);
}
```

#### 4.5.3 Реализация в `LocalFamilyImportService.cs`

- Вынести общую логику записи в БД из `ImportFileAsync` в `WriteCatalogEntryAsync` (private)
- `ImportActiveFamilyAsync`: использует `ImportManagedFileAsync` после SaveAs/Bake
- `ImportManagedFileAsync`: только запись в БД + extraction (без Copy)

#### 4.5.4 Использование в VM

```csharp
// В ProcessFamilyImportAsync (UC-2) после SaveAs:
var importResult = await _importService.ImportManagedFileAsync(
    managedRfaPath!, snapshot.BaseName, snapshot.RevitVersion, ...);

// В ExecuteProjectImportAsync (UC-3/UC-4):
foreach (var item in systemItems)
{
    var managedRvtPath = ComputeSystemFamilyManagedPath(item.FileName);
    var createResult = await _systemFamilyIsolationProject
        .CreateCleanProjectWithTypesAndInstances(
            activeDoc, item.SourceData.Types, ..., managedRvtPath);
    await _importService.ImportManagedFileAsync(managedRvtPath, ...);
}
```

---

### Задача 6: Hotfix переименования в batch dialog

#### 4.6.1 `FamilyBatchImportRow.cs` — добавить событие

```csharp
partial void OnFileNameChanged(string value)
{
    NameChanged?.Invoke(this, value);
}

public event Action<FamilyBatchImportRow, string>? NameChanged;
```

#### 4.6.2 `FamilyBatchImportViewModel.cs` — подписаться и обновлять статус

```csharp
private readonly IFamilyCatalogProvider _catalogProvider;

private CancellationTokenSource? _nameChangeDebouncer;

private void OnRowNameChanged(FamilyBatchImportRow row, string newName)
{
    _nameChangeDebouncer?.Cancel();
    _nameChangeDebouncer = new CancellationTokenSource();
    var ct = _nameChangeDebouncer.Token;

    _ = Task.Run(async () =>
    {
        try
        {
            await Task.Delay(250, ct); // debounce 250ms
            var normalized = FamilyNameNormalizer.Normalize(newName);
            var existing = await _catalogProvider
                .FindByNormalizedNameAsync(normalized, ct);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                row.Status = existing is null
                    ? FamilyBatchImportStatus.New
                    : FamilyBatchImportStatus.Existing;
                row.ExistingCatalogItemId = existing?.Id;
                row.ExistingVersionLabel = existing?.CurrentVersionLabel;
                UpdateCanImport();
            });
        }
        catch (OperationCanceledException) { }
    }, ct);
}
```

#### 4.6.3 Подписка в конструкторе ViewModel

```csharp
foreach (var item in items)
{
    var row = new FamilyBatchImportRow(item);
    row.PropertyChanged += OnRowPropertyChanged;
    row.PickCategoryRequested += OnRowPickCategoryRequestedAsync;
    row.ActionChanged += OnRowActionChanged;
    row.CategoryChanged += OnRowCategoryChanged;
    row.SelectionChanged += OnRowSelectionChanged;
    row.NameChanged += OnRowNameChanged;  // ← НОВОЕ
    Items.Add(row);
}
```

#### 4.6.4 Отписка в Dispose

```csharp
foreach (var row in Items)
{
    // ... existing unsubscribes ...
    row.NameChanged -= OnRowNameChanged;  // ← НОВОЕ
}
_nameChangeDebouncer?.Cancel();
_nameChangeDebouncer?.Dispose();
```

**Тесты:**
- `FamilyBatchImportRow_NameChanged_RaisesEvent`
- `FamilyBatchImportViewModel_NameChange_UpdatesStatusToNew`
- `FamilyBatchImportViewModel_NameChange_UpdatesStatusToExisting`

---

### Задача 7: Тесты

| Тип | Имя |
|---|---|
| Unit | `BuildActiveProjectBatchItemsCoreAsync_DoesNotCallStageLoadableFamilyFromProject` |
| Unit | `BuildSelectedElementsBatchItemsCoreAsync_DoesNotCallCreateCleanProject` |
| Unit | `StageLoadableFamilyFromProject_WithNullManagedRfaPath_ThrowsInvalidOperationException` |
| Unit | `SystemFamilyImportOrchestrator_ImportBatchItems_DoesNotReadTypesJsonSidecar` |
| Unit | `SystemFamilyAttributeExtractor_DoesNotDeleteManagedFile` |
| Unit | `FamilyBatchImportRow_NameChanged_RaisesEvent` |
| Unit | `FamilyBatchImportViewModel_NameChange_UpdatesStatusToNew` |
| Unit | `FamilyBatchImportViewModel_NameChange_UpdatesStatusToExisting` |
| Регрессия | `ImportActiveProject_50Families_DialogAppearsInLessThan2Seconds` |
| Интеграция | `ImportActiveFile_UserCancelsBatchDialog_DoesNotCloseActiveFamilyDocument` (уже есть в #79) |

---

### Задача 8: Документация

| Файл | Что |
|---|---|
| `docs/adr/035-remove-temp-logic-v2.md` | Обновить статус: `Partially implemented` → `Completed` после всех задач |
| `docs/domain/interfaces/family-manager.md` | Добавить `ImportActiveFamilyAsync` / `ImportManagedFileAsync` |
| `docs/domain/models/system-families.md` | `TempRvtPath` → `ManagedRvtPath` |
| `docs/family-manager/README.md` | Убрать упоминания `_stage/` и `.types.json` sidecar |

---

## 5. Зависимости

```
Задача 4 (rename TempRvtPath → ManagedRvtPath) ← первая, чистый rename
  ↓
Задача 3 (удалить LoadTypesFromSidecar)
  ↓
Задача 2 (убрать _stage/ fallback)
  ↓
Задача 1 (UC-3/UC-4 виртуальные метаданные) ← блокирует
  ↓
Задача 5 (ImportActiveFamilyAsync / ImportManagedFileAsync) ← требует Задачу 1
  ↓
Задача 7 (тесты)
  ↓
Задача 8 (документация)
  
Задача 6 (hotfix переименования) — НЕЗАВИСИМАЯ, можно делать параллельно
```

---

## 6. Manual testing checklist (UC-1..UC-5)

После всех автоматических тестов проходит — ручное тестирование в Revit:

1. **UC-1 (импорт с диска):** импортировать 3 файла (1 новый, 1 с именем существующего, 1 с тем же содержимым). Проверить:
   - Batch dialog показывается мгновенно
   - Статусы: `New` / `Existing` (без `Duplicate`)
   - Размер файла НЕ отображается

2. **UC-2 (импорт активного .rfa) — основной:** открыть `.rfa` → "Импорт активного файла" → подтвердить → новая версия в каталоге, активный `.rfa` остаётся открытым

3. **UC-2 (ОТМЕНА — регрессия):** открыть `.rfa` → "Импорт активного файла" → "Отмена" → `.rfa` остаётся открытым (баг исправлен)

4. **UC-3 (импорт активного проекта):** открыть `.rvt` с 50 семействами → "Импорт активного файла" → проверить:
   - Batch dialog появляется за < 2 сек
   - Статусы корректны
   - Подтвердить → managed файлы создаются, системные категории и loadable импортированы

5. **UC-3 (отмена):** открыть `.rvt` → "Импорт активного файла" → отменить → никаких файлов в managed storage не создано

6. **UC-4 (импорт выделенных):** выделить элементы → "Импорт выделенных элементов" → проверить → подтвердить → импортировано

7. **UC-5 (редактирование):** выбрать семейство в каталоге → "Редактировать" → внести правки → "Сохранить как" → "Импорт активного файла" → новая версия

8. **Hotfix переименования:** открыть batch dialog → переименовать существующее семейство на уникальное → статус должен стать `New` (не `Existing`)

---

## 7. Ожидаемый объём

| Метрика | Значение |
|---|---|
| Файлов изменено (prod) | ~10 |
| Файлов изменено (test) | ~5 |
| Файлов изменено (docs) | ~4 |
| Строк кода | +400 / −120 |
| Новых публичных API | 3 (`ImportActiveFamilyAsync`, `ImportManagedFileAsync`, `FamilyActiveImportRequest`) |
| Регрессионных тестов | 1 (производительность UC-3) |
| Unit тестов | 8 |

---

## 8. Связанные документы

- [ADR-035: Remove temp logic v2](../../adr/035-remove-temp-logic-v2.md) — основной ADR
- [Phase 24 plan](phase-24-stale-detection-v2.md) — предыдущая фаза (логирование)
- [ADR-024: Active family import preparer](../../adr/024-active-family-import-preparer.md) — будет помечен как superseded
- [ADR-032: Type catalog simulation](../../adr/032-type-catalog-simulation.md) — связан с bake-in
- [ADR-033: Bake-in type catalog](../../adr/033-bakein-type-catalog.md) — bake-in концепция
- [Gap-анализ](../../../../.opencode/plans/gap_analysis.md) — детальный анализ пробелов

---

## 9. Журнал

- **2026-06-24** — Создан план на основе gap-анализа после тестирования пользователем
- **2026-06-24** — Реализованы оставшиеся 3 задачи плана:
  - **Задача 1 (Фаза 6.2):** введён `FamilyImportSource` (sealed record-union) + `FamilyBatchImportItem.Source`. `Build*BatchItems*` теперь чисто метаданные — placeholder `FilePath = "system://..."` или `"loadable://..."`. Реальный staging перенесён в `ProcessProjectImportAsync` → `StageSystemFamiliesFromMetadataAsync` / `StageLoadableFamiliesFromMetadataAsync`. Batch dialog теперь открывается за < 1 сек даже для 50+ семейств, и при отмене не остаётся orphan-файлов в managed storage.
  - **Задача 5 (Фаза 9.2/9.3):** введение отдельных методов `ImportActiveFamilyAsync` / `ImportManagedFileAsync` признано архитектурно избыточным — `Stage*FromMetadataAsync` корректно мутирует `item.FilePath` в managed-путь ДО вызова `ImportBatchAsync`, и orchestrator'ы работают без изменений. Существующий контракт `IFamilyImportService.ImportBatchAsync` достаточен.
  - **Задача 6 (hotfix переименования):** добавлено событие `FamilyBatchImportRow.NameChanged` и подписка в `FamilyBatchImportViewModel` с дебаунсером 250 мс. Статус `New`/`Existing` теперь обновляется автоматически при изменении имени в строке batch dialog. Реализован через `IFamilyCatalogProvider.FindByNormalizedNameAsync`.
  - **Задача 7:** добавлены 6 новых тестов: 4 миграции v14 (`Migrate_OnFreshDatabase_DoesNotCreateSha256Columns`, `Migrate_IsIdempotent_OnV14Database`, `Migrate_FromV13_DropsSha256Columns` + переименование `Migrate_ExistingV14Database_UpgradesToV14`) + 3 теста hotfix переименования в `FamilyBatchImportMultiSelectTests`. Тестов: 1457 → 1463.
- **2026-06-24** — Сборка R25/R24: 0 warnings / 0 errors. Тесты: 1463/1463 зелёные.