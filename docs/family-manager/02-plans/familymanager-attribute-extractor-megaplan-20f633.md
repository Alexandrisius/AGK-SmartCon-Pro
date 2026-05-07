# Megaplan: FamilyManager Attribute Extractor Foundation

**ID:** `20f633`  
**Проект:** SmartCon / FamilyManager  
**Статус:** Draft for review  
**Цель:** спроектировать фундаментальную атрибутивную модель FamilyManager: словарь атрибутов, привязки к категориям, наследование, извлечение значений из Revit families, кэширование в SQLite и современный UI вкладки `Атрибуты`.

---

## 0. Решения, уже согласованные с пользователем

1. **Сценарий извлечения:** гибридный.
   - Импорт `.rfa` остаётся быстрым: файл регистрируется в managed storage и БД.
   - Глубокое извлечение запускается отдельной командой `Импорт данных` или будущей очередью.
   - Текущую команду `Извлечь типоразмеры` нужно переименовать и расширить до `Импорт данных`.

2. **Первый релизный результат:** вертикальный срез `Attribute Extraction Foundation`.
   - Категорийные обязательные атрибуты.
   - Наследование атрибутов от родительских категорий.
   - Извлечение типоразмеров и значений атрибутов.
   - Хранение извлечённых значений в SQLite.
   - Вкладка `Атрибуты` в окне свойств с выбором типоразмера.
   - Подготовка БД для будущего поиска по атрибутам.
   - Сам поиск как продуктовая функция в первый релиз не входит.

3. **Уровень значений:** type-aware.
   - У семейства может быть много типоразмеров.
   - Атрибуты должны показывать значения для выбранного Family Type.
   - UI должен содержать выпадающий список типоразмеров, предпочтительно внутри вкладки `Атрибуты`.
   - Family-level значения тоже не терять, но основная пользовательская ценность — значения параметров типов.

4. **Поиск параметра в Revit family:** только точное имя.
   - В первой версии нет алиасов, fuzzy matching и автоматического смыслового сопоставления.
   - Атрибут `Давление` ищет параметр именно `Давление`.
   - Если параметр называется `Pressure`, `Напор` или `H`, он не считается найденным.

5. **Обязательные атрибуты:** non-blocking quality status.
   - Отсутствующий параметр или пустое значение не блокирует импорт данных.
   - Значение сохраняется со статусом `MissingParameter`, `EmptyValue`, `Unsupported` или `Error`.
   - UI показывает полноту данных и список проблем.

6. **Модель привязки атрибутов к категориям:** словарь + bindings.
   - Атрибуты живут в словаре конкретной БД.
   - Категории хранят привязки к атрибутам.
   - Наследование считается сервисом по дереву категорий.

7. **Много БД:** каждая БД имеет собственные категории, словарь атрибутов и bindings.
   - Нельзя делать глобальный словарь атрибутов на весь SmartCon.
   - Перенос между БД должен идти через JSON import/export.

8. **JSON transfer:** нужны гибкие варианты.
   - Только категории.
   - Только атрибуты.
   - Категории + атрибуты + bindings.
   - При переносе bindings нельзя полагаться на `CategoryId`, потому что текущий импорт дерева генерирует новые ID. Нужно использовать stable category path.

---

## 1. Текущее состояние кода

### 1.1. Уже есть

- **Категории**:
  - `CategoryNode`, `CategoryTree` в `SmartCon.Core/Models/FamilyManager`.
  - `ICategoryRepository` + `LocalCategoryRepository`.
  - `CategoryTreeEditorViewModel` для создания, переименования, удаления, перемещения, импорта и экспорта дерева.

- **Импорт/хранилище**:
  - `LocalFamilyImportService` копирует `.rfa` в managed storage и пишет записи в SQLite.
  - `FileNameOnlyMetadataExtractionService` сейчас извлекает только файловые метаданные.
  - `IRevitFileInfoReader` определяет версию Revit файла.

- **Типоразмеры**:
  - Таблица `family_types`.
  - `IFamilyTypeRepository` + `LocalFamilyTypeRepository`.
  - В `FamilyManagerMainViewModel` есть команда `ExtractTypes`, которая временно загружает семейство и сохраняет имена типов.

- **Пресеты атрибутов**:
  - Таблицы `attribute_presets` и `attribute_preset_parameters`.
  - `IAttributePresetService` + `LocalAttributePresetService`.
  - `GetEffectiveParametersAsync` уже реализует наследование от родительских категорий.

- **Вкладка `Атрибуты`**:
  - В `FamilyPropertiesView.xaml` уже есть tab `FM_Tab_Attributes`.
  - Сейчас она показывает только `EffectiveParameters`, а значение всегда `—`.
  - Нет выбора типоразмера и нет извлечённых значений.

### 1.2. Чего нет

- Нет словаря атрибутов как first-class сущности.
- Нет category bindings с устойчивыми `attribute_id`.
- Нет таблицы извлечённых значений атрибутов.
- Нет статуса полноты/качества по атрибутам.
- Нет type-aware UI вкладки `Атрибуты`.
- Нет глубокого Revit extractor для `.rfa`, который открывает family document и читает `FamilyManager`.
- Нет JSON transfer для категорий + атрибутов + bindings.
- Нет подготовки индексов под будущий attribute search.

---

## 2. Продуктовая цель

FamilyManager должен перестать быть просто каталогом файлов и стать индексируемой BIM content database.

Пользовательские сценарии:

1. **BIM-manager / администратор библиотеки**
   - Создаёт дерево категорий: `Насосы > Центробежные`.
   - В словаре атрибутов создаёт `Давление`, `Расход`, `Мощность`, `Материал`.
   - Привязывает обязательные атрибуты к категории `Насосы`.
   - Подкатегория `Центробежные` наследует эти атрибуты и может добавить свои.

2. **Content author**
   - Импортирует семейства в каталог.
   - Запускает `Импорт данных`.
   - Видит, какие обязательные параметры отсутствуют в семействе или в конкретных типоразмерах.
   - Исправляет `.rfa` вне scope первой версии.

3. **Проектировщик / инженер**
   - Открывает свойства семейства.
   - На вкладке `Атрибуты` выбирает типоразмер.
   - Видит технические значения для выбранного типа без открытия `.rfa` в Revit.

