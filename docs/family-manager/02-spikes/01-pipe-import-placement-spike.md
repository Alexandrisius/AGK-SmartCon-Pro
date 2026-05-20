# Spike E-1: Импорт и размещение PipeType через FamilyManager

**Дата:** 2026-05-21
**Статус:** Spike завершён, результат положительный
**Ветка:** `feature/system-families` (коммит `1762b22`)

---

## Что мы сделали

Реализовали end-to-end MVP для системных семейств на примере труб (PipeType):

1. **Импорт** — пользователь выделяет трубы в проекте Revit → плагин извлекает уникальные PipeType'ы → копирует их в чистый .rvt через `CopyElements` → сохраняет .rvt в managed storage → записывает метаданные в SQLite.

2. **Размещение** — пользователь выбирает тип трубы в дереве FM → плагин открывает сохранённый .rvt → находит PipeType по имени → копирует в активный проект через `CopyElements` → активирует инструмент размещения через `PostRequestForElementTypePlacement`.

---

## Как это работает

### Импорт (путь данных)

```
[Пользователь выделяет трубы]
        ↓
PipeSelectionFilter (OST_PipeCurves)
        ↓
SystemFamilyRevitOperations.PickPipeTypes()
  — ISelectionFilter, сбор уникальных PipeType по имени
        ↓
SystemFamilyRevitOperations.CreateCleanProjectWithTypes()
  — app.NewProjectDocument(UnitSystem.Metric)
  — CopyElements: PipeType + RoutingPreferenceManager зависимости
        ↓
SystemFamilyImportService.SaveToStorageAsync()
  — StoragePathResolver: каталог + .rvt файл
  — SQLite: catalog_items (family_source='system'), family_types (type_name)
  — Временный .rvt удаляется
```

### Размещение (путь данных)

```
[ПКМ на типе → "Разместить тип"]
        ↓
PlaceType() → typeNode.UniqueId != null → PlaceSystemType()
        ↓
SystemFamilyPlacementService.LoadAndPlaceSystemType()
  — IFamilyFileResolver: путь к .rvt в managed storage
  — OpenDocumentFile(.rvt)
  — FindTypeByName(sourceDoc, typeName)
  — Если тип уже есть в проекте → PostRequestForElementTypePlacement
  — Если нет → CopyElements → PostRequestForElementTypePlacement
  — sourceDoc.Close()
```

### Ключевое открытие

`CopyElements` корректно переносит **все зависимости** PipeType:
- Сегменты (PipeSegment)
- Фитинги (переходы, отводы, тройники)
- Размеры (sizes)
- Все правила RoutingPreferenceManager по всем группам

---

## Новые файлы

| Файл | Назначение |
|------|-----------|
| `Core/Models/FamilyManager/SystemFamilyImportResult.cs` | Модель результата импорта |
| `Core/Services/Interfaces/ISystemFamilyImportService.cs` | Контракт импорта |
| `Core/Services/Interfaces/ISystemFamilyPlacementService.cs` | Контракт размещения |
| `Core/Services/Interfaces/ISystemFamilyRevitOperations.cs` | Контракт Revit-операций |
| `Revit/Selection/PipeSelectionFilter.cs` | ISelectionFilter для труб |
| `Revit/FamilyManager/SystemFamilyRevitOperations.cs` | Пикер + CopyElements + создание .rvt |
| `Revit/FamilyManager/SystemFamilyPlacementService.cs` | OpenDoc + CopyElements + PostRequest |
| `FamilyManager/Services/SystemFamilyImportService.cs` | Оркестрация: selection → storage → DB |

### Изменённые файлы

| Файл | Изменение |
|------|-----------|
| `FamilyCatalogSql.cs` | V8 миграция, CreateCatalogItems/Types с новыми колонками |
| `LocalCatalogMigrator.cs` | MigrateV8Async + EnsureCriticalColumnsAsync для V8 |
| `LocalFamilyTypeRepository.cs` | SQL читает type_unique_id |
| `FamilyTypeDescriptor.cs` | Поле UniqueId |
| `FamilyTypeNodeViewModel.cs` | Свойство UniqueId |
| `FamilyManagerMainViewModel.Import.cs` | Команда ImportSystemFamily |
| `FamilyManagerMainViewModel.LoadPlace.cs` | PlaceSystemType, ветвление LoadAndPlace/LoadToProject |
| `FamilyManagerPaneControl.xaml` | Кнопка "Импорт системного семейства" |
| `ServiceRegistrar.cs` | DI для 3 новых сервисов |

### Миграция БД V8

- `catalog_items.family_source` — `'loadable'` (по умолчанию) или `'system'`
- `catalog_items.revit_category` — `'OST_PipeCurves'` для труб
- `family_types.type_unique_id` — UniqueId типа (в спайке не используется для поиска, см. костыли)

---

