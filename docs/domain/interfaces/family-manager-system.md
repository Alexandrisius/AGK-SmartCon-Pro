---
module: family-manager-system-interfaces
---
# System Families интерфейсы

> Загружать: при работе с импортом системных семейств из активного проекта.
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## ISystemFamilyRevitOperations

Низкоуровневые операции Revit для системных семейств: picker (system + loadable),
анализ активного проекта по 14 категориям, копирование размещённых типов в чистый .rvt
с размещением инстансов на сетке 2×2 м. Все методы **должны вызываться внутри ExternalEvent**
(Revit UI thread).

**Файл:** `ISystemFamilyRevitOperations.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/SystemFamilyRevitOperations.cs`

```csharp
public interface ISystemFamilyRevitOperations
{
    SelectedElementsAnalysis PickSelectedElements();
    IReadOnlyList<CategoryAnalysis> AnalyzeActiveProject(Document activeDoc);
    CreateCleanProjectResult CreateCleanProjectWithTypesAndInstances(
        Document sourceDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory category,
        string displayName);
}
```

**Заметки по реализации:**
- `PickSelectedElements` (Phase 22, заменил `PickSystemTypes`) использует `AnyElementSelectionFilter`:
  пропускает `FamilyInstance` (loadable) + элементы в `SystemCategoryRegistry.SupportedCategories`
  (system). Для каждого `FamilyInstance` через `fi.Symbol?.Family` получает уникальный
  `Family` и через `GroupBy` дедуплицирует выбор. Возвращает `SelectedElementsAnalysis`
  с двумя списками: `SystemTypes` + `LoadableFamilies`.
- `AnalyzeActiveProject` использует `CategoryCompat.GetBuiltInCategory` для
  кросс-TFM-резолвинга `Category → BuiltInCategory` (Revit 2022+: `Category.BuiltInCategory`;
  Revit 2019–2021: guarded cast).
- `CreateCleanProjectWithTypesAndInstances` пишет результат в
  `%TEMP%\SmartCon\SystemFamilyLoadFromProject\<GUID>\<safeName>.rvt` — путь берётся из
  `SystemFamilyTempLayout` (single source of truth для cleanup).
- **Удалено (Phase 11)**: legacy `CreateCleanProjectWithTypes(IReadOnlyList<string>)` — был неконсистентен
  с `CreateCleanProjectWithTypesAndInstances` (не размещал инстансы, использовал другой temp-путь,
  не поддерживал категоризацию). Заменён на единый `CreateCleanProjectWithTypesAndInstances`, который
  используется обоими flow'ами (ImportActiveFile + Picker).

---

## ISystemFamilyIsolationProjectService

Stage-isolation step: единая точка создания временного `.rvt` с копиями выбранных
системных типов **и** инстансами на сетке 2×2 м. Используется **обоими** flow'ами
(ImportActiveFile + Picker), что и было основной целью унификации (Phase 1–8).

**Файл:** `ISystemFamilyIsolationProjectService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/SystemFamilyIsolationProjectAdapter.cs`
**Wraps:** `ISystemFamilyRevitOperations` (тонкая обёртка для логирования `[SystemImport.Create]`)

```csharp
public interface ISystemFamilyIsolationProjectService
{
    /// <summary>
    /// Создаёт изолированный .rvt, содержащий указанные типы и по одному нормализованному
    /// инстансу на тип. Source = активный проект. Должен вызываться на Revit UI thread
    /// (внутри ExternalEvent). Путь сохранения — из <c>SystemFamilyTempLayout</c>.
    /// </summary>
    CreateCleanProjectResult CreateCleanProjectWithTypesAndInstances(
        Document sourceDoc,
        IReadOnlyList<string> typeUniqueIds,
        BuiltInCategory category,
        string displayName);
}
```

---

## ISystemFamilyImportOrchestrator

