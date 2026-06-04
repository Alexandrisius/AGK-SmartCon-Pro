# План реализации: Системные семейства в FamilyManager

## Версия документа
- **Статус:** Требует ревизии (rev. 2)
- **Дата:** 2026-06-04
- **Автор:** AI-агент SmartCon
- **Ветка:** develop (feature/system-families-v2)
- **История:**
  - rev. 1 (отклонён) — первая попытка реализации, провалена из-за критических багов
  - rev. 2 (текущий) — исправлены баги: `family_source` не персистился, GUID в имени temp-файла, неверный паттерн транзакции

---

## 1. Общее описание задачи

Необходимо реализовать поддержку **всех системных семейств Revit** в модуле FamilyManager (FM). На текущий момент FM поддерживает только загружаемые семейства (.rfa). Цель — дать пользователям возможность импортировать, хранить, редактировать и размещать системные типы (трубы, стены, воздуховоды и т.д.) наравне с .rfa.

**Ключевая концепция:** Один чистый .rvt файл = одно семейство в FM. ElementType'ы внутри .rvt = типоразмеры.

---

## 2. Архитектура и принципиальные решения

### 2.1. Модель хранения

| Аспект | Решение |
|---|---|
| **Файл семейства** | Чистый .rvt (создаётся через `NewProjectDocument` + `CopyElements`) |
| **Структура хранения** | `files/{catalogItemId}/{versionLabel}/{fileName}.rvt` (единая с .rfa) |
| **Различие типов** | `catalog_items.family_source` = 'loadable' \| 'system' |
| **Категория Revit** | `catalog_items.revit_category` = BuiltInCategory (например 'OST_PipeCurves') |
| **ReadOnly** | Да, `FileAttributes.ReadOnly` на файле (как для .rfa) |
| **Версионность** | Один .rvt = одна версия. Нет multi-Revit (R25 .rvt не откроется в R24) |

### 2.2. Идентификация типов

**Проблема:** `UniqueId` элемента меняется при `CopyElements`. Нельзя использовать как стабильный ключ.

**Решение:** Идентификация типов внутри .rvt по **имени** (`type_name`). В рамках одной категории имена типов уникальны в Revit.

### 2.3. Механизм копирования

**Импорт (в хранилище FM):**
```
[Пользователь выделяет элементы в проекте]
    ↓
SystemFamilyRevitOperations.PickSystemTypes()
    ↓
SystemFamilyRevitOperations.CreateCleanProjectWithTypes()
    — NewProjectDocument(UnitSystem.Metric)
    — CopyElements: типы + все зависимости (автоматически)
    — SaveAs → temp .rvt
    ↓
SystemFamilyImportService.SaveToStorageAsync()
    — SHA256 + DB INSERT + move to managed storage
```

**Размещение (в проект пользователя):**
```
[DnD типа из FM]
    ↓
FamilyPlacementDropHandler.Execute()
    — Resolve .rvt из storage
    — OpenDocumentFile(.rvt)
    — FindTypeByName (по имени)
    — CopyElements в активный проект
    — PostRequestForElementTypePlacement(elementType)
    — Close source doc
```

### 2.4. DnD (Drag-and-Drop)

В develop уже реализован механизм DnD через Revit native `UIApplication.DoDragDrop`:
- Payload: `FamilyPlacementDragData` (CatalogItemId, FamilyName, TypeName, TargetRevitVersion, IsVirtual)
- Для системных типов DropHandler должен понять что это system family и вызвать другую логику

**Как DropHandler узнаёт что тип системный:**
1. В `FamilyTypeNodeViewModel` добавляем свойство `FamilySource` (основной механизм)
2. В `FamilyTypeNodeViewModel` добавляем свойство `UniqueId` (fallback для обратной совместимости со старой feature-веткой)
3. При создании `FamilyPlacementDragData` включаем `FamilySource`
4. В `FamilyPlacementDropHandler.Execute()` проверяем в следующем порядке:
   - `dragData.FamilySource == "system"` → системное семейство
   - `typeNode.UniqueId is not null` (если в payload нет FamilySource) → системное семейство
   - Иначе — загружаемое семейство (текущая логика)
5. Если да — вызываем `_systemFamilyPlacementService.LoadAndPlaceSystemType()`
6. Если нет — текущая логика `LoadFamilyAsync` + `ActivateAndPlaceType`

**⚠️ КРИТИЧНО: Fallback на UniqueId нужен потому что:**
- `FamilySource` зависит от корректной записи в БД при INSERT
- Если INSERT пропустит `family_source` (как было в rev. 1) — все типы получат default `'loadable'`
- DropHandler тогда пойдёт в ветку `LoadFamilyAsync` для .rvt файла → краш (несовместимый формат)
- UniqueId в `family_types` — независимый сигнал: если он заполнен, тип точно из .rvt

---

## 3. Изменения базы данных (Миграция V11)

### 3.1. Текущая схема (develop)

Текущая версия схемы: **V10**. Колонки `family_source` и `revit_category` **отсутствуют**.

### 3.2. Новые колонки

**Таблица `catalog_items`:**
```sql
ALTER TABLE catalog_items ADD COLUMN family_source TEXT NOT NULL DEFAULT 'loadable';
ALTER TABLE catalog_items ADD COLUMN revit_category TEXT;
```

**Таблица `family_types`:**
```sql
ALTER TABLE family_types ADD COLUMN type_unique_id TEXT;
```

### 3.3. Значения

- `family_source`: `'loadable'` (по умолчанию, для .rfa) или `'system'` (для .rvt)
- `revit_category`: строковое представление `BuiltInCategory` (например `'OST_PipeCurves'`), NULL для .rfa
- `type_unique_id`: `UniqueId` элемента внутри .rvt (информационное поле, не используется для поиска)

### 3.4. Индексы

```sql
CREATE INDEX IF NOT EXISTS ix_catalog_items_family_source ON catalog_items (family_source);
```

### 3.5. Safety Net

Добавить `EnsureCriticalColumnsAsync` в `LocalCatalogMigrator` для обратной совместимости:
```csharp
if (!await ColumnExistsAsync(connection, "catalog_items", "family_source"))
    // ALTER TABLE ...
if (!await ColumnExistsAsync(connection, "catalog_items", "revit_category"))
    // ALTER TABLE ...
if (!await ColumnExistsAsync(connection, "family_types", "type_unique_id"))
    // ALTER TABLE ...
```

---

## 4. Доменные модели (SmartCon.Core)

### 4.1. Изменения существующих моделей

**`FamilyCatalogItem`** — добавить:
```csharp
public sealed record FamilyCatalogItem(
    // ... existing fields ...
    DateTimeOffset UpdatedAtUtc,
    string FamilySource = "loadable",        // NEW
    string? RevitCategory = null);            // NEW
```

**`FamilyTypeDescriptor`** — добавить:
```csharp
public sealed record FamilyTypeDescriptor(
    // ... existing fields ...
    string? ExtractionRunId = null,
    string? UniqueId = null);                  // NEW
```

**`FamilyCatalogItemRow`** (ViewModel) — добавить:
```csharp
[ObservableProperty] private string _familySource = "loadable";
```

### 4.2. Новые модели

**`SystemFamilyBatchImportItem`**:
```csharp
public sealed record SystemFamilyBatchImportItem(
    string Id,                                  // GUID
    string SourceCategoryName,                  // BuiltInCategory display name (e.g. "Трубы")
    string FamilyName,                          // Имя семейства в FM (editable)
    string NormalizedName,
    IReadOnlyList<string> TypeNames,            // Список имён типов
    int TypeCount,
    FamilyBatchImportStatus Status,             // New / Existing / Duplicate
    FamilyBatchImportAction Action,             // IncrementVersion / OverwriteCurrent / Skip
    string? ExistingCatalogItemId = null,
    string? TempRvtPath = null,                 // Путь к temp .rvt
    string? Sha256 = null,
    string? TargetCategoryId = null);
```