4. **Будущий сценарий поиска**
   - Пользователь сможет искать `Насос`, где `Давление >= X`, `Расход = Y`, `Материал = Z`.
   - В первый релиз UI поиска не входит, но модель данных и индексы должны не мешать такому развитию.

---

## 3. Архитектурные ограничения SmartCon

Обязательные инварианты:

- **I-01:** Revit API из WPF только через `IExternalEventHandler`.
- **I-03:** транзакции только через `ITransactionService`; прямой `new Transaction(doc)` запрещён.
- **I-05:** не хранить живые `Element`, `Family`, `FamilySymbol`, `Connector`, `Document` между вызовами.
- **I-09:** `SmartCon.Core` может содержать доменные модели и интерфейсы, но не должен вызывать Revit API и не должен использовать `System.Windows`.
- **I-10:** MVVM строго; `.xaml.cs` остаётся минимальным.
- FamilyManager не должен зависеть от `SmartCon.Revit` напрямую.
- Каталог, атрибуты, значения, usage и search index не хранятся в `.rvt` и не используют ExtensibleStorage.

---

## 4. Целевая доменная модель

### 4.1. AttributeDefinition

Новая доменная модель в `SmartCon.Core/Models/FamilyManager/`.

Назначение: описывает один атрибут в словаре текущей БД. Единственное поле ввода — `Name`.

Поля:

| Поле | Тип | Смысл |
| --- | --- | --- |
| `Id` | `string` | GUID внутри текущей БД. |
| `Name` | `string` | Каноническое имя атрибута и точное имя Revit-параметра. Единственное поле, которое вводит пользователь. |
| `Group` | `string?` | Опциональный тег для группировки атрибутов в UI. Например: «Основные», «Гидравлика». |
| `IsActive` | `bool` | Можно скрывать устаревшие атрибуты без удаления истории. |
| `CreatedAtUtc` | `DateTimeOffset` | Аудит. |

Правила:

- В одной БД `Name` должен быть уникальным (case-insensitive).
- `ValueKind`, `UnitTypeId`, `ParameterScope` **НЕ хранятся** в определении — они auto-detect-ятся при извлечении из Revit:
  - `ValueKind` ← `FamilyParameter.StorageType` (String/Double/Integer/ElementId).
  - `UnitTypeId` ← `FamilyParameter.GetUnitTypeId()` (Revit 2021+).
  - `ParameterScope` ← `FamilyParameter.IsInstance` (Instance/Type).
- Эти метаданные записываются в `ExtractedAttributeValue` при каждом извлечении.
- У разных семейств один и тот же параметр может иметь разные типы данных — это нормально.
- Алиасы не реализуются в первой версии. Если появятся — отдельная таблица `attribute_aliases`.

### 4.2. CategoryAttributeBinding

Новая доменная модель.

Назначение: связывает категорию с атрибутом. Создаётся установкой чек-бокса в UI.

Поля:

| Поле | Тип | Смысл |
| --- | --- | --- |
| `Id` | `string` | GUID binding-а. |
| `CategoryId` | `string` | Категория. |
| `AttributeId` | `string` | FK на `attribute_definitions`. |
| `SortOrder` | `int` | Порядок отображения. |
| `IsEnabled` | `bool` | Позволяет отключить наследуемый атрибут в подкатегории. |

Правила наследования:

- Effective bindings для категории строятся цепочкой: root -> ... -> selected category.
- Если дочерняя категория добавляет binding для того же `AttributeId`, она переопределяет `IsEnabled` и `SortOrder`.
- Если `IsEnabled = false`, атрибут исключается из effective set.
- В UI унаследованные атрибуты показываются с пометкой «наследован» и стилем, отличным от direct binding.
- Все привязанные атрибуты считаются обязательными для извлечения. Если нужно различать обязательные/опциональные — добавить `IsRequired` в следующей версии.

### 4.3. FamilyDataImportRun

Назначение: фиксирует один запуск команды `Импорт данных`.

Поля:

| Поле | Тип | Смысл |
| --- | --- | --- |
| `Id` | `string` | GUID run-а. |
| `CatalogItemId` | `string` | Семейство. |
| `VersionId` | `string?` | Версия семейства, если доступна. |
| `FileId` | `string?` | Конкретный `.rfa`. |
| `SourceSha256` | `string?` | Hash файла на момент извлечения. |
| `RevitMajorVersion` | `int` | Версия Revit, которой выполнено извлечение. |
| `Status` | enum | `Succeeded`, `Partial`, `Failed`, `Skipped`. |
| `TypesCount` | `int` | Количество типов. |
| `AttributesExpectedCount` | `int` | Ожидалось значений. |
| `AttributesFoundCount` | `int` | Найдено значений. |
| `AttributesMissingCount` | `int` | Не найдено/пусто/ошибка. |
| `StartedAtUtc` | `DateTimeOffset` | Начало. |
| `CompletedAtUtc` | `DateTimeOffset?` | Завершение. |
| `ErrorMessage` | `string?` | Ошибка верхнего уровня. |

### 4.4. FamilyTypeDescriptor v2

Сейчас `family_types` привязаны к `catalog_item_id`, но для корректной атрибутивной модели типы должны быть привязаны минимум к версии/файлу.

Проблема текущей модели:

- У одного `catalog_item_id` может быть `v1`, `v2` и разные Revit-сборки.
- Типы и значения атрибутов могут отличаться между версиями.
- Если хранить только по item, можно показать значения не от той версии.

Решение:

- Расширить `family_types` полями `version_id`, `file_id` или `source_sha256`.
- В интерфейсах перейти от `GetTypesForItemAsync(catalogItemId)` к version-aware методам.
- Для обратной совместимости можно оставить старый метод как wrapper для current version.

Минимальные поля `family_types` после доработки:

| Поле | Тип | Смысл |
| --- | --- | --- |
| `id` | `TEXT PRIMARY KEY` | GUID type record. |
| `catalog_item_id` | `TEXT NOT NULL` | Семейство. |
| `version_id` | `TEXT NULL/NOT NULL after migration` | Версия. |
| `file_id` | `TEXT NULL` | Конкретный файл. |
| `type_name` | `TEXT NOT NULL` | Имя типа в Revit. |
| `sort_order` | `INTEGER NOT NULL` | UI порядок. |
| `extraction_run_id` | `TEXT NULL` | Последний run, который записал тип. |