Catalog-side оркестрация staged system-family import: принимает список
`FamilyBatchImportItem` (по одному на staged .rvt), прогоняет через batch importer,
синхронизирует type descriptors в локальный каталог, возвращает
`SystemFamilyExtractionTask[]` для `ISystemFamilyAttributeExtractor`.

**Файл:** `ISystemFamilyImportOrchestrator.cs`
**Реализация:** `SmartCon.FamilyManager/Services/SystemFamilyImportOrchestrator.cs`

```csharp
public interface ISystemFamilyImportOrchestrator
{
    Task<SystemFamilyImportResult> ImportBatchItemsAsync(IReadOnlyList<FamilyBatchImportItem> items);
}

public sealed record SystemFamilyPendingImport(
    string CategoryName,
    IReadOnlyList<SelectedSystemType> Types,
    string TempRvtPath);
```

**Заметки:**
- **Удалено (Phase 7)**: legacy `ISystemFamilyImportService` снесён полностью. Staging-логика
  (`PickAndPrepare` / `AnalyzeAndPrepareForProject`) переехала в `ISystemFamilyIsolationProjectService`,
  catalog-sync остался здесь.
- Не владеет extraction — это контракт `ISystemFamilyAttributeExtractor`.

---

## ISystemFamilyAttributeExtractor

DRY-извлечение Type-параметров из staged `.rvt` через AwaitableEvent. Заменяет
дублированную inline-логику, которая раньше жила в
`FamilyManagerMainViewModel.ExtractAttributesFromRvtsAsync` (active project)
и `ExtractSystemFamilyAttributesAsync` (picker). Единая реализация покрыта
unit-тестами (`SystemFamilyAttributeExtractorTests`, 6 тестов).

**Файл:** `ISystemFamilyAttributeExtractor.cs`
**Реализация:** `SmartCon.FamilyManager/Services/SystemFamilyAttributeExtractor.cs`

```csharp
public interface ISystemFamilyAttributeExtractor
{
    /// <summary>
    /// Открывает каждый staged .rvt через awaitable external event, извлекает Type-параметры,
    /// сохраняет в каталог. Реализация ОБЯЗАНА дождаться всех in-flight сохранений
    /// перед возвратом, чтобы вызывающий код мог безопасно удалить temp-файлы
    /// (защита от race condition cleanup).
    /// </summary>
    Task ExtractAndSaveAsync(
        IReadOnlyList<SystemFamilyExtractionTask> tasks,
        CancellationToken ct = default);
}
```

**Внутренние зависимости:**
- `IFamilyDataExtractionService` / `ISystemFamilyAttributeExtractionService` — реальная
  работа с Revit API для открытия .rvt, чтения параметров, закрытия документа.
- `IFamilyDataImportService` — сохранение результатов в каталог.
- `IFamilyManagerAwaitableEvent` — обёртка над ExternalEvent для UI-thread выполнения.
  Реализация — `RevitFamilyManagerAwaitableEvent` (см. `[AwaitableEvent] ...` в логах).

---

## ISystemFamilyAttributeExtractionService

Низкоуровневый helper: открыть `.rvt` как background-документ, прочитать Type-параметры,
закрыть документ. **Не используется напрямую VM/Orchestrator** — только как scoped-зависимость
`ISystemFamilyAttributeExtractor`. Сохранён ради инкапсуляции Revit API.

**Файл:** `ISystemFamilyAttributeExtractionService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/SystemFamilyAttributeExtractionService.cs`

```csharp
public interface ISystemFamilyAttributeExtractionService
{
    /// <param name="rvtFilePath">Absolute path to the .rvt file.</param>
    /// <param name="typeNames">
    /// Names of ElementTypes to extract (case-insensitive). If null/empty,
    /// the service will try to extract from any non-default type.
    /// </param>
    FamilyExtractionResult ExtractFromRvt(string rvtFilePath, IReadOnlyList<string>? typeNames);
}
```

---

## ISystemFamilyPlacementService

