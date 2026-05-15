# ADR-017: FamilyManager Attribute Extraction Foundation

**Status:** accepted
**Date:** 2026-05-06

## Context

FamilyManager (ADR-015, schema v5) хранит каталог семейств, но не извлекает и не индексирует технические параметры (давление, расход, мощность и т.д.). Это делает его каталогом файлов, а не индексируемой BIM content database.

Пользовательские сценарии:
1. BIM-manager создаёт словарь атрибутов и привязывает их к категориям
2. Content author импортирует семейства и видит полноту обязательных параметров
3. Инженер просматривает технические значения без открытия `.rfa` в Revit
4. Будущий поиск: `Насос` где `Давление >= X`

## Decisions

### FM-017-001: Attribute Dictionary per Database

Каждая БД имеет собственный словарь атрибутов (`attribute_definitions`). Нет глобального словаря на весь SmartCon.

`AttributeDefinition`: Id (GUID), Name (unique case-insensitive), Group (optional tag), IsActive, CreatedAtUtc.

### FM-017-002: Category Bindings with Inheritance

`CategoryAttributeBinding` связывает категорию с атрибутом. Binding хранит AttributeId, CategoryId, SortOrder, IsEnabled.

Effective bindings строятся walk-ом по ancestor chain: root → ... → selected category. Дочерняя категория может переопределить `IsEnabled` (отключить наследованный атрибут) и `SortOrder`.

### FM-017-003: Exact Name Matching

В первой версии match только по `AttributeDefinition.Name == FamilyParameter.Definition.Name`. Без алиасов, fuzzy matching, автоперевода RU/EN.

Unique constraint на `attribute_definitions.name` — `COLLATE NOCASE`.
Matching при извлечении из Revit — `StringComparer.Ordinal`.

### FM-017-004: Auto-detect Parameter Metadata

Тип данных, scope и единицы определяются автоматически при извлечении из Revit API:
- `ParameterScope` ← `FamilyParameter.IsInstance` (Instance/Type)
- `StorageType` ← `FamilyParameter.StorageType` (String/Double/Integer/ElementId)
- `UnitTypeId` ← `FamilyParameter.GetUnitTypeId()` (Revit 2021+, null для R19/R20)

Эти метаданные записываются в `ExtractedAttributeValue`, не в `AttributeDefinition`.

### FM-017-005: Non-blocking Quality Status

Отсутствующий параметр или пустое значение не блокирует импорт. Статусы: `Found`, `MissingParameter`, `EmptyValue`, `UnsupportedStorageType`, `ReadError`.

### FM-017-006: Type-aware Values

Значения читаются через `FamilyType` per-type. `TypeId` привязывает значение к конкретному типоразмеру. UI содержит type selector внутри вкладки Атрибуты.

### FM-017-007: Invisible Document Extraction

Извлечение использует `Application.OpenDocumentFile(fileName)` вместо временной загрузки в активный проект. Преимущества: не загрязняет проект, не требует rollback, лучше для batch.

### FM-017-008: Schema v6

4 новые таблицы: `attribute_definitions`, `category_attribute_bindings`, `family_data_import_runs`, `extracted_attribute_values`.
Расширение `family_types` полями `version_id`, `file_id`, `extraction_run_id`.
Миграция legacy `attribute_presets` → новые таблицы.

### FM-017-009: JSON Transfer Package v2

Новый формат: `sections` (categories/attributes/bindings), bindings через `categoryPath` + `attributeName` (не через IDs). Merge/upsert — единственный режим.

### FM-017-010: Read-only Attributes Tab

Вкладка «Атрибуты» в свойствах — строго read-only. Импорт данных — только через контекстное меню семейства (не через вкладку свойств).

## SQLite Schema v6

12 таблиц (8 существующих + 4 новых) + расширение `family_types`.

Существующие: database_meta, schema_info, catalog_items, catalog_versions, family_files, family_assets, catalog_tags, project_usage, categories, family_types, attribute_presets, attribute_preset_parameters.

Новые: attribute_definitions, category_attribute_bindings, family_data_import_runs, extracted_attribute_values.

## Layers

| Layer | Responsibility |
|---|---|
| `SmartCon.Core/Models/FamilyManager/` | AttributeDefinition, CategoryAttributeBinding, EffectiveCategoryAttribute, ExtractedAttributeValue, FamilyDataImportRun, enums |
| `SmartCon.Core/Services/Interfaces/` | IAttributeDefinitionRepository, ICategoryAttributeBindingService, IAttributeValueRepository, IFamilyDataImportRunRepository, IFamilyDataExtractionService, IFamilyDataImportService, IFamilyMetadataPackageService |
| `SmartCon.Revit/FamilyManager/` | RevitFamilyDataExtractionService (OpenDocumentFile, FamilyManager.Types, parameter matching) |
| `SmartCon.FamilyManager/Services/LocalCatalog/` | Local repositories + FamilyDataImportService orchestration |
| `SmartCon.FamilyManager/ViewModels/` | CategoryAttributesEditorViewModel, updated FamilyPropertiesViewModel |
| `SmartCon.FamilyManager/Views/` | CategoryAttributesEditorView, updated FamilyPropertiesView |

## Consequences

- FamilyManager становится индексируемой BIM content database
- Данные готовы к будущему attribute search без schema changes
- Legacy `attribute_presets` мигрируются автоматически
- Revit API вызывается только через ExternalEvent (I-01)
- Core не зависит от Revit API runtime (I-09)