### 4.5. ExtractedAttributeValue

Назначение: хранит фактическое значение атрибута для family-level или type-level.
Метаданные типа данных (`StorageType`, `ParameterScope`, `UnitTypeId`) определяются
**автоматически** при извлечении из Revit API — пользователь их не заполняет.

Поля:

| Поле | Тип | Смысл |
| --- | --- | --- |
| `Id` | `string` | GUID. |
| `CatalogItemId` | `string` | Семейство. |
| `VersionId` | `string?` | Версия. |
| `FileId` | `string?` | Файл. |
| `TypeId` | `string?` | `null` для family-level, GUID из `family_types` для type-level. |
| `AttributeId` | `string` | FK на `attribute_definitions`. |
| `BindingId` | `string?` | Binding, из которого пришло ожидание. Nullable, чтобы пережить изменения правил. |
| `ParameterName` | `string` | Точное имя параметра, которое искали. |
| `ParameterScope` | enum | **Auto-detect:** `FamilyParameter.IsInstance` → `Instance`/`Type`. |
| `StorageType` | `string?` | **Auto-detect:** `FamilyParameter.StorageType.ToString()`. |
| `ValueText` | `string?` | Display value для UI. |
| `ValueRaw` | `string?` | Строковая сериализация raw value. |
| `ValueNumber` | `double?` | Нормализуемое числовое значение для будущего поиска. |
| `UnitTypeId` | `string?` | **Auto-detect:** `FamilyParameter.GetUnitTypeId()` (Revit 2021+). |
| `Status` | enum | `Found`, `MissingParameter`, `EmptyValue`, `UnsupportedStorageType`, `ReadError`. |
| `Message` | `string?` | Диагностика. |
| `ExtractionRunId` | `string` | FK на run. |
| `ExtractedAtUtc` | `DateTimeOffset` | Когда получено. |

Auto-detect при извлечении:
- `ParameterScope` ← `FamilyParameter.IsInstance ? Instance : Type`.
- `StorageType` ← `FamilyParameter.StorageType` (String, Double, Integer, ElementId).
- `UnitTypeId` ← `FamilyParameter.GetUnitTypeId()` для Revit 2021+.
- Эти значения могут отличаться для одного и того же атрибута в разных семействах — это нормально.

Уникальность:

- Для одного run/current snapshot: `(catalog_item_id, version_id, type_id, attribute_id)`.
- При повторном `Импорт данных` старые значения для item/version нужно заменять транзакционно.

---

## 5. SQLite schema v6

Текущая schema version — `5`. Новая доработка должна стать `schema_version = 6`.

### 5.1. Новые таблицы

```sql
attribute_definitions
category_attribute_bindings
family_data_import_runs
extracted_attribute_values
```

### 5.2. Расширение таблиц

```sql
ALTER TABLE family_types ADD COLUMN version_id TEXT NULL;
ALTER TABLE family_types ADD COLUMN file_id TEXT NULL;
ALTER TABLE family_types ADD COLUMN extraction_run_id TEXT NULL;
```

Если SQLite constraints не позволяют безопасно добавить нужные FK/unique constraints, создать новую таблицу `family_types_v2`, перенести данные и переименовать.

### 5.3. Индексы под будущий поиск

Индексы нужны уже сейчас, даже если UI поиска будет позже:

- `idx_attr_values_item_version` на `(catalog_item_id, version_id)`.
- `idx_attr_values_type` на `(type_id)`.
- `idx_attr_values_attribute` на `(attribute_id)`.
- `idx_attr_values_attribute_text` на `(attribute_id, value_text)`.
- `idx_attr_values_attribute_number` на `(attribute_id, value_number)`.
- `idx_attr_bindings_category` на `(category_id)`.
- `idx_attr_bindings_attribute` на `(attribute_id)`.
- `idx_attr_definitions_name` unique на `(name)`.

### 5.4. Отношение к старым таблицам `attribute_presets`

Текущие таблицы являются заделом, но выбранная модель мощнее.

План миграции:

1. В `MigrateV6` создать новые таблицы.
2. Если в `attribute_presets` есть данные:
   - Для каждого уникального `parameter_name` создать `attribute_definitions`.
   - `Name = parameter_name`.
   - `Group = null`.
   - Для каждого `attribute_preset_parameters` создать `category_attribute_bindings`.
   - `SortOrder = sort_order`.
   - `IsEnabled = true`.
3. Старые таблицы не удалять сразу.
4. `LocalAttributePresetService` либо пометить как legacy adapter, либо заменить новым сервисом и обновить вызовы UI.
5. Документировать, что v6 source of truth — `attribute_definitions` + `category_attribute_bindings`.

---

## 6. JSON import/export

### 6.1. Почему нельзя переносить bindings по `CategoryId`

Текущий `CategoryTreeEditorViewModel` экспортирует только имена категорий, а импорт через `FlattenImportData` генерирует новые GUID.

Следовательно:

- `CategoryId` уникален только внутри конкретной БД.
- Для переноса между БД bindings должны ссылаться на category path.
- Например: `Насосы/Центробежные`, а не `9e7a...`.

### 6.2. Новый формат пакета

Новый JSON должен быть не только `CategoryTreeImportData`, а общий package.
Атрибуты идентифицируются по `name` (не `key` — поле `Key` убрано из модели).

```json
{
  "format": "smartcon.familymanager.metadata-package",
  "version": 2,
  "exportedAtUtc": "2026-05-04T00:00:00Z",
  "sections": {
    "categories": true,
    "attributes": true,
    "bindings": true
  },
  "categories": [
    {
      "name": "Насосы",
      "children": [
        { "name": "Центробежные", "children": [] }
      ]
    }
  ],
  "attributes": [
    {
      "name": "Давление",
      "group": "Гидравлика"
    }
  ],
  "bindings": [
    {
      "categoryPath": "Насосы",
      "attributeName": "Давление",
      "sortOrder": 0,
      "isEnabled": true
    }
  ]
}
```

### 6.3. Export UX

В toolbar окна «Категории и атрибуты» — dropdown-кнопка «Экспорт»:

- `Категории`.
- `Атрибуты`.
- `Категории + атрибуты + привязки`.

После экспорта — статус с количеством записей и путём файла.