Размещение (load + place) одного типа системного семейства в активный проект
пользователя. Используется после импорта — пользователь выбирает тип в
каталоге, и плагин синхронизирует его с эталоном (ADR-061) и активирует
размещение в сцене.

**Файл:** `ISystemFamilyPlacementService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/SystemFamilyPlacementService.cs`

```csharp
public interface ISystemFamilyPlacementService
{
    SystemPlacementResult LoadAndPlaceSystemType(string catalogItemId, string typeName, int targetRevitVersion);
}
```

**ADR-027 Phase 2:** возвращаемое значение — `SystemPlacementResult` (было
`bool`). Тип, чья категория не размещается интерактивно
(`UIDocument.CanPlaceElementType` = false — изоляция требует host), даёт
`LoadedManualPlacementRequired`: синхронизированный тип остаётся в проекте,
пользователь размещает его вручную (раньше — краш
`PostRequestForElementTypePlacement` на `ArgumentException`).

---

## CategoryCompat (Core/Compatibility)

Кросс-TFM абстракция `Category → BuiltInCategory`:
- **Revit 2022+** — канонический `Category.BuiltInCategory` (корректно для standard, INVALID для custom sub-category).
- **Revit 2019–2021** — guarded cast `(BuiltInCategory)(int)catId.GetValue()` через
  `ElementIdCompat.GetValue()`.

**Файл:** `SmartCon.Core/Compatibility/CategoryCompat.cs`

```csharp
public static class CategoryCompat
{
    public static BuiltInCategory GetBuiltInCategory(Category? category); // Revit 2022+
    // или guarded cast fallback для Revit 2019–2021
}
```

Используется в `AnyElementSelectionFilter.AllowElement` (Phase 22, заменил
`SystemFamilySelectionFilter`) и `SystemFamilyRevitOperations.PickSelectedElements`.
После резолвинга результат обязательно сверяется с `SystemCategoryRegistry.SupportedCategories`
— defense in depth.

---

## ISystemTypeFinder