**`SystemFamilyImportResult`**:
```csharp
public record SystemFamilyImportResult(
    bool Success,
    string? Message,
    string? CatalogItemId,
    int TypesCount);
```

**`SelectedSystemType`** (record для Revit-операций):
```csharp
public record SelectedSystemType(
    string UniqueId,    // UniqueId в исходном проекте
    string Name,        // Имя типа
    string CategoryName // Имя категории
);
```

**`CreateCleanProjectResult`**:
```csharp
public record CreateCleanProjectResult(
    bool Success,
    string? FilePath,
    string? Error,
    int CopiedElementsCount,
    string? CategoryName = null);
```

### 4.3. Обновление docs/domain/models.md

**ОБЯЗАТЕЛЬНО** добавить новые модели в `docs/domain/models.md` после реализации.

---

## 5. Интерфейсы (SmartCon.Core/Services/Interfaces/)

### 5.1. Новые интерфейсы

**`ISystemFamilyImportService`** (`ISystemFamilyImportService.cs`):
```csharp
public interface ISystemFamilyImportService
{
    SystemFamilyImportResult ImportFromSelection();
}
```

**`ISystemFamilyPlacementService`** (`ISystemFamilyPlacementService.cs`):
```csharp
public interface ISystemFamilyPlacementService
{
    void LoadAndPlaceSystemType(string catalogItemId, string typeName, int targetRevitVersion);
}
```

**`ISystemFamilyRevitOperations`** (`ISystemFamilyRevitOperations.cs`):
```csharp
public interface ISystemFamilyRevitOperations
{
    IReadOnlyList<SelectedSystemType> PickSystemTypes();
    CreateCleanProjectResult CreateCleanProjectWithTypes(IReadOnlyList<string> typeUniqueIds);
}
```

### 5.2. Изменения существующих интерфейсов

**`IFamilyPlacementService`** — добавить метод:
```csharp
public interface IFamilyPlacementService
{
    void ActivateAndPlaceType(string familyName, string typeName);
    void LoadAndPlaceFamily(string filePath, string familyName, string? preferredTypeName = null);
    void LoadAndPlaceSystemType(string catalogItemId, string typeName);  // NEW
}
```

**`IFamilyCatalogProvider`** — добавить методы:
```csharp
Task<IReadOnlyList<FamilyCatalogItem>> GetItemsBySourceAsync(string familySource, CancellationToken ct = default);
```

**`FamilyPlacementDragData`** — добавить поле:
```csharp
public sealed record FamilyPlacementDragData(
    string CatalogItemId,
    string FamilyName,
    string TypeName,
    int TargetRevitVersion,
    bool IsVirtual = false,
    string FamilySource = "loadable");  // NEW
```

### 5.3. Обновление docs/domain/interfaces.md

**ОБЯЗАТЕЛЬНО** добавить новые интерфейсы в `docs/domain/interfaces.md` после реализации.

---

## 6. Revit-реализации (SmartCon.Revit)

### 6.1. Новые файлы

#### A. `src/SmartCon.Revit/Selection/SystemFamilySelectionFilter.cs`

Фильтр для `PickObjects` — разрешает выбор только элементов поддерживаемых системных категорий.

```csharp
using Autodesk.Revit.DB;
using Autodesk.Revit.UI.Selection;

namespace SmartCon.Revit.Selection;

internal sealed class SystemFamilySelectionFilter : ISelectionFilter
{
    private static readonly IReadOnlySet<BuiltInCategory> SupportedCategories = new HashSet<BuiltInCategory>
    {
        BuiltInCategory.OST_PipeCurves,
        BuiltInCategory.OST_DuctCurves,
        BuiltInCategory.OST_FlexPipeCurves,
        BuiltInCategory.OST_FlexDuctCurves,
        BuiltInCategory.OST_Conduit,
        BuiltInCategory.OST_CableTray,
        BuiltInCategory.OST_DuctInsulations,
        BuiltInCategory.OST_PipeInsulations,
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Roofs,
        BuiltInCategory.OST_Ceilings,
        BuiltInCategory.OST_Stairs,
        BuiltInCategory.OST_Railings,
        BuiltInCategory.OST_WallFoundation,
        // ... расширяем при необходимости
    };

    public bool AllowElement(Element elem)
    {
        return elem.Category?.Id is ElementId catId 
            && SupportedCategories.Contains((BuiltInCategory)catId.IntegerValue);
    }

    public bool AllowReference(Reference reference, XYZ position) => false;
}
```

**Ключевые моменты:**
- Список категорий — все `BuiltInCategory` из `ElementTypeGroup` Revit API
- `AllowReference` возвращает `false` (не выбираем по точке, только элементы)

#### B. `src/SmartCon.Revit/FamilyManager/SystemFamilyRevitOperations.cs`

Основной класс для Revit-операций с системными семействами.

```csharp
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Selection;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;
using SmartCon.Revit.Selection;

namespace SmartCon.Revit.FamilyManager;

public sealed class SystemFamilyRevitOperations : ISystemFamilyRevitOperations
{
    private readonly RevitContext _revitContext;

    public SystemFamilyRevitOperations(IRevitContext revitContext)
    {
        _revitContext = (RevitContext)revitContext;
    }

    public IReadOnlyList<SelectedSystemType> PickSystemTypes()
    {
        // 1. Получаем UIApplication
        var uiApp = _revitContext.GetUIApplication();
        var uidoc = uiApp.ActiveUIDocument;
        var doc = uidoc.Document;

        // 2. PickObjects с фильтром
        IList<Reference> refs;
        try
        {
            refs = uidoc.Selection.PickObjects(
                ObjectType.Element,
                new SystemFamilySelectionFilter(),
                "Select system family elements");
        }
        catch (Autodesk.Revit.Exceptions.OperationCanceledException)
        {
            return [];
        }

        // 3. Извлекаем уникальные типы
        var types = new Dictionary<string, SelectedSystemType>();
        foreach (var r in refs)
        {
            var elem = doc.GetElement(r);
            if (elem is null) continue;

            var typeId = elem.GetTypeId();
            if (typeId == ElementId.InvalidElementId) continue;

            var typeElem = doc.GetElement(typeId);
            if (typeElem is null) continue;

            var categoryName = typeElem.Category?.Name ?? "Unknown";
            
            if (!types.ContainsKey(typeElem.UniqueId))
                types[typeElem.UniqueId] = new SelectedSystemType(
                    typeElem.UniqueId, 
                    typeElem.Name, 
                    categoryName);
        }

        return types.Values.ToList();
    }

    public CreateCleanProjectResult CreateCleanProjectWithTypes(IReadOnlyList<string> typeUniqueIds)
    {
        var uiApp = _revitContext.GetUIApplication();
        var doc = uiApp.ActiveUIDocument.Document;
        var app = uiApp.Application;

        // 1. Разрешаем ElementId из UniqueIds
        var typeIds = new List<ElementId>();
        foreach (var uid in typeUniqueIds)
        {
            var elem = doc.GetElement(uid);
            if (elem is not null)
                typeIds.Add(elem.Id);
        }

        if (typeIds.Count == 0)
            return new CreateCleanProjectResult(false, null, "No type elements found", 0);

        // 2. Создаём пустой проект
        Document newDoc;
        try
        {
            newDoc = app.NewProjectDocument(UnitSystem.Metric);
        }
        catch (Exception ex)
        {
            return new CreateCleanProjectResult(false, null, $"Failed to create project: {ex.Message}", 0);
        }

        // 3. Копируем типы
        try
        {
            int copiedCount;
            using (var tx = new Transaction(newDoc, "Copy system types"))
            {
                tx.Start();

                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new SkipDuplicateTypesHandler());

                var copiedIds = ElementTransformUtils.CopyElements(
                    doc, typeIds, newDoc, null, options);

                copiedCount = copiedIds.Count;
                tx.Commit();
            }

            // 4. Сохраняем во временный файл
            // ⚠️ КРИТИЧНО: имя файла = имя категории (например "OST_PipeCurves.rvt")
            // НЕ используем GUID — иначе имя семейства в каталоге станет GUID'ом
            var categoryName = newDoc.OwnerFamily is null
                ? doc.Title ?? "SystemFamily"
                : typeElem?.Category?.Name ?? "SystemFamily";

            var safeFileName = SanitizeFileName(categoryName) + ".rvt";
            var tempPath = Path.Combine(
                Path.GetTempPath(),
                "SmartCon",
                "SystemFamily",
                safeFileName);

            Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

            newDoc.SaveAs(tempPath, new SaveAsOptions { OverwriteExistingFile = true });
            newDoc.Close(false);

            return new CreateCleanProjectResult(true, tempPath, null, copiedCount, categoryName);
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[SystemFamilyRevitOps] Failed: {ex.GetType().Name}: {ex.Message}");
            try { newDoc.Close(false); } catch { }
            return new CreateCleanProjectResult(false, null, ex.Message, 0);
        }
    }

    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder(name.Length);
        foreach (var c in name)
        {
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        }
        return sb.ToString();
    }

    private sealed class SkipDuplicateTypesHandler : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesArgs args)
        {
            return DuplicateTypeAction.UseDestinationTypes;
        }
    }
}
```