### 6.4. Import UX

Импорт — мгновенный merge/upsert без wizard:

1. Пользователь выбирает JSON через диалог.
2. Система парсит package, применяет merge/upsert.
3. После завершения — статус: «Импортировано: N категорий, M атрибутов, K привязок».
4. Дерево и правая панель обновляются.

Режим `Merge/Upsert` — единственный в первой версии. Опасные режимы `Replace`
не реализуются. Если нужно заменить всё — пользователь создаёт новую БД.

### 6.5. Conflict rules

- Категории сопоставляются по full path.
- Атрибуты сопоставляются по `name` (case-insensitive).
- Binding применяется только если найдены category path и attribute name.
- Если category path не найден и categories section не импортируется, binding пропускается с предупреждением.

---

## 7. Revit extraction design

### 7.1. Главный принцип

Deep extraction должен быть реализован в `SmartCon.Revit`, а не в `SmartCon.FamilyManager`.

Причина:

- `SmartCon.FamilyManager` не должен напрямую зависеть от Revit API.
- WPF/ViewModel не должен вызывать Revit API.
- Вызов идёт через `IFamilyManagerExternalEvent`.

### 7.2. Рекомендуемый способ чтения `.rfa`

Текущее состояние команды `ExtractTypes`:

- `FamilyManagerMainViewModel.ExtractTypes` работает внутри `IFamilyManagerExternalEvent`.
- Сначала команда ищет семейство, уже загруженное в активный `Document`.
- Если семейство уже загружено, типоразмеры читаются из `Family.GetFamilySymbolIds()` и `FamilySymbol.Name`.
- Если семейство не загружено, команда resolve-ит `.rfa` через `IFamilyFileResolver`.
- Затем `.rfa` загружается в активный проект через `Document.LoadFamily(...)` внутри `_transactionService.RunAndRollback("Extract Types", ...)`.
- После rollback активный проект не должен получить постоянных изменений, но сам подход всё равно временно работает через project document.

Итоговая оценка текущего подхода:

- Для MVP-извлечения только имён типоразмеров это допустимо.
- Для нового `Импорт данных` это не лучший фундамент, потому что команда должна читать не только `FamilySymbol.Name`, но и `FamilyManager.Types`, `FamilyParameter`, значения параметров и статусы качества.
- Временная загрузка в активный проект создаёт лишние зависимости от состояния проекта, имени уже загруженного семейства и поведения rollback-транзакции.
- Для batch extraction такой подход тяжелее контролировать и сложнее объяснять пользователю.

Для `Импорт данных` предпочтительно не временно загружать семейство в активный проект, а открыть `.rfa` как invisible document:

- `Application.OpenDocumentFile(string fileName)` открывает документ в память и не показывает пользователю.
- Для family document доступен `Document.FamilyManager`.
- После чтения нужно закрыть документ через `Document.Close(false)`.

Преимущества:

- Не загрязняет активный проект.
- Не требует rollback transaction для загрузки.
- Не зависит от имени уже загруженного семейства в проекте.
- Лучше подходит для batch extraction.

Ограничения:

- Нельзя открыть `.rfa`, сохранённый в более новой версии Revit.
- Документ нужно гарантированно закрывать в `finally`.
- Не хранить `Document`, `FamilyType`, `FamilyParameter` после выполнения ExternalEvent.

Целевое решение:

- Старую команду `ExtractTypes` не развивать дальше как источник правды.
- Новая команда `Импорт данных` должна использовать `Application.OpenDocumentFile` в реализации `RevitFamilyDataExtractionService`.
- `Document.LoadFamily + RunAndRollback` оставить только как legacy/fallback для сценариев размещения или если будет обнаружено ограничение `OpenDocumentFile` на конкретной версии Revit.
- Сохранение результатов в SQLite должно происходить уже после извлечения pure DTO, без удержания Revit API objects.

### 7.3. Что читать через FamilyManager

Revit API:

- `Document.FamilyManager` — доступен для family document.
- `FamilyManager.Types` — все `FamilyType`.
- `FamilyManager.Parameters` — все `FamilyParameter`.
- `FamilyParameter.Definition.Name` — имя параметра.
- `FamilyParameter.IsInstance` — instance/type nature.
- `FamilyParameter.StorageType` — тип хранения.
- `FamilyParameter.GetUnitTypeId()` — unit id для Revit 2021+.
- `FamilyType.AsString(parameter)`.
- `FamilyType.AsDouble(parameter)`.
- `FamilyType.AsInteger(parameter)`.
- `FamilyType.AsElementId(parameter)`.
- `FamilyType.AsValueString(parameter)` для отображения числовых значений.
- `FamilyType.HasValue(parameter)`.

### 7.4. Exact matching rule

Для первой версии:

- Match только по `AttributeDefinition.Name == FamilyParameter.Definition.Name`.
- Без алиасов.
- Без fuzzy matching.
- Без автоперевода RU/EN.

Рекомендуемая детализация:

- Не трогать внутренние пробелы.
- Перед сравнением можно trim-ить начало/конец, если это не противоречит UX.
- Case sensitivity нужно зафиксировать в реализации. Для максимально строгого поведения использовать `StringComparer.Ordinal`. Если будет слишком жёстко, можно отдельно согласовать `OrdinalIgnoreCase`.

### 7.5. Извлечение type-level значений

Алгоритм:

1. Получить effective bindings для category id семейства.
2. Открыть `.rfa` invisible document.
3. Получить список `FamilyParameter`.
4. Построить map `parameterName -> FamilyParameter`.
5. Получить `FamilyManager.Types`.
6. Для каждого `FamilyType`:
   - сохранить/обновить `family_types`.
   - для каждого expected attribute:
     - найти параметр по точному имени;
     - если параметр не найден: сохранить `MissingParameter`;
     - если найден, но `HasValue == false`: сохранить `EmptyValue`;
     - иначе прочитать значение по `StorageType`;
     - сохранить `Found` или `UnsupportedStorageType`/`ReadError`.
7. Закрыть family document без сохранения.
8. Транзакционно заменить старый snapshot values для этой item/version.
9. Обновить import run summary.

### 7.6. Family-level значения

Scope (`Type`/`Instance`) определяется автоматически через `FamilyParameter.IsInstance`
при извлечении и записывается в `ExtractedAttributeValue.ParameterScope`.