Поиск системных типов (`ElementType`: PipeType, WallType, DuctType, …) в проекте по (имя, ordinal категории) (Issue #104, ADR-061). Категория обязательна для разрешения коллизий одинаковых имён в разных категориях («Стандартный» труба vs стена). Revit main thread only (I-01).

**Файл:** `ISystemTypeFinder.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitSystemTypeFinder.cs`

```csharp
public interface ISystemTypeFinder
{
    ElementId? FindTypeByName(Document doc, string typeName, int? categoryOrdinal);
    IReadOnlyList<SystemTypeLocation> CollectTypes(
        Document doc, IReadOnlyCollection<int> categoryOrdinals);
}
```

---

## ISystemTypeSyncService

Ядро синхронизации системного типа (Issue #104, ADR-061): читает эталон из открытого мини-проекта и записывает в проект БЕЗ копирования элементов. Существующий тип перезаписывается на месте; отсутствующий создаётся `Duplicate()` типа-болванки той же категории. Одна транзакция на тип: создание + параметры (+фолбэк создания материала при ElementId-резолве) + сегменты + структура + правила трассировки + ES-маркер (одна точка отмены). Вызывается на Revit main thread; владельцем sourceDoc является caller (оркестратор переиспользует одно открытие на батч).

**Файл:** `ISystemTypeSyncService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/SystemTypeSyncService.cs`

```csharp
public interface ISystemTypeSyncService
{
    SystemTypeSyncResult SyncTypeFromSource(
        Document sourceDoc,
        Document activeDoc,
        string typeName,
        string catalogItemId,
        string versionLabel,
        int sourceRevitVersion);
}
```

---

## ISystemTypeSyncOrchestrator

Оркестратор поверх `ISystemTypeSyncService` (Issue #104, ADR-061): резолвит managed-файл мини-проекта, открывает его один раз на батч типов, закрывает в finally. `IsProjectTypeCurrent` — fast-path для DnD/размещения (без открытия файла, тот же вердикт `SystemTypeStaleLogic.ComputeReason`, что у stale-детектора). SQLite-чтения внутри — через `AsyncBridge.RunSync` на Revit main thread.

**Файл:** `ISystemTypeSyncOrchestrator.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/SystemFamilySyncOrchestrator.cs`

```csharp
public interface ISystemTypeSyncOrchestrator
{
    bool IsProjectTypeCurrent(
        Document activeDoc, string catalogItemId, string typeName, int targetRevitVersion);

    SystemFamilySyncResult SyncTypes(
        Document activeDoc, string catalogItemId,
        IReadOnlyList<string> typeNames, int targetRevitVersion);
}
```

---

## IMaterialSyncService

Синхронизация материала по имени (Issue #104, ADR-061): никогда не копируется между документами (Revit 2024+ дублирует). Существующий обновляется на месте (графика, appearance через `AppearanceAssetEditScope`, физика/теплотехника через `PropertySetElement`) — shared-ассеты предварительно детачатся (`Duplicate`), чтобы не задеть чужие материалы. Отсутствующий создаётся дубликатом прототипа с asset. Отсутствующий ассет в эталоне НЕ очищает целевой (асимметрия осознанная). Вызывается внутри открытой транзакции.

**Файл:** `IMaterialSyncService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitMaterialSyncService.cs`

```csharp
public interface IMaterialSyncService
{
    ElementId? SyncMaterial(Document sourceDoc, Document activeDoc, string materialName);
}
```

---

## ISegmentSyncService

Синхронизация сегмента трубы/воздуховода по имени (Issue #104, ADR-061): таблица размеров к эталону — добавление/коррекция всегда; удаление только неиспользуемых размеров и только pipe-сегментов (usage через `RBS_PIPE_SEGMENT_PARAM`+диаметр; duct usage API не проверить — не трогаем); последний размер не удаляем. Создание через `PipeSegment.Create` + `PipeScheduleType.Create` (duct-сегменты API не создаёт — Warn + skip). Остаток — `SizesNotConverged` → пользовательский счётчик.

**Файл:** `ISegmentSyncService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitSegmentSyncService.cs`

```csharp
public interface ISegmentSyncService
{
    SegmentSnapshot? ReadSegment(Document sourceDoc, string segmentName);
    SegmentSyncResult SyncSegment(Document sourceDoc, Document activeDoc, string segmentName);
}
```

---

## IFittingDependencyResolver

Разрешение фитингов трассировки (Issue #104, ADR-061): фитинг — обычное loadable-семейство каталога; есть в проекте по `"{Family}:{Type}"` — используется, нет — догружается из каталога, нет в каталоге — правило пропускается (Warn). Вызывается СТРОГО вне транзакции (`LoadFamily` запрещён в modifiable document) — оркестратор вызывает его до открытия sync-транзакции.

**Файл:** `IFittingDependencyResolver.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/CatalogFittingDependencyResolver.cs`

```csharp
public interface IFittingDependencyResolver
{
    ElementId? EnsureFitting(
        Document activeDoc, string familyName, string typeName, int targetRevitVersion);
}
```

---

## ICompoundStructureSyncService

Замена compound structure слоистого типа (стены/перекрытия/крыши/потолки) эталоном (Issue #104, ADR-061): слои в эталонном порядке (материалы по имени через `IMaterialSyncService`), shell-границы ПОСЛЕ слоёв, variable layer, точный `StructuralMaterialIndex`, `EndCap`/`OpeningWrapping` + по-слойные `LayerCapFlag`/`ParticipatesInWrapping`. Невалидная структура отклоняется без изменения типа (rejection-safe, not-converged). `EndCap=NoEndCap` для не-стен. Вертикально-составные стены — known limitation.

**Файл:** `ICompoundStructureSyncService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitCompoundStructureSyncService.cs`

```csharp
public interface ICompoundStructureSyncService
{
    int SyncStructure(
        Document sourceDoc, Document activeDoc,
        ElementType target, CompoundStructureSnapshot structure);
}
```
