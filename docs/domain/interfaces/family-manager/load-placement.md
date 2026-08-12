---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager — Загрузка и размещение

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## IFamilyLoadService

Загрузка семейства в проект Revit. **Не содержит `Document` в параметрах** — Document получается через `IRevitContext` в реализации.
**Вызывать только из ExternalEvent handler (I-01).**

С версии Issue #67 поддерживает опциональный callback `onSharedDecision` для интерактивного выбора режима загрузки общих вложенных семейств (shared nested). Если callback не передан, используется безопасный дефолт `UseProject` (back-compat).

С версии Issue #77 (ADR-034) добавлен опциональный параметр `nestedSharedNames` —
список имён shared nested, извлечённых при импорте в FM. Используется как fallback
для имени в диалоге, когда Revit API возвращает `null` (REVIT-198137 в Revit
2023 / Revit 2024 < 24.3.0.13). Если `null`/пусто, сервис пытается резолвить
через `ISharedNestedFamilyRepository.GetNamesForCurrentVersionAsync(catalogItemId)`.

**Файл:** `IFamilyLoadService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyLoadService.cs`

```csharp
public interface IFamilyLoadService
{
    Task<FamilyLoadResult> LoadFamilyAsync(
        FamilyResolvedFile file, FamilyLoadOptions options,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        CancellationToken ct = default);

    Task<FamilyLoadResult> LoadFamilySymbolAsync(
        string filePath, string typeName,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        CancellationToken ct = default);

    Task<FamilyLoadResult> ReloadFamilyPreservingLoadedTypesAsync(
        FamilyResolvedFile file, bool overwriteParameterValues,
        Action<string>? onStatusMessage = null,
        Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? onSharedDecision = null,
        IReadOnlyList<string>? nestedSharedNames = null,
        CancellationToken ct = default);
}
```

`ReloadFamilyPreservingLoadedTypesAsync` (Issue #101): перезагружает семейство,
сохраняя набор уже загруженных в проект типов (per-type `LoadFamilySymbol` внутри
одной `TransactionGroup`-сессии через `ITransactionService.BeginGroupSession` — I-03).
Параметр `nestedSharedNames` (добавлен вместе с аудит-харденингом): вызывающие,
которые блокируют метод синхронно внутри ExternalEvent-callback
(`StaleFamilyUpdater`, `.GetAwaiter().GetResult()`), **обязаны** предварительно
резолвить и передать список — это убирает единственный асинхронный (SQLite) await
из метода и гарантирует синхронное завершение на Revit main thread
(латентный deadlock: async-методы Microsoft.Data.Sqlite завершаются синхронно,
но контракт на это не полагается).

`onSharedDecision` вызывается один раз для каждого конфликтующего shared nested
(когда Revit сообщает `OnSharedFamilyFound`). Должен блокировать вызывающий поток
(Revit main thread) до ответа пользователя через WPF `ShowDialog`.

---
## IFamilyLoadServiceSourceAware

Source-aware расширение `IFamilyLoadService` (#209) для ветки обновления вложенного
семейства внутри открытого family-документа. Позволяет передать УЖЕ открытый
исходный документ, чтобы один цикл обновления (pre-verify → poke → doc-to-doc
merge → post-verify) делил одно `OpenDocumentFile` на семейство вместо 2-3
открытий того же файла. Реализуется тем же `RevitFamilyLoadService`; вызывающий
(`StaleFamilyUpdater`) обнаруживает его через `is`-проверку и при отсутствии
уходит в legacy-путь `ReloadFamilyPreservingLoadedTypesAsync`.
Метод синхронный — вызывается только на Revit main thread (внутри ExternalEvent
callback, I-01). Переданный документ — BORROWED: владение и закрытие остаются
за вызывающим, сервис его не закрывает; при `null`/null-результате провайдера
сервис открывает (и закрывает) файл сам. Когда документ передан, guard
«файл версии открыт в редакторе» пропускается — вызывающий обязан выполнить
эквивалентную проверку ДО открытия.

**Файл:** `IFamilyLoadServiceSourceAware.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyLoadService.cs`

```csharp
public interface IFamilyLoadServiceSourceAware : IFamilyLoadService
{
    FamilyLoadResult ReloadNestedInFamilyDocument(
        string normalizedPath,
        string familyName,
        bool overwriteParameterValues,
        Func<Document?>? preOpenedSourceDocProvider = null);
}
```

---
## ISharedNestedFamilyRepository

CRUD-репозиторий для имён общих вложенных семейств, персистленных в локальной
SQLite-БД каталога FM. Заполняется при импорте **внутри**
`IFamilyDataExtractionService.ExtractFromManagedFile` open-close цикла
(см. ADR-034 §2), читается при загрузке в проект. **V3 simplification**:
отдельный `ISharedNestedFamilyExtractor` (V2) удалён, чтобы не открывать
`.rfa` дважды.

**Файл:** `ISharedNestedFamilyRepository.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalSharedNestedFamilyRepository.cs`

```csharp
public interface ISharedNestedFamilyRepository
{
    Task ReplaceForVersionAsync(
        string catalogItemId, string versionId,
        IReadOnlyList<string> nestedSharedNames,
        CancellationToken ct = default);

    Task<IReadOnlyList<string>> GetNamesForCurrentVersionAsync(
        string catalogItemId,
        CancellationToken ct = default);
}
```

- `ReplaceForVersionAsync` — атомарно заменяет список имён для пары
  `(catalogItemId, versionId)`. Дедупликация case-insensitive.
- `GetNamesForCurrentVersionAsync` — возвращает имена для **текущей** версии
  (по `catalog_items.current_version_label`), отсортированные по `ordinal`.
  Пустой список если данных нет (legacy-каталог).

**Где вызывается из ViewModel:** `FamilyManagerMainViewModel.SaveSharedNestedNamesAsync`
(новый helper в `FamilyManagerMainViewModel.Extract.cs`) — после каждого
успешного `ExtractFromManagedFileAsync`. Helper-метод no-op при
`versionId == null` (legacy items) или `sharedNames.Count == 0`.

---

## IFamilyPlacementService

Размещение семейств и типоразмеров в проекте Revit. Все операции выполняются в контексте ExternalEvent (I-01).

**Файл:** `IFamilyPlacementService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyPlacementService.cs`

```csharp
public interface IFamilyPlacementService
{
    void ActivateAndPlaceType(string familyName, string typeName);
    void LoadAndPlaceFamily(string filePath, string familyName, string? preferredTypeName = null);
}
```

---

## IFamilyPlacementDragService

Инициирует нативную Revit drag-and-drop операцию для размещения типоразмера семейства. Реализация вызывает `UIApplication.DoDragDrop` с кастомным `IDropHandler`.

**Файл:** `IFamilyPlacementDragService.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyPlacementDragService.cs`

```csharp
public interface IFamilyPlacementDragService
{
    void StartPlacementDrag(FamilyPlacementDragData data);
    event Action? PlacementCompleted;
    event Action<string>? PlacementFailed;
    event Action<string>? PlacementSucceeded;
    event Action<string>? PlacementStatusMessage;
    event Func<SharedFamilyDecisionRequest, SharedFamiliesLoadChoice>? SharedFamilyDecisionRequested;
}
```

`SharedFamilyDecisionRequested` (issue #67) срабатывает когда drop-handler загружает
семейство с конфликтующим shared nested. Подписчик (FamilyManagerMainViewModel)
обязан вызвать WPF-диалог на Revit main thread и вернуть выбор пользователя.