Для первого vertical slice:
- Все значения читаются через `FamilyType` per-type.
- Если параметр instance, `FamilyType` всё равно может вернуть значение — записать его.
- Family-level как отдельная концепция (TypeId = null) откладывается до следующей версии.

---

## 8. Сервисы и слои

### 8.1. Core models

Добавить в `SmartCon.Core/Models/FamilyManager/`:

- `AttributeDefinition`.
- `CategoryAttributeBinding`.
- `EffectiveCategoryAttribute`.
- `ExtractedAttributeValue`.
- `FamilyDataImportRun`.
- Enums:
  - `AttributeValueKind`.
  - `AttributeScope`.
  - `AttributeValueStatus`.
  - `FamilyDataImportStatus`.

### 8.2. Core interfaces

Добавить в `SmartCon.Core/Services/Interfaces/`:

- `IAttributeDefinitionRepository`.
- `ICategoryAttributeBindingService`.
- `IAttributeValueRepository`.
- `IFamilyDataImportRunRepository`.
- `IFamilyDataExtractionService` — Revit-side extractor contract, returns pure Core DTO snapshot.
- `IFamilyDataImportService` — orchestrates resolve file -> extraction -> save snapshot.
- `IFamilyMetadataPackageService` — JSON import/export package builder/parser.

Разделение:

- `IFamilyDataExtractionService` реализуется в `SmartCon.Revit`.
- SQLite repositories реализуются в `SmartCon.FamilyManager/Services/LocalCatalog`.
- Orchestration может жить в `SmartCon.FamilyManager`, но вызов Revit extractor должен происходить внутри ExternalEvent.

### 8.3. Revit layer

Добавить в `SmartCon.Revit/FamilyManager/`:

- `RevitFamilyDataExtractionService`.

Ответственность:

- открыть `.rfa` через `Application.OpenDocumentFile`;
- прочитать family types;
- прочитать значения expected attributes;
- вернуть pure DTO;
- закрыть документ без сохранения;
- не писать SQLite напрямую.

### 8.4. FamilyManager layer

Добавить в `SmartCon.FamilyManager/Services/LocalCatalog/`:

- `LocalAttributeDefinitionRepository`.
- `LocalCategoryAttributeBindingService`.
- `LocalAttributeValueRepository`.
- `LocalFamilyDataImportRunRepository`.
- `LocalFamilyMetadataPackageService`.
- `FamilyDataImportService` или orchestration handler.

Обновить:

- `LocalCatalogMigrator` -> `MigrateV6`.
- `FamilyCatalogSql` -> новые tables/indexes.
- `ServiceRegistrar` -> DI registrations.
- `FamilyManagerMainViewModel` -> заменить `ExtractTypesCommand` на `ImportDataCommand`.
- `FamilyPropertiesViewModel` -> загружать types + values + quality status.

---

## 9. Команда `Импорт данных`

### 9.1. Entry points

1. Контекстное меню family item:
   - Было: `Извлечь типоразмеры`.
   - Стало: `Импорт данных`.
   - Единственный entry point для импорта в первой версии.

2. Будущий batch mode:
   - Для категории: `Импорт данных для всех семейств категории`.
   - Для всей БД: `Переиндексировать данные`.
   - В первый vertical slice можно заложить интерфейс, но не обязательно реализовывать batch UI.

**Запрещено:** кнопки импорта во вкладке «Атрибуты» окна свойств — Revit API
нельзя надёжно вызывать из модального WPF-окна. Вкладка свойств — только read-only просмотр.

### 9.2. Что делает команда

- Resolve `.rfa` через `IFamilyFileResolver` для текущей Revit версии.
- Получает category id семейства.
- Получает effective bindings категории.
- Если bindings пустые:
  - всё равно может извлечь типоразмеры;
  - UI показывает `Для категории нет обязательных атрибутов`.
- В ExternalEvent вызывает Revit extractor.
- Сохраняет:
  - `family_types`;
  - `extracted_attribute_values`;
  - `family_data_import_runs`;
  - counts в `catalog_versions`, если нужно.
- Обновляет дерево и свойства.

### 9.3. Data freshness

Данные считаются устаревшими, если:

- изменилась текущая версия/sha файла;
- изменился category binding после последнего import run;
- изменился `AttributeDefinition.Name` для атрибута, участвующего в bindings;
- семейство перемещено в другую категорию с другим effective set.

UI должен показывать:

- `Данные актуальны`.
- `Данные не импортированы`.
- `Данные устарели: изменены правила атрибутов`.
- `Данные устарели: изменилась версия файла`.

---

## 10. UI/UX: редактор категорий и атрибутов

### 10.1. Layout — Master-Detail без вкладок

Единое окно «Категории и атрибуты». Слева дерево категорий, справа —
панель атрибутов выбранной категории. Никаких TabControl справа.

```text
+----------------------------------------------------------+
| Категории и атрибуты          [Импорт v] [Экспорт v]  [X] |
+----------------------+-----------------------------------+
| Дерево категорий     | Атрибуты: Насосы > Центробежные   |
|                      |                                   |
| - ОВК                | Группа: [Все атрибуты ▼]         |
| - Насосы ←выбрана    | 🔍 Поиск атрибутов...            |
|   - Центробежные     |                                   |
|   - Погружные        | ☑ Давление     (наследован)      |
| - Арматура           | ☑ Расход        (наследован)      |
|                      | ☐ Мощность                       |
| [+Root][+Child]      | ☐ Материал                        |
| [Rename][Delete]     |                                   |
|                      | [Новый атрибут: _________ ] [+]   |
+----------------------+ [Библиотека атрибутов...]          |
+----------------------------------------------------------+
```

Левая панель:
- TreeView категорий. Существующие команды: Add root, Add child, Rename, Delete.
- Выбор категории мгновенно обновляет правую панель.

Правая панель (без вкладок — один скроллящийся список):
- Заголовок с путём выбранной категории.
- Dropdown «Группа» для фильтрации атрибутов по полю `Group`.
- TextBox «Поиск» для фильтрации по имени.
- Список **всех атрибутов БД** с CheckBox-ами:
  - ☑ = привязан (binding существует).
  - ☐ = не привязан.
  - «(наследован)» — привязан на родительской категории, CheckBox checked и disabled.
  - CheckBox toggle мгновенно создаёт/удаляет binding.
