# FamilyManager MVP — Технический план реализации

> **Статус:** FINAL — утверждён для execution
> **Дата:** 2026-04-28
> **Основан на:** `13-technical-mvp-plan.pplx.md` + критический анализ реальной архитектуры SmartCon
> **Предшествующие документы:** `01-mvp-prd` — `13-technical-mvp-plan` (все одобрены как контекст)

---

## 0. Решения после ревью

### v2 (первый раунд)

- Singleton dockable ViewModel не хранит live Revit objects.
- SQL builder не размещается в Core.
- MVP storage mode по умолчанию — Cached.
- SQLite schema включает таблицу `previews`.
- `cached_path` хранится как относительный путь от canonical root.
- `LocalCatalogProvider` регистрируется один раз.
- `FamilyManagerMainViewModel` не в `WpfDialogPresenter`.
- Deep metadata extraction standalone `.rfa` — Post-MVP.
- `RevitFamilyMetadataExtractionService` возвращает extraction DTO.
- `TaskDialog.Show` вместо `MessageBox.Show`.

### v3 (второй раунд)

- **`IFamilyLoadService` не принимает `Document` как параметр.** Revit-layer сам
  разрешает актуальный `Document` через `IRevitContext`. ViewModel не участвует
  в передаче `Document`.
- **`IFamilyMetadataExtractionService` — optional boundary.** MVP не зависит от
  deep extraction для завершения импорта. Импорт успешен только с file-level metadata.
- **`RevitFamilyMetadataExtractionService` перемещён из обязательной Phase 12D
  в Post-MVP.** В MVP достаточно stub/`FileNameOnlyMetadataExtractionService`.
- **`FamilyLoadOptions`** заложены в контракт `IFamilyLoadService` для future conflict handling.
- **`FamilyManagerPaneProvider` живёт только в `SmartCon.FamilyManager`** (не "или App").
- **DI-регистрация полная:** `FamilyManagerMainViewModel`, `FamilyManagerPaneControl`,
  `FamilyManagerPaneProvider` — все через DI, OnStartup только разрешает и регистрирует pane.
- **csproj включает `UseWPF` и RevitAPI/RevitAPIUI** compile-time references
  (как PipeConnect/ProjectManagement).
- **Иконки canonical naming:** `FamilyMan_*` (существующие файлы).
- **previews table** — forward compatibility, MVP может оставлять пустой.
- **Test fixtures:** unit tests используют fake binary placeholders, не настоящий `.rfa`.
- **ADR numbering:** следуем стилю репозитория — `014-familymanager-mvp-architecture.md`
  (один aggregate ADR).
- **`FamilyManagerDialogService`** — WinForms `FolderBrowserDialog` допустим
  (как в `PipeConnectDialogService`).
- **Тесты:** "all existing tests pass + 50+ new", без хардкода чисел.

---

## 1. Цель документа

Финальный технический план MVP, скорректированный после сверки 16 документов family-manager
с реальной архитектурой репозитория. Этот документ заменяет `13-technical-mvp-plan.pplx.md`
как канонический план реализации.

---

## 2. Критический анализ: расхождения документов с реальной архитектурой

При сверке документов с кодом выявлены следующие проблемы. Каждая исправлена в данном плане.

### 2.1. Неверные пути в документации

| Документ говорит | Реальность | Исправление |
|---|---|---|
| `docs/familymanager/README.md` | Фактический путь: `docs/family-manager/README.md` | Все пути в данном плане используют реальную структуру |
| `SmartCon.PipeConnect/Services/LanguageManager.cs` | `LanguageManager` находится в `SmartCon.UI/LanguageManager.cs` | Семантика верна, но ссылаться нужно на `SmartCon.UI` |
| `SmartCon.PipeConnect/Services/StringLocalization.cs` | `StringLocalization` находится в `SmartCon.UI/StringLocalization.cs` | Аналогично |
| `docs/familymanager/*` | Все документы лежат в `docs/family-manager/` | Исправлено |

### 2.2. Dockable Panel — требуется новая инфраструктура

В SmartCon **нет** `IDockablePaneProvider` и регистрации dockable panel. Все текущие окна — модальные
(`ShowDialog`) или немодальные (`Show`) через `WpfDialogPresenter`. Однако FamilyManager как каталог
семейств **должен** быть dockable panel — это фундаментальный UX, подтверждённый пользователем.

**Решение MVP:** Dockable panel через `IDockablePaneProvider`.
- Регистрация через `UIControlledApplication.RegisterDockablePane()` в `App.OnStartup`.
- `FrameworkElement` — `UserControl` с содержимым FamilyManager (не `Window`).
- Singleton ViewModel — живёт на протяжении всего lifetime Revit.
- Revit API вызовы — через `FamilyManagerExternalEvent`.
- Ribbon кнопка переключает видимость: `DockablePane.Show()` / `Hide()`.

**API подтверждён через Revit API docs (v26.0.4.0):**
- `UIControlledApplication.RegisterDockablePane(DockablePaneId, string, IDockablePaneProvider)`
- `IDockablePaneProvider.SetupDockablePane(DockablePaneProviderData)` — задаёт `FrameworkElement`
- `DockablePaneProviderData.FrameworkElement` — WPF-контент панели
- `DockablePaneProviderData.InitialState` — начальная позиция (DockPosition, FloatingRectangle)
- `DockablePaneId` — `new DockablePaneId(Guid)` — уникальный ID панели
- `DockablePane.Show()` / `DockablePane.Hide()` / `DockablePane.IsShown()`
- Доступ к `DockablePane` — через `UIApplication.GetDockablePane(DockablePaneId)`

### 2.3. PipeConnectExternalEvent владеет ExternalEvent

Документ `09-provider-contract` и `13-technical-mvp-plan` не учитывают, что в SmartCon
два паттерна ExternalEvent:

1. **`ActionExternalEventHandler`** — generic, не владеет `ExternalEvent`, получает его
   как параметр `Raise(externalEvent, action)`.
2. **`PipeConnectExternalEvent`** — владеет `ExternalEvent`, `Raise(action)` без параметра.

Для FamilyManager нужен **собственный handler** по модели PipeConnect (владеет event),
поскольку FamilyManager будет жить как отдельная DLL и не должен зависеть от PipeConnect.

### 2.4. DialogService — не один интерфейс

Документы предполагают единый `IDialogService`, но в реальности:
- `IDialogService` (Core) — специфичный для PipeConnect (8 методов, завязанных на CTC).
- `IDialogPresenter` (Core) — generic VM→View маппинг.
- Реализация `PipeConnectDialogService` — в PipeConnect, не в UI.

**Решение MVP:** FamilyManager создаёт собственный `FamilyManagerDialogService`
(аналог `PipeConnectDialogService`) с методами для File/Folder Browser, Message, Settings.
Регистрирует VM→View маппинги через `WpfDialogPresenter` в `ServiceRegistrar`.

### 2.5. ViewModelFactory — обязательный паттерн