**Ключевые моменты:**
- `PickSystemTypes` возвращает уникальные типы (дедупликация по `UniqueId`)
- `CreateCleanProjectWithTypes` создаёт пустой проект и копирует типы через `CopyElements`
- `CopyElements` автоматически переносит все зависимости (материалы, сегменты, фитинги)
- `SkipDuplicateTypesHandler` — пропускаем дубликаты при копировании
- **Имя temp-файла = имя категории**, НЕ GUID. Иначе `ImportFileAsync` возьмёт GUID в качестве имени семейства

#### C. `src/SmartCon.Revit/FamilyManager/SystemFamilyPlacementService.cs`

Сервис размещения системного типа в проект пользователя.

```csharp
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using SmartCon.Core.Logging;
using SmartCon.Core.Services.Interfaces;
using SmartCon.Revit.Context;

namespace SmartCon.Revit.FamilyManager;

public sealed class SystemFamilyPlacementService : ISystemFamilyPlacementService
{
    private readonly RevitContext _revitContext;
    private readonly IFamilyFileResolver _fileResolver;

    public SystemFamilyPlacementService(
        IRevitContext revitContext,
        IFamilyFileResolver fileResolver)
    {
        _revitContext = (RevitContext)revitContext;
        _fileResolver = fileResolver;
    }

    public void LoadAndPlaceSystemType(string catalogItemId, string typeName, int targetRevitVersion)
    {
        var uiApp = _revitContext.GetUIApplication();
        var activeDoc = _revitContext.GetDocument();

        if (uiApp is null || activeDoc is null)
        {
            SmartConLogger.Freeze("[SystemFamilyPlacement] ABORT: uiApp or activeDoc is null");
            return;
        }

        // 1. Резолвим путь к .rvt
        var resolved = _fileResolver.ResolveForLoadAsync(catalogItemId, targetRevitVersion).GetAwaiter().GetResult();

        if (string.IsNullOrEmpty(resolved.AbsolutePath))
        {
            SmartConLogger.Freeze("[SystemFamilyPlacement] ABORT: No file resolved");
            return;
        }

        // 2. Открываем .rvt как фоновый документ
        Document? sourceDoc = null;
        try
        {
            sourceDoc = uiApp.Application.OpenDocumentFile(resolved.AbsolutePath);
        }
        catch (Exception ex)
        {
            SmartConLogger.Freeze($"[SystemFamilyPlacement] OpenDocumentFile failed: {ex.Message}");
            return;
        }

        try
        {
            // 3. Ищем тип по имени
            var sourceType = FindTypeByName(sourceDoc, typeName);
            if (sourceType is null)
            {
                SmartConLogger.Freeze($"[SystemFamilyPlacement] Type '{typeName}' not found in source doc");
                sourceDoc.Close(false);
                return;
            }

            // 4. Проверяем есть ли такой тип уже в проекте
            var existingType = FindTypeByName(activeDoc, sourceType.Name, sourceType.Category?.Id);
            if (existingType is not null)
            {
                // Тип уже есть — активируем существующий
                sourceDoc.Close(false);
                ActivatePlacement(uiApp, existingType);
                return;
            }

            // 5. Копируем тип в активный проект
            // ⚠️ КРИТИЧНО: используем ПРЯМОЙ new Transaction(activeDoc, ...)
            // НЕЛЬЗЯ использовать _transactionService.RunInTransaction — он берёт документ
            // из контекста, а не передаёт его явно. Для cross-document CopyElements
            // нужен Transaction ПРИВЯЗАННЫЙ к activeDoc, иначе Revit выбросит
            // "Transaction cannot be started in the current context" или копия попадёт
            // не в тот документ. Этот паттерн проверен в feature/system-families.
            using (var tx = new Transaction(activeDoc, "Copy system type"))
            {
                tx.Start();

                var options = new CopyPasteOptions();
                options.SetDuplicateTypeNamesHandler(new SkipDuplicateTypesHandler());

                var failOpts = tx.GetFailureHandlingOptions();
                failOpts.SetFailuresPreprocessor(new SuppressCopyDuplicatesPreprocessor());
                tx.SetFailureHandlingOptions(failOpts);

                ElementTransformUtils.CopyElements(
                    sourceDoc, new List<ElementId> { sourceType.Id }, activeDoc, null, options);

                tx.Commit();
            }

            // 6. Ищем скопированный тип (по имени, т.к. ID изменился)
            var copiedType = FindTypeByName(activeDoc, sourceType.Name, sourceType.Category?.Id);

            sourceDoc.Close(false);
            sourceDoc = null;

            // 7. Активируем инструмент размещения
            // ⚠️ КРИТИЧНО: PostRequestForElementTypePlacement вызывается
            // ВНЕ транзакции (using-блок уже закрыт) И ВНЕ sourceDoc lifecycle
            if (copiedType is not null)
            {
                ActivatePlacement(uiApp, copiedType);
            }
        }
        finally
        {
            sourceDoc?.Close(false);
        }
    }

    private static void ActivatePlacement(UIApplication uiApp, ElementType elementType)
    {
        uiApp.ActiveUIDocument?.PostRequestForElementTypePlacement(elementType);
    }

    private static ElementType? FindTypeByName(Document doc, string name, ElementId? categoryId = null)
    {
        var collector = new FilteredElementCollector(doc).OfClass(typeof(ElementType));
        
        if (categoryId is not null && categoryId != ElementId.InvalidElementId)
        {
            try { collector = collector.OfCategoryId(categoryId); }
            catch { }
        }

        return collector.Cast<ElementType>()
            .FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SkipDuplicateTypesHandler : IDuplicateTypeNamesHandler
    {
        public DuplicateTypeAction OnDuplicateTypeNamesFound(DuplicateTypeNamesHandlerArgs args)
        {
            return DuplicateTypeAction.UseDestinationTypes;
        }
    }

    private sealed class SuppressCopyDuplicatesPreprocessor : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor failuresAccessor)
        {
            var failures = failuresAccessor.GetFailureMessages();
            foreach (var f in failures)
            {
                if (f.GetFailureDefinitionId() == BuiltInFailures.CopyPasteFailures.CannotCopyDuplicates)
                    failuresAccessor.DeleteWarning(f);
            }
            return FailureProcessingResult.Continue;
        }
    }
}
```