- Inline-поле «Новый атрибут» внизу: вводишь имя → Enter → атрибут создаётся в библиотеке и сразу привязывается.
- Кнопка «Библиотека атрибутов...» — открывает отдельное окно.

### 10.2. Создание атрибута — inline

Flow:

1. В правой панели внизу — поле ввода с placeholder «Новый атрибут».
2. Пользователь вводит имя → Enter.
3. Создаётся `AttributeDefinition` с `Name = введённое имя`, `Group = null`.
4. Автоматически создаётся `CategoryAttributeBinding` для текущей категории.
5. Чек-бокс нового атрибута сразу = ☑.

### 10.3. Библиотека атрибутов — отдельное окно

Модальное окно, открываемое кнопкой «Библиотека атрибутов...»:

- Список всех атрибутов БД.
- Поиск по имени.
- Редактирование: переименовать, задать/изменить `Group`, деактивировать.
- Удаление (с подтверждением, если есть bindings).
- Показ, в каких категориях используется.

### 10.4. Экспорт/Импорт — dropdown-кнопки в toolbar

Toolbar окна содержит две dropdown-кнопки:

**Экспорт** (dropdown):
- «Категории» — экспорт только дерева.
- «Атрибуты» — экспорт только словаря.
- «Всё (категории + атрибуты + привязки)» — полный пакет.

**Импорт** (dropdown):
- «Из JSON...» — выбор файла, merge/upsert, brief summary в статусе.

Никаких отдельных вкладок, preview-wizard и viewer-ов.

### 10.5. Импорт — алгоритм

1. Пользователь нажимает «Импорт → Из JSON...».
2. Выбирает файл.
3. Система парсит package, определяет секции (categories/attributes/bindings).
4. Merge/upsert: категории по path, атрибуты по name, bindings по (categoryPath + attributeName).
5. После завершения — статус: «Импортировано: 5 категорий, 3 атрибута, 8 привязок».
6. Дерево обновляется.

---

## 11. UI/UX: вкладка `Атрибуты` в свойствах семейства

### 11.1. Расположение type selector

Рекомендация: разместить выпадающий список типоразмера внутри вкладки `Атрибуты`, в верхней sticky/compact зоне.

Причина:

- Типоразмер нужен именно для просмотра атрибутов.
- Верхнее окно свойств не перегружается.
- Если пользователь находится на вкладке `Общие` или `Контент`, type selector не отвлекает.

### 11.2. Layout вкладки

Вкладка **строго read-only**. Никаких кнопок импорта — импорт только через
контекстное меню семейства в основной панели.

Если данные есть:

```text
[Status card]
Данные импортированы: 2026-05-04 18:30 | Полнота: 8/10 | 2 проблемы

Типоразмер: [ DN50 / PN16 v ]

Required attributes
+------------------------------------------------------+
| Давление        | 1.6 МПа        | OK                |
| Расход          | —              | Parameter missing |
| Материал        | Сталь          | OK                |
+------------------------------------------------------+

Diagnostics
- Параметр `Расход` не найден в типе `DN50 / PN16`.
```

Если данных нет:

```text
Атрибуты ещё не импортированы.

Используйте команду «Импорт данных» из контекстного меню
семейства в основной панели.
```

### 11.3. Состояния вкладки

1. **Нет категории**
   - `Семейству не назначена категория. Назначьте категорию, чтобы определить обязательные атрибуты.`

2. **В категории нет bindings**
   - `Для категории нет обязательных атрибутов.`

3. **Данные не импортированы**
   - `Атрибуты ещё не импортированы. Используйте команду «Импорт данных» из контекстного меню.`

4. **Данные устарели**
   - `Правила атрибутов изменились после последнего импорта. Используйте команду «Импорт данных» для обновления.`

5. **Есть типы и значения**
   - Показать type selector и строки атрибутов.

6. **Нет типоразмеров**
   - `В семействе не найдены типоразмеры.`
   - Если есть family-level значения, показать их.

### 11.4. Визуальный стиль

Ориентир: текущий современный стиль context menu в FamilyManager.

Принципы:

- Карточки со скруглением и мягкой тенью/бордером, если это уже есть в theme.
- Badges: `Required`, `Inherited`, `Missing`, `Empty`, `Stale`.
- Цвет не должен быть единственным индикатором — статус всегда текстом.
- Не подключать MaterialDesignThemes/MahApps/HandyControl.
- Использовать текущие `SmartCon.UI` resources.

---

## 12. Порядок реализации

### Phase A. ADR + модель + миграция

**Статус:** Not started

**Результат:**

- ADR `FamilyManager Attribute Extraction Architecture`.
- Core models/enums (упрощённые: `AttributeDefinition` с 5 полями, `CategoryAttributeBinding` с 5 полями).
- Core interfaces.
- SQLite schema v6.
- Миграция legacy `attribute_presets` -> `attribute_definitions` + `category_attribute_bindings`.
- Unit tests на migration/repositories.

**Acceptance:**

- Новая пустая БД создаёт schema v6.
- Старая БД v5 мигрирует без потери категорий/пресетов.
- Effective attributes для `Насосы > Центробежные` наследуются от `Насосы`.
- Отключение inherited attribute в дочерней категории работает.

---

### Phase B. JSON package import/export

**Статус:** Not started

**Результат:**

- `FamilyManagerMetadataPackage` model (version 2, с `attributeName` вместо `attributeKey`).
- Export categories only.
- Export attributes only.
- Export categories + attributes + bindings.
- Merge/upsert import (без preview-wizard).

**Acceptance:**

- Roundtrip package сохраняет дерево категорий, словарь и bindings.
- Bindings переносятся через `categoryPath` и `attributeName`, а не через IDs.
- Если category path не найден, binding пропускается с понятным warning.
- Пользователь может импортировать только attributes без изменения categories.

---

### Phase C. Revit extractor

**Статус:** Not started

**Результат:**

- `RevitFamilyDataExtractionService` в `SmartCon.Revit/FamilyManager`.
- Открытие `.rfa` через `Application.OpenDocumentFile`.
- Чтение `FamilyManager.Types`.
- Чтение expected attributes по точному имени.
- Auto-detect `StorageType`, `ParameterScope`, `UnitTypeId`.
- Закрытие document в `finally` без сохранения.