В SmartCon все Commands используют Factory-интерфейсы (`IPipeConnectViewModelFactory`,
`IShareSettingsViewModelFactory`), а не Service Locator. Документы family-manager
не упоминают этот паттерн.

**Решение MVP:** Создать `IFamilyManagerViewModelFactory` + реализацию,
регистрировать в DI.

### 2.6. IObservableRequestClose + DialogWindowBase

Все модальные окна наследуют `DialogWindowBase` из `SmartCon.UI` и реализуют
`IObservableRequestClose` в ViewModel. Документы не описывают этот паттерн.

**Решение MVP:** Все FamilyManager ViewModel для модальных окон реализуют
`IObservableRequestClose`. Все View наследуют `DialogWindowBase`.

### 2.7. Localization — StringLocalization.Keys

Документы упоминают локализацию, но не описывают реальный паттерн:
- `StringLocalization.BuildRu()` / `BuildEn()` → `ResourceDictionary` с ~200 ключами
- `LanguageManager` управляет сменой языка
- `DynamicResource` в XAML + `LanguageManager.GetString()` в C#
- DataGrid заголовки — программно через `x:Name` + `LanguageManager.GetString()`

**Решение MVP:** Добавить ключи FM_* в `StringLocalization`, использовать
существующий паттерн.

### 2.8. Иконки

В `SmartCon.App/Resources/Icons/` есть иконки для PipeConnect, Settings,
ShareProject, About, Align, Rotate. **Нет иконок для FamilyManager.**

Пользователь упомянул, что иконки уже есть в ресурсах. Нужно уточнить — возможно,
они не в `Icons/` или ещё не добавлены в проект.

### 2.9. Document не хранится в Singleton ViewModel

В реальном коде `PipeConnectEditorViewModel` получает `Document _doc` напрямую
через factory — это работает для модальных окон с коротким lifetime.

Для FamilyManager dockable panel это **недопустимо**: singleton ViewModel переживает
смену/закрытие документа. `Document` **не передаётся** в ViewModel и не хранится в нём.

Все Revit-операции разрешают актуальный `Document` через `IRevitContext`
внутри `FamilyManagerExternalEvent.Execute()` в момент выполнения операции.

### 2.10. SQLite-зависимость — Microsoft.Data.Sqlite 8.x

**`Microsoft.Data.Sqlite 8.x` совместимость с .NET 8 подтверждена:**

- Target framework: `.NET Standard 2.0` — совместим с `net8.0-windows` и `net48`.
- Native assets: `SQLitePCLRaw.bundle_e_sqlite3 >= 2.1.6` — автоматически подтягивает
  `e_sqlite3.dll` для Windows x64. Для .NET 8 native DLL резолвится через
  `AssemblyLoadContext` / NuGet runtime store. Для net48 — через `AssemblyResolve`
  (уже есть в `App.cs`).
- Зависимости: `Microsoft.Data.Sqlite.Core 8.x` — pure managed, проблем нет.
- Версия 8.x выбрана потому что:
  1. Заморожена на 8.x для совместимости с `MEDI 8.x` и `STJ 8.x`.
  2. Поддерживает все нужные функции: WAL mode, FTS5, параметризованные запросы.
  3. Native assets проверены на работоспособность в .NET 8 desktop приложениях.

**Риск:** Native SQLite DLL (`e_sqlite3.dll`) должна корректно загружаться в Revit 2025.
Существующий `AssemblyResolve` handler в `App.cs` обеспечивает загрузку DLL из папки плагина.
Smoke-тест в Phase 12A подтверждает это перед основной реализацией.

### 2.11. Dockable Panel + Singleton ViewModel

Dockable panel подразумевает **singleton ViewModel** — один экземпляр на всю Revit-сессию.
Это отличается от текущих модальных окон (PipeConnect, ProjectManagement), где ViewModel
создаётся при каждом открытии.

**Следствия:**
- ViewModel должен корректно обновляться при смене активного документа.
- ViewModel должен обрабатывать состояние "нет активного документа".
- Данные каталога (SQLite) не привязаны к конкретному документу — переживают смену.
- Revit-операции (LoadFamily) — через ExternalEvent с актуальным Document из `IRevitContext`.

**Критическое правило (I-05 strict):**

Singleton dockable ViewModel **не хранит** `Document`, `Element`, `Family`, `FamilySymbol`
или другие live Revit objects. Он хранит только DTO, ID, cached file paths и UI state.
Каждая Revit-операция выполняется через `FamilyManagerExternalEvent`, который разрешает
актуальный `Document` из `IRevitContext` **в момент выполнения**, не при создании ViewModel.

---

## 3. MVP Definition of Done

MVP считается завершённым, когда:

1. `SmartCon.FamilyManager` проект создан, компилируется на R25/R24/R21/R19.
2. Ribbon содержит кнопку Family Manager.
3. По кнопке открывается dockable panel FamilyManager.
4. SQLite БД создаётся при первом использовании.
5. Пользователь импортирует `.rfa` файл — запись появляется в каталоге.
6. Пользователь импортирует папку — batch import с прогрессом.
7. Пользователь ищет по имени/категории/тегам.
8. Пользователь открывает карточку семейства.
9. Пользователь загружает семейство в активный проект.
10. Project usage записывается в локальную SQLite БД.
11. Все конфигурации R19/R21/R24/R25/R26 собираются.
12. Все существующие тесты проходят, без регрессий.
13. Добавлено 50+ новых тестов.
14. Инварианты I-01..I-13 не нарушены.
15. Зависимость `SmartCon.FamilyManager` → `SmartCon.Revit` **отсутствует**.

---

## 4. Фазы реализации

### Phase 12A: Architecture Spike (1-2 дня)

**Цель:** Проверить техническую осуществимость критических решений до написания боевого кода.

#### 12A-1. SQLite smoke test

- Создать spike-ветку `feature/family-manager-spike`.
- Добавить `Microsoft.Data.Sqlite 8.x` в `Directory.Packages.props`.
- Создать минимальный консольный тест: создать SQLite БД в `%APPDATA%\AGK\SmartCon\FamilyManager\`.
- Собрать `Debug.R25` и `Debug.R24` — убедиться, что native assets включаются.
- Протестировать загрузку DLL в Revit 2025.

#### 12A-2. Dockable Panel spike

- Создать минимальный `IDockablePaneProvider` с `UserControl` + текстом "Hello FamilyManager".
- Зарегистрировать через `application.RegisterDockablePane(new DockablePaneId(guid), "Family Manager", provider)` в `App.OnStartup`.
- Добавить Ribbon кнопку для `DockablePane.Show()`.
- Проверить lifecycle: открытие/закрытие/повторное открытие/смена документа.
- Проверить, что singleton ViewModel корректно реагирует на смену активного документа.

**Ключевой паттерн Dockable Panel в Revit:**

```csharp
// 1. Provider (в SmartCon.FamilyManager — UI-инфраструктура модуля)
// SmartCon.App только вызывает RegisterDockablePane в OnStartup
public class FamilyManagerPaneProvider : IDockablePaneProvider
{
    private readonly FamilyManagerPaneControl _control;

