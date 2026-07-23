---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager — Актуализация БД

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## ICatalogActualizationService

**Единый** сервис актуализации БД (ADR-054): движок, который union'ит детекты всех зарегистрированных `IDatabaseActualizationTask`, открывает каждую pending-семью ровно один раз (snapshot + геометрия в одной сессии через `IFamilyMigrationExtractor.ExtractLoadableWithGeometryAsync`; staged system `.rvt` — category-only через `ExtractSystemCategoryAsync`, диспатч по расширению managed-файла) и применяет только pending-задачи. Группы грузятся для `family_source IN ('loadable','system')` — system-группы инертны для задач с loadable-scope детектом. Новые extraction-time фичи = новый класс-задача — движок, диалог, гейт, resume и purge бесплатны.

**Файл:** `Services/Interfaces/ICatalogActualizationService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Actualization/CatalogActualizationService.cs`

```csharp
public interface ICatalogActualizationService
{
    Task<DatabasePendingBreakdown> CountPendingBreakdownAsync(
        int revitMajorVersion, CancellationToken ct = default);
    Task<DatabaseMigrationResult> RunAllPendingAsync(
        int revitMajorVersion,
        IProgress<DatabaseMigrationProgress>? progress,
        CancellationToken ct = default);
    Task<(int DeletedItems, int DeletedVersions, int FailedDirectories)> PurgeMissingAsync(
        IReadOnlyList<HashRecalculationMissingFile> missing,
        CancellationToken ct = default);
}
```

Алгоритм `RunAllPendingAsync`:
1. File-free passes задач (работа без файлов, напр. system re-flag хэша).
2. Детекты задач → union ключей `catalogItemId|versionLabel`; группы загружаются одним запросом, openable-вариант — наивысший `revit_major_version` ≤ запущенного Revit; newer-only группы — счётчик в сводке.
3. По группе: файл не найден → missing + `HandleGroupFailureAsync(MissingFile)` задач; не прочитался → failed + `HandleGroupFailureAsync(ExtractionFailed)`; иначе один open → `ApplyAsync` pending-задач по `Order` (сбой одной задачи не мешает остальным — её артефакт остаётся pending).
4. `PurgeMissingAsync` — по подтверждению пользователя удаляет записи о недоступных файлах: versions (FK CASCADE чистит types/attributes/nested), file records, items без версий; если удалённая версия была активной — active переключается на новейшую оставшуюся с ресинком хэша и имени.

---

## IDatabaseActualizationTask

Контракт одного «запроса на обновление» (задачи) движка актуализации (ADR-054, `docs/architecture/database-migrations.md`). Задачи обнаруживаются через DI (`IEnumerable<IDatabaseActualizationTask>`). CRITICAL задачи с pending > 0 поднимают баннер + красный badge и гейтят все write-операции; OPTIONAL — влияют только на видимость команды «Обновить базу» (Owner/BimMaster).

**Файл:** `Services/Interfaces/IDatabaseActualizationTask.cs`
**Реализации:** `HashFormatActualizationTask` (`hash-v3`, Order=10, critical, ADR-056), `AttributesActualizationTask` (`attributes-v1`, Order=20, optional), `GlbPreviewActualizationTask` (`glb-v1`, Order=30, optional) — `SmartCon.FamilyManager/Services/Actualization/`.

```csharp
public interface IDatabaseActualizationTask
{
    string Id { get; }
    int Order { get; }
    bool IsCritical { get; }
    Task<int> CountPendingAsync(int revitMajorVersion, CancellationToken ct = default);
    Task<NewerOnlyPendingInfo> GetNewerOnlyPendingAsync(int revitMajorVersion, CancellationToken ct = default);
    Task<IReadOnlyCollection<string>> LoadPendingGroupKeysAsync(int revitMajorVersion, CancellationToken ct = default);
    Task<int> RunFileFreePassAsync(int revitMajorVersion, CancellationToken ct = default);
    Task ApplyAsync(FamilyActualizationContext context, CancellationToken ct = default);
    Task HandleGroupFailureAsync(ActualizationGroup group, ActualizationFailureKind kind, CancellationToken ct = default);
}
```