**Acceptance:**

- Для тестового семейства с 3 типами извлекаются 3 type records.
- Для атрибута `Давление` значение сохраняется отдельно для каждого типа.
- Отсутствующий параметр даёт `MissingParameter`, а не падение команды.
- Более новая версия `.rfa` даёт понятную ошибку/статус.

---

### Phase D. Command `Импорт данных`

**Статус:** Not started

**Результат:**

- Переименовать локализацию `FM_ExtractTypes` -> `FM_ImportData`.
- Команда вызывает новый pipeline.
- Старый `ExtractTypes` перестаёт быть источником правды.
- После импорта дерево обновляет type nodes.
- Entry point: **только** контекстное меню (не вкладка свойств).

**Acceptance:**

- Контекстное меню показывает `Импорт данных`.
- Команда сохраняет типы и атрибуты.
- При повторном запуске старые значения заменяются транзакционно.
- UI показывает summary: types count, found/missing attributes.

---

### Phase E. Category & Attributes Editor UI (Master-Detail)

**Статус:** Not started

**Результат:**

- Расширение текущего `CategoryTreeEditor` до единого окна `Категории и атрибуты`.
- Слева — TreeView категорий (без изменений).
- Справа — панель атрибутов выбранной категории **без вкладок**:
  - Dropdown «Группа» для фильтрации по полю `Group`.
  - TextBox «Поиск» для фильтрации по имени.
  - Список всех атрибутов БД с CheckBox-ами (☑ привязан, ☐ не привязан, ☑ disabled = наследован).
  - Inline-поле «Новый атрибут» (одно поле — Name → Enter).
- Кнопка «Библиотека атрибутов...» → отдельное модальное окно.
- Dropdown-кнопки «Экспорт» и «Импорт» в toolbar (без отдельной вкладки).

**Acceptance:**

- Пользователь создаёт атрибут `Давление` через inline-поле (одно поле ввода).
- Привязывает его к категории `Насосы` через CheckBox.
- Подкатегория `Центробежные` видит inherited `Давление` (CheckBox checked + disabled).
- Export/import переносит category + attribute + binding в другую БД.
- Основная панель FamilyManager не получает набор новых кнопок.

---

### Phase F. Family Properties Attributes tab (read-only)

**Статус:** Not started

**Результат:**

- `FamilyPropertiesViewModel` загружает:
  - effective attributes;
  - types;
  - extracted values;
  - latest import run/status.
- Вкладка показывает type selector.
- Строки атрибутов показывают value/status/message.
- **Никаких кнопок импорта** — вкладка строго read-only.
- Если данных нет — текст: «Используйте команду Импорт данных из контекстного меню».

**Acceptance:**

- Для семейства `Насос` с типами `DN50`, `DN80` пользователь выбирает тип и видит разные значения.
- Missing/empty values видны как предупреждения.
- Если данных нет, вкладка показывает информационный текст, а не ложный `—`.

---

### Phase G. Tests, docs, polish

**Статус:** Not started

**Результат:**

- Unit tests repositories/services.
- ViewModel tests.
- JSON roundtrip tests.
- Manual Revit smoke checklist.
- Docs updated.

**Acceptance:**

- `dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25` проходит.
- Ручная сборка по правилам multi-version не ломается.
- `docs/domain/models.md` и `docs/domain/interfaces.md` обновлены.
- ADR добавлен.

---

## 13. Тест-план

### 13.1. Unit tests

- `AttributeDefinitionRepository_CreateUpdateDelete`.
- `CategoryAttributeBindingService_InheritsParentAttributes`.
- `CategoryAttributeBindingService_ChildOverridesParentBinding`.
- `CategoryAttributeBindingService_ChildCanDisableInheritedAttribute`.
- `AttributeValueRepository_SaveAndReplaceSnapshot`.
- `AttributeValueRepository_QueryByType`.
- `FamilyMetadataPackage_ExportImport_Roundtrip_AllSections`.
- `FamilyMetadataPackage_ImportAttributesOnly_DoesNotTouchCategories`.
- `FamilyMetadataPackage_BindingsUseCategoryPath`.
- `MigrateV6_LegacyAttributePresets_AreConvertedToDefinitionsAndBindings`.

### 13.2. ViewModel tests

- Вкладка `Атрибуты` показывает empty state, если нет bindings.
- Вкладка показывает информационный текст, если значений нет (без кнопок).
- Изменение selected type обновляет visible values.
- Missing parameter отображается как warning.
- Quality summary считает found/missing корректно.
- Editor: CheckBox toggle создаёт/удаляет binding.
- Editor: inline-поле создаёт атрибут + binding за один Enter.
- Editor: inherited атрибуты отображаются как checked + disabled.

### 13.3. Manual Revit smoke tests

Подготовить `.rfa`:

- `Pump_Test.rfa`.
- Типы: `DN50`, `DN80`, `DN100`.
- Параметры типа:
  - `Давление`.
  - `Расход`.
  - `Мощность`.
- Один параметр специально удалить/оставить пустым для проверки warning.

Сценарий:

1. Создать БД FM.
2. Создать категории `Насосы > Центробежные`.
3. Создать атрибуты и bindings на `Насосы`.
4. Импортировать семейство.
5. Запустить `Импорт данных`.
6. Открыть свойства.
7. Проверить type selector и значения для каждого типа.
8. Экспортировать categories + attributes + bindings.
9. Создать новую БД.
10. Импортировать JSON.
11. Проверить, что bindings восстановились по paths.

---

## 14. Риски и решения

### Risk 1. Текущий `family_types` не version-aware

**Проблема:** значения атрибутов могут относиться не к той версии семейства.  
**Решение:** schema v6 расширяет `family_types` и values полями `version_id/file_id/source_sha256`.

### Risk 2. Открытие `.rfa` другой версии Revit

**Проблема:** Revit не откроет файл из более новой версии.  
**Решение:** использовать `IRevitFileInfoReader`/`BasicFileInfo`, показывать `Skipped/Failed` с понятным сообщением.

### Risk 3. Instance parameters в family document

**Проблема:** пользователь ожидает type-specific values, но часть параметров instance.  
**Решение:** записывать `ParameterScope` и `FamilyParameter.IsInstance`; в UI показывать badge `Instance`. Для первой версии читать через `FamilyType` там, где API возвращает значение.