**Ключевые моменты:**
- `OpenDocumentFile` открывает .rvt как фоновый документ
- `FindTypeByName` ищет по имени (не по UniqueId, т.к. он меняется при CopyElements)
- `PostRequestForElementTypePlacement` нельзя вызывать внутри транзакции
- `SuppressCopyDuplicatesPreprocessor` подавляет предупреждения о дубликатах

### 6.2. Изменения существующих файлов

#### D. `src/SmartCon.Revit/FamilyManager/RevitFamilyPlacementService.cs`

Добавить метод:
```csharp
public void LoadAndPlaceSystemType(string catalogItemId, string typeName)
{
    var targetRevit = int.Parse(_revitContext.GetRevitVersion());
    _systemFamilyPlacementService.LoadAndPlaceSystemType(catalogItemId, typeName, targetRevit);
}
```

#### E. `src/SmartCon.Revit/FamilyManager/FamilyPlacementDropHandler.cs`

Изменить `Execute`:
```csharp
public void Execute(UIDocument document, object data)
{
    if (data is not FamilyPlacementDragData dragData) return;

    if (dragData.FamilySource == "system")
    {
        // Системное семейство
        _systemFamilyPlacementService.LoadAndPlaceSystemType(
            dragData.CatalogItemId, 
            dragData.TypeName, 
            dragData.TargetRevitVersion);
    }
    else
    {
        // Загружаемое семейство — текущая логика
        // ... existing code ...
    }
}
```

#### F. `src/SmartCon.Revit/FamilyManager/RevitFamilyPlacementDragService.cs`

При создании `FamilyPlacementDragData` добавить `FamilySource`:
```csharp
var data = new FamilyPlacementDragData(
    leaf.CatalogItemId,
    leaf.DisplayName,
    typeNode.TypeName,
    CurrentRevitVersion,
    typeNode.IsVirtual,
    typeNode.FamilySource);  // NEW
```

---

## 7. FamilyManager сервисы (SmartCon.FamilyManager)

### 7.1. Новые файлы

#### A. `src/SmartCon.FamilyManager/Services/SystemFamilyImportService.cs`

Оркестрация импорта системных семейств.

```csharp
using System.IO;
using Microsoft.Data.Sqlite;
using SmartCon.Core.Logging;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services;

internal sealed class SystemFamilyImportService : ISystemFamilyImportService
{
    private readonly ISystemFamilyRevitOperations _revitOps;
    private readonly IFamilyCatalogProvider _catalogProvider;
    private readonly IFamilyImportService _importService;
    private readonly IRevitContext _revitContext;
    private readonly StoragePathResolver _pathResolver;
    private readonly LocalCatalogDatabase _database;
    private readonly LocalCatalogMigrator _migrator;

    public SystemFamilyImportService(
        ISystemFamilyRevitOperations revitOps,
        IFamilyCatalogProvider catalogProvider,
        IFamilyImportService importService,
        IRevitContext revitContext,
        StoragePathResolver pathResolver,
        LocalCatalogDatabase database,
        LocalCatalogMigrator migrator)
    {
        _revitOps = revitOps;
        _catalogProvider = catalogProvider;
        _importService = importService;
        _revitContext = revitContext;
        _pathResolver = pathResolver;
        _database = database;
        _migrator = migrator;
    }

    public SystemFamilyImportResult ImportFromSelection()
    {
        // 1. Пикер
        var selectedTypes = _revitOps.PickSystemTypes();
        if (selectedTypes.Count == 0)
            return new SystemFamilyImportResult(false, "No system types selected", null, 0);

        // 2. Группировка по категории
        var groupedByCategory = selectedTypes
            .GroupBy(t => t.CategoryName)
            .ToList();

        // 3. Для каждой категории создаём temp .rvt
        var batchItems = new List<SystemFamilyBatchImportItem>();
        var tempFiles = new List<string>();

        foreach (var group in groupedByCategory)
        {
            var categoryName = group.Key;
            var types = group.ToList();
            var uniqueIds = types.Select(t => t.UniqueId).ToList();

            // Создаём чистый проект с типами
            var createResult = _revitOps.CreateCleanProjectWithTypes(uniqueIds);
            if (!createResult.Success || string.IsNullOrEmpty(createResult.FilePath))
                continue;

            tempFiles.Add(createResult.FilePath);

            // Считаем SHA256
            var sha256 = ComputeSha256(createResult.FilePath);
            
            // Проверяем дедупликацию
            var normalizedName = FamilyNameNormalizer.Normalize(categoryName);
            var existingByHash = await _catalogProvider.FindByHashAsync(sha256);
            var existingByName = await _catalogProvider.FindByNormalizedNameAsync(normalizedName);

            FamilyBatchImportStatus status;
            FamilyBatchImportAction action;
            string? existingId = null;

            if (existingByHash is not null)
            {
                status = FamilyBatchImportStatus.Duplicate;
                action = FamilyBatchImportAction.Skip;
                existingId = existingByHash.CatalogItemId;
            }
            else if (existingByName is not null)
            {
                status = FamilyBatchImportStatus.Existing;
                action = FamilyBatchImportAction.IncrementVersion;
                existingId = existingByName.Id;
            }
            else
            {
                status = FamilyBatchImportStatus.New;
                action = FamilyBatchImportAction.IncrementVersion;
            }

            batchItems.Add(new SystemFamilyBatchImportItem(
                Guid.NewGuid().ToString(),
                categoryName,
                categoryName,  // default name = category name
                normalizedName,
                types.Select(t => t.Name).ToList(),
                types.Count,
                status,
                action,
                existingId,
                createResult.FilePath,
                sha256));
        }

        // 4. Показываем batch диалог
        // ... (показать FamilyBatchImportView с batchItems)
        
        // 5. Если пользователь нажал "Загрузить" — импортируем
        // ... (сохраняем в storage, пишем в БД)
        
        // 6. Очистка temp файлов
        foreach (var tempFile in tempFiles)
        {
            try { File.Delete(tempFile); } catch { }
        }

        return new SystemFamilyImportResult(true, $"Imported {batchItems.Count} system families", null, selectedTypes.Count);
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var sha = System.Security.Cryptography.SHA256.Create();
        var hash = sha.ComputeHash(stream);
        return BitConverter.ToString(hash).Replace("-", "");
    }
}
```

### 7.2. Изменения существующих файлов

#### B. `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogMigrator.cs`

Добавить:
```csharp
public async Task MigrateAsync(CancellationToken ct = default)
{
    // ... existing migrations V1-V10 ...
    await MigrateV11Async(connection, ct);
    await EnsureCriticalColumnsAsync(connection, ct);
}

private static async Task MigrateV11Async(SqliteConnection connection, CancellationToken ct)
{
    var currentVersion = await GetSchemaVersionAsync(connection, ct);
    if (currentVersion >= 11) return;

    if (!await ColumnExistsAsync(connection, "catalog_items", "family_source", ct))
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN family_source TEXT NOT NULL DEFAULT 'loadable'";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    if (!await ColumnExistsAsync(connection, "catalog_items", "revit_category", ct))
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "ALTER TABLE catalog_items ADD COLUMN revit_category TEXT";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    if (!await ColumnExistsAsync(connection, "family_types", "type_unique_id", ct))
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "ALTER TABLE family_types ADD COLUMN type_unique_id TEXT";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    using var idxCmd = connection.CreateCommand();
    idxCmd.CommandText = FamilyCatalogSql.CreateV11Indexes;
    await idxCmd.ExecuteNonQueryAsync(ct);

    using var versionCmd = connection.CreateCommand();
    versionCmd.CommandText = "UPDATE schema_info SET value = '11' WHERE key = 'schema_version'";
    await versionCmd.ExecuteNonQueryAsync(ct);
}
```

