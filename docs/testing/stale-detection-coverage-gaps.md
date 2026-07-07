# Stale Detection — Test Coverage Gaps

> **Дата:** 2026-06-19
> **Модуль:** `SmartCon.FamilyManager/Services/Stale/` + `SmartCon.Core/Models/FamilyManager/Stale*`
> **Версия:** Phase 24 + 4 раунда bugfix-аудита (2026-06-09..19)

## TL;DR

| Категория | Покрыто unit-тестами | Покрыто в production logs | Требует ручного тестирования в Revit |
|---|:---:|:---:|:---:|
| `StaleSnapshotLogic` (Merge/Remove/Filter) | ✅ 100% | ✅ | — |
| `StaleCategoryAggregator` (Aggregate/BuildMap) | ✅ 100% | ✅ | — |
| `StaleDetector` constructor + cache | ✅ 100% | ✅ | — |
| `StaleDetector.CheckFamilyAsync` (контракт) | ✅ ~80% | ✅ | ✅ |
| `StaleDetector.CheckCategoryAsync` | ❌ **НЕ unit** | ✅ | ✅ |
| `StaleFamilyUpdater` (всё) | ✅ ~90% | ✅ | ✅ |
| `LocalCatalogQueryBuilder` (все Stale-комбинации) | ✅ 100% | ✅ | — |
| `FamilyStaleSnapshot` / `StaleCheckResult` / etc. | ✅ 100% | — | — |
| `IFamilyVersionStore` (read/write) | ❌ Revit ES | ✅ | ✅ |
| `IFamilyVersionWriter.WriteVersionMarkerAsync` | ❌ Revit ES | ✅ | ✅ |
| `IFamilyFinder.FindByName` | ❌ Revit FilteredElementCollector | ✅ | ✅ |
| `IFamilyLoadService.LoadFamilyAsync` | ❌ Revit transaction | ✅ | ✅ |
| `CategoryTreeAdapter` | ❌ WPF VM (sealed) | ✅ | ✅ |

## Что покрыто unit-тестами

### `StaleDetectorTests.cs` (25 тестов)

| Метод | Кейсы |
|---|---|
| Constructor | 6 (один happy path + пять ArgumentNullException для каждого параметра) |
| `GetCachedSnapshot` | 2 (до check → null, после check → snapshot) |
| `InvalidateCache` | 1 (после check → null) |
| `MarkUpdated` | 5 (null/empty no-op, known id removed, unknown id leaves unchanged, на empty cache no-op) |
| `GetMergedSnapshot` | 3 (null cache → null, preserves other entries, overwrite same id) |
| `CheckFamilyAsync` | 8 (null doc/familyId, NotInCatalog, NoEntityStorage, VersionMismatch, RevitVersionMismatch, ES marker corrupted, AllMatch, ResolveTargetRevit throws) |

### `StaleFamilyUpdaterTests.cs` (15 тестов)

| Метод | Кейсы |
|---|---|
| Constructor | 9 (happy path + 8 параметров через `[Theory]`) |
| `UpdateFamilyAsync` | 6 (success, load failure, empty path, marker write throws, cancellation, Revit version throws) |
| `UpdateBatchAsync` | 6 (null request, empty ids, all success, partial failure, cancellation, progress, overwrite propagated) |

### `LocalCatalogQueryBuilderTests.cs` (расширен +7 тестов)

| Сценарий | Кейсы |
|---|---|
| Stale-комбинации | 7 (`IncludeUncategorized` only, `CategoryIdsFilter` only, обе, `+CategoryFilter`, `+recursive`, все 3, empty filter) |

### `StaleSnapshotAdditionalTests.cs` (18 тестов)

POCO equality: `FamilyStaleSnapshot` (6), `StaleCheckResult` (2), `StaleUpdateRequest` (2), `StaleBatchUpdateResult` (3), `StaleBatchUpdateProgress` (1), `CategoryStaleStats` (2), `StaleReason` enum (1).

## Что НЕ покрыто и почему

### 1. `StaleDetector.CheckCategoryAsync` — inline `FilteredElementCollector`

**Причина:** внутри метода есть вызов:

```csharp
using var collector = new FilteredElementCollector(doc).OfClass(typeof(Autodesk.Revit.DB.Family));
foreach (Autodesk.Revit.DB.Family f in collector) { ... }
```

Это `sealed` класс из `Autodesk.Revit.DB` (RevitAPI 2024/2025 native assembly). `Moq` не может замокать sealed native class. Inline-вызов (не через интерфейс) означает, что даже при наличии `Mock<Document>` мы получим `NotImplementedException` на `new FilteredElementCollector(doc)`.

