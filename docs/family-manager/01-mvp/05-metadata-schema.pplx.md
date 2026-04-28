# FamilyManager Metadata Schema

## Цель документа

Документ описывает, какие данные хранит FamilyManager, где они лежат и как версионируются.

## Storage Split

| Данные | Хранилище | Почему |
| --- | --- | --- |
| Каталог семейств | SQLite | Быстрый поиск, фильтры, локальная библиотека |
| Файлы `.rfa` | Исходный путь или file cache | Не хранить бинарники в SQLite |
| Preview | File cache | Можно пересоздать |
| Search index | SQLite tables / FTS5 later | Локальный поиск |
| Project usage | SQLite в MVP; серверная БД в corporate phase | История загрузок и использования контента |
| Server credentials | Не в MVP | Позже через Windows Credential Manager abstraction |

Канонический локальный root MVP: `%APPDATA%\AGK\SmartCon\FamilyManager\`.

## Local SQLite Schema

### `schema_info`

| Column | Type | Notes |
| --- | --- | --- |
| `key` | `TEXT PRIMARY KEY` | Например `schema_version` |
| `value` | `TEXT NOT NULL` | Значение |

### `catalog_items`

| Column | Type | Notes |
| --- | --- | --- |
| `id` | `TEXT PRIMARY KEY` | Guid |
| `provider_id` | `TEXT NOT NULL` | MVP: `local` |
| `name` | `TEXT NOT NULL` | Display name |
| `normalized_name` | `TEXT NOT NULL` | Для поиска |
| `description` | `TEXT NULL` | Ручное описание |
| `category_name` | `TEXT NULL` | Revit category |
| `manufacturer` | `TEXT NULL` | Производитель |
| `status` | `TEXT NOT NULL` | Draft/Verified/Deprecated/Archived |
| `current_version_id` | `TEXT NULL` | FK логически |
| `created_at_utc` | `TEXT NOT NULL` | ISO 8601 |
| `updated_at_utc` | `TEXT NOT NULL` | ISO 8601 |

### `catalog_versions`

| Column | Type | Notes |
| --- | --- | --- |
| `id` | `TEXT PRIMARY KEY` | Guid |
| `catalog_item_id` | `TEXT NOT NULL` | Связь с item |
| `file_id` | `TEXT NOT NULL` | Связь с file |
| `version_label` | `TEXT NOT NULL` | Human label |
| `sha256` | `TEXT NOT NULL` | Duplicate detection |
| `revit_major_version` | `INTEGER NULL` | Если извлечено |
| `types_count` | `INTEGER NULL` | Если извлечено |
| `parameters_count` | `INTEGER NULL` | Если извлечено |
| `imported_at_utc` | `TEXT NOT NULL` | ISO 8601 |

### `family_files`

| Column | Type | Notes |
| --- | --- | --- |
| `id` | `TEXT PRIMARY KEY` | Guid |
| `original_path` | `TEXT NULL` | Может стать недоступным |
| `cached_path` | `TEXT NULL` | Относительный путь в cache |
| `file_name` | `TEXT NOT NULL` | Имя файла |
| `size_bytes` | `INTEGER NOT NULL` | Размер |
| `sha256` | `TEXT NOT NULL` | Hash |
| `last_write_time_utc` | `TEXT NULL` | Для reindex |
| `storage_mode` | `TEXT NOT NULL` | Linked/Cached/Missing |

### `family_types`

| Column | Type | Notes |
| --- | --- | --- |
| `id` | `TEXT PRIMARY KEY` | Guid |
| `version_id` | `TEXT NOT NULL` | Family version |
| `name` | `TEXT NOT NULL` | Type name |
| `sort_order` | `INTEGER NOT NULL` | UI ordering |

### `family_parameters`

| Column | Type | Notes |
| --- | --- | --- |
| `id` | `TEXT PRIMARY KEY` | Guid |
| `version_id` | `TEXT NOT NULL` | Family version |
| `type_id` | `TEXT NULL` | Null для family-level |
| `name` | `TEXT NOT NULL` | Parameter name |
| `storage_type` | `TEXT NULL` | Snapshot |
| `value_text` | `TEXT NULL` | Display value |
| `is_instance` | `INTEGER NULL` | 0/1 |
| `is_readonly` | `INTEGER NULL` | 0/1 |
| `forge_type_id` | `TEXT NULL` | Если доступно |

### `catalog_tags`

| Column | Type | Notes |
| --- | --- | --- |
| `catalog_item_id` | `TEXT NOT NULL` | Item |
| `tag` | `TEXT NOT NULL` | Original tag |
| `normalized_tag` | `TEXT NOT NULL` | Search tag |

### `attachments`

| Column | Type | Notes |
| --- | --- | --- |
| `id` | `TEXT PRIMARY KEY` | Guid |
| `catalog_item_id` | `TEXT NOT NULL` | Item |
| `attachment_type` | `TEXT NOT NULL` | PDF, DWG, XLSX, URL |
| `display_name` | `TEXT NOT NULL` | UI name |
| `relative_path_or_url` | `TEXT NOT NULL` | Cache path or URL |
| `sha256` | `TEXT NULL` | For local files |
| `created_at_utc` | `TEXT NOT NULL` | ISO 8601 |

### `project_usage`

| Column | Type | Notes |
| --- | --- | --- |
| `id` | `TEXT PRIMARY KEY` | Guid |
| `catalog_item_id` | `TEXT NOT NULL` | Loaded item |
| `version_id` | `TEXT NOT NULL` | Loaded version |
| `provider_id` | `TEXT NOT NULL` | Source provider |
| `project_fingerprint` | `TEXT NOT NULL` | Stable local project identity |
| `revit_project_path_hash` | `TEXT NULL` | Optional privacy-safe path hash |
| `action` | `TEXT NOT NULL` | Loaded, Reloaded, Placed, Replaced |
| `created_at_utc` | `TEXT NOT NULL` | ISO 8601 |

### `previews`

| Column | Type | Notes |
| --- | --- | --- |
| `id` | `TEXT PRIMARY KEY` | Guid |
| `catalog_item_id` | `TEXT NOT NULL` | Item |
| `version_id` | `TEXT NULL` | Specific or generic |
| `relative_path` | `TEXT NOT NULL` | File cache path |
| `width` | `INTEGER NULL` | px |
| `height` | `INTEGER NULL` | px |
| `created_at_utc` | `TEXT NOT NULL` | ISO 8601 |

## ExtensibleStorage Boundary

ExtensibleStorage **не используется** для хранения каталога семейств.

Запрещено хранить в ExtensibleStorage:

- `.rfa` файлы;
- записи каталога;
- версии семейств;
- metadata;
- теги;
- preview;
- search index;
- usage history.

Для MVP FamilyManager не проектируется `FamilyManagerSchema.cs` в `SmartCon.Revit/Storage`. Если позже понадобится записывать что-то в `.rvt`, это должно быть отдельное архитектурное решение вне MVP и не должно становиться заменой локальной/серверной БД.

## Indexing Strategy

### MVP

- `normalized_name`;
- `category_name`;
- `status`;
- tags;
- `sha256`;
- simple `LIKE` search.

### Post-MVP

SQLite FTS5 can be added after smoke validation. SQLite FTS5 is a virtual table module for full-text search and supports `MATCH` queries ([SQLite FTS5 documentation](https://www.sqlite.org/fts5.html)).

## Migration Rules

1. SQLite has its own `schema_version`.
2. Server database schema has its own migrations in corporate phase.
3. File cache layout has its own `cache_version` if needed.
4. Migrations must be idempotent.
5. Deserializers must tolerate unknown fields.
6. Любой Revit project document может быть закрыт, переименован или недоступен; локальный каталог всё равно должен работать как самостоятельная БД.
7. Повреждённая SQLite база должна иметь recovery path: backup + recreate.

## Hash Rules

- Основной hash: SHA-256.
- Hash вычисляется по содержимому файла.
- Duplicate detection в MVP строится по SHA-256.
- Hash не считается security boundary.
- Для UI можно показывать short hash.