#### C. `src/SmartCon.FamilyManager/Services/LocalCatalog/FamilyCatalogSql.cs`

Добавить:
```csharp
public const string MigrateV11AddSystemFamilyColumns = """
    ALTER TABLE catalog_items ADD COLUMN family_source TEXT NOT NULL DEFAULT 'loadable';
    ALTER TABLE catalog_items ADD COLUMN revit_category TEXT;
    ALTER TABLE family_types ADD COLUMN type_unique_id TEXT
    """;

public const string CreateV11Indexes = """
    CREATE INDEX IF NOT EXISTS ix_catalog_items_family_source ON catalog_items (family_source)
    """;
```

Обновить `CreateCatalogItems`:
```sql
CREATE TABLE IF NOT EXISTS catalog_items (
    id TEXT PRIMARY KEY,
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    description TEXT,
    category_name TEXT,
    category_id TEXT,
    manufacturer TEXT,
    content_status TEXT NOT NULL DEFAULT 'Active',
    current_version_label TEXT,
    published_by TEXT,
    family_source TEXT NOT NULL DEFAULT 'loadable',  -- NEW
    revit_category TEXT,                              -- NEW
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
)
```

#### D. `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogProvider.cs`

Добавить метод:
```csharp
public async Task<IReadOnlyList<FamilyCatalogItem>> GetItemsBySourceAsync(
    string familySource, CancellationToken ct = default)
{
    var result = new List<FamilyCatalogItem>();
    
    using var connection = _database.CreateConnection();
    await connection.OpenAsync(ct);
    using var cmd = connection.CreateCommand();
    cmd.CommandText = """
        SELECT id, name, normalized_name, description, category_name, category_id, 
               manufacturer, content_status, current_version_label, published_by,
               family_source, revit_category, created_at_utc, updated_at_utc
        FROM catalog_items 
        WHERE family_source = @source
        ORDER BY name
        """;
    cmd.Parameters.Add(new SqliteParameter("@source", familySource));
    
    using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        result.Add(MapCatalogItem(reader));
    }
    
    return result.AsReadOnly();
}
```

Обновить `MapCatalogItem` — добавить чтение `family_source` и `revit_category`.

#### E. `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyTypeRepository.cs`

Обновить `GetAllTypesBatchAsync`:
```csharp
// Добавить type_unique_id в SELECT
cmd.CommandText = $"SELECT id, catalog_item_id, type_name, sort_order, version_id, file_id, extraction_run_id, type_unique_id FROM family_types WHERE catalog_item_id IN ({placeholders}) ORDER BY sort_order";

// При чтении:
var type = new FamilyTypeDescriptor(
    reader.GetString(0),
    itemId,
    reader.GetString(2),
    reader.GetInt32(3),
    reader.IsDBNull(4) ? null : reader.GetString(4),
    reader.IsDBNull(5) ? null : reader.GetString(5),
    reader.IsDBNull(6) ? null : reader.GetString(6),
    reader.IsDBNull(7) ? null : reader.GetString(7));  // type_unique_id
```

Обновить `SaveTypesAsync` — добавить `type_unique_id` в INSERT.

#### F. `src/SmartCon.FamilyManager/Services/LocalCatalog/StoragePathResolver.cs`

Добавить метод:
```csharp
public string GetRvtFilePath(string catalogItemId, string versionLabel, string fileName)
{
    return Path.Combine(GetVersionDirectory(catalogItemId, versionLabel), fileName);
}
```

#### G. `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalFamilyImportService.cs`

Добавить поддержку .rvt в `CopyToManagedStorageAsync`:
- Проверять расширение файла (.rfa / .rvt)
- Для .rvt использовать `GetRvtFilePath` вместо `GetRfaFilePath`
- Устанавливать `FileAttributes.ReadOnly`

**⚠️ КРИТИЧНО: `FamilyImportRequest` ДОЛЖЕН содержать `FamilySource`**

`LocalFamilyImportService` принимает `FamilyImportRequest` (см. раздел 4.1). Этот record **обязан** содержать поле `FamilySource` (default = "loadable") и `RevitCategory` (nullable).

`ImportBatchAsync` должен:
1. Пробросить `item.FamilySource` из `FamilyBatchImportRow` в `FamilyImportRequest`
2. Пробросить `item.CategoryId` (для системных) в `FamilyImportRequest.RevitCategory`

**⚠️ КРИТИЧНО: `InsertCatalogItemAsync` ДОЛЖЕН писать `family_source` в БД**

`InsertCatalogItemAsync` (метод `LocalFamilyImportService.Database.cs`) выполняет SQL INSERT в таблицу `catalog_items`. В rev. 1 этот метод **не содержал `family_source` в списке колонок INSERT** — все записи получали дефолтное значение `'loadable'`, что приводило к падению DnD для системных типов.

**Обязательно** включить в INSERT:
```sql
INSERT INTO catalog_items (
    id, name, normalized_name, description, category_name, category_id,
    manufacturer, content_status, current_version_label, published_by,
    family_source,           -- ОБЯЗАТЕЛЬНО
    revit_category,          -- ОБЯЗАТЕЛЬНО
    created_at_utc, updated_at_utc
) VALUES (
    @id, @name, @normalizedName, @description, @categoryName, @categoryId,
    @manufacturer, @contentStatus, @currentVersionLabel, @publishedBy,
    @familySource,           -- ИЗ FamilyImportRequest.FamilySource
    @revitCategory,          -- ИЗ FamilyImportRequest.RevitCategory
    @createdAtUtc, @updatedAtUtc
)
```

**Тестовая проверка после INSERT:**
```csharp
// В Debug-сборке добавить assert
Debug.Assert(
    inserted.FamilySource == request.FamilySource,
    $"BUG: family_source not persisted! Expected '{request.FamilySource}', got '{inserted.FamilySource}'"
);
```

**Все сигнатуры и пути миграции (раздел 7.2.A, B, C, D, E, F) ДОЛЖНЫ быть реализованы ДО первой попытки импорта** — иначе каскад ошибок сделает отладку невозможной.

---

## 8. ViewModel изменения

### 8.1. `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.cs`

Добавить поля:
```csharp
private readonly ISystemFamilyImportService _systemFamilyImportService;
private readonly ISystemFamilyPlacementService _systemFamilyPlacementService;
```

Добавить свойство:
```csharp
[ObservableProperty]
[NotifyCanExecuteChangedFor(nameof(ImportSystemFamilyCommand))]
private bool _canImportSystemFamily;
```

**⚠️ КРИТИЧНО: обновить `FamilyCatalogItemRow` (часть `Tree.cs`)**

В rev. 1 `FamilyCatalogItemRow` создавался БЕЗ поля `FamilySource`, и `FamilyLeafNodeViewModel` (построенный по этому row) тоже имел дефолтное `"loadable"`. В результате ВСЕ семейства в дереве, включая реально системные, отображались как `loadable`.

