# ADR-015: FamilyManager Published Storage Architecture

**Status:** accepted
**Date:** 2026-04-30
**Supersedes:** ADR-014

## Context

FamilyManager MVP (ADR-014) хранил БД в `%APPDATA%`, файлы — в AppData-кэше по SHA256, с режимами Linked/Cached/Missing. Это был локальный кэш, а не Published-хранилище.

Новая концепция: FamilyManager = единый достоверный источник утверждённого BIM-контента (Published-зона по ISO 19650). Рабочие папки (WIP/Shared) остаются зоной разработки, но утверждённые семейства публикуются в FM.

## Decisions

### FM-015-001: Configurable Database Location

БД и файлы хранятся вместе в управляемой папке, путь к которой задаёт пользователь:

```
{db-root}/
├── catalog.db                      # SQLite
├── files/
│   └── {family-id}/
│       └── {version-label}/
│           ├── r{revit-major}/     # .rfa (SHA256-named)
│           └── assets/             # вспомогательные материалы
│               ├── images/
│               ├── videos/
│               ├── documents/
│               ├── models/
│               └── lookup/
```

Реестр подключений — в `%APPDATA%\SmartCon\FamilyManager\registry.json` (локальные настройки).

### FM-015-002: Published-Only Content

FM — Published-зона. Всё, что импортировано — опубликовано.

Статусы контента: `Active` (по умолчанию), `Deprecated` (устаревшее), `Retired` (снято с публикации).

### FM-015-003: Managed Storage (единый режим)

Убраны режимы Linked/Cached/Missing. Единый режим: Managed. При импорте файл копируется в управляемое хранилище.

### FM-015-004: Version → Revit-Version Model

Семейство может иметь несколько версий (v1, v2...), каждая — с несколькими Revit-сборками. При загрузке FM автоматически выбирает подходящую Revit-версию.

### FM-015-005: Auxiliary Assets

Связанные материалы (картинки, видео, документы, FBX, lookup-таблицы) привязываются к семейству и хранятся в managed storage.

### FM-015-006: Clean Slate

Новый формат БД (schema v2). Нет миграции со старого формата.

## SQLite Schema v2

8 таблиц: database_meta, schema_info, catalog_items, catalog_versions, family_files, family_assets, catalog_tags, project_usage.

Ключевые отличия от v1:
- `content_status` вместо `status` (Active/Deprecated/Retired)
- `current_version_label` вместо `current_version_id`
- `relative_path` вместо `original_path`/`cached_path`/`storage_mode`
- Новая таблица `family_assets`
- `revit_major_version` NOT NULL в catalog_versions
- FOREIGN KEY constraints с CASCADE

## Consequences

**Плюсы:** FM = Published-зона, произвольный путь к БД, multi-user (single-writer), управляемое хранилище, версионность по Revit, assets.

**Минусы:** Нет миграции со старого формата, SQLite по сети может быть медленным (future: локальный кэш).