## Почему это НЕ production-ready

### 1. Поиск типа по имени вместо стабильного идентификатора

**Костыль:** `FindTypeByName` ищет PipeType по строковому имени. Если в проекте два PipeType с одинаковым именем — возьмёт первый.

**Причина:** UniqueId меняется при `CopyElements` — Revit генерирует новые StableUniqueIds в целевом документе. Первоначальный дизайн хранил UniqueId из исходного проекта, но это не работает.

**Как правильно:** Нужен стабильный ключ — например, хэш из имени + параметров типа, или кастомный GUID, генерируемый FM и записываемый в ExtensibleStorage или через `Parameter` типа.

### 2. Нет диалога настроек импорта

**Костыль:** Имя семейства автогенерируется как `"Трубы {timestamp}"`. Пользователь не может задать имя, категорию, описание.

**Как правильно:** Диалог с полями: имя, описание, теги. Аналогично диалогу импорта .rfa.

### 3. Только трубы (OST_PipeCurves)

**Костыль:** Хардкод на трубы. `PipeSelectionFilter`, `PipeType`, `RoutingPreferenceManager` — всё привязано к plumbing.

**Как правильно:** Обобщённая архитектура:
- `ISystemFamilySelectionFilter` с фабрикой по `BuiltInCategory`
- Поддержка: ducts, conduits, cable trays, pipes
- Каждый тип — свой набор зависимостей при CopyElements

### 4. `StoragePathResolver.GetRfaFilePath()` для .rvt файлов

**Костыль:** Метод называется `GetRfaFilePath`, а используется для .rvt. Имя вводит в заблуждение.

**Как правильно:** Переименовать в `GetFamilyFilePath` или добавить `GetProjectFilePath` с ясной семантикой.

### 5. `type_unique_id` в БД — не используется

**Костыль:** Колонка `type_unique_id` в `family_types` создана, но размещение ищет по `type_name`. Колонка мёртвая.

**Как правильно:** Либо удалить и использовать стабильный хэш, либо реализовать запись стабильного идентификатора при импорте.

### 6. Нет обработки ошибок для пользователя

**Костыль:** Ошибки пишутся в лог, но пользователь видит только generic "Load error" или ничего. Нет валидации:
- Пустой выбор (0 типов)
- Файл .rvt повреждён
- Тип не найден в .rvt
- Revit version mismatch

**Как правильно:** Человекочитаемые сообщения об ошибках в статус-баре, локализованные.

### 7. `targetRevit=0` в логах

**Костыль:** `CurrentRevitVersion` возвращает 0 в некоторых вызовах. `int.Parse(_revitContext.GetRevitVersion())` может вернуть 0 или упасть.

**Как правильно:** Получать версию из `uiApp.Application.VersionNumber` в контексте ExternalEvent, не из кеша.

### 8. `NewProjectDocument(UnitSystem.Metric)` без шаблона

**Риск:** Чистый проект без шаблона может не содержать MEP-зависимостей для сложных систем. Для труб работает, для других системных семейств может потребоваться шаблон.

**Как правильно:** Конфигурируемый путь к шаблону, fallback на `DefaultProjectTemplate` из Revit settings.

### 9. Файл .rvt в managed storage — полный проект, не минимальный

**Костыль:** При импорте 2 типов PipeType сохраняется целый .rvt (несколько МБ). Нет очистки от лишних элементов.

**Как правильно:** После CopyElements — удалить из .rvt всё кроме нужных типов и их зависимостей. Или хранить только минимальный набор данных.

### 10. Нет дедупликации при импорте

**Костыль:** Каждый импорт создаёт новый catalog_item. Если пользователь импортирует те же трубы дважды — будет два элемента в каталоге.

**Как правильно:** Проверка по SHA256 + имени типа. Если тип уже в каталоге — обновление, не дубликат.

---

## Что можно использовать как есть

- **DB миграция V8** — columns `family_source`, `revit_category` — правильная схема, менять не нужно
- **`PipeSelectionFilter`** — корректный фильтр, обобщается на другие категории
- **`EnsureCriticalColumnsAsync`** — safety net паттерн, переносим в production
- **Ветвление PlaceType по UniqueId** — паттерн правильный, меняется только содержимое ветки

---

## Рекомендации для production

1. **ADR** — зафиксировать решение о CopyElements как механизме переноса системных типов
2. **Обобщённая фабрика** — `ISystemFamilyHandler` с реализациями для Pipe, Duct, Conduit, CableTray
3. **Стабильные идентификаторы** — отказаться от UniqueId, использовать хэш или GUID
4. **Диалог импорта** — имя, описание, предпросмотр типов
5. **Очистка .rvt** — минимальный файл с нужными типами
6. **Дедупликация** — проверка SHA256 + типы
7. **Unit-тесты** — на миграцию, на SQL-запросы, на валидацию