**Обязательно** в `FamilyManagerMainViewModel.Tree.cs`:
1. При создании `FamilyCatalogItemRow` — пробрасывать `FamilySource` из `FamilyCatalogItem` (из `LocalCatalogProvider.GetAllItemsAsync`):
   ```csharp
   var row = new FamilyCatalogItemRow(
       item.Id,
       item.Name,
       item.CategoryId,
       item.FamilySource);  // <-- ИЗ БД, не дефолт
   ```
2. При создании `FamilyLeafNodeViewModel` в методах `BuildCategoryNode`, `_noCategoryNode`, `_orphanedItemsNode` — пробрасывать `row.FamilySource`:
   ```csharp
   var leaf = new FamilyLeafNodeViewModel(
       row.Id,
       row.DisplayName,
       row.CategoryId,
       row.FamilySource);  // <-- ИЗ row, не дефолт
   ```
3. Проверить, что `LocalCatalogProvider.MapCatalogItem` читает `family_source` из ридера (раздел 7.2.D — уже указано, но убедиться)

**Тестовая проверка после построения дерева:**
```csharp
// В Debug добавить логирование
foreach (var leaf in treeNodes.OfType<FamilyLeafNodeViewModel>()
    .Concat(_noCategoryNode.Children.OfType<FamilyLeafNodeViewModel>()))
{
    SmartConLogger.Info($"[Tree] {leaf.DisplayName}: FamilySource={leaf.FamilySource}");
}
```

Обновить `AttachTypesToNodes` — передавать `FamilySource` в `FamilyTypeNodeViewModel` (см. раздел 8.5).

### 8.2. `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Import.cs`

Добавить команду:
```csharp
[RelayCommand(CanExecute = nameof(CanImportFiles))]
private async Task ImportSystemFamilyAsync()
{
    IsLoading = true;
    StatusMessage = "Select system family elements in Revit...";

    _externalEvent.Raise(() =>
    {
        try
        {
            var result = _systemFamilyImportService.ImportFromSelection();
            
            StatusMessage = result.Success
                ? $"System family imported: {result.TypesCount} types"
                : result.Message ?? "Import failed";

            if (result.Success)
                FireAndForget(async () => await LoadTreeAsync());
        }
        catch (Exception ex)
        {
            StatusMessage = $"Import error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    });
}
```

### 8.3. `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.LoadPlace.cs`

Изменить `StartPlacementDrag`:
```csharp
[RelayCommand(CanExecute = nameof(CanStartPlacementDrag))]
private void StartPlacementDrag(object? item)
{
    if (item is not FamilyTypeNodeViewModel typeNode) return;
    var parent = FindParentOf(TreeNodes, typeNode);
    if (parent is not FamilyLeafNodeViewModel leaf) return;

    // ⚠️ КРИТИЧНО: FamilySource идёт из leaf (FamilyLeafNodeViewModel),
    // а тот получает его из row.FamilySource (FamilyCatalogItemRow),
    // а row получает из FamilyCatalogItem.FamilySource (из БД)
    // Если в rev. 1 здесь был default "loadable" — DnD всегда шёл в ветку .rfa
    var data = new FamilyPlacementDragData(
        leaf.CatalogItemId,
        leaf.DisplayName,
        typeNode.TypeName,
        CurrentRevitVersion,
        typeNode.IsVirtual,
        leaf.FamilySource);  // <-- НЕ дефолт, а из БД

    _placementDragService.StartPlacementDrag(data);
}
```

Добавить метод `PlaceSystemType`:
```csharp
private void PlaceSystemType(string catalogItemId, string typeName, int targetRevit)
{
    _externalEvent.Raise(() =>
    {
        try
        {
            _systemFamilyPlacementService.LoadAndPlaceSystemType(catalogItemId, typeName, targetRevit);
            StatusMessage = $"System type \"{typeName}\" — click to place";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Load error: {ex.Message}";
        }
    });
}
```

В `PlaceType` добавить ветвление:
```csharp
if (leaf.FamilySource == "system")
{
    PlaceSystemType(catalogItemId, typeName, targetRevit);
    return;
}
```

### 8.4. `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.FamilyEdit.cs`

Добавить команды:
```csharp
[RelayCommand(CanExecute = nameof(CanEdit))]
private async Task EditSystemFamilyAsync()
{
    if (SelectedTreeNode is not FamilyLeafNodeViewModel leaf) return;
    
    var resolved = await _fileResolver.ResolveForLoadAsync(leaf.CatalogItemId, CurrentRevitVersion);
    if (string.IsNullOrEmpty(resolved.AbsolutePath)) return;

    _externalEvent.RaiseWithApplication(obj =>
    {
        var app = (UIApplication)obj;
        app.OpenAndActivateDocument(resolved.AbsolutePath);
    });
}

[RelayCommand]
private async Task LoadActiveSystemFamilyAsync()
{
    _externalEvent.RaiseWithApplication(obj =>
    {
        var app = (UIApplication)obj;
        var activeDoc = app.ActiveUIDocument.Document;
        
        // Проверяем что активный документ — это .rvt (не .rfa)
        if (activeDoc.IsFamilyDocument) return;
        
        // Сохраняем во временный .rvt
        var tempDir = Path.Combine(Path.GetTempPath(), "SmartCon", "SystemFamilyLoad", Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var tempPath = Path.Combine(tempDir, activeDoc.Title + ".rvt");
        activeDoc.SaveAs(tempPath);
        
        // Считаем SHA256, показываем batch диалог, импортируем
        // ... (аналогично LoadActiveFamilyAsync для .rfa)
        
        // Закрываем документ и возвращаемся на проект
        var projectDoc = app.Documents.Cast<Document>()
            .FirstOrDefault(d => !d.IsFamilyDocument && !d.IsLinked && d.PathName != activeDoc.PathName);
        
        if (projectDoc != null)
        {
            app.OpenAndActivateDocument(projectDoc.PathName);
        }
        
        activeDoc.Close(false);
    });
}
```

### 8.5. `src/SmartCon.FamilyManager/ViewModels/FamilyTypeNodeViewModel.cs`

Добавить:
```csharp
public sealed class FamilyTypeNodeViewModel : CatalogTreeNodeViewModel
{
    public string CatalogItemId { get; }
    public string TypeName { get; }
    public bool IsVirtual { get; }
    public string FamilySource { get; }   // NEW: основной сигнал
    public string? UniqueId { get; }     // NEW: fallback для старых записей в БД

    public bool IsSystemType =>
        FamilySource == "system" || (UniqueId is not null && UniqueId.Length > 0);

    public FamilyTypeNodeViewModel(
        string catalogItemId,
        string typeName,
        bool isVirtual = false,
        string familySource = "loadable",
        string? uniqueId = null)          // NEW
    {
        CatalogItemId = catalogItemId;
        TypeName = typeName;
        IsVirtual = isVirtual;
        FamilySource = familySource;
        UniqueId = uniqueId;
        DisplayName = typeName;
    }
}
```

**⚠️ КРИТИЧНО: при создании `FamilyTypeNodeViewModel` в `AttachTypesToNodes`**

В rev. 1 `FamilyTypeNodeViewModel` создавался с дефолтным `familySource = "loadable"`. Нужно ОБЯЗАТЕЛЬНО пробрасывать `FamilySource` и `UniqueId` из `FamilyTypeDescriptor` (который читается из `family_types.type_unique_id`):

```csharp
// В AttachTypesToNodes
foreach (var type in typesForItem)
{
    var typeNode = new FamilyTypeNodeViewModel(
        leaf.CatalogItemId,
        type.TypeName,
        type.IsVirtual,
        leaf.FamilySource,    // <-- из leaf (см. раздел 8.1)
        type.UniqueId);       // <-- из БД (FamilyTypeDescriptor.UniqueId)
    leaf.Children.Add(typeNode);
}
```

