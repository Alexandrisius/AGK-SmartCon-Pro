---
module: family-manager-loadable-interfaces
---
# Loadable Family Import интерфейсы (Phase 22)

> Загружать: при работе с импортом loadable families из активного проекта.
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## ILoadableFamilyScanner

Возвращает уникальные loadable families (с фильтром `!IsInPlace`).
Реализация — `FilteredElementCollector.OfClass(Family)` (O(F), не O(N))
без `EditFamily + SaveAs` (это делает `LoadableFamilyImportOrchestrator` после
подтверждения пользователя).

**Файл:** `ILoadableFamilyScanner.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/LoadableFamilyScanner.cs`

```csharp
public interface ILoadableFamilyScanner
{
    IReadOnlyList<LoadableFamilyInfo> GetUniqueFamilies(Document activeDoc);
}
```

---

## ILoadableFamilyTypeResolver

Открывает `.rfa` файл, читает `FamilyManager.Types` и `FamilySymbol`s,
создаёт `FamilyTypeDescriptor` (с UniqueId) для каждого типа.
Используется после `ImportBatchAsync` для сохранения типов в `IFamilyTypeRepository`.

**Файл:** `ILoadableFamilyTypeResolver.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/LoadableFamilyTypeResolver.cs`

```csharp
public interface ILoadableFamilyTypeResolver
{
    IReadOnlyList<FamilyTypeDescriptor> ResolveTypesFromRfa(
        string rfaFilePath,
        string catalogItemId,
        string? versionId = null,
        string? fileId = null);
}
```

---

## ILoadableFamilyImportOrchestrator

Каталог-side орчестрация для loadable: batch import (managed storage) +
resolve managed путь + список extraction tasks для `IFamilyDataExtractionService`.

**Файл:** `ILoadableFamilyImportOrchestrator.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LoadableFamilyImportOrchestrator.cs`

```csharp
public interface ILoadableFamilyImportOrchestrator
{
    Task<LoadableFamilyImportResult> ImportAndPersistTypesAsync(
        IReadOnlyList<FamilyBatchImportItem> items,
        int targetRevitVersion,
        CancellationToken ct = default);
}
```

---

## IContentHashAnalyticsRepository

Чтение контент-хэш аналитики версий каталога (#249, Phase 4): секционные хэши/строки (`section_hashes`/`section_strings`) и per-type хэши (`family_type_hashes`) по (catalogItemId, versionLabel). `null` = аналитика ещё не вычислена (pending backfill), пустой список = легитимный ответ для бестипового семейства. Питает diff-окно batch-диалога и DB-side per-type stale карту `StaleDetector` (VersionMismatch) — без открытия файлов семейств.

**Файл:** `IContentHashAnalyticsRepository.cs`
**Реализация:** `SmartCon.FamilyManager/Services/LocalCatalog/LocalContentHashAnalyticsRepository.cs`

```csharp
public interface IContentHashAnalyticsRepository
{
    Task<IReadOnlyDictionary<string, string>?> GetSectionHashesAsync(string catalogItemId, string versionLabel, CancellationToken ct);
    Task<IReadOnlyDictionary<string, string>?> GetSectionStringsAsync(string catalogItemId, string versionLabel, CancellationToken ct);
    Task<IReadOnlyList<FamilyTypeHashEntry>?> GetTypeHashesAsync(string catalogItemId, string versionLabel, CancellationToken ct);
}
```