**Покрытие:** только через ручное тестирование в Revit + integration-логи (production logs). Все наблюдаемые сценарии (`__no_category__` mixed mode, 5 категорий с 10 семейств, ES corruption detection) подтверждены в `smartcon - 2025.log` от 2026-06-19.

**Возможные workaround-ы (НЕ реализованы):**
- Вынести `FilteredElementCollector` в `IFamilyFinder.GetAllFamiliesAsync()` — потребует рефакторинга
- Создать integration test suite, использующий `RevitTestFramework` или `xUnit + revit-addin-host` — за рамками текущей задачи

### 2. `IFamilyVersionStore` (Read/Write/ReadMany) — Revit ExtensibleStorage

**Причина:** все методы работают с `Schema` (Guid `36f59d37-6aac-4a05-9543-32f14671b1d2`) и `Entity`/`Field` — это `Autodesk.Revit.DB.ExtensibleStorage`. Schema регистрируется **только** в запущенном Revit (видно в логах: `FamilyVersionSchema.GetOrCreate: schema SmartCon_FamilyVersion_v1 not found, creating new one`).

**Покрытие:** только через ручное тестирование. В production logs подтверждено:
- Schema creation в Revit 2023 (net48) и Revit 2025 (net8) с одинаковым GUID ✅
- Read/Write выполняется (snapshot `merged 3 results (3 stale)` после batch read)

### 3. `IFamilyVersionWriter.WriteVersionMarkerAsync` — Revit ES + Family lookup

**Причина:** использует `IFamilyFinder.FindByName` (Revit `FilteredElementCollector`) + `IFamilyVersionStore.WriteToLoadedFamily` (Revit ES).

**Покрытие:** только через ручное тестирование. В production logs подтверждено:
- `MarkUpdated: removed 1 entries` после успешной LoadFamily + WriteVersionMarker (Revit 2025) ✅

### 4. `IFamilyFinder.FindByName` — Revit `FilteredElementCollector`

**Причина:** см. п.1 — sealed native class.

**Покрытие:** только через ручное тестирование. Подтверждено в production:
- `Family 'BP_A0208_Aquafilter_FH20B1-B-WB' found in project (Id=1426379, VersionGuid=...)` (Revit 2025) ✅
- `Family 'ADSK_...' found in project (Id=1625702, VersionGuid=...)` (Revit 2025) ✅

### 5. `IFamilyLoadService.LoadFamilyAsync` — Revit transaction + IExternalEvent

**Причина:** использует `IFamilyLoadOptions.OnFamilyFound` callback (вызывается Revit'ом из native кода). Требует активную Revit-сессию + IExternalEventHandler.

**Покрытие:** только через ручное тестирование. Подтверждено в production:
- `Family 'BP_A0208_Aquafilter_FH20B1-B-WB' updated to latest version` (Revit 2025) ✅
- `Family saved in newer version: 2025` — корректный отказ на 2023 (file version > project version) ✅

### 6. `CategoryTreeAdapter` — WPF VM (sealed)

**Причина:** принимает `CategoryNodeViewModel` (concrete WPF ViewModel) и `FamilyLeafNodeViewModel`. Оба класса sealed и требуют полный WPF DI graph (Messenger, observable collections, parent-child wiring) для создания.

**Покрытие:** только через ручное тестирование + существующий `StaleCategoryAggregatorTests` (который использует `Mock<ICategoryNodeInfo>` + `Mock<ICategoryLeafProvider>` напрямую, минуя адаптер). Подтверждено в production: `computed reasons for N/M families` после `BuildCatalogToCategoryMap`.

**Возможные workaround-ы (НЕ реализованы):**
- Рефакторинг `CategoryTreeAdapter` для приёма `ICategoryNodeInfo`-совместимого DTO вместо `CategoryNodeViewModel` — расширит границу тестирования

## Рекомендации для следующих итераций

| Приоритет | Действие | Усилие |
|---|---|---|
| **HIGH** | Создать `SmartCon.Tests.Integration` с Revit test host (revit-addin-host или NUnit+Revit) | 5-10 дней |
| **MEDIUM** | Рефакторинг `CategoryTreeAdapter` для тестируемости | 1-2 дня |
| **MEDIUM** | Вынести `FilteredElementCollector` в `IFamilyFinder` (для `CheckCategoryAsync`) | 0.5 дня |
| **LOW** | `IFamilyVersionStore` — partial mock через in-memory Schema (только для smoke-тестов) | 2-3 дня |

## Связанные документы

- `docs/testing/coverage-baseline-2026-06.md` — общий baseline проекта
- `docs/adr/030-stale-detection.md` — архитектурное решение
- `docs/invariants.md` — I-01 (ExternalEvent), I-03 (Transaction), I-05 (ElementId lifetime)
- Issue #69 — оригинальный запрос на stale detection