Где `type.UniqueId` — это `type_unique_id` из таблицы `family_types` (см. раздел 7.2.E — `LocalFamilyTypeRepository` ДОЛЖЕН читать `type_unique_id` из ридера).

### 8.6. `src/SmartCon.FamilyManager/ViewModels/FamilyBatchImportRow.cs`

Добавить свойства:
```csharp
public int TypeCount { get; set; }
public string FamilySource { get; set; } = "loadable";
```

Обновить `AvailableActions`:
```csharp
AvailableActions = Status switch
{
    FamilyBatchImportStatus.Duplicate => [Skip],
    FamilyBatchImportStatus.New => [IncrementVersion, Skip],
    FamilyBatchImportStatus.Existing => [IncrementVersion, OverwriteCurrent, Skip],
    _ => [Skip]
};
```

---

## 9. UI изменения

### 9.1. `src/SmartCon.FamilyManager/Views/FamilyManagerPaneControl.xaml`

В Popup `ImportSplitToggle` добавить кнопку:
```xml
<Button Content="Import System Family"
        Command="{Binding ImportSystemFamilyCommand}"
        MinWidth="140"
        HorizontalContentAlignment="Left"
        Padding="10,6"
        Background="Transparent"
        BorderThickness="0"
        Cursor="Hand"/>
```

### 9.2. `src/SmartCon.FamilyManager/Views/FamilyBatchImportView.xaml`

Адаптировать DataGrid для отображения системных семейств:
- Добавить колонку "Type Count" (видима только для system)
- Для system скрыть Size и RevitVersion (не актуально)
- Для loadable — текущие колонки

### 9.3. `src/SmartCon.FamilyManager/Views/FamilyManagerPaneControl.xaml` (Context Menu)

Добавить пункты меню для системных семейств:
- Для FamilyLeafNodeViewModel с `FamilySource == "system"`:
  - "Edit System Family" → `EditSystemFamilyCommand`
  - "Load Active System Family" → `LoadActiveSystemFamilyCommand`

---

## 10. DI регистрация (ServiceRegistrar.cs)

В `src/SmartCon.App/DI/ServiceRegistrar.cs` добавить:
```csharp
// System Families
services.AddSingleton<ISystemFamilyRevitOperations, SystemFamilyRevitOperations>();
services.AddSingleton<ISystemFamilyPlacementService, SystemFamilyPlacementService>();
services.AddSingleton<ISystemFamilyImportService, SystemFamilyImportService>();
```

---

## 11. Локализация

Добавить ключи в `LocalizationService.Keys.FamilyManager.cs`:
```csharp
public const string FM_ImportSystemFamily = "FM_ImportSystemFamily";
public const string FM_SystemFamilyImported = "FM_SystemFamilyImported";
public const string FM_EditSystemFamily = "FM_EditSystemFamily";
public const string FM_LoadActiveSystemFamily = "FM_LoadActiveSystemFamily";
```

---

## 12. Порядок реализации (этапы)

### Этап 1: Фундамент (DB + Core)
1. ✅ Миграция V11 (`family_source`, `revit_category`, `type_unique_id`)
2. ✅ Обновить `FamilyCatalogItem`, `FamilyTypeDescriptor`, `FamilyCatalogItemRow`
3. ✅ Новые интерфейсы: `ISystemFamilyImportService`, `ISystemFamilyPlacementService`, `ISystemFamilyRevitOperations`
4. ✅ Обновить `IFamilyPlacementService`, `IFamilyCatalogProvider`, `FamilyPlacementDragData`
5. ✅ Обновить `LocalCatalogProvider` (методы, маппинг, **обязательно** `MapCatalogItem` читает `family_source`)
6. ✅ Обновить `LocalFamilyTypeRepository` (чтение/запись `type_unique_id`)
7. ✅ Обновить `StoragePathResolver` (добавить `GetRvtFilePath`)
8. ✅ Обновить `FamilyCatalogSql` (V11 SQL)
9. ✅ Обновить `LocalCatalogMigrator` (V11 migration + EnsureCriticalColumns)

**🛑 GATE 1 (после Этапа 1):**
- [ ] `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25` — собирается без ошибок
- [ ] `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25` — все тесты проходят
- [ ] Юнит-тест на `LocalFamilyImportService.InsertCatalogItemAsync`: вызвать с `FamilySource="system"`, прочитать из БД, убедиться что `family_source == "system"` (а не "loadable")
- [ ] Юнит-тест на `LocalCatalogProvider.GetAllItemsAsync`: создать запись с `family_source="system"`, прочитать, убедиться что `FamilySource` правильно маппится

### Этап 2: Revit-операции
10. ✅ `SystemFamilySelectionFilter`
11. ✅ `SystemFamilyRevitOperations` (PickSystemTypes + CreateCleanProjectWithTypes, **обязательно имя temp .rvt = имя категории**)
12. ✅ `SystemFamilyPlacementService` (LoadAndPlaceSystemType, **обязательно прямой `new Transaction(activeDoc, ...)`**)
13. ✅ Обновить `RevitFamilyPlacementService` (добавить LoadAndPlaceSystemType)
14. ✅ Обновить `FamilyPlacementDropHandler` (ветвление по `FamilySource` ИЛИ `UniqueId`)
15. ✅ Обновить `RevitFamilyPlacementDragService` (передача `FamilySource`)

**🛑 GATE 2 (после Этапа 2):**
- [ ] `dotnet build` R25 + R24 — собирается
- [ ] `dotnet test` — все тесты проходят
- [ ] Code review: убедиться что в `SystemFamilyPlacementService` используется **прямой** `new Transaction(activeDoc, ...)`, а не `_transactionService.RunInTransaction` (без перегрузки с Document)
- [ ] Code review: убедиться что `CreateCleanProjectWithTypes` сохраняет temp .rvt с именем категории, а не GUID

### Этап 3: Импорт сервис
16. ✅ `SystemFamilyImportService` (оркестрация + batch dialog, **обязательно пробросить `FamilySource` в `FamilyImportRequest`**)
17. ✅ Адаптация `FamilyBatchImportView` / `FamilyBatchImportViewModel` для системных
18. ✅ Интеграция batch-диалога

**🛑 GATE 3 (после Этапа 3):**
- [ ] `dotnet build` R25 — собирается
- [ ] `dotnet test` — все тесты проходят
- [ ] Юнит-тест на `SystemFamilyImportService`: проверить что при импорте `FamilyBatchImportRow.FamilySource="system"` доходит до `FamilyImportRequest` (а не теряется как в rev. 1)

### Этап 4: ViewModel + UI
19. ✅ `ImportSystemFamilyCommand` в `FamilyManagerMainViewModel.Import.cs`
20. ✅ Разветвление DnD по `FamilySource`
21. ✅ `EditSystemFamilyCommand` и `LoadActiveSystemFamilyCommand`
22. ✅ Обновить `FamilyTypeNodeViewModel` (добавить `FamilySource` + `UniqueId` + `IsSystemType`)
23. ✅ Обновить `FamilyManagerMainViewModel.Tree.cs` (**обязательно** пробросить `FamilySource` в `FamilyCatalogItemRow` и `FamilyLeafNodeViewModel`)
24. ✅ Обновить XAML (кнопка в Popup, Context Menu)
25. ✅ DI регистрация