    public FamilyManagerPaneProvider(FamilyManagerPaneControl control)
    {
        _control = control;
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = _control;
        data.VisibleByDefault = false;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Tabbed,
            TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser
        };
    }
}

// 2. UserControl (не Window!) — контент панели
public partial class FamilyManagerPaneControl : UserControl
{
    public FamilyManagerPaneControl(FamilyManagerMainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}

// 3. Регистрация в App.OnStartup
var paneId = new DockablePaneId(new Guid("..."));
var control = new FamilyManagerPaneControl(viewModel);
var provider = new FamilyManagerPaneProvider(control);
application.RegisterDockablePane(paneId, "Family Manager", provider);

// 4. Ribbon кнопка — toggle видимости
// В command: UIApplication.GetDockablePane(paneId).Show()
```

#### 12A-3. Утверждение ADR

Создать aggregate ADR в `docs/adr/` (следуя стилю репозитория):

| ADR | Секция внутри ADR |
|---|---|
| `docs/adr/014-familymanager-mvp-architecture.md` | FM-001: module boundary, FM-002: dockable panel, FM-003: local SQLite, FM-004: file cache (Cached), FM-006: provider abstraction, FM-007: project usage DB |

**Acceptance:**
- [ ] SQLite БД создаётся из Revit 2025 add-in.
- [ ] Legacy build R24 компилируется.
- [ ] Dockable panel открывается из Ribbon, отображает WPF-контент.
- [ ] ADR утверждены.

---

### Phase 12B: Core Domain Models and Interfaces (2-3 дня)

**Цель:** Создать все модели и интерфейсы в `SmartCon.Core`.

#### 12B-1. Models

**Расположение:** `SmartCon.Core/Models/FamilyManager/`

```
FamilyCatalogItem.cs          — логическая запись каталога
FamilyCatalogVersion.cs       — версия файла
FamilyFileRecord.cs           — физический файл
FamilyTypeDescriptor.cs       — типоразмер
FamilyParameterDescriptor.cs  — параметр
FamilyCatalogQuery.cs         — поисковый запрос
FamilyImportRequest.cs        — запрос импорта
FamilyImportResult.cs         — результат импорта файла
FamilyBatchImportResult.cs    — результат batch импорта
FamilyFolderImportRequest.cs  — запрос импорта папки
FamilyImportProgress.cs       — прогресс batch импорта
FamilyLoadRequest.cs          — запрос загрузки в проект
FamilyLoadResult.cs           — результат загрузки
FamilyLoadOptions.cs          — опции загрузки
FamilyResolvedFile.cs         — разрешённый файл для загрузки
FamilyMetadataExtractionResult.cs — результат extraction (DTO, не доменная сущность)
ProjectFamilyUsage.cs         — запись использования
FamilyContentStatus.cs        — enum: Draft/Verified/Deprecated/Archived
FamilyFileStorageMode.cs      — enum: Linked/Cached/Missing
CatalogProviderKind.cs        — enum: Local/Remote/Corporate/PublicReadOnly
FamilyCatalogCapabilities.cs  — capabilities provider'а
FamilyCatalogSort.cs          — enum сортировки
```

**Правила моделей:**
- `record` с `init`-свойствами (иммутабельные).
- `IReadOnlyList<T>` для коллекций.
- **Нет** `using System.Windows`.
- **Нет** `using Autodesk.Revit.UI`.
- `Guid` для ID (не `ElementId` — каталог не привязан к Revit project).
- `DateTimeOffset` для UTC-времени (ISO 8601 в SQLite).
- XML-doc на всех публичных членах.

#### 12B-2. Interfaces

**Расположение:** `SmartCon.Core/Services/Interfaces/`

```
IFamilyCatalogProvider.cs            — чтение каталога
IWritableFamilyCatalogProvider.cs    — запись в каталог
IFamilyImportService.cs              — импорт файлов/папок
IFamilyFileResolver.cs               — разрешение пути файла
IFamilyLoadService.cs                — загрузка в Revit (Document разрешается внутри)
IFamilyMetadataExtractionService.cs  — optional metadata extraction boundary; MVP may return partial metadata only
IProjectFamilyUsageRepository.cs     — запись usage history
IFamilyManagerDialogService.cs       — UI-диалоги FamilyManager
```

**Критические правила:**
- `IFamilyLoadService.LoadFamilyAsync(FamilyResolvedFile, FamilyLoadOptions, CancellationToken)`
  — не принимает `Document`. Revit-реализация разрешает актуальный `Document` через
  `IRevitContext` самостоятельно в момент выполнения. ViewModel не участвует в передаче `Document`.
- Контракт включает `FamilyLoadOptions` для future conflict handling (overwrite, type conflicts,
  version update policies). MVP использует simplest overload.
- Revit layer не экспонирует `Family`/`FamilySymbol` наружу — возвращает только DTO:
  `FamilyLoadResult` с success, family name, message/error.
- `IFamilyMetadataExtractionService` — optional boundary. **MVP не зависит от deep extraction
  для завершения импорта.** Импорт успешен с file-level metadata: имя, hash, размер, timestamps,
  manual category/tags/description. Deep extraction — Post-MVP через отдельный ADR.
- `IFamilyMetadataExtractionService` работает с путём к файлу, не с `Document`.
- `IProjectFamilyUsageRepository` не зависит от Revit API — пишет в SQLite.

#### 12B-3. Domain Services (pure C#)

**Расположение:** `SmartCon.Core/Services/FamilyManager/`

```
FamilySearchNormalizer.cs      — нормализация поисковых токенов
FamilyNameNormalizer.cs        — нормализация имён для поиска (lowercase, diacritics)
FamilyCatalogQueryValidator.cs — валидация FamilyCatalogQuery
```

**Важно:** SQL-specific код (`FamilyCatalogQueryBuilder`, SQL-константы) **НЕ** размещается
в Core. Core только валидирует и нормализует query. SQL-билдер живёт в
`SmartCon.FamilyManager/Services/LocalCatalog/LocalCatalogQueryBuilder.cs`.

#### 12B-4. Обновить документацию

- `docs/domain/models.md` — добавить секцию FamilyManager.
- `docs/domain/interfaces.md` — добавить секцию FamilyManager.
- `docs/domain/glossary.md` — добавить термины FamilyManager.

**Acceptance:**
- [ ] Core компилируется без `System.Windows`.
- [ ] Core не вызывает Revit API.
- [ ] Все модели — `record` с XML-doc.
- [ ] Unit-тесты: нормализация, query builder, status enum (10+).
- [ ] Инвариант I-09 соблюдён.

---

### Phase 12C: Local SQLite Provider (3-4 дня)

**Цель:** Реализовать полный SQLite-стек для локального каталога.

#### 12C-1. Database Infrastructure

**Расположение:** `SmartCon.FamilyManager/Services/LocalCatalog/`

```
LocalCatalogDatabase.cs        — создание/открытие БД, connection management
LocalCatalogMigrator.cs        — запуск миграций (idempotent)
FamilyCatalogSql.cs            — константы SQL и helper-методы
LocalCatalogQueryBuilder.cs    — построение SQL из FamilyCatalogQuery (НЕ в Core)
Sha256FileHasher.cs            — SHA-256 хеширование файлов
LocalCatalogProvider.cs        — IFamilyCatalogProvider
LocalFamilyImportService.cs    — IFamilyImportService
LocalFamilyFileResolver.cs     — IFamilyFileResolver
LocalProjectFamilyUsageRepository.cs — IProjectFamilyUsageRepository
```

#### 12C-2. SQLite Schema (Migration 001)

```sql
-- schema_info
CREATE TABLE schema_info (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
INSERT INTO schema_info (key, value) VALUES ('schema_version', '1');

-- catalog_items
CREATE TABLE catalog_items (
    id TEXT PRIMARY KEY,
    provider_id TEXT NOT NULL DEFAULT 'local',
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    description TEXT NULL,
    category_name TEXT NULL,
    manufacturer TEXT NULL,
    status TEXT NOT NULL DEFAULT 'Draft',
    current_version_id TEXT NULL,
    created_at_utc TEXT NOT NULL,
    updated_at_utc TEXT NOT NULL
);
CREATE INDEX idx_catalog_items_name ON catalog_items(normalized_name);
CREATE INDEX idx_catalog_items_category ON catalog_items(category_name);
CREATE INDEX idx_catalog_items_status ON catalog_items(status);

-- catalog_versions
CREATE TABLE catalog_versions (
    id TEXT PRIMARY KEY,
    catalog_item_id TEXT NOT NULL,
    file_id TEXT NOT NULL,
    version_label TEXT NOT NULL,
    sha256 TEXT NOT NULL,
    revit_major_version INTEGER NULL,
    types_count INTEGER NULL,
    parameters_count INTEGER NULL,
    imported_at_utc TEXT NOT NULL
);
CREATE INDEX idx_catalog_versions_item ON catalog_versions(catalog_item_id);
CREATE INDEX idx_catalog_versions_sha256 ON catalog_versions(sha256);

-- family_files
CREATE TABLE family_files (
    id TEXT PRIMARY KEY,
    original_path TEXT NULL,
    cached_path TEXT NULL,
    file_name TEXT NOT NULL,
    size_bytes INTEGER NOT NULL,
    sha256 TEXT NOT NULL,
    last_write_time_utc TEXT NULL,
    storage_mode TEXT NOT NULL DEFAULT 'Cached'
);

-- family_types
CREATE TABLE family_types (
    id TEXT PRIMARY KEY,
    version_id TEXT NOT NULL,
    name TEXT NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0
);

-- family_parameters
CREATE TABLE family_parameters (
    id TEXT PRIMARY KEY,
    version_id TEXT NOT NULL,
    type_id TEXT NULL,
    name TEXT NOT NULL,
    storage_type TEXT NULL,
    value_text TEXT NULL,
    is_instance INTEGER NULL,
    is_readonly INTEGER NULL,
    forge_type_id TEXT NULL
);

-- catalog_tags
CREATE TABLE catalog_tags (
    catalog_item_id TEXT NOT NULL,
    tag TEXT NOT NULL,
    normalized_tag TEXT NOT NULL,
    PRIMARY KEY (catalog_item_id, tag)
);
CREATE INDEX idx_catalog_tags_tag ON catalog_tags(normalized_tag);

-- project_usage
CREATE TABLE project_usage (
    id TEXT PRIMARY KEY,
    catalog_item_id TEXT NOT NULL,
    version_id TEXT NOT NULL,
    provider_id TEXT NOT NULL,
    project_fingerprint TEXT NOT NULL,
    action TEXT NOT NULL,
    created_at_utc TEXT NOT NULL
);
CREATE INDEX idx_project_usage_item ON project_usage(catalog_item_id);
CREATE INDEX idx_project_usage_project ON project_usage(project_fingerprint);

-- previews (forward compatibility — MVP может оставлять пустой, UI показывает "Preview unavailable")
CREATE TABLE previews (
    id TEXT PRIMARY KEY,
    catalog_item_id TEXT NOT NULL,
    version_id TEXT NULL,
    relative_path TEXT NOT NULL,
    width INTEGER NULL,
    height INTEGER NULL,
    created_at_utc TEXT NOT NULL
);
CREATE INDEX idx_previews_item ON previews(catalog_item_id);
```

#### 12C-3. Import Pipeline

**Двухфазная индексация:**

Phase 1 (MVP — без Revit API):
- Имя файла
- Путь
- Размер
- SHA-256 hash
- Дата модификации
- Расширение (.rfa)
- Пользователь может вручную указать категорию, теги, описание

Phase 2 (Post-MVP — через Revit API):
- Категория Revit
- Типоразмеры
- Параметры
- Revit version
- Preview

**Import flow:**
1. Выбрать файл/папку → `FamilyImportRequest` / `FamilyFolderImportRequest`
2. Вычислить SHA-256 (background thread)
3. Проверить дубликаты по hash
4. Создать `FamilyFileRecord` (storage_mode = **Cached**):
   - Скопировать `.rfa` в `{canonical_root}/cache/rfa/{sha256-prefix}/{sha256}.rfa`
   - `cached_path` хранить как **относительный путь** от canonical root:
     `cache/rfa/ab/abcdef....rfa`. Полный путь собирается через `IFamilyFileResolver`
     в runtime. Это упрощает backup/restore и перенос профиля.
   - `original_path` — абсолютный путь исходного файла, хранится только справочно.
5. Создать `FamilyCatalogItem` (status = Draft)
6. Создать `FamilyCatalogVersion` (version_label = auto)
7. Записать теги (normalized)
8. Вернуть `FamilyImportResult`

**Batch import:**
- `IProgress<FamilyImportProgress>` для отчёта.
- `CancellationToken` для отмены.
- Ошибка одного файла не останавливает batch.
- Итог: `FamilyBatchImportResult` с success/skipped/errors.

**Правило атомарности import:**
DB-записи одного файла (file record + catalog item + version + tags) выполняются
в **одной SQLite transaction**. Если cache-копирование прошло, но DB commit упал —
cached file удаляется best-effort. Если DB commit прошёл, но cache-копирование упало —
запись получает `storage_mode = Missing`. Это гарантирует консистентность между
файловой системой и БД: нет orphan cached files без DB-записей, нет DB-записей
без доступных файлов (кроме явного Missing state).

**Acceptance:**
- [ ] SQLite БД создаётся.
- [ ] Миграции идемпотентны.
- [ ] Импорт одного `.rfa` файла работает.
- [ ] Импорт папки с прогрессом работает.
- [ ] Дубликаты по hash определяются.
- [ ] Поиск по имени/категории/тегам/status работает.
- [ ] Tests: SQLite repository CRUD, migrations, duplicate detection, search (15+).

---

### Phase 12D: Revit Layer (2-3 дня)

**Цель:** Реализовать Revit API операции за интерфейсами Core.

#### 12D-1. Services

**Расположение:** `SmartCon.Revit/FamilyManager/`

```
RevitFamilyLoadService.cs              — IFamilyLoadService
```

**Post-MVP (отдельный ADR):**

```
RevitFamilyMetadataExtractionService.cs — IFamilyMetadataExtractionService (deep extraction)
```

**MVP metadata extraction:** реализуется lightweight `FileNameOnlyMetadataExtractionService`
в `SmartCon.FamilyManager/Services/` — извлекает имя файла, расширение, размер, timestamps.
Не требует Revit API. Зарегистрирован как `IFamilyMetadataExtractionService` в DI.

#### 12D-2. RevitFamilyLoadService

- Разрешает `Document` через `IRevitContext` (не принимает как параметр).
- Внутри: `IRevitContext.GetDocument()` → `doc.LoadFamily(path)` (simplest MVP overload).
- Работает **только** через ExternalEvent (I-01).
- Использует `ITransactionService` если нужна транзакция (I-03).
- Не возвращает `Family`/`FamilySymbol` наружу — возвращает `FamilyLoadResult` с именем, message/error.
- Логирует через `SmartConLogger`.
- Контракт `FamilyLoadOptions` заложен для future conflict handling (overwrite, type conflicts).

#### 12D-3. RevitFamilyMetadataExtractionService

- **Внимание:** MVP metadata extraction **не требует** глубокого анализа standalone `.rfa`
  через Revit API. MVP хранит имя файла, hash, размер, timestamps и вручную введённые
  category/tags/description.
- Deep extraction (типы, параметры, Revit category, preview) — **Post-MVP** через отдельный ADR.
- Если deep extraction понадобится: для уже загруженных в проект семейств можно использовать
  `doc.EditFamily(family)` (только через ExternalEvent, I-01). Для standalone `.rfa` файлов
  — открытие family document, чтение типов/параметров, закрытие без сохранения — требует
  отдельного ADR.
- Возвращает **extraction DTO** (`FamilyMetadataExtractionResult` с `FamilyTypeDescriptor[]`,
  `FamilyParameterDescriptor[]`), не `FamilyCatalogVersion`. Запись в SQLite и обновление
  версии — через application/provider layer.
- Работает **только** через ExternalEvent (I-01).

**Acceptance:**
- [ ] Валидный `.rfa` загружается в Revit 2025 проект.
- [ ] Revit errors логируются и показываются пользователю.
- [ ] I-01, I-03, I-05 соблюдены.
- [ ] Нет live `Family`/`FamilySymbol` в ViewModel.

---

### Phase 12E: UI Module — SmartCon.FamilyManager (4-5 дней)

**Цель:** Создать WPF/MVVM модуль с dockable panel.

#### 12E-1. Проект

Создать `SmartCon.FamilyManager.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <!-- TFM и RevitVersion — из Directory.Build.props -->
    <UseWPF>true</UseWPF>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\SmartCon.Core\SmartCon.Core.csproj" />
    <ProjectReference Include="..\SmartCon.UI\SmartCon.UI.csproj" />
  </ItemGroup>
  <ItemGroup>
    <!-- RevitAPI/RevitAPIUI — compile-time только (CopyLocal=false), через Directory.Build.targets.
         Паттерн идентичен SmartCon.PipeConnect.csproj / SmartCon.ProjectManagement.csproj.
         Модуль использует IExternalCommand, IDockablePaneProvider, DockablePaneId, TaskDialog. -->
    <PackageReference Include="Nice3point.Revit.Api.RevitAPI" />
    <PackageReference Include="Nice3point.Revit.Api.RevitAPIUI" />
  </ItemGroup>
  <ItemGroup>
    <!-- Central Package Management — версии в Directory.Packages.props -->
    <PackageReference Include="Microsoft.Data.Sqlite" />
  </ItemGroup>
</Project>
```

**Dependency notes:**
- `UseWPF=true` обязателен для WPF-модуля.
- `Nice3point.Revit.Api.RevitAPI` / `RevitAPIUI` — compile-time (CopyLocal=false),
  версия определяется из конфигурации через `Directory.Build.targets`. Аналог PipeConnect.
- `Microsoft.Data.Sqlite` — версия зафиксирована в `Directory.Packages.props`.
- `CommunityToolkit.Mvvm` — **не нужен** в csproj модуля, тянется транзитивно через
  `SmartCon.UI` (который уже зависит от Core + CommunityToolkit).
- Запрещено: `Newtonsoft.Json`, `Microsoft.Extensions.*` 9+, сторонние WPF themes.

**Структура проекта:**

```
SmartCon.FamilyManager/
├── SmartCon.FamilyManager.csproj
├── Commands/
│   └── FamilyManagerCommand.cs        — IExternalCommand (toggle dockable pane)
├── ViewModels/
│   ├── FamilyManagerMainViewModel.cs  — главный ViewModel (каталог + поиск)
│   ├── FamilyImportViewModel.cs       — импорт файла/папки
│   ├── FamilyDetailsViewModel.cs      — карточка семейства
│   ├── FamilyMetadataEditViewModel.cs — редактирование metadata
│   └── FamilyCatalogItemRow.cs        — ObservableObject для строки списка
├── Views/
│   ├── FamilyManagerPaneControl.xaml/.cs — UserControl для dockable panel
│   ├── FamilyImportView.xaml/.cs      — модальный диалог импорта
│   └── FamilyMetadataEditView.xaml/.cs— модальный диалог metadata
├── Services/
│   ├── FamilyManagerDialogService.cs  — IFamilyManagerDialogService
│   ├── IFamilyManagerViewModelFactory.cs — фабрика главного VM
│   └── FamilyManagerViewModelFactory.cs
└── Events/
    └── FamilyManagerExternalEvent.cs  — IExternalEventHandler
```

#### 12E-2. Dockable Panel Infrastructure

**FamilyManagerPaneControl** — `UserControl` (не `Window`!), контент dockable панели:

```xml
<UserControl x:Class="SmartCon.FamilyManager.Views.FamilyManagerPaneControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Layout: Search + Filters + Grid + Details card -->
</UserControl>
```

```csharp
public partial class FamilyManagerPaneControl : UserControl
{
    public FamilyManagerPaneControl(FamilyManagerMainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
```

**FamilyManagerPaneProvider** — реализация `IDockablePaneProvider`:

```csharp
public class FamilyManagerPaneProvider : IDockablePaneProvider
{
    private readonly FamilyManagerPaneControl _control;

    public FamilyManagerPaneProvider(FamilyManagerPaneControl control)
    {
        _control = control;
    }

    public void SetupDockablePane(DockablePaneProviderData data)
    {
        data.FrameworkElement = _control;
        data.VisibleByDefault = false;
        data.InitialState = new DockablePaneState
        {
            DockPosition = DockPosition.Tabbed,
            TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser
        };
    }
}
```

**Регистрация** — в `App.OnStartup`:

```csharp
// В App.cs OnStartup, после ServiceLocator.Initialize:
var fmPaneId = new DockablePaneId(new Guid("<generated-guid>"));
var fmMainVm = ServiceLocator.GetService<FamilyManagerMainViewModel>();
var fmControl = new FamilyManagerPaneControl(fmMainVm);
var fmProvider = new FamilyManagerPaneProvider(fmControl);
application.RegisterDockablePane(fmPaneId, "Family Manager", fmProvider);
```

**Ribbon** — кнопка показывает панель:

```csharp
// В FamilyManagerCommand.Execute:
var paneId = new DockablePaneId(new Guid("<generated-guid>"));
var dockablePane = commandData.Application.GetDockablePane(paneId);
if (dockablePane.IsShown())
    dockablePane.Hide();
else
    dockablePane.Show();
```

#### 12E-3. FamilyManagerMainViewModel

Главный ViewModel dockable panel. **Singleton** — живёт на протяжении lifetime Revit сессии.
Не реализует `IObservableRequestClose` (это только для модальных окон).

```csharp
public sealed class FamilyManagerMainViewModel : ObservableObject
{
    // Сервисы (inject через factory):
    private readonly IFamilyCatalogProvider _catalogProvider;
    private readonly IWritableFamilyCatalogProvider _writableProvider;
    private readonly IFamilyImportService _importService;
    private readonly IFamilyFileResolver _fileResolver;
    private readonly IFamilyLoadService _loadService;
    private readonly IProjectFamilyUsageRepository _usageRepo;
    private readonly IFamilyManagerDialogService _dialogService;
    private readonly FamilyManagerExternalEvent _externalEvent;
    private readonly IRevitContext _revitContext;

    // ObservableProperty:
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private FamilyCatalogItemRow? _selectedItem;
    [ObservableProperty] private IReadOnlyList<FamilyCatalogItemRow> _items = [];
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private int _totalItemCount;
    [ObservableProperty] private bool _canLoadToProject;

    // Команды:
    [RelayCommand] private async Task SearchAsync();
    [RelayCommand] private async Task ImportFileAsync();
    [RelayCommand] private async Task ImportFolderAsync();
    [RelayCommand] private void LoadToProject();
    [RelayCommand] private void EditMetadata();
    [RelayCommand] private void Reindex();
}
```

**Критическое правило — Revit API isolation:**

`FamilyManagerMainViewModel` **не вызывает** `IFamilyLoadService` напрямую.
`LoadToProject()` ставит action в `FamilyManagerExternalEvent`:

```csharp
[RelayCommand]
private void LoadToProject()
{
    var selectedItem = SelectedItem;
    if (selectedItem is null) return;
    var versionId = selectedItem.CurrentVersionId;

    _externalEvent.Raise(app =>
    {
        // Этот код выполняется в Revit main thread (ExternalEvent.Execute)
        var ctx = _revitContext;
        var doc = ctx.GetDocument();
        if (doc is null) return;

        var resolvedFile = _fileResolver.ResolveForLoadAsync(versionId, CancellationToken.None).Result;
        var result = _loadService.LoadFamilyAsync(resolvedFile, FamilyLoadOptions.Default, CancellationToken.None).Result;
        // Записать usage, обновить UI через Dispatcher
    });
}
```

ViewModel хранит только DTO и UI state. Все Revit-операции — через `_externalEvent.Raise()`.

#### 12E-4. FamilyManagerPaneControl (UserControl)

Dockable panel контент — `UserControl` (не `Window`):

```xml
<UserControl x:Class="SmartCon.FamilyManager.Views.FamilyManagerPaneControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
```

Layout (как описано в `07-ux-ia.pplx.md`):

```
+-------------------------------------------------------------+
| Search bar                                  Import Settings  |
+----------------------+----------------------+---------------+
| Filters              | Family grid/list     | Details card  |
| - Status             | - Name               | - Preview     |
| - Category           | - Category           | - Metadata    |
| - Tags               | - Status             | - Types       |
| - Manufacturer       | - Version            | - Actions     |
+----------------------+----------------------+---------------+
| Status bar: catalog path, item count, indexing state         |
+-------------------------------------------------------------+
```

#### 12E-5. FamilyManagerDialogService

Аналог `PipeConnectDialogService`:

```csharp
public sealed class FamilyManagerDialogService : IFamilyManagerDialogService
{
    private readonly IDialogPresenter _presenter;

    // System dialogs:
    public string? ShowOpenFileDialog(...) => new OpenFileDialog(...);
    public string? ShowFolderBrowserDialog(...) => new FolderBrowserDialog(...);
    // WinForms FolderBrowserDialog допустим — аналогично PipeConnectDialogService.
    // Работает на net48 и net8.0-windows.
    public void ShowWarning(string title, string message) => TaskDialog.Show(title, message);
    public void ShowError(string title, string message) => TaskDialog.Show(title, message);

    // Custom dialogs — через presenter:
    public void ShowMetadataEdit(FamilyMetadataEditViewModel vm) => _presenter.ShowDialog(vm);
}
```

#### 12E-6. Localization

Добавить ключи в `SmartCon.UI/StringLocalization.cs`:

```csharp
// Префикс FM_ для всех ключей FamilyManager
public const string FM_SearchPlaceholder = "FM_SearchPlaceholder";
public const string FM_ImportFile = "FM_ImportFile";
public const string FM_ImportFolder = "FM_ImportFolder";
public const string FM_LoadToProject = "FM_LoadToProject";
public const string FM_EditMetadata = "FM_EditMetadata";
public const string FM_NoActiveDocument = "FM_NoActiveDocument";
public const string FM_ImportProgress = "FM_ImportProgress";
// ... ~40 ключей
```

**Acceptance:**
- [ ] MVVM Toolkit `[ObservableProperty]`, `[RelayCommand]`.
- [ ] Dockable panel контент — `UserControl`, не `Window`.
- [ ] Модальные диалоги (Import, Metadata) наследуют `DialogWindowBase`.
- [ ] `IObservableRequestClose` для модальных VM (Import, Metadata).
- [ ] RU/EN ключи добавлены.
- [ ] Empty/loading/error states реализованы.
- [ ] **No active document:** panel не падает. Load To Project disabled, показывает
  `FM_NoActiveDocument`. Каталог (search/import) работает — БД не привязана к Document.
- [ ] **Document switch:** смена активного документа не оставляет stale references.
  `CanLoadToProject` пересчитывается при смене документа (через `IRevitContext` или
  `UIApplication.ViewActivated` event).
- [ ] **Document close:** закрытие документа не crash-ит panel. Panel корректно
  переходит в "no active document" state.

---

### Phase 12F: App Integration — Ribbon + DI + Dockable Panel (1-2 дня)

**Цель:** Интегрировать FamilyManager в SmartCon с dockable panel.

#### 12F-1. RibbonBuilder

Добавить кнопку Family Manager (toggle dockable pane):

```csharp
// --- Family Manager Panel ---
var fmPanel = app.CreateRibbonPanel(TabName, "Family Manager");
var fmAssembly = Path.Combine(appDir, "SmartCon.FamilyManager.dll");

var familyManagerButton = new PushButtonData(
    name: "FamilyManager",
    text: "Family\nManager",
    assemblyName: fmAssembly,
    className: "SmartCon.FamilyManager.Commands.FamilyManagerCommand")
{
    ToolTip = "Manage Revit family library",
    LargeImage = GetEmbeddedImage("SmartCon.App.Resources.Icons.FamilyMan_32x32.png"),
    Image = GetEmbeddedImage("SmartCon.App.Resources.Icons.FamilyMan_16x16.png")
};

fmPanel.AddItem(familyManagerButton);
```

#### 12F-2. App.OnStartup — Dockable Panel Registration

Все компоненты dockable panel регистрируются через DI. OnStartup только разрешает и регистрирует pane:

```csharp
// В App.cs OnStartup, после ServiceLocator.Initialize(application):
var fmProvider = ServiceLocator.GetRequiredService<FamilyManagerPaneProvider>();
var fmPaneId = FamilyManagerPaneIds.FamilyManagerPane;
application.RegisterDockablePane(fmPaneId, "Family Manager", fmProvider);
```

**FamilyManagerPaneIds** — статический класс с `DockablePaneId`.
Лежит в `SmartCon.FamilyManager`. Требует compile-time reference на RevitAPIUI
(для `DockablePaneId`), что допустимо для UI-модуля Revit add-in.

```csharp
public static class FamilyManagerPaneIds
{
    public static readonly DockablePaneId FamilyManagerPane =
        new DockablePaneId(new Guid("<generated-guid>"));
}
```

**Dependency note:** `SmartCon.FamilyManager` как Revit UI-модуль compile-time ссылается
на RevitAPI/RevitAPIUI (для `IExternalCommand`, `IDockablePaneProvider`, `DockablePaneId`,
`TaskDialog`). Это соответствует паттерну PipeConnect/ProjectManagement. Запрещено вызывать
model/document API напрямую — только через интерфейсы Core → Revit layer.

#### 12F-3. ServiceRegistrar

Добавить регистрацию FamilyManager сервисов:

```csharp
// --- FamilyManager (Phase 12) ---
// SQLite provider + import
services.AddSingleton<LocalCatalogDatabase>();
services.AddSingleton<LocalCatalogMigrator>();
services.AddSingleton<LocalCatalogProvider>();
services.AddSingleton<IFamilyCatalogProvider>(sp => sp.GetRequiredService<LocalCatalogProvider>());
services.AddSingleton<IWritableFamilyCatalogProvider>(sp => sp.GetRequiredService<LocalCatalogProvider>());
services.AddSingleton<IFamilyImportService, LocalFamilyImportService>();
services.AddSingleton<IFamilyFileResolver, LocalFamilyFileResolver>();
services.AddSingleton<IProjectFamilyUsageRepository, LocalProjectFamilyUsageRepository>();

// Revit implementations
services.AddSingleton<IFamilyLoadService, RevitFamilyLoadService>();
// MVP: lightweight extraction без Revit API
services.AddSingleton<IFamilyMetadataExtractionService, FileNameOnlyMetadataExtractionService>();

// UI services
services.AddSingleton<IFamilyManagerDialogService, FamilyManagerDialogService>();

// Dockable panel components (singleton lifetime — вся Revit-сессия)
services.AddSingleton<FamilyManagerMainViewModel>();
services.AddSingleton<FamilyManagerPaneControl>();
services.AddSingleton<FamilyManagerPaneProvider>();

// External Event
var fmHandler = new FamilyManagerExternalEvent(revitContext);
var fmEvent = ExternalEvent.Create(fmHandler);
fmHandler.Initialize(fmEvent);
services.AddSingleton(fmHandler);
// Правило: ViewModel не вызывает Revit API. Он может только ставить действия
// в очередь через FamilyManagerExternalEvent.Raise(action).

// ViewModel Factory
services.AddSingleton<IFamilyManagerViewModelFactory, FamilyManagerViewModelFactory>();

// Dialog Presenter — маппинги для модальных диалогов FamilyManager
// (добавить к существующему WpfDialogPresenter)
// Важно: FamilyManagerMainViewModel НЕ регистрируется здесь —
// dockable panel создаётся через FamilyManagerPaneProvider, а не через presenter.
presenter.Register<FamilyMetadataEditViewModel>(vm => new FamilyMetadataEditView(vm));
presenter.Register<FamilyImportViewModel>(vm => new FamilyImportView(vm));
```

#### 12F-4. FamilyManagerCommand

```csharp
public class FamilyManagerCommand : IExternalCommand
{
    public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
    {
        var paneId = FamilyManagerPaneIds.FamilyManagerPane;
        var dockablePane = commandData.Application.GetDockablePane(paneId);
        if (dockablePane.IsShown())
            dockablePane.Hide();
        else
            dockablePane.Show();
        return Result.Succeeded;
    }
}
```

#### 12F-5. Solution Update

- Добавить `SmartCon.FamilyManager.csproj` в `SmartCon.sln`.
- Добавить `Microsoft.Data.Sqlite` в `Directory.Packages.props` (если ещё не добавлен).
- Обновить `.addin` файлы если нужно.

**Acceptance:**
- [ ] Ribbon кнопка открывает/закрывает dockable panel.
- [ ] Dockable panel регистрируется в `App.OnStartup`.
- [ ] DI разрешает все сервисы.
- [ ] Singleton ViewModel живёт на протяжении Revit-сессии.
- [ ] Panel корректно реагирует на смену активного документа.

---

### Phase 12G: Tests (2-3 дня)

**Цель:** 50+ новых тестов без регрессий.

#### 12G-1. Unit Tests (SmartCon.Tests/FamilyManager/)

```
Core/
├── FamilySearchNormalizerTests.cs     — 5 tests
├── FamilyNameNormalizerTests.cs       — 5 tests
├── FamilyCatalogQueryValidatorTests.cs — 5 tests
├── FamilyContentStatusTests.cs        — 3 tests
└── FamilyImportResultAggregatorTests.cs — 3 tests

Repository/
├── LocalCatalogMigratorTests.cs       — 5 tests
├── LocalCatalogProviderTests.cs       — 8 tests (CRUD, search, filter)
├── LocalFamilyImportServiceTests.cs   — 5 tests
├── Sha256FileHasherTests.cs           — 3 tests
└── LocalProjectFamilyUsageRepositoryTests.cs — 5 tests

ViewModels/
├── FamilyManagerMainViewModelTests.cs — 8 tests
└── FamilyMetadataEditViewModelTests.cs — 5 tests
```

#### 12G-2. Test Fixtures

- Unit tests используют **fake binary placeholders** (любые файлы с `.rfa` расширением),
  не настоящий Revit family. Это тестирует import/hash/cache logic без Revit runtime.
- Настоящий `.rfa` — только для manual smoke tests в Revit, не обязателен для CI.
- Файл с кириллицей в имени/пути — для path robustness.
- Дубликат по содержимому — для hash detection.

**Acceptance:**
- [ ] 50+ новых тестов.
- [ ] Все существующие тесты проходят (baseline на момент написания: 676+).
- [ ] `Debug.R25` test run: 0 падений.

---

### Phase 12H: Multi-Version Polish (1-2 дня)

**Цель:** Все 5 конфигураций собираются.

#### 12H-1. Работа

- Собрать `Debug.R25` — primary target.
- `dotnet restore` + `Debug.R24` — net48.
- `Debug.R21` — net48.
- `Debug.R19` — net48.
- `Debug.R26` — CI validation.
- `#if` директивы — **только** в `SmartCon.Revit/` и `SmartCon.App/`.
- Проверить, что `Microsoft.Data.Sqlite` native assets включены для обоих TFM.
- Отключить Revit metadata extraction в legacy если API не поддерживает.

#### 12H-2. Запреты

- Нет `Microsoft.Extensions.*` 9+.
- Нет `Newtonsoft.Json`.
- Нет сторонних WPF themes.
- Нет прямой ссылки `SmartCon.FamilyManager` → `SmartCon.Revit`.

**Acceptance:**
- [ ] R19/R21/R24/R25/R26: 0 ошибок, 0 предупреждений.
- [ ] `build-and-deploy.bat` — 0 ошибок.

---

### Phase 12I: Documentation and Release (1 день)

#### 12I-1. Обновить документы

- `docs/domain/models.md` — добавить FamilyManager модели.
- `docs/domain/interfaces.md` — добавить FamilyManager интерфейсы.
- `docs/roadmap.md` — добавить Phase 12.
- `docs/README.md` — обновить индекс.
- `docs/architecture/solution-structure.md` — добавить `SmartCon.FamilyManager`.
- `docs/architecture/dependency-rule.md` — обновить матрицу.
- `docs/family-manager/README.md` — обновить статус.
- `CHANGELOG.md` — добавить запись.

#### 12I-2. ADR

- `docs/adr/014-familymanager-mvp-architecture.md` — aggregate ADR с секциями:
  FM-001 (module boundary), FM-002 (dockable panel), FM-003 (SQLite), FM-004 (file cache),
  FM-006 (provider abstraction), FM-007 (project usage DB).

#### 12I-3. Release Checklist

1. Код компилируется.
2. Все тесты проходят (baseline + 50+ новых FamilyManager).
3. Manual Revit 2025 smoke.
4. Legacy builds проверены.
5. Docs обновлены.
6. ADR добавлены.
7. Risk register просмотрен.
8. Package dependency review.

---

## 5. Итоговая карта файлов

| Область | Расположение |
|---|---|
| Core модели | `SmartCon.Core/Models/FamilyManager/*.cs` |
| Core интерфейсы | `SmartCon.Core/Services/Interfaces/IFamily*.cs` |
| Core services | `SmartCon.Core/Services/FamilyManager/*.cs` |
| SQLite provider | `SmartCon.FamilyManager/Services/LocalCatalog/*.cs` |
| UI ViewModels | `SmartCon.FamilyManager/ViewModels/*.cs` |
| UI Views | `SmartCon.FamilyManager/Views/FamilyManagerPaneControl.xaml.cs` (UserControl) |
| UI Dialogs | `SmartCon.FamilyManager/Views/FamilyImportView.xaml.cs`, `FamilyMetadataEditView.xaml.cs` |
| ExternalEvent | `SmartCon.FamilyManager/Events/FamilyManagerExternalEvent.cs` |
| Dockable Provider | `SmartCon.FamilyManager/FamilyManagerPaneProvider.cs` |
| Pane IDs | `SmartCon.FamilyManager/FamilyManagerPaneIds.cs` |
| Revit load/extract | `SmartCon.Revit/FamilyManager/*.cs` |
| DI регистрация | `SmartCon.App/DI/ServiceRegistrar.cs` |
| Ribbon | `SmartCon.App/Ribbon/RibbonBuilder.cs` |
| Иконки | `SmartCon.App/Resources/Icons/FamilyMan_16x16.png`, `FamilyMan_32x32.png` |
| Локализация | `SmartCon.UI/StringLocalization.cs` (FM_* ключи) |
| Тесты | `SmartCon.Tests/FamilyManager/**/*.cs` |
| ADR | `docs/adr/014-familymanager-mvp-architecture.md` |

---

## 6. Минимальный slice для первого вертикального прототипа

Если нужно максимально быстро доказать, что архитектура работает:

1. `FamilyCatalogItem` + `FamilyContentStatus` (Core model).
2. `IFamilyCatalogProvider` + `IFamilyImportService` (Core interface).
3. `LocalCatalogDatabase` + `001_initial.sql` (SQLite).
4. `LocalFamilyImportService` — импорт одного `.rfa` (только name + path + hash).
5. `FamilyManagerPaneControl` — `UserControl` с минимальным layout (список + search).
6. `FamilyManagerPaneProvider` — `IDockablePaneProvider`.
7. `FamilyManagerCommand` + Ribbon кнопка (toggle dockable pane).
8. Регистрация в `App.OnStartup` + `ServiceRegistrar`.

Это даёт: клик по Ribbon → dockable panel → импорт `.rfa` → видишь в списке → поиск.

Остальное (загрузка в проект, metadata extraction, batch import, tests, multi-version)
наращивается поверх этого прототипа.

---

## 7. Открытые вопросы для утверждения

| # | Вопрос | Блокирует | Рекомендация |
|---|---|---|---|
| 1 | Иконки FamilyManager — проверить до merge | Нет | Проверить `FamilyMan_16x16.png` / `FamilyMan_32x32.png` в `SmartCon.App/Resources/Icons/`. Убедиться, что embedded resource name в `RibbonBuilder` точно совпадает. |
| 2 | Storage mode в MVP: Linked или Cached? | Нет | **Cached** — `.rfa` копируются в `%APPDATA%\AGK\SmartCon\FamilyManager\cache\rfa\` при импорте. Original path сохраняется, но загрузка идёт из кэша. |
| 3 | Preview generation: обязателен или accept "unavailable"? | Нет | Accept "Preview unavailable" в MVP |
| 4 | Связь с PipeConnect: нужна в MVP или позже? | Нет | Позже (Phase 3 по strategy roadmap) |
| 5 | Минимальный набор metadata без EditFamily? | Нет | Имя файла, размер, hash, дата, расширение |
| 6 | Legacy Revit: что отключать? | Нет | Отключить metadata extraction через EditFamily |

---

## 8. Сроки (оценка)

| Фаза | Дни | Зависимость |
|---|---|---|
| 12A: Architecture Spike | 1-2 | — |
| 12B: Core Models + Interfaces | 2-3 | 12A |
| 12C: SQLite Provider | 3-4 | 12B |
| 12D: Revit Layer | 2-3 | 12B |
| 12E: UI Module | 4-5 | 12C, 12D |
| 12F: App Integration | 1-2 | 12E |
| 12G: Tests | 2-3 | 12E, 12F |
| 12H: Multi-Version | 1-2 | 12G |
| 12I: Docs + Release | 1 | 12H |
| **Итого** | **17-25 дней** | |

Фазы 12D и 12C можно делать параллельно (после 12B).
Фазы 12E и 12G частично параллельны (тесты на Core пишутся до UI).
