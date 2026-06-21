---
module: cross-cutting
---
# Cross-cutting модели

> Загружать: при работе с утилитами и инфраструктурой, не привязанными к конкретному модулю.
> Источник истины: `src/SmartCon.Core/Models/*.cs`, `src/SmartCon.Core/Compatibility/`, `src/SmartCon.Core/Services/Json/`, `src/SmartCon.Core/Services/Storage/`, `src/SmartCon.Core/Services/Implementation/`, `src/SmartCon.Core/Data/`.

## JsonOptions

`SmartCon.Core/Services/Json/JsonOptions.cs` — статические singleton-ы для JSON сериализации:
- `Default` — strict (no indented, no relaxed escaping)
- `WriteIndented` — pretty
- `RelaxedWriteIndented` — кириллица (Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping)

---

## Guard

Polyfill для `ArgumentNullException.ThrowIfNull` (нет в net48). Удовлетворяет CA1510.

**Файл:** `SmartCon.Core/Common/Guard.cs`

```csharp
public static class Guard
{
    public static void ThrowIfNull<T>(T? value, [CallerArgumentExpression(nameof(value))] string? paramName = null);
}
```

---

## SqliteConnectionExtensions

Extension-методы для `Microsoft.Data.Sqlite.SqliteConnection`:
- `ExecuteAsync(sql, params?, ct)` → `int` (rows affected)
- `ExecuteScalarAsync<T>(sql, params?, ct)` → `T?` (null-safe)
- `QueryAsync<T>(sql, map, params?, ct)` → `IReadOnlyList<T>`
- `QuerySingleOrDefaultAsync<T>(sql, map, params?, ct)` → `T?`

**net48 compat:** использует `using` (sync) для `SqliteCommand`/`SqliteDataReader`, не `await using` (CS8417).
**CA1510:** использует `Guard.ThrowIfNull` для null-check.

**Файл:** `SmartCon.Core/Data/SqliteConnectionExtensions.cs`

---

## SystemFamilyTempLayout

Single source of truth для путей temp-папок system-family pipeline.
И staging-producer, и cleanup-consumer ОБЯЗАНЫ брать пути отсюда — иначе риск
"зависшего" .rvt между save и cleanup.

**Файл:** `SmartCon.Core/Services/FamilyManager/SystemFamilyTempLayout.cs`

```csharp
public static class SystemFamilyTempLayout
{
    public const string TempRoot       = "SmartCon";
    public const string StagingSubdir  = "SystemFamilyLoadFromProject";
}
```

Layout под `Path.GetTempPath()`:
```
%TEMP%\SmartCon\SystemFamilyLoadFromProject\<GUID>\<safeName>.rvt
```

---

## SmartConLogger

Главный фасад логирования. Методы: `Debug`, `Info`, `Warn`, `Error`, `Fatal`, `BeginScope`, `Measure`.

**Файл:** `SmartCon.Core/Logging/SmartConLogger.cs`

Подробности: см. навык `smartcon-logging` и ADR-026.

---

## SmartConLoggerAdapter

Адаптер между `ISmartConLogger` и внешними фреймворками логирования (Serilog, NLog, ILogger из net8).

**Файл:** `SmartCon.Core/Logging/SmartConLoggerAdapter.cs`

---

## MappingPayload

DTO для маппинга фитингов, сериализуемый в JSON (per-project ExtensibleStorage ↔ JSON файл).

**Файл:** `SmartCon.Core/Services/Storage/MappingPayload.cs`

---

## MappingPayloadDto

DTO для JSON-сериализации `MappingPayload` (для `FittingMappingJsonSerializer`).

**Файл:** `SmartCon.Core/Services/Storage/MappingPayloadDto.cs`

---

## MappingRuleDto

DTO для одного правила маппинга (FromType, ToType, IsDirectConnect, FamilyMappings) при JSON-сериализации.

**Файл:** `SmartCon.Core/Services/Storage/MappingRuleDto.cs`

---

## FittingMappingDto

DTO для одного `FittingMapping` (FamilyName, SymbolName, Priority) при JSON-сериализации.

**Файл:** `SmartCon.Core/Services/Storage/FittingMappingDto.cs`

---

## ConnectorTypeDto

DTO для `ConnectorTypeDefinition` (Code, Name, Description) при JSON-сериализации.

**Файл:** `SmartCon.Core/Services/Storage/ConnectorTypeDto.cs`

---

## FittingMappingJsonSerializer

Pure C# сериализатор `MappingPayload ↔ JSON` для импорта/экспорта правил маппинга. Unit-tested без Revit API.

**Файл:** `SmartCon.Core/Services/Storage/FittingMappingJsonSerializer.cs`

---

## ShareSettingsJsonSerializer