**🛑 GATE 4 (после Этапа 4):**
- [ ] `dotnet build` R25 + R24 — собирается
- [ ] `dotnet test` — все тесты проходят
- [ ] Code review: убедиться что в `Tree.cs` `FamilyCatalogItemRow` создаётся с `row.FamilySource` из БД, а не с дефолтом
- [ ] Code review: убедиться что в `AttachTypesToNodes` `FamilyTypeNodeViewModel` создаётся с `leaf.FamilySource` и `type.UniqueId`

### Этап 5: Атрибуты
26. ✅ Авто-извлечение параметров при импорте системных семейств
27. ✅ Интеграция с `IFamilyDataExtractionService` для .rvt

### Этап 6: Документация
28. ✅ Создать ADR `docs/adr/024-system-families.md`
29. ✅ Обновить `docs/domain/models.md`
30. ✅ Обновить `docs/domain/interfaces.md`

### Этап 7: Тестирование
31. ✅ Собрать R25: `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25`
32. ✅ Собрать R24: `dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24`
33. ✅ Unit-тесты: `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25`
34. ✅ Ручное тестирование в Revit:
    - **Шаг проверки 1**: Импортировать системное семейство. Открыть SQLite БД, проверить `family_source == "system"` в `catalog_items` и `type_unique_id IS NOT NULL` в `family_types`
    - **Шаг проверки 2**: Открыть FM-панель. У импортированного семейства в дереве через Debug-лог проверить `leaf.FamilySource == "system"`
    - **Шаг проверки 3**: Запустить DnD. Через Debug-лог проверить `dragData.FamilySource == "system"`. DropHandler должен вызвать `SystemFamilyPlacementService`, а не `LoadFamilyAsync`
    - **Шаг проверки 4**: Убедиться что имя импортированного семейства = имя категории, а не GUID
    - **Шаг проверки 5**: Полный сценарий:
        - Импорт системных семейств (трубы, стены, воздуховоды)
        - Batch-диалог (дедупликация, режимы импорта)
        - DnD размещение в проект
        - Редактирование системного семейства
        - Загрузка активного системного семейства
        - Атрибуты (параметры типов)

---

## 13. Критерии приёмки

### 13.1. Функциональные критерии

1. ✅ Пользователь может выделить элементы в проекте Revit и импортировать системные типы через batch-диалог
2. ✅ Batch-диалог показывает: имя (editable), статус, категория FM, режим импорта, количество типов
3. ✅ Дедупликация работает по SHA256: дубликаты пропускаются, конфликты имен — выбор режима
4. ✅ Системные семейства отображаются в дереве FM без визуального отличия от .rfa
5. ✅ Пользователь может перетащить (DnD) тип из FM в проект Revit — тип копируется и активируется стандартный инструмент
6. ✅ Системный тип корректно копируется в проект вместе с зависимостями (материалы, сегменты, фитинги)
7. ✅ Если тип уже существует в проекте — FM использует существующий, не создавая дубликатов
8. ✅ Атрибуты типоразмера извлекаются автоматически при импорте
9. ✅ Пользователь может редактировать системное семейство через "Edit" (открывается как проект Revit)
10. ✅ Пользователь может загрузить отредактированное системное семейство через "Load Active System Family"
11. ✅ Поддерживаются все основные системные семейства: стены, перекрытия, трубы, воздуховоды, лотки, короба, крыши
12. ✅ Файлы .rvt хранятся в managed storage с ReadOnly-флагом

### 13.2. Технические критерии (для отлова багов rev. 1)

13. ✅ **FamilySource персистится в БД**: после импорта системного семейства SQL-запрос `SELECT family_source FROM catalog_items WHERE id = ?` возвращает `'system'`, а не `'loadable'`
14. ✅ **FamilySource читается из БД**: после перезапуска FM-панели `LocalCatalogProvider.GetAllItemsAsync` возвращает `FamilySource="system"` для импортированного типа
15. ✅ **FamilySource пробрасывается в дерево**: `FamilyLeafNodeViewModel.FamilySource == "system"` для системного типа, не дефолт `"loadable"`
16. ✅ **FamilySource пробрасывается в FamilyTypeNodeViewModel**: `typeNode.IsSystemType == true` для системного типа
17. ✅ **DnD корректно маршрутизирует**: при drag системного типа `dragData.FamilySource == "system"`, DropHandler вызывает `SystemFamilyPlacementService.LoadAndPlaceSystemType`
18. ✅ **UniqueId fallback работает**: если в БД отсутствует `family_source` (старая запись), но `type_unique_id IS NOT NULL`, DropHandler всё равно маршрутизирует в `SystemFamilyPlacementService`
19. ✅ **Имя temp .rvt = имя категории**: после импорта в SQLite `catalog_items.name` НЕ содержит GUID (например, должно быть `"OST_PipeCurves"`, а не `"a3f5b8c1d4..."`)
20. ✅ **Placement transaction — прямой new Transaction**: в `SystemFamilyPlacementService.LoadAndPlaceSystemType` используется `new Transaction(activeDoc, "...")`, а не `_transactionService.RunInTransaction(...)`
21. ✅ **PostRequestForElementTypePlacement вне транзакции**: `uiApp.ActiveUIDocument?.PostRequestForElementTypePlacement(elementType)` вызывается ПОСЛЕ `tx.Commit()` и закрытия `using-блока`
22. ✅ **Source doc закрывается до активации**: `sourceDoc.Close(false)` вызывается ДО `ActivatePlacement`

---

## 14. Известные риски

| Риск | Митигация |
|---|---|
| `CopyElements` не копирует все зависимости | Доказано в spike E-1 — CopyElements корректно переносит зависимости |
| UniqueId меняется при CopyElements | Используем имя типа как стабильный ключ |
| .rvt несовместим между версиями Revit | Сообщение "Нет версии для Revit {version}" |
| `PostRequestForElementTypePlacement` не принимает Transaction | Вызывать ВНЕ транзакции и ВНЕ sourceDoc lifecycle |
| Пустой проект без MEP-инфраструктуры | Не требуется — зависимости копируются с типами |
| Batch-диалог для системных и loadable различаются | Используем один диалог с условным отображением колонок |
| **`FamilySource` не персистится в БД (баг rev. 1)** | Явное указание `family_source` в `InsertCatalogItemAsync` + Debug.Assert после INSERT + юнит-тест |
| **GUID в имени temp .rvt (баг rev. 1)** | Имя файла = sanitized имя категории через `SanitizeFileName` |
| **FamilySource не пробрасывается в дерево (баг rev. 1)** | Явный проброс в `Tree.cs`: `row.FamilySource → leaf.FamilySource → typeNode.FamilySource` + Debug-логирование |
| **`_transactionService.RunInTransaction` ломает cross-document CopyElements** | Прямой `new Transaction(activeDoc, ...)` в `SystemFamilyPlacementService` (паттерн проверен в feature/system-families) |
| **DropHandler идёт в ветку .rfa для .rvt файла (баг rev. 1)** | Fallback на `UniqueId` в `FamilyTypeNodeViewModel` + проверка в DropHandler |
| **git clean -fd удаляет несохранённые файлы** | Перед checkout — `git stash -u` или ручной backup. Никогда не использовать `git clean -fd` без отдельного backup неужели отслеживаемых файлов |

---

## 15. Связанные документы

- `docs/family-manager/00-strategy/02-familymanager-systemfamilies-case.md` — бизнес-кейс
- `docs/family-manager/02-spikes/01-pipe-import-placement-spike.md` — spike E-1 (опыт)
- `docs/invariants.md` — жёсткие правила I-01..I-17
- `docs/architecture/dependency-rule.md` — правило зависимостей
- `docs/domain/models.md` — доменные модели
- `docs/domain/interfaces.md` — интерфейсы

---

**Конец документа.**
