# FamilyManager для smartCon: технический стек

## 1. Короткое решение

FamilyManager нужно проектировать как `net8-first` модуль для Revit 2025+ с legacy-совместимостью smartCon для Revit 2019–2024 через `net48`, а не как модуль, ограниченный минимальным общим знаменателем. Revit 2025/2026 API использует .NET 8, а legacy-линия smartCon для Revit 2019–2024 собирается на .NET Framework 4.8 ([Autodesk Development Requirements](https://help.autodesk.com/view/RVT/2025/ENU/?guid=Revit_API_Revit_API_Developers_Guide_Introduction_Getting_Started_Welcome_to_the_Revit_Platform_API_Development_Requirements_html)).

Главное техническое решение: клиентский FamilyManager должен оставаться лёгким Revit add-in модулем, не тащить тяжёлый application framework, не вводить второй UI framework и не обходить существующие smartCon-инварианты. MVP-стек: `CommunityToolkit.Mvvm`, MEDI 8.x, собственный smartCon ExternalEvent-паттерн, `Microsoft.Data.Sqlite 8.x`, Dapper или ручной ADO.NET, простые SQL-миграции, SQLite FTS5 после smoke-проверки, WPF/SmartCon.UI без сторонних тем, `System.Text.Json 8.x`, SmartConLogger, xUnit/Moq.

Серверный стек нужно отделить от Revit-клиента. Для corporate/self-hosted фаз рекомендуемый сервер: ASP.NET Core 8, REST JSON API, PostgreSQL через Npgsql/EF Core 8, object storage S3/MinIO, PostgreSQL full-text search сначала, OpenSearch только в enterprise-фазе, JWT/OIDC-compatible auth, background jobs через `BackgroundService`/Channels сначала и Quartz/Hangfire позже.

## 2. Платформенная рамка

| Область | Решение |
| --- | --- |
| Основная целевая платформа | Revit 2025+ / `net8.0-windows` |
| Legacy-платформа | Revit 2019–2024 / `net48` |
| Язык | C# 12, но shared-код должен проходить multi-target build |
| UI | WPF внутри Revit dockable panel |
| Архитектура | Clean Architecture по текущим правилам smartCon |
| Revit API | Только через `SmartCon.Revit`, `IExternalEventHandler` и `ITransactionService` |
| Клиентское хранилище | SQLite + файловый кэш |
| Серверное хранилище | PostgreSQL + object storage |
| API | REST JSON first |
| Поиск | SQLite FTS5 локально, PostgreSQL FTS серверно, OpenSearch позже |

## 3. Текущий стек smartCon

Существующий smartCon уже задаёт правильный каркас для FamilyManager:

- multi-target через конфигурации `R19/R21/R22/R23/R24/R25/R26`;
- `R25/R26` собираются как `net8.0-windows`;
- `R19/R21/R22/R23/R24` собираются как `net48`;
- `CommunityToolkit.Mvvm 8.4.2`;
- `Microsoft.Extensions.DependencyInjection 8.0.1`;
- `System.Text.Json 8.0.6`;
- `xunit`, `Moq`, `coverlet`;
- `Nice3point.Revit.Api.*` как compile-time Revit API references;
- собственный `SmartConLogger`;
- собственный `ActionExternalEventHandler`;
- ExtensibleStorage уже используется в smartCon другими модулями, но не входит в data plane FamilyManager;
- WPF-инфраструктура в `SmartCon.UI`;
- central package management через `Directory.Packages.props`.

Решение для FamilyManager: не заменять этот стек, а добавить только те зависимости, без которых нельзя нормально сделать локальный каталог, поиск, миграции, кэш и будущий remote provider.

## 4. Принцип выбора библиотек

1. Если библиотека нужна только серверу, она не должна попадать в Revit add-in.
2. Если библиотека нужна клиенту, она должна либо поддерживать `net48`, либо быть изолирована в `net8`-only implementation.
3. Любая зависимость `Microsoft.Extensions.*` выше 8.x запрещена в Revit-клиенте, потому что smartCon держит MEDI 8.x для legacy compatibility.
4. Любая библиотека с native assets должна пройти smoke build/install в Revit 2025 и хотя бы legacy build для R24/R21/R19.
5. UI-библиотеки не добавлять без крайней необходимости: WPF ResourceDictionary и Revit add-in loading слишком чувствительны к конфликтам.
6. Revit async wrapper библиотеки можно изучать как open-source reference, но не подключать в MVP, потому что smartCon уже имеет утверждённый ExternalEvent-паттерн.
7. MVP должен иметь минимальный dependency footprint.

## 5. Client MVP stack

| Категория | Recommended | Legacy fallback | Alternative | Avoid |
| --- | --- | --- | --- | --- |
| MVVM | `CommunityToolkit.Mvvm 8.4.2` | тот же пакет | нет смысла менять | ReactiveUI, DynamicData |
| DI | `Microsoft.Extensions.DependencyInjection 8.0.1` | тот же пакет | собственная фабрика для узких мест | MEDI 9/10 в клиенте |
| JSON | `System.Text.Json 8.0.6` | тот же пакет | source-generated serializers | Newtonsoft.Json, MessagePack |
| Revit async/API bridge | smartCon `ActionExternalEventHandler` + специализированные handlers | тот же паттерн | Revit.Async как reference | прямые API-вызовы из WPF |
| UI | WPF + `SmartCon.UI` + собственные стили | тот же UI | отдельные lightweight controls вручную | MaterialDesignThemes, MahApps, HandyControl |
| Local DB | `Microsoft.Data.Sqlite 8.x` | тот же пакет, если native assets проходят build | LiteDB только как fallback-прототип | EF Core в Revit-клиенте |
| Data access | Dapper 2.x или ручной ADO.NET | Dapper поддерживает старые TFMs | sqlite-net-pcl | тяжёлый ORM в MVP |
| Migrations | собственный SQL migrator + `schema_version` | тот же механизм | FluentMigrator 8.x | EF migrations в add-in |
| Local search | SQLite `LIKE` + normalized tokens → SQLite FTS5 | тот же SQL, FTS5 после проверки | Lucene.NET для Phase 3+ | OpenSearch внутри клиента |
| Fuzzy search | простой in-house scoring | тот же код | FuzzySharp как optional | ML/AI в MVP |
| File cache | BCL `System.IO` + SHA256 | compatibility helper | content-addressed store | внешние blob SDK в клиенте |
| Image preview | WPF `BitmapImage`, `RenderTargetBitmap`, `PngBitmapEncoder` | тот же WPF | ImageSharp для offline image processing | SkiaSharp в клиенте |
| Credentials | abstraction only в MVP; Meziantou CredentialManager в remote phase | тот же через net462/netstandard | AdysTech CredentialManager | токены в JSON |
| HTTP | `HttpClient` + STJ | `System.Net.Http` package for net48 | typed facade | RestSharp без нужды |
| Background indexing | `Task`, `CancellationToken`, bounded queue | simple queue / optional Channels package | `System.Threading.Channels` | Hangfire/Quartz в клиенте |
| Logging | `SmartConLogger` | тот же | адаптер к ILogger только серверно | Serilog/NLog в клиенте |
| Testing | xUnit + Moq | net8 tests + legacy build smoke | integration tests on temp SQLite | тесты только руками |

## 6. Новые клиентские пакеты

### 6.1. Microsoft.Data.Sqlite

Рекомендация: добавить `Microsoft.Data.Sqlite 8.x` как основной клиентский provider для локальной БД FamilyManager. `Microsoft.Data.Sqlite` является lightweight ADO.NET provider для SQLite и может использоваться напрямую без EF Core ([Microsoft Learn: Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/)).

Почему:

- SQLite идеально подходит для локального каталога metadata;
- база переносима и проста для backup;
- SQL удобен для фильтров, индексов и будущего FTS;
- пакет совместим с `.NET Standard 2.0`, а NuGet показывает computed compatibility для `net48` и совместимость с современными .NET TFMs ([NuGet: Microsoft.Data.Sqlite 8.0.10](https://www.nuget.org/packages/Microsoft.Data.Sqlite/8.0.10)).

Как использовать:

- хранить БД в `%APPDATA%\AGK\SmartCon\FamilyManager\familymanager.db`;
- включить WAL mode;
- создать таблицы `schema_info`, `catalog_items`, `catalog_versions`, `family_files`, `family_types`, `family_parameters`, `catalog_tags`, `attachments`, `project_usage`, `previews`;
- файлы `.rfa` и превью хранить не в БД, а в content-addressed file cache;
- metadata хранить в нормализованных таблицах, а raw extracted metadata можно хранить отдельным JSON-полем.

Риски:

- native SQLite assets должны корректно копироваться в bundle Revit add-in;
- нужно smoke-тестировать загрузку пакета в Revit 2025 и legacy сборки;
- FTS5 нужно включать только после runtime-проверки доступности.

### 6.2. Dapper

Рекомендация: использовать Dapper только если ручной ADO.NET начнёт раздувать repository-код. Dapper является micro-ORM для SQL Server, MySQL, SQLite и других баз, а NuGet показывает совместимость с `netstandard` и старыми .NET Framework target frameworks ([NuGet: Dapper](https://www.nuget.org/packages/dapper/1.50.2)).

Почему не EF Core в клиенте:

- EF Core в Revit add-in увеличивает dependency surface;
- migrations EF Core плохо сочетаются с простым локальным каталогом;
- EF Core в клиенте усложнит multi-target и конфликт `Microsoft.Extensions.*`;
- для FamilyManager MVP достаточно SQL-запросов и repository layer.

Решение:

- MVP: начать с `Microsoft.Data.Sqlite` + ручной SQL;
- если SQL mapping станет шумным, добавить Dapper;
- EF Core оставить для server-side.

### 6.3. Миграции БД

Рекомендация: для клиентской SQLite БД сделать собственный minimal migrator.

Формат:

```text
/SmartCon.FamilyManager/Services/LocalCatalog/Migrations/
  001_initial.sql
  002_add_search_index.sql
  003_add_project_usage.sql
```

Таблица:

```sql
CREATE TABLE schema_info (
    key TEXT PRIMARY KEY,
    value TEXT NOT NULL
);
```

Для MVP достаточно ключа `schema_version`; детальный журнал применённых миграций можно добавить позже отдельным ADR, если он реально понадобится.

Почему не FluentMigrator по умолчанию: FluentMigrator является полноценной open-source библиотекой для versioned database schema changes и поддерживает `net48`/современные .NET TFMs, но для embedded Revit add-in это лишний runtime footprint на старте ([NuGet: FluentMigrator 8.0.1](https://www.nuget.org/packages/fluentmigrator)).

Когда взять FluentMigrator:

- если появится несколько провайдеров БД;
- если миграций станет много;
- если потребуется единый migration engine для клиента и сервера.

### 6.4. SQLite FTS5

Рекомендация: Phase 1 начать с нормализованного поиска по токенам, Phase 2 включить SQLite FTS5 для полнотекстового поиска. SQLite FTS5 является virtual table module для full-text search и поддерживает `MATCH` queries ([SQLite FTS5](https://www.sqlite.org/fts5.html)).

Применение:

- `family_search_fts(name, description, manufacturer, tags, parameter_text)`;
- обновлять FTS index при импорте/изменении metadata;
- fallback на `LIKE` если FTS5 недоступен в runtime;
- отдельный smoke test: `CREATE VIRTUAL TABLE test_fts USING fts5(x);`.

Почему не Lucene.NET в MVP: Lucene.NET мощнее, но текущая 4.8 линия остаётся beta, хотя NuGet показывает совместимость с `net8.0` и `net462` для связанных пакетов ([NuGet: Lucene.Net.Analysis.Common](https://www.nuget.org/packages/Lucene.Net.Analysis.Common/)). Lucene.NET лучше держать как Phase 3+ alternative, если SQLite FTS перестанет хватать.

### 6.5. Fuzzy search

Рекомендация: сначала реализовать простое ранжирование внутри FamilyManager:

- exact match;
- starts with;
- contains;
- token overlap;
- parameter match boost;
- verified boost;
- recently used boost.

FuzzySharp можно рассмотреть как optional library, потому что пакет является fuzzy string matcher и имеет no-dependency target для .NET Framework 4.6 ([NuGet: FuzzySharp](https://www.nuget.org/packages/FuzzySharp)). Но для MVP лучше не добавлять зависимость, пока не будет понятен реальный search UX.

### 6.6. Credentials

Рекомендация: для remote provider фаз добавить `ICredentialStore` abstraction и реализацию через Windows Credential Manager. `Meziantou.Framework.Win32.CredentialManager` имеет targets для .NET 8, .NET Standard 2.0 и .NET Framework 4.6.2, а также оборачивает Windows Credential Store API ([NuGet: Meziantou.Framework.Win32.CredentialManager](https://www.nuget.org/packages/Meziantou.Framework.Win32.CredentialManager)).

Применение:

- хранить access token/PAT для remote catalog;
- target name: `AGK.SmartCon.FamilyManager:{serverId}:{userId}`;
- settings JSON хранит только server URL и credential key;
- токен не хранится в `%APPDATA%` plaintext.

Alternative: `AdysTech.CredentialManager` также поддерживает .NET 8 и .NET Standard 2.0 и работает с Windows Credential Store ([NuGet: AdysTech.CredentialManager](https://www.nuget.org/packages/AdysTech.CredentialManager)).

## 7. Локальная БД: рекомендуемая схема

Минимальная схема MVP должна совпадать с `05-metadata-schema.pplx.md`; этот документ является каноническим источником таблиц и колонок.

```text
schema_info
  key
  value

catalog_items
  id
  provider_id
  name
  normalized_name
  description
  category_name
  manufacturer
  status
  current_version_id
  created_at_utc
  updated_at_utc

catalog_versions
  id
  catalog_item_id
  file_id
  version_label
  sha256
  revit_major_version
  types_count
  parameters_count
  imported_at_utc

family_files
  id
  original_path
  cached_path
  file_name
  size_bytes
  sha256
  last_write_time_utc
  storage_mode

family_types
  id
  version_id
  name
  sort_order

family_parameters
  id
  version_id
  type_id
  name
  storage_type
  value_text
  is_instance
  is_readonly
  forge_type_id

catalog_tags
  catalog_item_id
  tag
  normalized_tag

attachments
  id
  catalog_item_id
  attachment_type
  display_name
  relative_path_or_url
  sha256
  created_at_utc

project_usage
  id
  catalog_item_id
  version_id
  provider_id
  project_fingerprint
  revit_project_path_hash
  action
  created_at_utc

previews
  id
  catalog_item_id
  version_id
  relative_path
  width
  height
  created_at_utc
```

Правило: SQLite хранит индекс и metadata, но не становится blob dump. `.rfa`, preview PNG и attachments лучше хранить в файловом кэше, а в БД держать пути, hash и metadata. Термин `attachments` предпочтительнее `documents`, чтобы не конфликтовать с `Autodesk.Revit.DB.Document`.

## 8. Файловый кэш

Рекомендуемая структура:

```text
%APPDATA%\AGK\SmartCon\FamilyManager\
  familymanager.db
  cache\
    rfa\
      sha256-prefix\
        {sha256}.rfa
    previews\
      sha256-prefix\
        {sha256}.png
    attachments\
      sha256-prefix\
        {sha256}.pdf
  logs\
  temp\
```

Почему:

- hash-addressed storage устраняет дубликаты;
- легко чистить orphan files;
- БД остаётся маленькой;
- можно переиспользовать один файл в нескольких версиях/каталогах;
- серверный remote provider потом сможет использовать тот же hash.

Хэширование:

- `SHA256` из BCL;
- в `net8` можно использовать современные API;
- для `net48` сделать compatibility helper.

## 9. Preview и изображения

Рекомендация: использовать WPF imaging и Revit preview API без сторонних image-библиотек в MVP. WPF `BitmapImage` и `Image` являются стандартным способом отображать изображения в WPF ([Microsoft Learn: BitmapImage](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-use-a-bitmapimage)).

Для генерации заглушек и простых bitmap можно использовать `RenderTargetBitmap`, который рендерит WPF `Visual` в bitmap ([Microsoft Learn: RenderTargetBitmap](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/graphics-multimedia/how-to-create-a-bitmap-from-a-visual)).

Почему не ImageSharp в MVP: ImageSharp является полноценной managed 2D graphics library и поддерживает `netstandard2.0`/современные TFMs, но она нужна только если появится реальная обработка изображений вне WPF/Revit ([NuGet: SixLabors.ImageSharp](https://www.nuget.org/packages/SixLabors.ImageSharp/2.1.13)).

Почему не SkiaSharp в клиенте: SkiaSharp имеет native assets и известные build/runtime нюансы в multi-target desktop проектах, поэтому в Revit add-in лучше не добавлять его без сильной причины ([GitHub: SkiaSharp issue with SDK-style net48](https://github.com/mono/SkiaSharp/issues/2450)).

## 10. WPF UI stack

Рекомендация: не подключать стороннюю WPF theme library в FamilyManager MVP.

Использовать:

- `SmartCon.UI`;
- `DialogWindowBase`;
- `WpfDialogPresenter`;
- `LanguageManager`;
- `StringLocalization`;
- существующий `Generic.xaml`;
- собственные `UserControl` для dockable panel;
- `DataGrid`, `TreeView`, `ListView`, `ItemsControl`, `CollectionViewSource`;
- `VirtualizingStackPanel`;
- встроенные WPF converters;
- embedded SVG/vector icons или lightweight path icons.

Почему не MaterialDesignThemes/MahApps:

- MaterialDesignThemes совместим с `net8.0-windows7.0` и `net462`, но тащит ResourceDictionary-heavy styling, что рискованно для Revit add-in с существующим `SmartCon.UI` ([NuGet: MaterialDesignThemes](https://www.nuget.org/packages/MaterialDesignThemes/)).
- MahApps.Metro совместим со старыми .NET Framework TFMs и современными computed TFMs, но добавляет отдельную стилистическую систему поверх smartCon UI ([NuGet: MahApps.Metro](https://www.nuget.org/packages/mahapps.metro/)).

Допустимые исключения:

- если позже будет отдельный design-system ADR;
- если UI smartCon будет целиком переводиться на одну WPF theme library;
- если библиотека подключается только в `SmartCon.UI`, а не внутри FamilyManager.

## 11. Revit API и async stack

Рекомендация: использовать существующий smartCon ExternalEvent-паттерн, а не подключать Revit.Async/Nice3point Toolkit в MVP.

Причина: Revit API остаётся main-thread API, а modeless WPF/dockable panel должен вызывать Revit через `IExternalEventHandler`. Revit.Async решает ту же проблему через task-based wrapper над ExternalEvent и подчёркивает, что “async” не означает выполнение Revit API на background thread ([GitHub: Revit.Async](https://github.com/KennanChan/Revit.Async)).

Nice3point.Revit.Toolkit также содержит AsyncExternalEvent и современные Revit API abstractions, но это скорее reference для идей, чем зависимость для smartCon, где уже есть собственный `ActionExternalEventHandler` и инварианты ([GitHub: Nice3point.RevitToolkit](https://github.com/Nice3point/RevitToolkit)).

Решение:

- оставить `ActionExternalEventHandler`;
- при необходимости сделать `FamilyManagerExternalEvent`;
- добавить typed request/result queue только внутри smartCon;
- все Revit API операции держать в `SmartCon.Revit`;
- `EditFamily`, `LoadFamily`, `PlaceInstance`, `ReplaceType`, `UpdateFromLibrary` выполнять только через approved service layer.

## 12. Client project layout

Рекомендуемые проекты:

```text
SmartCon.Core
  Models/FamilyManager/
  Services/Interfaces/FamilyManager/
  Services/FamilyManager/
    FamilyMetadataNormalizer.cs
    FamilyCatalogQueryBuilder.cs

SmartCon.Revit
  FamilyManager/
    RevitFamilyMetadataExtractor.cs
    RevitFamilyLoadService.cs
    RevitFamilyPreviewService.cs
    RevitFamilyProjectScanner.cs

SmartCon.FamilyManager
  Commands/
  ViewModels/
  Views/
  Services/
    LocalCatalog/
      LocalCatalogProvider.cs
      LocalCatalogMigrator.cs
      FamilyCatalogSql.cs
      Migrations/
    Search/
      FamilySearchNormalizer.cs
      FamilySearchQueryBuilder.cs
  Events/
    FamilyManagerExternalEvent.cs

SmartCon.Tests
  FamilyManager/
```

Важное правило: если `SmartCon.FamilyManager` начнёт ссылаться на `SmartCon.Revit`, архитектура сломана. Модуль UI должен зависеть от Core/UI, а Revit implementations должны регистрироваться в composition root.

## 13. Server stack

Server-side стек не обязан поддерживать `net48`, потому что он не загружается в Revit. Это позволяет использовать полноценный .NET 8 backend.

| Категория | Recommended | Alternative | Avoid на старте |
| --- | --- | --- | --- |
| Runtime | ASP.NET Core 8 | ASP.NET Core 9/10 позже | смешивать server runtime с Revit add-in |
| API | REST JSON + OpenAPI | gRPC later for internal services | gRPC first для Revit legacy client |
| DB | PostgreSQL + EF Core/Npgsql 8 | Dapper + SQL | SQLite как основной enterprise DB |
| Object storage | S3-compatible MinIO | Azure Blob/S3 native | хранить `.rfa` в PostgreSQL bytea |
| Search | PostgreSQL FTS + trigram | OpenSearch | отдельный search cluster для MVP |
| Auth | JWT + OIDC-compatible design | Keycloak/Entra ID integration | custom password crypto без стандартов |
| Jobs | `BackgroundService` + Channels | Quartz/Hangfire | cron-like logic inside controllers |
| Observability | ASP.NET Core logging + health checks | OpenTelemetry later | client logger на сервере |

Npgsql EF Core provider является open-source EF Core provider для PostgreSQL и поддерживает .NET 8 ([NuGet: Npgsql.EntityFrameworkCore.PostgreSQL 8.0.10](https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/8.0.10)).

MinIO .NET SDK предназначен для MinIO и S3-compatible object storage и поддерживает .NET 8 ([NuGet: Minio](https://www.nuget.org/packages/Minio/)).

OpenSearch имеет low-level и high-level .NET clients; OpenSearch.Net предоставляет low-level communication layer, а OpenSearch.Client даёт strongly typed interface ([OpenSearch .NET clients](https://docs.opensearch.org/latest/clients/OSC-dot-net/)).

## 14. Server DB model

Серверная БД должна расширять локальную модель, а не быть другой системой.

Основные таблицы должны расширять локальную модель, а не переименовывать её:

- organizations;
- users;
- spaces;
- libraries;
- catalog_items;
- catalog_versions;
- family_files;
- family_types;
- family_parameters;
- catalog_tags;
- attachments;
- workflow_states;
- review_requests;
- audit_log;
- usage_events;
- api_tokens.

Object storage:

- `.rfa`;
- previews;
- attachments;
- exported packages;
- thumbnails.

PostgreSQL хранит metadata и ссылки на object keys, а не большие файлы.

## 15. Search strategy

### Client

Phase 1:

- normalized columns;
- SQL indexes;
- `LIKE`;
- token table;
- ranking in C#.

Phase 2:

- SQLite FTS5;
- parameter text index;
- tag boost;
- usage boost.

Phase 3+:

- Lucene.NET only if SQLite FTS is insufficient.

### Server

Phase 5:

- PostgreSQL full-text search;
- trigram similarity;
- indexes by organization/library/status/category.

Phase 7:

- OpenSearch for enterprise-scale semantic/filter search;
- PostgreSQL remains source of truth;
- search index is derived and rebuildable.

## 16. Open-source references

| Проект | Что изучить | Подключать как dependency? |
| --- | --- | --- |
| Revit.Async | Task-based wrapper над ExternalEvent и возврат результата в modeless UI ([GitHub: Revit.Async](https://github.com/KennanChan/Revit.Async)) | Нет, использовать как reference |
| Nice3point.RevitToolkit | AsyncExternalEvent, Revit context helpers, modern add-in patterns ([GitHub: Nice3point.RevitToolkit](https://github.com/Nice3point/RevitToolkit)) | Нет в MVP |
| ricaun.Revit.Templates | Multi-version шаблоны Revit add-in и структура проектов ([GitHub: ricaun.Revit.Templates](https://github.com/ricaun-io/ricaun.Revit.Templates)) | Нет, только reference |
| LiteDB | Embedded NoSQL document DB, single DLL, .NET Framework/.NET Standard support ([LiteDB](https://www.litedb.org)) | Только fallback/prototype |
| sqlite-net-pcl | Lightweight SQLite ORM for .NET/Mono/Xamarin, `netstandard2.0` compatible ([NuGet: sqlite-net-pcl](https://www.nuget.org/packages/sqlite-net-pcl/)) | Нет, если выбран Dapper/manual SQL |
| Lucene.NET | Full-text search engine library ([NuGet: Lucene.Net](https://www.nuget.org/packages/lucene.net/)) | Только Phase 3+ |

## 17. Avoid list

### Client/Revit add-in

| Не использовать | Почему |
| --- | --- |
| `Microsoft.Extensions.*` 9/10 | ломает текущую legacy-совместимость smartCon |
| EF Core в Revit-клиенте | тяжёлый dependency surface для локального каталога |
| Newtonsoft.Json | smartCon уже стандартизирован на STJ |
| MessagePack | не нужен для metadata и усложнит compatibility |
| AutoMapper | лишняя магия для небольшого домена |
| MediatR | лишний application framework внутри add-in |
| ReactiveUI/Rx/DynamicData | слишком тяжело для текущего MVVM-стиля |
| Serilog/NLog | конфликтует с существующим SmartConLogger в клиенте |
| MaterialDesignThemes/MahApps/HandyControl | риск ResourceDictionary/theme конфликтов |
| WinUI/WindowsAppSDK | не подходит для Revit WPF add-in |
| Revit.Async as dependency | дублирует существующий ExternalEvent-паттерн |
| OpenSearch client в Revit add-in | search cluster не должен быть клиентской зависимостью |
| MinIO/S3 SDK в Revit add-in | клиент должен работать через FamilyManager API |
| plaintext token storage | security risk |

### Server

| Не использовать | Почему |
| --- | --- |
| SQLite как enterprise DB | не подходит как основная multi-user БД |
| хранение `.rfa` в PostgreSQL bytea | усложняет backup, streaming, CDN и storage lifecycle |
| custom auth без OIDC/JWT-compatible модели | enterprise-клиенты потребуют стандартную интеграцию |
| OpenSearch в первой серверной версии | преждевременная инфраструктурная сложность |

## 18. Фазирование зависимостей

### Phase 0: Architecture spike

Добавлять:

- ничего нового, кроме тестовых spike branch experiments.

Проверить:

- загрузка `Microsoft.Data.Sqlite` в Revit 2025;
- legacy build R24/R21/R19;
- native asset copy;
- SQLite FTS5 runtime availability.

### Phase 1: Local Catalog MVP

Добавить в `Directory.Packages.props`:

- `Microsoft.Data.Sqlite` 8.x;
- возможно `Dapper` 2.x, если будет выбран micro-ORM;
- возможно `System.Threading.Channels` 8.x только если нужен единый async queue для net48.

Не добавлять:

- EF Core;
- UI theme libraries;
- remote auth packages;
- Lucene.NET;
- ImageSharp.

### Phase 2: Rich Metadata Search

Добавить только если нужно:

- FuzzySharp или in-house fuzzy scoring;
- SQLite FTS5 migrations;
- metadata import/export helpers без Excel dependency.

### Phase 3: Project Integration

Добавлять:

- ничего внешнего по умолчанию;
- развивать Revit services для анализа активного проекта и записывать usage history в локальную/серверную БД через provider/application layer.

### Phase 4: Team / Shared Catalog

Добавлять:

- ничего внешнего по умолчанию для MVP;
- `SharedFolderCatalogProvider` только после отдельного ADR;
- optimistic concurrency через hash/version columns;
- simple read/write roles в metadata, без enterprise auth.

Не добавлять:

- серверную БД внутри Revit client;
- сетевые locks как основной механизм консистентности;
- credentials/auth пакеты до remote/server phase.

### Phase 5: Remote Provider

Добавить в клиент:

- `Meziantou.Framework.Win32.CredentialManager`;
- HTTP provider на `HttpClient`;
- retry logic вручную или lightweight policy без Polly на старте.

Добавить на сервер:

- ASP.NET Core 8;
- EF Core 8;
- Npgsql EF Core provider;
- PostgreSQL;
- MinIO/S3 SDK;
- OpenAPI tooling.

### Phase 7: Enterprise

Добавлять:

- OpenSearch;
- OpenTelemetry;
- Quartz/Hangfire;
- OIDC provider integration;
- admin UI stack.

## 19. ADR backlog

Перед реализацией нужны ADR:

1. `ADR-FM-001 FamilyManager module boundary`
2. `ADR-FM-002 Dockable panel lifecycle`
3. `ADR-FM-003 Local SQLite catalog`
4. `ADR-FM-004 File cache strategy`
5. `ADR-FM-005 Metadata extraction levels`
6. `ADR-FM-006 Provider abstraction`
7. `ADR-FM-007 Project usage database storage`
8. `ADR-FM-008 Search strategy`
9. `ADR-FM-009 Security and credentials`
10. `ADR-FM-010 Legacy mode`

## 20. Итоговая рекомендация

Для первой технической реализации FamilyManager нужно добавить минимум новых зависимостей:

```text
Client MVP:
  Microsoft.Data.Sqlite 8.x
  Dapper 2.x                 optional
  System.Threading.Channels  optional, only if needed for net48 indexing queue

Remote client phase:
  Meziantou.Framework.Win32.CredentialManager

Server phase:
  ASP.NET Core 8
  Microsoft.EntityFrameworkCore 8
  Npgsql.EntityFrameworkCore.PostgreSQL 8
  Minio 7.x
  OpenSearch.Client later
  Quartz/Hangfire later
```

Всё остальное должно строиться на уже существующем стеке smartCon: MVVM Toolkit, MEDI 8.x, STJ 8.x, SmartConLogger, SmartCon.UI, ExternalEvent, `ITransactionService` и текущая build/release матрица. ExtensibleStorage остаётся паттерном существующих модулей smartCon, но для FamilyManager каталог, metadata, версии, usage history, избранное и search index хранятся только в локальной или серверной БД.

Главная идея стека: Revit-клиент должен быть максимально стабильным и лёгким, а вся тяжёлая enterprise-инфраструктура должна жить на сервере или за provider abstraction.
