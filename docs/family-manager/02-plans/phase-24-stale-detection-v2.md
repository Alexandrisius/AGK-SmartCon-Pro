# Phase 24: Stale Detection v2 — Технический план реализации

> **ADR:** [ADR-030](../../adr/030-phase-24-stale-detection-v2.md)
> **Issue:** [#69](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/69)
> **Версия:** 2.0.0 (Breaking Change)
> **Дата:** 2026-06-18

---

## 1. Обзор

Реализация on-demand stale detection через маркер версии на `Family` элементе в проекте Revit (ExtensibleStorage Schema `SmartCon_FamilyVersion_v1`). Заменяет сломанную pull-based логику (баг: `Tree.cs:143 == LoadPlace.cs:105` → stale никогда не показывался).

**Ключевые решения:** см. [ADR-030 §Решение](../../adr/030-phase-24-stale-detection-v2.md).

---

## 2. Стратегия коммитов (порядок важен)

| # | Коммит | Что | Тесты | Риск |
|---|---|---|---|---|
| **C1** | `chore(fm): add ADR-030 + technical plan` | Документация (ADR-030 + этот план) | — | Низкий |
| **C2** | `feat(fm.core): add Phase 24 domain models` | 4 модели в Core (`FamilyVersion`, `StaleCheckResult`+enum, `StaleUpdateRequest`, `FamilyStaleSnapshot`) | Unit-тесты на equality/empty | Низкий |
| **C3** | `feat(fm.core): add Phase 24 interfaces` | 4 интерфейса в Core (`IFamilyVersionStore`, `IStaleDetector`, `IStaleFamilyUpdater`, `IStaleCategoryAggregator`) | Compile-time | Низкий |
| **C4** | `feat(fm): add SmartConFamilyVersionSchema` | Schema `FamilyVersionSchema` в `SmartCon.Revit/FamilyManager/` | Manual smoke test | Средний |
| **C5** | `feat(revit): add RevitFamilyVersionStore` | Реализация `IFamilyVersionStore` в `SmartCon.Revit/FamilyManager/` | Manual smoke + integration | Средний |
| **C6** | `feat(fm): add StaleCategoryAggregator` | Pure logic в `SmartCon.FamilyManager/Services/Stale/` | Unit-тесты | Низкий |
| **C7** | `feat(fm): add StaleDetector + StaleFamilyUpdater` | 2 реализации в `SmartCon.FamilyManager/Services/Stale/` | Unit-тесты через InMemory | Средний |
| **C8** | `feat(fm): add StaleCheck*Command/Update*Command` | Команды в `FamilyManagerMainViewModel` partial | ViewModel-тесты | Средний |
| **C9** | `feat(fm): update XAML context menus + indicators` | UI изменения в `FamilyManagerPaneControl.xaml` | Manual UI test | Низкий |
| **C10** | `feat(fm): SQLite migration V12 — drop project_usage` | `MigrateV12Async` + `MigrateV12DropProjectUsage` SQL | Migration tests | Средний |
| **C11** | `refactor(fm): remove IProjectFamilyUsageRepository` | Удаление `LocalProjectFamilyUsageRepository`, `ProjectFamilyUsage`, `IProjectFamilyUsageRepository` | Compile-time | Средний |
| **C12** | `refactor(fm): remove stale logic from Tree.cs` | Удаление `IsStale` (Tree.cs:122-148, 199-225) + `GetLoadedVersionLabelsAsync` | Compile-time | Низкий |
| **C13** | `refactor(fm): remove RecordUsageAsync from LoadPlace.cs` | Удаление блока 104-124, замена на `WriteToLoadedFamily` | Manual UI test | Средний |
| **C14** | `feat(fm): localization (ru + en) for new strings` | 10 новых ключей в `StringLocalization.Keys` | Локализационные тесты | Низкий |
| **C15** | `docs(fm): update domain models/interfaces/glossary` | `docs/domain/models/family-manager.md`, `interfaces/family-manager.md`, `glossary.md` | `validate-docs.ps1` | Низкий |
| **C16** | `chore(fm): bump to 2.0.0-beta.1 (ADR-021)` | `Version.txt`, `Directory.Build.props` | — | Низкий |
| **C17** | `chore(fm): update CHANGELOG` | CHANGELOG.md | — | Низкий |

**Рекомендация:** мерджить в develop после C15, **до** C16 (pre-release). C16-C17 — отдельный PR.

---

## 3. Новые файлы (C2, C3, C4, C5, C6, C7)

### 3.1 `src/SmartCon.Core/Models/FamilyManager/FamilyVersion.cs` (C2)

```csharp
namespace SmartCon.Core.Models.FamilyManager;

public sealed record FamilyVersion(
    int SchemaVersion,
    string CatalogItemId,
    string VersionLabel,
    DateTimeOffset LoadedAtUtc,
    int SourceRevitVersion)
{
    public const int CurrentSchemaVersion = 1;
    public static FamilyVersion Empty { get; } = new(0, "", "", DateTimeOffset.MinValue, 0);
}
```

### 3.2 `src/SmartCon.Core/Models/FamilyManager/StaleCheckResult.cs` (C2)

```csharp
namespace SmartCon.Core.Models.FamilyManager;

public enum StaleReason
{
    None = 0,
    NoEntityStorage = 1,        // Семейство без ES (старая загрузка)
    VersionMismatch = 2,         // Версия в ES != каталог
    RevitVersionMismatch = 3,    // Revit major version изменился
    NotInCatalog = 4,            // Семейство не в каталоге FM
}

public sealed record StaleCheckResult(
    string CatalogItemId,
    string FamilyName,
    string CurrentVersionLabel,
    string? LoadedVersionLabel,
    bool IsStale,
    StaleReason Reason);
```

### 3.3 `src/SmartCon.Core/Models/FamilyManager/StaleUpdateRequest.cs` (C2)

```csharp
namespace SmartCon.Core.Models.FamilyManager;

public sealed record StaleUpdateRequest(
    IReadOnlyList<string> CatalogItemIds,
    bool OverwriteParameterValues,
    bool Recursive = true);
```

### 3.4 `src/SmartCon.Core/Models/FamilyManager/StaleBatchUpdateResult.cs` (C2)

```csharp
namespace SmartCon.Core.Models.FamilyManager;

public sealed record StaleBatchUpdateResult(
    int TotalRequested,
    int SuccessCount,
    int FailedCount,
    IReadOnlyList<string> FailedCatalogItemIds);

public sealed record StaleBatchUpdateProgress(
    int Completed,
    int Total,
    string CurrentFamilyName);
```

### 3.5 `src/SmartCon.Core/Models/FamilyManager/FamilyStaleSnapshot.cs` (C2)

```csharp
namespace SmartCon.Core.Models.FamilyManager;

public sealed record FamilyStaleSnapshot(
    IReadOnlyDictionary<string, StaleCheckResult> Results,
    DateTimeOffset CheckedAtUtc)
{
    public static FamilyStaleSnapshot Empty { get; } = new(
        new Dictionary<string, StaleCheckResult>(),
        DateTimeOffset.MinValue);
}
```

### 3.6 `src/SmartCon.Core/Services/Interfaces/IFamilyVersionStore.cs` (C3)

```csharp
using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IFamilyVersionStore
{
    FamilyVersion? ReadFromLoadedFamily(Document doc, ElementId familyId);
    void WriteToLoadedFamily(Document doc, ElementId familyId, FamilyVersion version);
    IReadOnlyDictionary<ElementId, FamilyVersion?> ReadManyFromDocument(
        Document doc, IEnumerable<ElementId> familyIds);
}
```

### 3.7 `src/SmartCon.Core/Services/Interfaces/IStaleDetector.cs` (C3)

```csharp
using Autodesk.Revit.DB;
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IStaleDetector
{
    Task<StaleCheckResult> CheckFamilyAsync(
        string catalogItemId, string familyName, Document doc, ElementId familyId, CancellationToken ct);

    Task<IReadOnlyList<StaleCheckResult>> CheckCategoryAsync(
        string categoryId, bool recursive, Document doc, CancellationToken ct);

    FamilyStaleSnapshot? GetCachedSnapshot();

    void InvalidateCache();
}
```

### 3.8 `src/SmartCon.Core/Services/Interfaces/IStaleFamilyUpdater.cs` (C3)

```csharp
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IStaleFamilyUpdater
{
    Task<bool> UpdateFamilyAsync(
        string catalogItemId, bool overwriteParameterValues, CancellationToken ct);

    Task<StaleBatchUpdateResult> UpdateBatchAsync(
        StaleUpdateRequest request,
        IProgress<StaleBatchUpdateProgress>? progress = null,
        CancellationToken ct = default);
}
```

### 3.9 `src/SmartCon.Core/Services/Interfaces/IStaleCategoryAggregator.cs` (C3)

```csharp
using SmartCon.Core.Models.FamilyManager;

namespace SmartCon.Core.Services.Interfaces;

public interface IStaleCategoryAggregator
{
    /// <summary>
    /// Roll-up HasStale per category (true если любое stale в категории или её подкатегориях).
    /// </summary>
    IReadOnlyDictionary<string, bool> AggregateByCategory(
        IReadOnlyList<StaleCheckResult> results,
        IReadOnlyDictionary<string, List<string>> categoryIndex);

    /// <summary>
    /// Строит маппинг catalogItemId -> [categoryId, ...] (с учётом recursive=true).
    /// Используется в ApplyStaleResultsToTreeAsync для обновления HasStale на каждой категории.
    /// </summary>
    IReadOnlyDictionary<string, HashSet<string>> GetCatalogItemCategoryMap(
        IEnumerable<string> catalogItemIds,
        IEnumerable<CategoryNodeViewModel> rootNodes);
}
```

> Примечание: `CategoryNodeViewModel` — это VM-тип, который лежит в `SmartCon.FamilyManager`, не в Core. Для сохранения I-09 (Core без зависимостей) этот метод должен принимать **абстракцию** категории (например, `IReadOnlyCollection<ICategoryNodeInfo>`). В финальной реализации уточнить — возможно выделить `ICategoryNodeInfo` в Core.

### 3.10 `src/SmartCon.Revit/FamilyManager/FamilyVersionSchema.cs` (C4)

```csharp
using Autodesk.Revit.DB.ExtensibleStorage;
using System;

namespace SmartCon.Revit.FamilyManager;

internal static class FamilyVersionSchema
{
    // ⚠️ ЗАФИКСИРОВАТЬ ОДИН РАЗ при первом коммите. Смена GUID = потеря данных.
    public static readonly Guid SchemaGuid = new("<ЗАФИКСИРОВАННЫЙ-GUID>");

    public const string SchemaName = "SmartCon_FamilyVersion_v1";
    public const string VendorId = "AGKSMARTCON";  // 9 chars, ≥4 required by API

    public const string FieldSchemaVersion = "SchemaVersion";
    public const string FieldCatalogItemId = "CatalogItemId";
    public const string FieldVersionLabel = "VersionLabel";
    public const string FieldLoadedAtUtc = "LoadedAtUtc";
    public const string FieldSourceRevitVersion = "SourceRevitVersion";

    public static Schema GetOrCreate() => Schema.Lookup(SchemaGuid) ?? Build();

    private static Schema Build()
    {
        using var builder = new SchemaBuilder(SchemaGuid);
        builder.SetVendorId(VendorId);
        builder.SetSchemaName(SchemaName);
        builder.SetDocumentation("Family version marker for SmartCon FamilyManager (Phase 24, ADR-030). Stored on Family element in project.");

        // Public+Public — workaround для .addin VendorId="AGK" (3 chars < 4 required).
        // Защита через уникальный GUID. (Аналогично FittingMappingSchema.cs:57-66.)
        builder.SetReadAccessLevel(AccessLevel.Public);
        builder.SetWriteAccessLevel(AccessLevel.Public);

        builder.AddSimpleField(FieldSchemaVersion, typeof(int));
        builder.AddSimpleField(FieldCatalogItemId, typeof(string));
        builder.AddSimpleField(FieldVersionLabel, typeof(string));
        builder.AddSimpleField(FieldLoadedAtUtc, typeof(string));
        builder.AddSimpleField(FieldSourceRevitVersion, typeof(int));
        return builder.Finish();
    }
}
```

### 3.11 `src/SmartCon.Revit/FamilyManager/RevitFamilyVersionStore.cs` (C5)

**Зависимости:** `ITransactionService`

**Ключевые методы:**

```csharp
public sealed class RevitFamilyVersionStore : IFamilyVersionStore
{
    private readonly ITransactionService _tx;

    public RevitFamilyVersionStore(ITransactionService tx)
    {
        _tx = tx;
    }

    public FamilyVersion? ReadFromLoadedFamily(Document doc, ElementId familyId)
    {
        try
        {
            var family = doc.GetElement(familyId) as Family;
            if (family is null) return null;

            var schema = FamilyVersionSchema.GetOrCreate();
            using var entity = family.GetEntity(schema);  // IDisposable!
            if (!entity.IsValid()) return null;

            return new FamilyVersion(
                entity.Get<int>(FamilyVersionSchema.FieldSchemaVersion),
                entity.Get<string>(FamilyVersionSchema.FieldCatalogItemId),
                entity.Get<string>(FamilyVersionSchema.FieldVersionLabel),
                DateTimeOffset.Parse(entity.Get<string>(FamilyVersionSchema.FieldLoadedAtUtc)),
                entity.Get<int>(FamilyVersionSchema.FieldSourceRevitVersion));
        }
        catch (Exception ex)
        {
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection", ("Method", nameof(ReadFromLoadedFamily)));
            SmartConLogger.Warn(
                $"ReadFromLoadedFamily[{familyId.GetValue()}]: failed: {ex.Message}. [Action: family skipped, продолжить batch]");
            return null;
        }
    }

    public void WriteToLoadedFamily(Document doc, ElementId familyId, FamilyVersion version)
    {
        _tx.RunInTransaction("SmartCon: Write FamilyVersion", txDoc =>
        {
            var family = txDoc.GetElement(familyId) as Family;
            if (family is null) return;

            var schema = FamilyVersionSchema.GetOrCreate();
            using var entity = new Entity(schema);
            entity.Set(FamilyVersionSchema.FieldSchemaVersion, FamilyVersion.CurrentSchemaVersion);
            entity.Set(FamilyVersionSchema.FieldCatalogItemId, version.CatalogItemId);
            entity.Set(FamilyVersionSchema.FieldVersionLabel, version.VersionLabel);
            entity.Set(FamilyVersionSchema.FieldLoadedAtUtc, version.LoadedAtUtc.ToString("o"));
            entity.Set(FamilyVersionSchema.FieldSourceRevitVersion, version.SourceRevitVersion);
            family.SetEntity(entity);
        });
    }

    public IReadOnlyDictionary<ElementId, FamilyVersion?> ReadManyFromDocument(
        Document doc, IEnumerable<ElementId> familyIds)
    {
        var schema = FamilyVersionSchema.GetOrCreate();
        var result = new Dictionary<ElementId, FamilyVersion?>();
        var counter = new HotLoopCounter(sampleEvery: 32);

        foreach (var id in familyIds)
        {
            try
            {
                var family = doc.GetElement(id) as Family;
                if (family is null) { result[id] = null; continue; }

                using var entity = family.GetEntity(schema);
                if (!entity.IsValid()) { result[id] = null; continue; }

                result[id] = new FamilyVersion(
                    entity.Get<int>(FamilyVersionSchema.FieldSchemaVersion),
                    entity.Get<string>(FamilyVersionSchema.FieldCatalogItemId),
                    entity.Get<string>(FamilyVersionSchema.FieldVersionLabel),
                    DateTimeOffset.Parse(entity.Get<string>(FamilyVersionSchema.FieldLoadedAtUtc)),
                    entity.Get<int>(FamilyVersionSchema.FieldSourceRevitVersion));
            }
            catch (Exception ex)
            {
                if (counter.ShouldLog())
                {
                    using var _scope = SmartConLogger.BeginScope(
                        "StaleDetection", ("Method", nameof(ReadManyFromDocument)));
                    SmartConLogger.Warn(
                        $"ReadManyFromDocument[{id.GetValue()}]: {ex.Message}. [Action: family skipped]");
                }
                result[id] = null;
            }
        }
        return result;
    }
}
```

### 3.12 `src/SmartCon.FamilyManager/Services/Stale/StaleCategoryAggregator.cs` (C6)

```csharp
using System.Collections.Generic;
using System.Linq;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.Core.Services.Interfaces;

namespace SmartCon.FamilyManager.Services.Stale;

internal sealed class StaleCategoryAggregator : IStaleCategoryAggregator
{
    public IReadOnlyDictionary<string, bool> AggregateByCategory(
        IReadOnlyList<StaleCheckResult> results,
        IReadOnlyDictionary<string, List<string>> categoryIndex)
    {
        // categoryIndex: catalogItemId -> [categoryId, ...]
        // Для каждой категории проверяем, есть ли stale в ней (или в её подкатегориях — recursive).
        // Эта логика упрощена: передаётся уже полный плоский список categoryIndex
        // с учётом recursive=true. Pure logic, без Revit.

        var staleCatalogIds = results
            .Where(r => r.IsStale)
            .Select(r => r.CatalogItemId)
            .ToHashSet();

        var hasStale = new Dictionary<string, bool>();
        foreach (var (catalogItemId, categoryIds) in categoryIndex)
        {
            if (!staleCatalogIds.Contains(catalogItemId)) continue;
            foreach (var catId in categoryIds)
            {
                hasStale[catId] = true;  // overwrite — true доминирует
            }
        }
        return hasStale;
    }
}
```

### 3.13 `src/SmartCon.FamilyManager/Services/Stale/StaleDetector.cs` (C7)

**Зависимости:** `IFamilyVersionStore`, `IFamilyCatalogProvider`, `IFamilyManagerAwaitableEvent`, `IRevitContext`

См. полную реализацию в [ADR-030 §Архитектура](../../adr/030-phase-24-stale-detection-v2.md#2-core-модели-новые-pure-c).

**Ключевые методы:**
- `CheckFamilyAsync` — single family
- `CheckCategoryAsync` — batch с `_awaitable.RaiseAsyncTask` для Revit API + `ReadManyFromDocument` для ES
- `GetCachedSnapshot` / `InvalidateCache`

### 3.14 `src/SmartCon.FamilyManager/Services/Stale/StaleFamilyUpdater.cs` (C7)

**Зависимости:** `IFamilyLoadService`, `IFamilyFileResolver`, `IFamilyVersionStore`, `IFamilyManagerDialogService`, `IFamilyManagerAwaitableEvent`, `IRevitContext`, `IClock`

**Ключевые методы:**
- `UpdateFamilyAsync` — single update + ES write
- `UpdateBatchAsync` — sequential с `IProgress<>` reporting

---

## 4. Изменения существующих файлов

### 4.1 `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.cs` (C8)

**Добавить поля:**
```csharp
[ObservableProperty] private FamilyStaleSnapshot? _currentStaleSnapshot;
[ObservableProperty] private bool _isStaleCheckInProgress;
[ObservableProperty] private string? _staleCheckMessage;

private readonly IFamilyVersionStore _versionStore;
private readonly IStaleDetector _staleDetector;
private readonly IStaleFamilyUpdater _staleUpdater;
private readonly IStaleCategoryAggregator _staleCategoryAggregator;
private readonly IClock _clock;
```

**Удалить:**
- `_usageRepo` поле
- `_loadedFamilyNamesCache`, `_loadedFamilyNamesCacheProjectPath`
- `GetLoadedFamilyNamesCached()`, `InvalidateLoadedFamilyNamesCache()` методы

**Обновить constructor** — добавить новые зависимости, убрать `IProjectFamilyUsageRepository`.

### 4.2 `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Tree.cs` (C8, C12)

**Удалить:**
- `FireAndForget(async () => { _usageRepo.DeleteOldUsagesAsync(...) })` (Tree.cs:22-37)
- `var familyIds = results.Select(r => r.Id).ToList(); loadedVersionLabels = ... GetLoadedVersionLabelsAsync(...)` (Tree.cs:85-101)
- Логику `isStale` в `BuildCategoryNode` (Tree.cs:122-148)
- Логику `isStale` в `BuildCategoryNode` recursive (Tree.cs:199-209)

**Заменить** на:
```csharp
// Используем snapshot из кеша
var staleSnapshot = _staleDetector.GetCachedSnapshot();
var isStale = staleSnapshot?.Results.TryGetValue(item.Id, out var r) == true && r.IsStale;
```

### 4.3 `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.LoadPlace.cs` (C8, C13)

**Удалить строки 104-124** (project_usage + InvalidateLoadedFamilyNamesCache + FireAndForget RecordUsageAsync).

**Заменить на:**
```csharp
// В ExecuteLoadOrUpdateAsync после успешного Load:
var familyVersion = new FamilyVersion(
    SchemaVersion: FamilyVersion.CurrentSchemaVersion,
    CatalogItemId: selectedId,
    VersionLabel: resolved.VersionLabel ?? "",
    LoadedAtUtc: _clock.UtcNow,
    SourceRevitVersion: targetRevit);

await _awaitable.RaiseAsyncTask(async _ =>
{
    var doc = _revitContext.GetDocument();
    var family = FindFamilyByName(doc, result.FamilyName ?? selectedName);
    if (family != null)
    {
        _versionStore.WriteToLoadedFamily(doc, family.Id, familyVersion);
    }
}, CancellationToken.None).ConfigureAwait(true);

_staleDetector.InvalidateCache();
LoadTreeAsync();  // для UI roll-up
```

**Удалить** строки 228-248 в `PlaceTypeAsync` (RecordUsageAsync блок) — аналогично.

### 4.4 `src/SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.NewCommands.cs` (C8, **новый файл**)

```csharp
using CommunityToolkit.Mvvm.Input;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.FamilyManager.ViewModels;

public sealed partial class FamilyManagerMainViewModel
{
    [RelayCommand(CanExecute = nameof(CanCheckCategory))]
    private async Task CheckCategoryAsync(CategoryNodeViewModel? category)
    {
        if (category is null) return;
        IsStaleCheckInProgress = true;
        StaleCheckMessage = LanguageManager.GetString(StringLocalization.Keys.FM_StaleCheckInProgress);
        try
        {
            using var _scope = SmartConLogger.BeginScope(
                "StaleDetection",
                ("Method", nameof(CheckCategoryAsync)),
                ("CategoryId", category.CategoryId));

            var doc = _revitContext.GetDocument();
            var results = await _staleDetector.CheckCategoryAsync(
                category.CategoryId, recursive: true, doc, CancellationToken.None);

            // Roll-up: обновить HasStale на категории и всех подкатегориях
            await ApplyStaleResultsToTreeAsync(results, CancellationToken.None);

            SmartConLogger.Info($"Check completed: {results.Count(r => r.IsStale)} stale of {results.Count}");
        }
        finally
        {
            IsStaleCheckInProgress = false;
            StaleCheckMessage = null;
        }
    }

    private bool CanCheckCategory(CategoryNodeViewModel? category) =>
        category != null && !IsStaleCheckInProgress;

    [RelayCommand(CanExecute = nameof(CanCheckFamily))]
    private async Task CheckFamilyAsync(FamilyLeafNodeViewModel? family) { /* similar */ }

    [RelayCommand(CanExecute = nameof(CanUpdateCategoryOverwrite))]
    private async Task UpdateCategoryOverwriteParamsAsync(CategoryNodeViewModel? category)
    {
        if (category is null) return;
        await UpdateCategoryStaleAsync(category, overwriteParameterValues: true);
    }

    [RelayCommand(CanExecute = nameof(CanUpdateCategoryKeep))]
    private async Task UpdateCategoryKeepParamsAsync(CategoryNodeViewModel? category)
    {
        if (category is null) return;
        await UpdateCategoryStaleAsync(category, overwriteParameterValues: false);
    }

    private async Task UpdateCategoryStaleAsync(CategoryNodeViewModel category, bool overwriteParameterValues)
    {
        var snapshot = _staleDetector.GetCachedSnapshot();
        if (snapshot is null) return;

        var staleIds = snapshot.Results
            .Where(r => r.IsStale)
            .Select(r => r.CatalogItemId)
            .ToList();

        if (staleIds.Count == 0) return;

        var request = new StaleUpdateRequest(staleIds, overwriteParameterValues, recursive: true);
        var progress = new Progress<StaleBatchUpdateProgress>(p =>
        {
            StaleCheckMessage = $"{p.Completed}/{p.Total}: {p.CurrentFamilyName}";
        });

        var result = await _staleUpdater.UpdateBatchAsync(request, progress, CancellationToken.None);

        SmartConLogger.Info($"Batch update: {result.SuccessCount}/{result.TotalRequested} succeeded");
        _staleDetector.InvalidateCache();
        await LoadTreeAsync();
    }

    // ... CanExecute helpers ...
}
```

### 4.5 `src/SmartCon.FamilyManager/ViewModels/FamilyLeafNodeViewModel.cs` (C8)

**Добавить поле:**
```csharp
[ObservableProperty] private StaleReason _staleReason;
```

### 4.6 `src/SmartCon.FamilyManager/ViewModels/CategoryNodeViewModel.cs` (C8)

**Добавить поля (для roll-up индикации и динамической видимости подменю «Обновить»):**
```csharp
[ObservableProperty] private bool _hasStale;     // true если любое семейство в категории (или подкатегории рекурсивно) stale
[ObservableProperty] private int _staleCount;    // сколько именно stale (для tooltip / индикации)
```

**Логика обновления `HasStale`** — вызывается из `MainViewModel.ApplyStaleResultsToTreeAsync` после `CheckCategoryAsync`:

```csharp
// В MainViewModel partial:
private async Task ApplyStaleResultsToTreeAsync(IReadOnlyList<StaleCheckResult> results, CancellationToken ct)
{
    // 1. Собрать stale catalogItemIds
    var staleIds = results.Where(r => r.IsStale).Select(r => r.CatalogItemId).ToHashSet();

    // 2. Получить маппинг catalogItemId -> [categoryId, ...] (рекурсивно вверх по дереву)
    var catalogItemCategories = _staleAggregator.GetCatalogItemCategoryMap(
        results.Select(r => r.CatalogItemId), TreeNodes);

    // 3. Для каждой категории: HasStale = (есть ли stale в её семействах ИЛИ в любой подкатегории)
    foreach (var node in AllCategoryNodes(TreeNodes))
    {
        var hasStale = catalogItemCategories
            .Where(kvp => kvp.Value.Contains(node.CategoryId))  // семья в этой категории или подкатегории
            .Any(kvp => staleIds.Contains(kvp.Key));
        node.HasStale = hasStale;
        node.StaleCount = catalogItemCategories
            .Count(kvp => kvp.Value.Contains(node.CategoryId) && staleIds.Contains(kvp.Key));
    }

    // 4. Обновить FamilyLeafNodeViewModel.IsStale + StaleReason
    foreach (var result in results)
    {
        var leaf = FindLeafByCatalogId(TreeNodes, result.CatalogItemId);
        if (leaf != null)
        {
            leaf.IsStale = result.IsStale;
            leaf.StaleReason = result.Reason;
        }
    }
}
```

**`IStaleCategoryAggregator` расширяется:**
```csharp
// Добавить в IStaleCategoryAggregator:
IReadOnlyDictionary<string, HashSet<string>> GetCatalogItemCategoryMap(
    IEnumerable<string> catalogItemIds,
    IEnumerable<CategoryNodeViewModel> rootNodes);
```

### 4.7 `src/SmartCon.FamilyManager/Views/FamilyManagerPaneControl.xaml` (C9)

**⚠️ КРИТИЧНО: динамический паттерн видимости сохраняется в точности как для семейства.**

Текущий паттерн в `FamilyLeafNodeContextMenu` (строки 494-524):
- `Load` — `Visible` по умолчанию, `Collapsed` при `IsSelectedFamilyStale=True`
- `Update` (с подменю) — `Collapsed` по умолчанию, `Visible` при `IsSelectedFamilyStale=True`
- Реализация: чистый XAML через `Style.Triggers` + `DataTrigger` + `Setter Property="Visibility"`
- Никакой code-behind (I-10)

**Для категории — тот же паттерн, привязка к `HasStale` (а не `IsSelectedFamilyStale`):**

#### 4.7.1 `CategoryNodeContextMenu` (заменить строки 484-489)

```xml
<ContextMenu x:Key="CategoryNodeContextMenu" Style="{StaticResource ModernContextMenu}"
             DataContext="{Binding PlacementTarget.Tag, RelativeSource={RelativeSource Self}}">

    <!-- Существующий пункт: Импорт в категорию — всегда видим -->
    <MenuItem Header="{loc:Loc FM_ImportToCategory}"
              Command="{Binding ImportFileToCategoryCommand}"
              Style="{StaticResource ModernMenuItem}" />

    <!-- НОВОЕ: Разделитель -->
    <Separator Style="{StaticResource ModernSeparator}" />

    <!-- НОВОЕ: Проверить — всегда видим -->
    <MenuItem Header="{loc:Loc FM_Check}"
              Command="{Binding DataContext.CheckCategoryCommand, RelativeSource={RelativeSource AncestorType=TreeView}}"
              CommandParameter="{Binding}"
              Style="{StaticResource ModernMenuItem}" />

    <!-- НОВОЕ: Обновить — динамический, видим ТОЛЬКО если HasStale=True -->
    <MenuItem Header="{loc:Loc FM_Update}">
        <MenuItem.Style>
            <Style TargetType="MenuItem" BasedOn="{StaticResource ModernMenuItem}">
                <!-- По умолчанию СКРЫТ (как у семейства — Collapsed) -->
                <Setter Property="Visibility" Value="Collapsed"/>
                <Style.Triggers>
                    <!-- Когда HasStale=True — ПОКАЗАТЬ -->
                    <DataTrigger Binding="{Binding HasStale}" Value="True">
                        <Setter Property="Visibility" Value="Visible"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </MenuItem.Style>

        <!-- Подменю с теми же 3 опциями что и у семейства -->
        <MenuItem Header="{loc:Loc FM_UpdateKeepParams}"
                  Command="{Binding DataContext.UpdateCategoryKeepParamsCommand, RelativeSource={RelativeSource AncestorType=TreeView}}"
                  CommandParameter="{Binding}"
                  Style="{StaticResource ModernMenuItem}" />
        <MenuItem Header="{loc:Loc FM_UpdateOverwriteParams}"
                  Command="{Binding DataContext.UpdateCategoryOverwriteParamsCommand, RelativeSource={RelativeSource AncestorType=TreeView}}"
                  CommandParameter="{Binding}"
                  Style="{StaticResource ModernMenuItem}" />
        <Separator Style="{StaticResource ModernSeparator}" />
        <MenuItem Header="{loc:Loc FM_UpdateBatchAllStale}"
                  Command="{Binding DataContext.UpdateCategoryBatchAllStaleCommand, RelativeSource={RelativeSource AncestorType=TreeView}}"
                  CommandParameter="{Binding}"
                  Style="{StaticResource ModernMenuItem}" />
    </MenuItem>
</ContextMenu>
```

**Ключевые моменты:**

1. **`HasStale` биндится напрямую** из `CategoryNodeViewModel`, не из `MainViewModel.IsSelectedFamilyStale` — это свойство **самой категории**, не выделения
2. **Visibility по умолчанию = Collapsed** (как у семейства)
3. **DataTrigger + HasStale=True → Visible** (как у семейства, но с `HasStale` вместо `IsSelectedFamilyStale`)
4. **Подменю с теми же пунктами**, что и у семейства (KeepParams / OverwriteParams), плюс **BatchAllStale** (новое)
5. **Отдельная команда `UpdateCategoryBatchAllStaleCommand`** — аналог `LoadToProjectCommand` для batch режима

#### 4.7.2 `FamilyLeafNodeContextMenu` — добавить «Проверить» + сохранить существующую динамику

Текущая динамика (строки 491-545) **остаётся как есть**:
- `Load` скрывается при `IsSelectedFamilyStale=True` ✓
- `Update` показывается при `IsSelectedFamilyStale=True` ✓

**Изменения в `FamilyLeafNodeContextMenu` — только добавить «Проверить» (в начало меню):**

```xml
<ContextMenu x:Key="FamilyLeafNodeContextMenu" Style="{StaticResource ModernContextMenu}"
             Focusable="False"
             DataContext="{Binding PlacementTarget.Tag, RelativeSource={RelativeSource Self}}">

    <!-- НОВОЕ: Проверить — всегда видим, до Load/Update -->
    <MenuItem Header="{loc:Loc FM_Check}"
              Command="{Binding DataContext.CheckFamilyCommand, RelativeSource={RelativeSource AncestorType=TreeView}}"
              CommandParameter="{Binding}"
              Style="{StaticResource ModernMenuItem}" />

    <!-- Существующая динамика СОХРАНЯЕТСЯ (Load ↔ Update переключение) -->
    <Separator Style="{StaticResource ModernSeparator}" />
    <MenuItem Header="{loc:Loc FM_LoadToProject}" ...>
        <MenuItem.Style>
            <Style TargetType="MenuItem" BasedOn="{StaticResource ModernMenuItem}">
                <Setter Property="Visibility" Value="Visible"/>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding IsStale}" Value="True">
                        <Setter Property="Visibility" Value="Collapsed"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </MenuItem.Style>
    </MenuItem>
    <MenuItem Header="{loc:Loc FM_Update}">
        <MenuItem.Style>
            <Style TargetType="MenuItem" BasedOn="{StaticResource ModernMenuItem}">
                <Setter Property="Visibility" Value="Collapsed"/>
                <Style.Triggers>
                    <DataTrigger Binding="{Binding IsStale}" Value="True">
                        <Setter Property="Visibility" Value="Visible"/>
                    </DataTrigger>
                </Style.Triggers>
            </Style>
        </MenuItem.Style>
        <MenuItem Header="{loc:Loc FM_UpdateKeepParams}" .../>
        <MenuItem Header="{loc:Loc FM_UpdateOverwriteParams}" .../>
    </MenuItem>

    <!-- Существующие Edit/Properties/Delete — БЕЗ ИЗМЕНЕНИЙ -->
</ContextMenu>
```

**⚠️ ВАЖНОЕ ИЗМЕНЕНИЕ в `FamilyLeafNodeContextMenu`:**

В Phase 24 заменяем биндинг с `IsSelectedFamilyStale` (свойство MainViewModel) на `IsStale` (свойство самого `FamilyLeafNodeViewModel`). Преимущества:
- `IsStale` обновляется через `OnPropertyChanged` при изменении — UI реагирует мгновенно
- Не зависит от `SelectedTreeNode` (что было ограничением — динамика работала только для выбранного семейства)
- `MainViewModel.IsSelectedFamilyStale` можно **удалить** как устаревшее

#### 4.7.3 Сводный паттерн динамической видимости

**Применяется к обоим контекстным меню (категория + семейство):**

| Состояние | `Load`/`Загрузить` | `Update` (с подменю) |
|---|---|---|
| Не stale (default) | **Visible** | **Collapsed** |
| Stale | **Collapsed** | **Visible** |

**Реализация — идентична для категории и семейства:**

```xml
<!-- ПАТТЕРН A: Load — Visible по умолчанию, Collapsed при stale -->
<MenuItem Header="..." Command="...">
    <MenuItem.Style>
        <Style TargetType="MenuItem" BasedOn="{StaticResource ModernMenuItem}">
            <Setter Property="Visibility" Value="Visible"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding [PROPERTY]}" Value="True">
                    <Setter Property="Visibility" Value="Collapsed"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </MenuItem.Style>
</MenuItem>

<!-- ПАТТЕРН B: Update — Collapsed по умолчанию, Visible при stale -->
<MenuItem Header="...">
    <MenuItem.Style>
        <Style TargetType="MenuItem" BasedOn="{StaticResource ModernMenuItem}">
            <Setter Property="Visibility" Value="Collapsed"/>
            <Style.Triggers>
                <DataTrigger Binding="{Binding [PROPERTY]}" Value="True">
                    <Setter Property="Visibility" Value="Visible"/>
                </DataTrigger>
            </Style.Triggers>
        </Style>
    </MenuItem.Style>
    <!-- подменю -->
</MenuItem>
```

**Где `[PROPERTY]`:**
- **Семейство:** `IsStale` (из `FamilyLeafNodeViewModel.IsStale` — обновляется через `OnPropertyChanged`)
- **Категория:** `HasStale` (из `CategoryNodeViewModel.HasStale` — roll-up от детей, обновляется в `ApplyStaleResultsToTreeAsync`)

**Чистый XAML, ноль code-behind (I-10).** Никаких обработчиков в `.xaml.cs` для динамики.

#### 4.7.4 Иконка stale на категории (в HierarchicalDataTemplate CategoryNodeViewModel)

Заменить существующий шаблон категории (строки 589-608), чтобы индикатор был рядом с `FamilyCount`:

```xml
<!-- Категория с индикатором stale -->
<StackPanel Orientation="Horizontal">
    <TextBlock Text="{Binding DisplayName}"
               Foreground="{DynamicResource TextPrimaryBrush}"
               VerticalAlignment="Center" />
    <TextBlock Text=" ⚠"
               Foreground="{DynamicResource WarningBrush}"
               FontSize="11"
               VerticalAlignment="Center"
               Margin="4,0,0,0"
               Visibility="{Binding HasStale, Converter={StaticResource BoolToVis}}"
               ToolTip="{Binding StaleCount, Converter={StaticResource StaleCountToTooltipConverter}}"/>
    <TextBlock Text="{Binding FamilyCount, StringFormat=' ({0})'}"
               Foreground="{DynamicResource TextMutedBrush}"
               FontSize="10"
               VerticalAlignment="Center"
               Margin="2,0,0,0" />
</StackPanel>
```

#### 4.7.5 Иконка stale на leaf (в HierarchicalDataTemplate FamilyLeafNodeViewModel)

Заменить строки 624-629:

```xml
<TextBlock Text=" [Устарело]"
           Foreground="{DynamicResource WarningBrush}"
           FontSize="10"
           VerticalAlignment="Center"
           Margin="4,0,0,0"
           Visibility="{Binding IsStale, Converter={StaticResource BoolToVis}}"
           ToolTip="{Binding StaleReason, Converter={StaticResource StaleReasonToTooltipConverter}}"/>
```

#### 4.7.6 Удалить dead code

Удалить объявление `<converters:BoolToStaleForegroundConverter x:Key="StaleForegroundConverter"/>` в строке 21 XAML (не используется).
Удалить файл `src/SmartCon.UI/Converters/BoolToStaleForegroundConverter.cs`.

Удалить поле `IsSelectedFamilyStale` из `FamilyManagerMainViewModel.cs:71` и логику его обновления (MainVM.cs:366, 388, 396, 403).

### 4.8 `src/SmartCon.UI/Converters/StaleReasonToTooltipConverter.cs` (C9, **новый**)

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SmartCon.Core.Models.FamilyManager;
using SmartCon.UI;

namespace SmartCon.UI.Converters;

public sealed class StaleReasonToTooltipConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not StaleReason reason) return null;

        var key = reason switch
        {
            StaleReason.NoEntityStorage => StringLocalization.Keys.FM_StaleTooltipNoES,
            StaleReason.VersionMismatch => StringLocalization.Keys.FM_StaleTooltipMismatch,
            StaleReason.RevitVersionMismatch => StringLocalization.Keys.FM_StaleTooltipRevitVer,
            _ => StringLocalization.Keys.FM_StaleTooltipNone,
        };
        return LanguageManager.GetString(key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

### 4.9 `src/SmartCon.UI/Converters/BoolToStaleForegroundConverter.cs` (C9)

**УДАЛИТЬ** — dead code, нигде не используется.

### 4.9b `src/SmartCon.UI/Converters/StaleCountToTooltipConverter.cs` (C9, **новый**)

Для tooltip'а на иконке `⚠` категории — показывает сколько именно stale семейств:

```csharp
using System;
using System.Globalization;
using System.Windows.Data;
using SmartCon.UI;

namespace SmartCon.UI.Converters;

public sealed class StaleCountToTooltipConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not int count) return null;
        if (count <= 0) return null;
        return count == 1
            ? "1 устаревшее семейство"
            : $"{count} устаревших семейств";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

> Примечание: строки tooltip'а должны быть в `StringLocalization.Keys` для i18n. Этот код-стаб для примера — в финальной реализации использовать `LanguageManager.GetString()`.

### 4.10 `src/SmartCon.FamilyManager/Services/LocalCatalog/FamilyCatalogSql.cs` (C10)

**Добавить:**
```csharp
public const string MigrateV12DropProjectUsage = """
    DROP TABLE IF EXISTS project_usage;
    """;

public const string DropProjectUsageIndex = """
    DROP INDEX IF EXISTS ix_project_usage_lookup;
    """;
```

### 4.11 `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogMigrator.cs` (C10)

**Добавить метод:**
```csharp
private static async Task MigrateV12Async(SqliteConnection connection, CancellationToken ct)
{
    var currentVersion = await GetSchemaVersionAsync(connection, ct);
    if (currentVersion >= 12) return;

    // 1. Drop index first
    using (var dropIdxCmd = connection.CreateCommand())
    {
        dropIdxCmd.CommandText = FamilyCatalogSql.DropProjectUsageIndex;
        await dropIdxCmd.ExecuteNonQueryAsync(ct);
    }

    // 2. Drop table
    using (var dropCmd = connection.CreateCommand())
    {
        dropCmd.CommandText = FamilyCatalogSql.MigrateV12DropProjectUsage;
        await dropCmd.ExecuteNonQueryAsync(ct);
    }

    // 3. Bump version
    using (var versionCmd = connection.CreateCommand())
    {
        versionCmd.CommandText = "UPDATE schema_info SET value = '12' WHERE key = 'schema_version'";
        await versionCmd.ExecuteNonQueryAsync(ct);
    }
}
```

**Добавить вызов в `MigrateAsync` (после `MigrateV11Async`):**
```csharp
await MigrateV12Async(connection, ct);
```

### 4.12 `src/SmartCon.FamilyManager/Services/FamilyManagerServices.cs` (C11)

**Удалить поле:**
```csharp
IProjectFamilyUsageRepository UsageRepository,
```

**Добавить поля:**
```csharp
IFamilyVersionStore VersionStore,
IStaleDetector StaleDetector,
IStaleFamilyUpdater StaleUpdater,
IStaleCategoryAggregator StaleCategoryAggregator,
IClock Clock,
```

### 4.13 `src/SmartCon.App/DI/ServiceRegistrar.cs` (C11)

**Удалить:**
```csharp
services.AddSingleton<IProjectFamilyUsageRepository>(...);
```

**Добавить:**
```csharp
services.AddSingleton<IFamilyVersionStore, RevitFamilyVersionStore>();
services.AddSingleton<IStaleDetector, StaleDetector>();
services.AddSingleton<IStaleFamilyUpdater, StaleFamilyUpdater>();
services.AddSingleton<IStaleCategoryAggregator, StaleCategoryAggregator>();
```

### 4.14 `src/SmartCon.UI/Localization/StringLocalization.cs` (C14)

**Добавить ключи в `Keys`:**
```csharp
public const string FM_Check = "FM_Check";
public const string FM_Update = "FM_Update";
public const string FM_UpdateKeepParams = "FM_UpdateKeepParams";
public const string FM_UpdateOverwriteParams = "FM_UpdateOverwriteParams";
public const string FM_UpdateBatchAllStale = "FM_UpdateBatchAllStale";
public const string FM_Stale = "FM_Stale";
public const string FM_StaleTooltipNone = "FM_StaleTooltipNone";
public const string FM_StaleTooltipNoES = "FM_StaleTooltipNoES";
public const string FM_StaleTooltipMismatch = "FM_StaleTooltipMismatch";
public const string FM_StaleTooltipRevitVer = "FM_StaleTooltipRevitVer";
public const string FM_StaleCheckInProgress = "FM_StaleCheckInProgress";
```

**Добавить переводы ru + en** в соответствующие словари.

### 4.15 `docs/domain/models/family-manager.md` (C15)

Добавить секции для `FamilyVersion`, `StaleCheckResult`, `StaleReason`, `StaleUpdateRequest`, `StaleBatchUpdateResult`, `StaleBatchUpdateProgress`, `FamilyStaleSnapshot`.

### 4.16 `docs/domain/interfaces/family-manager.md` (C15)

Добавить секции для `IFamilyVersionStore`, `IStaleDetector`, `IStaleFamilyUpdater`, `IStaleCategoryAggregator`.

### 4.17 `docs/domain/glossary.md` (C15)

Добавить термины: `Stale`, `FamilyVersion`, `StaleReason`, `ES Schema` (если ещё нет).

---

## 5. Удаляемые файлы (C11)

| Файл | Причина |
|---|---|
| `src/SmartCon.FamilyManager/Services/LocalCatalog/LocalProjectFamilyUsageRepository.cs` | V12 migration drops `project_usage` table |
| `src/SmartCon.Core/Models/FamilyManager/ProjectFamilyUsage.cs` | Model для repository — больше не нужен |
| `src/SmartCon.Core/Services/Interfaces/IProjectFamilyUsageRepository.cs` | Interface — больше не нужен |
| `src/SmartCon.UI/Converters/BoolToStaleForegroundConverter.cs` | Dead code (объявлен в XAML, не используется) |

---

## 6. Тесты

### 6.1 Unit-тесты (Core, без Revit)

**`src/SmartCon.Tests/FamilyManager/Stale/FamilyVersionTests.cs`** (C2)
- `Empty` constant проверки
- Equality двух record
- `CurrentSchemaVersion == 1`

**`src/SmartCon.Tests/FamilyManager/Stale/StaleCheckResultTests.cs`** (C2)
- Equality
- Все 5 StaleReason enum values

**`src/SmartCon.Tests/FamilyManager/Stale/StaleCategoryAggregatorTests.cs`** (C6)
- Пустой список → пустой dict
- Все fresh → все категории false
- 1 stale в категории → true для этой категории
- 1 stale в подкатегории → true для родителя (если recursive=true)
- Multiple stale в одной категории → один entry (deduped)

**`src/SmartCon.Tests/FamilyManager/Stale/FamilyStaleSnapshotTests.cs`** (C2)
- Empty constant
- Create with results
- CachedAt истек через X секунд — API не предоставляет, проверка что CheckedAtUtc корректный

### 6.2 ViewModel-тесты

**`src/SmartCon.Tests/FamilyManager/ViewModels/CheckCategoryCommandTests.cs`** (C8)
- Команда вызывается с категорией → `_staleDetector.CheckCategoryAsync` вызван с правильными параметрами
- Roll-up: `ApplyStaleResultsToTreeAsync` обновляет `HasStale` на категории и подкатегориях
- IsStaleCheckInProgress true во время, false после
- Logger.Info вызван с правильным счётчиком

**`src/SmartCon.Tests/FamilyManager/ViewModels/UpdateCategoryStaleTests.cs`** (C8)
- Snapshot null → no-op
- StaleIds пустой → no-op
- Multiple stale → batch update вызван с правильным запросом
- Прогресс репортится через `IProgress<>`
- InvalidateCache + LoadTreeAsync вызваны после

### 6.3 Test Doubles (без Revit)

**`src/SmartCon.Tests/TestDoubles/InMemoryStaleDetector.cs`** (C7)
```csharp
internal sealed class InMemoryStaleDetector : IStaleDetector
{
    public Dictionary<string, StaleCheckResult> FakeResults { get; } = new();
    public FamilyStaleSnapshot? CachedSnapshot { get; set; }

    public Task<StaleCheckResult> CheckFamilyAsync(...) => Task.FromResult(
        FakeResults.GetValueOrDefault(catalogItemId) ?? new StaleCheckResult(
            catalogItemId, familyName, "", null, false, StaleReason.NotInCatalog));

    public Task<IReadOnlyList<StaleCheckResult>> CheckCategoryAsync(...) => Task.FromResult(
        (IReadOnlyList<StaleCheckResult>)FakeResults.Values.ToList());

    public FamilyStaleSnapshot? GetCachedSnapshot() => CachedSnapshot;
    public void InvalidateCache() => CachedSnapshot = null;
}
```

**`src/SmartCon.Tests/TestDoubles/InMemoryStaleFamilyUpdater.cs`** (C7)
- Mock — возвращает pre-configured результаты
- Проверяет что batch запрос корректный

**`src/SmartCon.Tests/TestDoubles/InMemoryFamilyVersionStore.cs`** (C5) — если нужно для VM-тестов

### 6.4 Integration-тесты (manual)

В Revit нельзя автоматизировать. 7 бизнес-кейсов (Issue #69 AC):

1. ✅ Категория из 30 семейств → "Проверить" < 500 мс
2. ✅ Семейство без ES → помечается stale
3. ✅ System family в `.rvt`-контейнере → **out of scope** (но кейс есть, для проверки что не сломали)
4. ✅ Семейство не в каталоге FM → пропускается (Reason = NotInCatalog)
5. ✅ Пакетное обновление категории (12 семейств)
6. ✅ Обновление с перезаписью параметров
7. ✅ Обновление без перезаписи параметров
8. ✅ Refresh кнопка ↻ НЕ делает stale-проверку (только каталог)

---

## 7. Инварианты (i-01..i-17) — проверка

| ID | Правило | Как соблюдаем |
|---|---|---|
| I-01 | Revit API однопоточен | `IFamilyManagerAwaitableEvent.RaiseAsync` для всех `doc.GetElement`, `Family.GetEntity` |
| I-03 | Транзакции через `ITransactionService` | `RevitFamilyVersionStore.WriteToLoadedFamily` использует `_tx.RunInTransaction` |
| I-03b | Family document — `new Transaction(familyDoc, ...)` | **Не используется в Phase 24** (маркер пишется только в проектный документ; см. ADR-030 §2) |
| I-05 | Не хранить `Element`/`Connector` между транзакциями | Только `ElementId` в Core (`LoadPlace.cs` хранит `ElementId` в ES, не сам `Family`) |
| I-07 | IFailuresPreprocessor | Через существующий `ITransactionService` (уже подключён) |
| I-09 | Core без Revit API вызовов | Все 4 интерфейса и модели — pure C#. `Document`/`ElementId` — как opaque parameters |
| I-10 | MVVM без code-behind | Все новые команды в MainViewModel partial, XAML — только bindings |
| I-11 | ElementIdCompat | Используем существующий `ElementIdCompat` для multi-version |
| I-12 | DataGridColumn.Header | Не применимо (TreeView, не DataGrid) |
| I-13 | Маппинг фитингов в ES активного проекта | Не нарушаем (наш ES — на `Family` element, не DataStorage) |
| I-14 | SQLite Thread Safety | Через `LocalCatalogDatabase` (I-14 — V12 migration) |
| I-15 | Dockable Panel Lifecycle | Не затрагиваем |
| I-16 | Managed Storage Immutability | Не затрагиваем (пишем в .rfa из managed storage через `IFamilyFileResolver`) |
| I-17 | Поиск — Exa/REF разделение | Все best practices проверены через Exa (см. ADR-030 §Источники) |

---

## 8. Логирование (per skill `smartcon-logging`)

**Scope:** `"StaleDetection"` (категория).

**Properties в `BeginScope`:**
- `Method` — `nameof(...)`
- `CategoryId` (для CheckCategoryAsync)
- `CatalogItemId` (для single operations)
- `FileName` = `Path.GetFileName(rfaFilePath)` — **L8 правило**, не full path

**Примеры:**
```csharp
using var _scope = SmartConLogger.BeginScope(
    "StaleDetection",
    ("Method", nameof(CheckCategoryAsync)),
    ("CategoryId", categoryId));

SmartConLogger.Info($"Check started: {familyIds.Count} families");

if (staleCount > 0)
{
    SmartConLogger.Warn(
        $"Found {staleCount} stale families out of {results.Count}. [Action: пользователь может обновить через ПКМ → Обновить]");
}
```

**L9 правило:** `Warn` всегда заканчивается `[Action: ...]`.

**C15 правило:** НЕ оборачивать `BeginScope` вокруг `RunInTransaction` (см. ADR-026).

**HotLoopCounter:** для batch-чтения 30+ семейств (sampleEvery=32).

---

## 9. Performance budget

**Target:** < 500 мс на категорию из 30 семейств (Issue #69 AC).

**Breakdown (budget):**
| Operation | Time | Notes |
|---|---|---|
| SQLite: `GetFamilyIdsByCategoryAsync` | < 30 мс | уже работает с индексом |
| `FilteredElementCollector.OfClass(Family)` | < 50 мс | O(Family count) |
| Batch `ReadManyFromDocument` (30 ES reads) | < 100 мс | in-memory, без I/O |
| Compute StaleReason (loop) | < 10 мс | pure logic |
| `ApplyStaleResultsToTreeAsync` (UI update) | < 50 мс | partial VM updates |
| **Total** | **< 240 мс** | < 500 мс target ✓ |

**Optimization:**
- НЕ открывать `.rfa` файлы в batch (это ~500 мс на каждый, ~15 сек на 30) — только in-memory ES reads
- HotLoopCounter + sampleEvery=32 для логирования в batch
- Параллелизм НЕ нужен (in-memory быстрее overhead)

---

## 10. Сборка и тестирование (per skill `smartcon-build-guide`)

### 10.1 Промежуточная сборка (для каждого C-коммита)

```bash
# Восстановить зависимости с правильной конфигурацией
dotnet restore src/SmartCon.App/SmartCon.App.csproj -p:Configuration=Debug.R25

# Собрать R25
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25

# Собрать R24 (для legacy)
dotnet restore src/SmartCon.App/SmartCon.App.csproj -p:Configuration=Debug.R24
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24
```

**⚠️ КРИТИЧНО:** При смене `-c` (R25↔R24) ВСЕГДА делать restore с конфигурацией (см. smartcon-build-guide SKILL.md §CRITICAL Rule #3).

### 10.2 Тесты

```bash
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25
```

### 10.3 Финальная сборка + деплой (ТОЛЬКО после manual test!)

```bash
build-and-deploy.bat
```

⚠️ **WARNING:** копирует файлы в Revit addins. **Только после manual test в Revit.**

### 10.4 Валидация документации

```bash
powershell -ExecutionPolicy Bypass -File tools/validate-docs.ps1
```

Должен проходить PASSED после C15.

---

## 11. Pre-release workflow (C16, C17)

После C15 (feature complete):
1. Обновить `Version.txt` → `2.0.0-beta.1`
2. Обновить `Directory.Build.props` (AssemblyVersion, FileVersion, VersionPrefix)
3. Создать git tag: `git tag -a 2.0.0-beta.1 -m "Phase 24: Stale Detection v2 (pre-release)"`
4. Push: `git push origin 2.0.0-beta.1`
5. GitHub Actions автоматически создаст pre-release (ADR-021)

После manual test в Revit (все 7 бизнес-кейсов):
1. Обновить `Version.txt` → `2.0.0`
2. Удалить `-beta.1` suffix
3. Tag: `git tag -a 2.0.0 -m "Phase 24: Stale Detection v2 (release)"`
4. CHANGELOG.md обновить с записью "Breaking change 2.0.0"

---

## 12. Чеклист готовности (Definition of Done)

### Функциональные критерии

- [ ] UC-01..UC-07 проходят ручной тест в Revit (Issue #69)
- [ ] Категория из 30 семейств → "Проверить" < 500 мс
- [ ] Семейство без ES помечается stale
- [ ] Семейство не в каталоге FM — пропускается
- [ ] Refresh кнопка ↻ НЕ делает stale-проверку
- [ ] Кеш работает: повторный "Проверить" без изменений = мгновенно
- [ ] Инвалидация кеша при Load/Update/Edit/смена БД

### Технические критерии

- [ ] Build R19/R21/R24/R25 — 0 errors / 0 warnings
- [ ] Все существующие тесты зелёные (`dotnet test`)
- [ ] Новые unit-тесты: StaleCategoryAggregatorTests, FamilyStaleSnapshotTests, CheckCategoryCommandTests, UpdateCategoryStaleTests
- [ ] Инварианты I-01..I-17 не нарушены
- [ ] ES Schema GUID зафиксирован, не меняется
- [ ] V12 миграция: drop table `project_usage`, drop index `ix_project_usage_lookup`
- [ ] `tools/validate-docs.ps1` PASSED
- [ ] `MainViewModel.IsSelectedFamilyStale` **удалён** (заменён на `leaf.IsStale`)
- [ ] `BoolToStaleForegroundConverter` **удалён** (dead code)

### Документация

- [x] ADR-030 создан
- [ ] `docs/family-manager/README.md` §Жёсткий запрет — обновлён (исключение для FamilyVersion.v1) ✅ **сделано**
- [ ] `docs/adr/014-familymanager-mvp-architecture.md` — нумерация FM-001 → FM-007 ✅ **сделано**
- [ ] `docs/domain/models/family-manager.md` — обновлён (7 новых моделей)
- [ ] `docs/domain/interfaces/family-manager.md` — обновлён (4 новых интерфейса, **+ расширение `IStaleCategoryAggregator.GetCatalogItemCategoryMap`**)
- [ ] `docs/domain/glossary.md` — добавлены термины
- [ ] Локализация: `ru` + `en` для 11 новых строк (+ `FM_StaleCategoryTooltip`)
- [ ] CHANGELOG.md обновлён
- [ ] Pre-release tag `2.0.0-beta.1`

### UX-инварианты (КРИТИЧНО)

- [ ] **Динамическая видимость "Обновить" работает одинаково для категории И семейства** (паттерн DataTrigger + Collapsed/Visible)
- [ ] Семейство: `Load` ↔ `Update` переключаются через `DataTrigger` на `IsStale`
- [ ] Категория: `Update ▸` (с подменю) **скрыто** по умолчанию, **видимо** при `HasStale=True`
- [ ] Roll-up `⚠` индикатор на категории появляется **только** если `HasStale=True`
- [ ] Иконка `⚠` исчезает после успешного Update (cache invalidation → ApplyStaleResultsToTreeAsync)

### Процессные критерии

- [ ] Пользователь **лично протестировал** в Revit (7 бизнес-кейсов)
- [ ] НЕ коммитить без подтверждения пользователя
- [ ] НЕ создавать PR без явного запроса

---

## 13. Связанные документы

- [ADR-030](../../adr/030-phase-24-stale-detection-v2.md) — основное решение Phase 24
- [Issue #69](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/69) — оригинальная постановка задачи
- [ADR-014](014-familymanager-mvp-architecture.md) — содержит override запрет §FM-007
- [ADR-015](015-familymanager-published-storage.md) — managed storage
- [ADR-018](018-familymanager-refactoring.md) — DI patterns, IClock
- [ADR-022](022-familymanager-rbac.md) — RBAC
- [ADR-025](025-refactoring-migration-backlog.md) — IClock миграция
- [ADR-026](026-logging-migration.md) — BeginScope, OpId
- [ADR-027](027-placed-families-v2.md) — `OfClass(FamilyInstance)` pattern
- [ADR-028](028-di-readiness.md) — DI patterns
- [ADR-029](029-shared-nested-load-dialog.md) — UI dialog precedent
- [docs/invariants.md](../../invariants.md) — I-01..I-17
- [.agents/skills/revit-api-best-practice](../../../.agents/skills/revit-api-best-practice/SKILL.md) — async/ExternalEvent
- [.agents/skills/smartcon-logging](../../../.agents/skills/smartcon-logging/SKILL.md) — BeginScope, L8/L9
- [.agents/skills/smartcon-build-guide](../../../.agents/skills/smartcon-build-guide/SKILL.md) — multi-version build
- [.agents/skills/smartcon-domain-docs](../../../.agents/skills/smartcon-domain-docs/SKILL.md) — docs validator