Pure C# сериализатор `ShareProjectSettings ↔ JSON` для импорта/экспорта настроек Share Project.

**Файл:** `SmartCon.Core/Services/Storage/ShareSettingsJsonSerializer.cs`

---

## JsonUpdateSettingsRepository

JSON-репозиторий для `UpdateSettings` (автообновление через GitHub). Файл: `%APPDATA%\SmartCon\update-settings.json`.

**Файл:** `SmartCon.Core/Services/Implementation/JsonUpdateSettingsRepository.cs`

---

## FittingMapper

Реализация `IFittingMapper` — поиск подходящих фитингов по типам коннекторов. Загружает правила из JSON (через `FittingMappingJsonSerializer`).

**Файл:** `SmartCon.Core/Services/Implementation/FittingMapper.cs`

---

## FittingChainResolver

Реализация `IFittingChainResolver` (ADR-010) — единая точка решений о цепочке фитингов/редьюсеров.

**Файл:** `SmartCon.Core/Services/Implementation/FittingChainResolver.cs`

---

## LocalCatalogDatabase

`public sealed class LocalCatalogDatabase` (ранее `internal`). Содержит путь к файлу БД и factory для `SqliteConnection`. Повышение видимости — CS0051 fix (public ctor принимал internal параметр).

---

## FileNameParser

Pure C# парсер имени файла по `FileNameTemplate`. Pure без Revit API — unit-tested. (В `SmartCon.Revit/Sharing/RevitFileNameParser.cs` — обёртка для Revit-специфичных операций.)

**Файл:** `SmartCon.Core/Services/Implementation/FileNameParser.cs`

---

## FormulaParamMatcher

Утилита матчинга имён параметров в формулах Revit (с учётом `BuiltInParameter` enum и shared params).

**Файл:** `SmartCon.Core/Services/Implementation/FormulaParamMatcher.cs`

---

## LookupColumnResolver

Утилита определения target-колонки в LookupTable по параметрам коннектора.

**Файл:** `SmartCon.Core/Services/Implementation/LookupColumnResolver.cs`

---

## FamilyNameNormalizer

Нормализация имён семейств: trim, lowercase, замена спецсимволов, валидация уникальности. Используется в каталоге FamilyManager.

**Файл:** `SmartCon.Core/Services/Implementation/FamilyNameNormalizer.cs`

---

## FamilySearchNormalizer

Нормализация поисковых запросов по каталогу семейств: токенизация, stemming, fuzzy-match.

**Файл:** `SmartCon.Core/Services/Implementation/FamilySearchNormalizer.cs`

---

## FamilyCatalogQueryValidator

Валидатор параметров запроса каталога семейств: проверка диапазонов пагинации, допустимых значений фильтров.

**Файл:** `SmartCon.Core/Services/Implementation/FamilyCatalogQueryValidator.cs`

---

## RadiusSetIntersector

Утилита пересечения множеств радиусов коннекторов: находит общие/уникальные DN для multi-port фитингов.

**Файл:** `SmartCon.Core/Services/Implementation/RadiusSetIntersector.cs`

---

## SafeFileName

Утилита санитизации имён файлов: замена запрещённых символов, усечение длины, проверка коллизий Windows reserved names.

**Файл:** `SmartCon.Core/Services/Implementation/SafeFileName.cs`

---

## TypeCatalogParser

Парсер Type Catalog (.txt) семейства Revit: чтение заголовка и строк типов, нормализация значений.

**Файл:** `SmartCon.Core/Services/Implementation/TypeCatalogParser.cs`

---

## ElementIdCompat

Кросс-TFM абстракция для `ElementId.GetValue()` (доступно с Revit 2022+, требует guarded cast в более ранних).

**Файл:** `SmartCon.Core/Compatibility/ElementIdCompat.cs`

---

## CategoryCompat

Кросс-TFM абстракция `Category → BuiltInCategory`:
- **Revit 2022+** — канонический `Category.BuiltInCategory` (корректно для standard, INVALID для custom sub-category).
- **Revit 2019–2021** — guarded cast `(BuiltInCategory)(int)catId.GetValue()` через
  `ElementIdCompat.GetValue()`.

**Файл:** `SmartCon.Core/Compatibility/CategoryCompat.cs`

---

## NetFrameworkCompat

Кросс-TFM хелперы для net48/net8 совместимости (Encoding, Path, Path.Combine и т.д.). Заменяет platform-specific методы, недоступные в net48.

**Файл:** `SmartCon.Core/Compatibility/NetFrameworkCompat.cs`

---

## Constants

Глобальные константы проекта (пути, ключи реестра, дефолтные значения).

**Файл:** `SmartCon.Core/Common/Constants.cs`

---

## ServiceHost

Базовый класс/хелпер для DI-хостинга сервисов в SmartCon.App. Управляет временем жизни singleton/scoped/transient.

