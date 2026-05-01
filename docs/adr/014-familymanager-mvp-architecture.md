# ADR-014: FamilyManager MVP Architecture

**Status:** superseded by ADR-015
**Date:** 2026-04-28

## Context

FamilyManager — новый модуль для управления семействами Revit (BIM content management). Модуль должен работать как dockable panel, управлять локальным каталогом семейств, импортом/загрузкой в проект и отслеживанием использования.

**Ключевые ограничения:**
- I-05: Нельзя хранить `Element`/`Connector` между транзакциями
- I-09: Core не вызывает Revit API
- I-10: MVVM строго, `.xaml.cs` содержит только `DataContext = viewModel`
- Multi-version: поддержка Revit 2019-2026

## Decisions

### FM-001: Module boundary

`SmartCon.FamilyManager` — отдельный модуль (свой `.csproj`), зависит только от `SmartCon.Core` и `SmartCon.UI`. **НЕ зависит** от `SmartCon.Revit`.

- Все интерфейсы для работы с Revit API — в `SmartCon.Core`
- Реализации — в `SmartCon.Revit` (регистрируются в `ServiceRegistrar`)
- Модуль следует тому же паттерну, что и `SmartCon.PipeConnect` и `SmartCon.ProjectManagement`

### FM-002: Dockable Panel

UI реализован как Dockable Panel через `IDockablePaneProvider`:
- Singleton ViewModel (`FamilyManagerViewModel`) — один экземпляр на сессию Revit
- Ribbon toggle button — показать/скрыть панель
- Panel state (visible/hidden) сохраняется между сессиями через `DockablePaneState`

### FM-003: Local SQLite

Для MVP используется локальная SQLite БД:
- `Microsoft.Data.Sqlite` 8.x ( frozen, см. Dependabot-правила)
- Файл: `%APPDATA%\SmartCon\FamilyManager\familymanager.db`
- Схема: `schema_info`, `catalog_items`, `catalog_versions`, `family_files`, `project_usage`
- Миграции: ручные (sql-скрипты в ресурсах), не EF Migrations

### FM-004: File Cache (Cached)

Физические `.rfa` файлы копируются в управляемый кэш:
- Кэш: `%APPDATA%\SmartCon\FamilyManager\Cache\`
- В БД хранится `cached_path` как относительный путь (от root FamilyManager)
- При импорте: копировать оригинал → кэш, записать метаданные в БД
- Удаление из каталога = удаление записи из БД + файла из кэша

### FM-005: No live Revit objects in ViewModel

ViewModel **строго** не содержит `Document`, `Element`, `Family` или другие Revit-типы:
- Все операции с `Document` — только внутри `IExternalEventHandler.Execute()`
- `Document` получается через `IRevitContext` в момент выполнения ExternalEvent
- ViewModel работает только с `ElementId` (I-05) и доменными моделями

### FM-006: Provider abstraction

Каталог абстрагирован через провайдеры:
- `IFamilyCatalogProvider` — чтение каталога (поиск, фильтрация, получение версий)
- `IWritableFamilyCatalogProvider` — запись (добавление, обновление, удаление)
- В MVP: `LocalCatalogProvider` — реализация над локальной SQLite
- Post-MVP: `RemoteCatalogProvider`, `CorporateCatalogProvider`, `CompositeCatalogProvider`

### FM-007: Project usage DB

История использования семейств в проектах:
- `IProjectFamilyUsageRepository` — CRUD для usage-записей
- Хранится в той же SQLite БД (таблица `project_usage`)
- **Не привязана** к `Document` или `ExtensibleStorage` (ADR-FM-001 запрет)
- Поля: `project_name`, `family_id`, `used_at`, `revit_version`

## Consequences

**Плюсы:**
- FamilyManager не нарушает I-09 (Core не зависит от Revit API вызовов)
- FamilyManager не нарушает I-05 (нет хранения Revit-объектов в ViewModel)
- Единая архитектурная модель с PipeConnect и ProjectManagement
- Провайдерная абстракция позволяет заменить локальное хранилище на корпоративное без изменения UI

**Минусы:**
- Дополнительный слой абстракции (Provider → SQLite → FileSystem)
- Необходимость синхронизации между файловым кэшем и SQLite
- Project usage не синхронизируется между машинами в MVP (локальная БД)

**Альтернативы рассмотренные:**
1. **ExtensibleStorage для catalog** — отклонено (ADR-FM-001, жёсткий запрет)
2. **EF Core + Migrations** — отклонено (лишняя зависимость, ручные скрипты проще для MVP)
3. **Прямая ссылка на SmartCon.Revit** — отклонено (нарушает dependency-rule)

## Обновления документации

- `docs/domain/models.md` — добавлены FamilyManager модели
- `docs/domain/interfaces.md` — добавлены FamilyManager интерфейсы
- `docs/architecture/solution-structure.md` — добавлен `SmartCon.FamilyManager`
- `docs/architecture/dependency-rule.md` — добавлена строка FamilyManager в матрицу