- `CountPendingAsync` — дешёвый SQL COUNT processable групп (фильтр Revit) для badge/меню; без побочных эффектов.
- `GetNewerOnlyPendingAsync` (ADR-054 §3a) — pending-группы БЕЗ единого openable-варианта + минимальный Revit, в котором они все обновятся за раз (`RequiredRevitVersion` = MAX over groups of MIN(variant revit)); openability считается по ВСЕМ вариантам группы (apply идёт на все). CRITICAL newer-only гейтит (база read-only до идеальной миграции), OPTIONAL — только янтарный индикатор.
- `LoadPendingGroupKeysAsync` — ключи `itemId|versionLabel` ВСЕХ pending-групп (любой Revit — движок классифицирует openability).
- `RunFileFreePassAsync` — мгновенная работа без файлов (0 для большинства; у hash — system re-flag).
- `ApplyAsync` — записывает своё из готового `FamilyActualizationContext` (snapshot+geometry из одного open'а); идемпотентен; короткие транзакции (I-14); записанный артефакт обязан погасить свой детект (возобновляемость).
- `HandleGroupFailureAsync` — задача сама решает семантику терминальных маркеров (hash: -2 missing / -1 unreadable; attributes/glb: no-op → ретрай при следующем запуске).
- Scope (active-only vs все версии, system vs loadable) — собственность детекта задачи.

---

## IFamilyMigrationExtractor

Revit-bound граница движка актуализации (ADR-054): открывает ОДИН managed family-файл на Revit main thread, извлекает snapshot (+ per-type геометрию), закрывает документ. Реализация в SmartCon.Revit маршалит через `IFamilyManagerAwaitableEvent`; вызывающий `ICatalogActualizationService` остаётся pure C# и юнит-тестируемым с fake-экстрактором.

**Файл:** `Services/Interfaces/IFamilyMigrationExtractor.cs`
**Реализация:** `SmartCon.Revit/FamilyManager/RevitFamilyMigrationExtractor.cs`

```csharp
public interface IFamilyMigrationExtractor
{
    Task<FamilyMigrationExtractResult> ExtractLoadableAsync(
        string absolutePath,
        CancellationToken ct = default);
    Task<FamilyMigrationExtractResult> ExtractLoadableWithGeometryAsync(
        string absolutePath,
        CancellationToken ct = default);
    Task<FamilyMigrationExtractResult> ExtractSystemCategoryAsync(
        string absolutePath,
        CancellationToken ct = default);
}
```

- Открывает `.rfa` через `OpenDocumentFile`, извлекает `FamilySnapshot`, закрывает без сохранения. Не бросает через границу — ошибки в результате.
- `ExtractLoadableWithGeometryAsync` (ADR-054) — та же open→extract→close сессия плюс per-type геометрия (`FamilyMigrationExtractResult.Geometry`): файл открывается ровно один раз. Ошибка геометрии НЕ роняет результат — snapshot остаётся валидным, `Geometry` = `null` (caller делает fallback на отдельный проход геометрии).
- `ExtractSystemCategoryAsync` — для staged system `.rvt` (проектный документ): извлекает ТОЛЬКО display name Revit-категории. Детект идёт через `SystemCategoryRegistry` — канонический whitelist системных категорий продукта (тот же, что у `AnalyzeActiveProject`): сначала по размещённым инстансам (доменная истина мини-проекта — типоразмерами в БД становится только выставленное на виде), fallback — по скопированным типам для Phase-2 категорий, которые копируются без размещения (`placed=0`: перекрытия/крыши/лестницы/...). Дефолтный контент чистого проекта (уровни, виды, материалы, импосты) исключён конструктивно — его нет в реестре. Ничего не найдено → Ok с пустой категорией (задача пишет терминальный `''` маркер, без вечного retry). Возвращает минимальный `FamilySnapshot` — единая форма контекста движка. Движок диспатчит по расширению managed-файла (`.rvt` → system, `.rfa` → loadable).
- После каждого Close — `IUiFreezeRecoveryService.Nudge(" ")` (workaround #96: DockablePane freeze после циклов OpenDocumentFile+Close, REVIT-236376/237190).

---

## IDatabaseUpdateStateService

Разделяемое singleton-состояние «база требует обновления» (`docs/architecture/database-migrations.md`, Issue #126). MainViewModel обновляет его после инициализации и на каждом переключении базы; любая VM модуля блокирует write-операции через `EnsureUpToDateAsync()`. Пока `IsUpdateRequired` — база read-only: импорт (файлы/активный файл/выделенные), загрузка в проект, редактирование/удаление семейств, управление версиями, ассеты и редактор категорий гейтятся диалогом с предложением обновить.

**Файл:** `Services/Interfaces/IDatabaseUpdateStateService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Migrations/DatabaseUpdateStateService.cs`

```csharp
public interface IDatabaseUpdateStateService
{
    bool IsUpdateRequired { get; }
    int PendingCount { get; }
    int OptionalPendingCount { get; }
    int NewerOnlyCriticalCount { get; }
    int NewerOnlyRequiredRevitVersion { get; }
    int NewerOnlyPendingCount { get; }
    bool IsRunning { get; }
    event EventHandler? StateChanged;
    Task RefreshAsync(int revitMajorVersion, CancellationToken ct = default);
    void Reset();
    Task<bool> EnsureUpToDateAsync();
    Task UpdateAsync();
}
```

- `RefreshAsync` — пересчёт через движок `ICatalogActualizationService` (`DatabasePendingBreakdown`); `Reset` — при отсутствии активной БД.
- `IsUpdateRequired` (ADR-054 §3a) — ЛЮБОЙ critical pending: processable (`PendingCount`) ИЛИ newer-only (`NewerOnlyCriticalCount`) — база read-only до идеальной миграции.
- `EnsureUpToDateAsync` — gate: read-only роль (Engineer) → styled info «обновление выполнит Owner/BIM-мастер» (без оффера — запись бы упала); processable critical → диалог «Обновить сейчас?»; только newer-critical → предупреждение с `NewerOnlyRequiredRevitVersion` (оффера нет — здесь не починить).
- `UpdateAsync` — ЕДИНЫЙ прогон движка через единый диалог (команда «Обновить базу», ADR-054); ошибки логируются, закоммиченные записи сохраняются.
- `OptionalPendingCount` (ADR-054) — processable pending OPTIONAL задач; влияет только на видимость команды «Обновить базу», никогда не гейтит write-операции.
- `NewerOnlyPendingCount` (ADR-054 §3a) — OPTIONAL newer-only записи; только янтарный индикатор, база остаётся рабочей.
