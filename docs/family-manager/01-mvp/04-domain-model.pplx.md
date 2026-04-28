# FamilyManager Domain Model

## Цель документа

Документ фиксирует язык домена FamilyManager. Главная задача — не смешивать физический `.rfa` файл, Revit `Family`, запись каталога и версию семейства.

## Core Terms

| Термин | Определение |
| --- | --- |
| Catalog | Набор записей BIM-контента, доступный через provider |
| Catalog Provider | Источник каталога: local SQLite, remote API, corporate server |
| Catalog Item | Логическая запись в каталоге, представляющая BIM-объект |
| Family | Revit family как логическая BIM-сущность |
| Family File | Физический `.rfa` файл |
| Family Version | Конкретная версия family file с hash и metadata |
| Family Type | Типоразмер внутри семейства |
| Family Parameter | Параметр семейства или типа |
| Project Usage | Факт загрузки catalog item/version в конкретный `.rvt` |
| Status | Состояние пригодности контента: Draft, Verified, Deprecated, Archived |
| Source | Происхождение контента: Local, Corporate, Public, Manufacturer |
| Preview | Изображение/thumbnail для карточки |
| Classification | Классификационная система или категория компании |

## Domain Boundary

FamilyManager управляет:

- каталогом семейств;
- метаданными;
- версиями файлов;
- поиском и фильтрацией;
- загрузкой семейств в проект;
- project usage history.

FamilyManager в MVP не управляет:

- полноценным approval workflow;
- серверными ролями;
- marketplace-публикацией;
- автоматической правкой всех параметров семейства;
- BIM-стандартами компании как отдельным rule engine.

## Primary Entities

### FamilyCatalogItem

Логическая запись каталога.

| Поле | Тип | Обязательность | Комментарий |
| --- | --- | --- | --- |
| `Id` | `Guid` | Да | Stable catalog ID |
| `ProviderId` | `string` | Да | Например `local` |
| `Name` | `string` | Да | Display name |
| `Description` | `string?` | Нет | Ручное описание |
| `CategoryName` | `string?` | Нет | Revit category при доступности |
| `Manufacturer` | `string?` | Нет | Ручное или extracted metadata |
| `Status` | `FamilyContentStatus` | Да | Draft по умолчанию |
| `Tags` | `IReadOnlyList<string>` | Да | Нормализуются для поиска |
| `CurrentVersionId` | `Guid?` | Нет | Последняя активная версия |
| `CreatedAtUtc` | `DateTimeOffset` | Да | Создание записи |
| `UpdatedAtUtc` | `DateTimeOffset` | Да | Последнее изменение |

### FamilyCatalogVersion

Конкретная версия файла.

| Поле | Тип | Обязательность | Комментарий |
| --- | --- | --- | --- |
| `Id` | `Guid` | Да | Stable version ID |
| `CatalogItemId` | `Guid` | Да | Связь с item |
| `FileId` | `Guid` | Да | Связь с physical file record |
| `VersionLabel` | `string` | Да | Например `1.0`, `2026-04-28`, `hash-short` |
| `ContentHashSha256` | `string` | Да | Основа duplicate detection |
| `RevitMajorVersion` | `int?` | Нет | Если удалось определить |
| `TypesCount` | `int?` | Нет | Если извлечено |
| `ParametersCount` | `int?` | Нет | Если извлечено |
| `ImportedAtUtc` | `DateTimeOffset` | Да | Когда версия попала в каталог |

### FamilyFileRecord

Физический файл.

| Поле | Тип | Обязательность | Комментарий |
| --- | --- | --- | --- |
| `Id` | `Guid` | Да | Stable file ID |
| `OriginalPath` | `string?` | Нет | Исходный путь |
| `CachedPath` | `string?` | Нет | Путь в локальном кэше |
| `FileName` | `string` | Да | Имя файла |
| `SizeBytes` | `long` | Да | Размер |
| `Sha256` | `string` | Да | Hash |
| `LastWriteTimeUtc` | `DateTimeOffset?` | Нет | Для reindex |
| `StorageMode` | `FamilyFileStorageMode` | Да | Linked или Cached |

### FamilyTypeDescriptor

Снимок типоразмера.

| Поле | Тип | Обязательность | Комментарий |
| --- | --- | --- | --- |
| `Id` | `Guid` | Да | Внутренний ID записи |
| `VersionId` | `Guid` | Да | Версия семейства |
| `Name` | `string` | Да | Имя типа |
| `Parameters` | `IReadOnlyList<FamilyParameterDescriptor>` | Нет | Для MVP можно lazy/partial |

### FamilyParameterDescriptor

Снимок параметра.

| Поле | Тип | Обязательность | Комментарий |
| --- | --- | --- | --- |
| `Name` | `string` | Да | Не полагаться на localized name для Revit операций |
| `StorageType` | `string` | Нет | Snapshot |
| `ValueText` | `string?` | Нет | Display value |
| `IsInstance` | `bool?` | Нет | Если известно |
| `IsReadOnly` | `bool?` | Нет | Если известно |
| `ForgeTypeIdValue` | `string?` | Нет | Для net8/Revit 2025+ |

### ProjectFamilyUsage

Запись истории использования семейства в проекте. В MVP хранится в локальной БД каталога, в корпоративной фазе — в серверной БД. Это не ExtensibleStorage.

| Поле | Тип | Обязательность | Комментарий |
| --- | --- | --- | --- |
| `CatalogItemId` | `Guid` | Да | Что загружали |
| `VersionId` | `Guid` | Да | Какую версию |
| `ProviderId` | `string` | Да | Источник |
| `LoadedFamilyName` | `string` | Да | Имя в проекте |
| `LoadedAtUtc` | `DateTimeOffset` | Да | Когда загрузили |
| `LoadedBy` | `string?` | Нет | Если доступно |
| `RevitDocumentGuid` | `string?` | Нет | Если безопасно получить |

## Enums

### FamilyContentStatus

| Значение | Смысл |
| --- | --- |
| `Draft` | Импортировано, но не проверено |
| `Verified` | Разрешено к обычному использованию |
| `Deprecated` | Не рекомендуется использовать |
| `Archived` | Скрыто из обычного поиска |

### FamilyFileStorageMode

| Значение | Смысл |
| --- | --- |
| `Linked` | Каталог хранит ссылку на исходный путь |
| `Cached` | Файл скопирован в FamilyManager cache |
| `Missing` | Исходный файл недоступен |

### CatalogProviderKind

| Значение | Смысл |
| --- | --- |
| `Local` | SQLite provider |
| `Remote` | HTTP API |
| `Corporate` | Self-hosted corporate provider |
| `PublicReadOnly` | Публичный readonly источник |

## SmartCon Rules

- Domain models размещаются в `SmartCon.Core/Models/FamilyManager`.
- Public API использует `IReadOnlyList<T>`.
- Immutable модели предпочтительно делать `record` с `init`.
- Не хранить Revit `Element`, `Family`, `FamilySymbol` в domain model.
- `ElementId` если понадобится сериализуется как `long` через `ElementIdCompat`.
- `Core` не вызывает Revit API и не использует `System.Windows`.