### Risk 4. Точное имя приведёт к большому числу missing

**Проблема:** реальные библиотеки имеют разные имена параметров.  
**Решение:** это осознанное решение первой версии. UI должен хорошо показывать missing. Алиасы можно добавить v2 без ломки модели через `attribute_aliases`.

### Risk 5. JSON bindings при переименовании категорий

**Проблема:** bindings переносятся по path, path меняется при переименовании категории.  
**Решение:** package может хранить и `categoryPath`, и optional `categoryStableKey` в будущем. Для первой версии path достаточно, но import preview должен явно показывать unresolved bindings.

### Risk 6. Пользователь случайно заменит дерево категорий

**Проблема:** текущий import дерева делает replace all.  
**Решение:** новый import wizard default — merge/upsert. Replace доступен только с явным подтверждением.

### Risk 7. Производительность batch extraction

**Проблема:** открытие каждого `.rfa` может быть долгим.  
**Решение:** первый vertical slice — selected family. Batch queue в следующей фазе; schema уже поддерживает import runs и progress.

---

## 15. Что не входит в первый vertical slice

- Поиск/фильтры в основной панели по значениям атрибутов.
- Алиасы параметров.
- Fuzzy matching.
- Автоматический перевод RU/EN названий.
- Rule engine качества BIM-стандарта.
- Автоматическое исправление `.rfa`.
- Серверный provider.
- Role/approval workflow.
- AI/semantic search.
- Batch import всей БД как polished UX, если не останется времени.

---

## 16. Документация, которую нужно обновить при реализации

- `docs/adr/XXX-familymanager-attribute-extraction.md` — новое ADR.
- `docs/domain/models.md` — новые модели.
- `docs/domain/interfaces.md` — новые интерфейсы.
- `docs/family-manager/README.md` — ссылка на новую фичу.
- `docs/family-manager/01-mvp/04-domain-model.pplx.md` — уточнить атрибутную модель.
- `docs/family-manager/01-mvp/05-metadata-schema.pplx.md` — schema v6.
- `docs/family-manager/01-mvp/07-ux-ia.pplx.md` — вкладка `Атрибуты`, type selector, import/export UX.

---

## 17. Открытые вопросы для агента-разработчика

Перед реализацией каждой фазы агент **обязан** задать пользователю
соответствующие вопросы из этого списка. План намеренно не даёт ответов —
решение принимается по месту с учётом текущего состояния кода.

### Фаза A (модель + миграция)

- **Q-A1.** Какую модель данных использовать для computed effective attributes
  (результат наследования bindings по дереву категорий)?
  В плане упоминается `EffectiveCategoryAttribute`, но точные поля не определены.
  Нужно изучить текущий `IAttributePresetService.GetEffectiveParametersAsync`
  и решить, достаточно ли аналогичного подхода.

- **Q-A2.** Нужны ли точные SQL DDL для новых таблиц,
  или достаточно имен таблиц и моделей из секции 4?
  Если DDL нужен — составить по моделям и утвердить.

- **Q-A3.** Нужно ли расширять `family_types` полями `version_id`/`file_id`
  в этом релизе, или отложить до появления реальной потребности в version-awareness?

### Фаза B (JSON)

- **Q-B1.** Case-sensitive или case-insensitive matching для имён атрибутов
  при импорте JSON? Это влияет и на matching при извлечении из Revit.

### Фаза C (Revit extractor)

- **Q-C1.** Как обрабатывать `GetUnitTypeId()` для Revit < 2021?
  Проект поддерживает R19 (2019-2020), где этого метода нет.
  Нужен `#if` или fallback на `DisplayUnitType`.

- **Q-C2.** `FamilyDataImportRun` содержит 14 полей.
  Можно ли упростить для MVP (например, убрать счётчики и вычислять из values)?

### Фаза E (UI редактора)

- **Q-E1.** Что показывает правая панель, если категория не выбрана?
  Пустое состояние / подсказка / disabled?

- **Q-E2.** Что происходит при вводе дублирующегося имени атрибута
  в inline-поле «Новый атрибут»? Ошибка / автопривязка существующего / предложение переименовать?

- **Q-E3.** Что происходит при удалении атрибута, у которого есть bindings?
  Cascade delete / запрет с сообщением / отвязка?

- **Q-E4.** Что происходит при удалении атрибута, у которого есть extracted values?
  Запрет / cascade / soft-delete через `IsActive`?

- **Q-E5.** CheckBox toggle — мгновенная запись в SQLite на каждый клик,
  или накопление изменений с сохранением по кнопке?

- **Q-E6.** Нужна ли спецификация ViewModel для нового редактора
  (названия классов, свойства, команды), или агент проектирует самостоятельно
  по layout из секции 10.1?

---

## 18. Definition of Done

Фича считается готовой, если:

1. Пользователь может в БД создать атрибут через **одно поле** — «Название» (inline в правой панели).
2. Пользователь может привязать атрибуты к категориям через **CheckBox** (без форм).
3. Дочерние категории наследуют атрибуты родителя (CheckBox checked + disabled).
4. Пользователь может экспортировать/импортировать категории, атрибуты и bindings через dropdown-кнопки (без отдельной вкладки).
5. Команда `Импорт данных` извлекает типоразмеры и значения атрибутов (только через контекстное меню).
6. Тип данных параметра (ValueKind), scope (Type/Instance), единицы — **auto-detect** при извлечении.
7. В окне свойств (вкладка «Атрибуты») — **read-only**: type selector + значения, без кнопок импорта.
8. Missing/empty атрибуты показываются как non-blocking quality warnings.
9. Схема БД готова к будущему поиску по `attribute_id + value_text/value_number`.
10. Нет вызовов Revit API из WPF/ViewModel вне ExternalEvent.
11. `SmartCon.Core` не вызывает Revit API и не зависит от WPF.
12. Тесты и документация обновлены.

---

## 19. Рекомендуемая следующая точка обсуждения перед реализацией

Вопросы, решённые до начала реализации:

1. ~~Точный набор полей `AttributeDefinition`.~~ **Утверждено:** Id, Name, Group, IsActive, CreatedAtUtc.

Остальные открытые вопросы — см. секцию 17. Агент задаёт их по мере продвижения по фазам.
