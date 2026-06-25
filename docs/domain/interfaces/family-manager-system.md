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
каталоге, и плагин загружает его из managed storage и размещает в сцене.

**Файл:** `ISystemFamilyPlacementService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/SystemFamilyPlacementService.cs`

```csharp
public interface ISystemFamilyPlacementService
{
    void LoadAndPlaceSystemType(string catalogItemId, string typeName, int targetRevitVersion);
}
```

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