**Файл:** `SmartCon.Core/Common/ServiceHost.cs`

---

## CommandHelper

Утилиты для обработки `IExternalCommand.Execute` входа: парсинг аргументов, логирование старта/конца, обработка исключений с откатом транзакции.

**Файл:** `SmartCon.Core/Common/CommandHelper.cs`

---

## DialogCloseHelper

Утилита закрытия WPF-диалогов из ViewModel через `IDialogPresenter` с поддержкой `ICloseAwareViewModel` и `IObservableRequestClose`.

**Файл:** `SmartCon.Core/Common/DialogCloseHelper.cs`

---

## AsyncBridge

Обёртка над `Task.Run + GetResult()` для безопасного вызова async-кода из sync-контекста Revit (I-01 ExternalEvent thread). **Не использовать `.GetAwaiter().GetResult()` напрямую — это даёт DEADLOCK** на UI thread.

**Файл:** `SmartCon.Core/Threading/AsyncBridge.cs`

---

## LogLevel

Enum уровней логирования (Trace, Debug, Info, Warn, Error, Fatal).

**Файл:** `SmartCon.Core/Logging/LogLevel.cs`

---

## LogScope

Иммутабельный scope для логирования: Category, Operation, Properties. Создаётся через `SmartConLogger.BeginScope`.

**Файл:** `SmartCon.Core/Logging/LogScope.cs`

---

## LogScopeProvider

Провайдер текущего `LogScope` для async-flow (через `AsyncLocal<T>`). Позволяет корректно пробрасывать scope через await-границы.

**Файл:** `SmartCon.Core/Logging/LogScopeProvider.cs`

---

## MeasureScope

Специализированный scope для замера времени операций (`Stopwatch` + автолог elapsed). Создаётся через `SmartConLogger.Measure`.

**Файл:** `SmartCon.Core/Logging/MeasureScope.cs`

---

## HotLoopCounter

Утилита подсчёта итераций в hot loops для диагностики производительности. Логирует warning при превышении порога.

**Файл:** `SmartCon.Core/Logging/HotLoopCounter.cs`

---

## SimplePriorityQueue

Минимальная приоритетная очередь (min-heap) на массиве. Используется в `IFittingChainResolver` для алгоритма Дейкстры.

**Файл:** `SmartCon.Core/Data/SimplePriorityQueue.cs`

---

## Language

Enum поддерживаемых языков UI (en, ru).

**Файл:** `SmartCon.Core/Localization/Language.cs`

---

## LocalizationService

Сервис локализации UI-строк с поддержкой `.resx` ресурсов и fallback на en.

**Файл:** `SmartCon.Core/Localization/LocalizationService.cs`

---

## LocalizationService.Keys.Common

Константы ключей локализации, общие для всех модулей (`OK`, `Cancel`, `Error`, `Warning`, `Save`, `Open`, ...).

**Файл:** `SmartCon.Core/Localization/LocalizationService.Keys.Common.cs`

---

## LocalizationService.Keys.FamilyManager

Константы ключей локализации модуля FamilyManager.

**Файл:** `SmartCon.Core/Localization/LocalizationService.Keys.FamilyManager.cs`

---

## LocalizationService.Keys.PipeConnect

Константы ключей локализации модуля PipeConnect.

**Файл:** `SmartCon.Core/Localization/LocalizationService.Keys.PipeConnect.cs`

---

## LocalizationService.Keys.ProjectManagement

Константы ключей локализации модуля ProjectManagement.

**Файл:** `SmartCon.Core/Localization/LocalizationService.Keys.ProjectManagement.cs`

---

## TypeCatalogValueApplier (Phase 25 / ADR-032)

Pure C# реализация `ITypeCatalogValueApplier`. Парсит сырое значение из Type Catalog (`.txt`) в типизированное значение, совместимое с Revit `StorageType`. Не вызывает Revit API — принимает `StorageTypeCode` (int-backed enum в Core) как opaque parameter. Это позволяет unit-тестирование без зависимости от Revit.

**Файл:** `SmartCon.Core/Services/Implementation/TypeCatalogValueApplier.cs`

Логика:
- `StgText` → return as-is
- `StgInt` → `int.Parse(Invariant)`
- `StgNumber` → `double.Parse(Invariant)` → fallback `CurrentCulture` (важно для русской локали)
- `StgElementId` → `long.Parse(Invariant)` (raw id, оборачивается в `new ElementId(id)` вызывающим кодом)
- Прочее → `UnsupportedStorageType`

**Тестирование:** 19 unit-тестов в `src/SmartCon.Tests/Core/Services/TypeCatalogValueApplierTests.cs` покрывают все ветки + null-safety + culture fallback + негативные кейсы (InvalidFormat для разных StorageType).
