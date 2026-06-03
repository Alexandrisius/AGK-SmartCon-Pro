# ADR-023: FamilyManager Type-Centric Workflow

**Status:** accepted
**Date:** 2026-06-04
**Supersedes:** ADR-017 (partially — ADR-017 remains valid for attribute extraction foundation)

## Context

FamilyManager (ADR-015/017) хранил каталог семейств как набор файлов `.rfa`. Каждая запись каталога = одно семейство. Это создавало фундаментальные проблемы:

1. **Несоответствие бизнес-модели**: В Revit MEP инженер работает с *типами* (типоразмерами) — "Насос Grundfos 32-40", а не с абстрактным "Насос Grundfos.rfa". Один `.rfa` может содержать 50+ типов.

2. **Type Catalog как неотъемлемая часть**: Autodesk поставляет официальные семейства с `.txt` sidecar-файлами (Type Catalog), содержащими параметры всех типов. Без их обработки каталог бессмысленен — параметры внутри `.rfa` часто не имеют значений (все типы созданы через `.txt`).

3. **Семейства без типов**: Некоторые `.rfa` (например, `AC_Дверь_Двупольная_Деревянная.rfa`) имеют `FamilyManager.Types.Size == 0`. Вся информация о типах в `.txt`, но в `.rfa` нет ни одного материализованного типа.

4. **Drag & Drop неясен**: Команда "Разместить семейство" не имела смысла без выбора конкретного типа.

## Decisions

### FM-023-001: Type-Centric Mental Model

Каталог хранит **типы**, а не семейства. Каждый тип — запись в `family_types` с FK на `catalog_items`. UI отображает дерево: Family → Type. Drag & Drop оперирует типом.

**Alternative considered**: Оставить семейство-центричную модель, добавить type selector в DnD dialog. Отклонено: добавляет friction (лишний клик), не решает проблему Type Catalog.

### FM-023-002: Type Catalog (.txt) is the Source of Truth

При импорте семейства:
1. Ищем `.txt` sidecar-файл рядом с `.rfa`
2. Парсим `.txt` → получаем имена типов и значения параметров
3. Типы из `.txt` записываем в `family_types` и `extracted_attribute_values`
4. **Type Catalog types take precedence** над встроенными типами `.rfa`
5. Экстракция из `.rfa` (через Revit API) — **merge** mode: добавляет отсутствующие значения к существующим типам из `.txt`

**Rationale**: В официальных библиотеках Autodesk `.txt` содержит актуальные данные, `.rfa` — только "пустые" типы-заглушки. Type Catalog редактируется пользователем, `.rfa` — read-only managed storage.

### FM-023-003: Universal Encoding Detection

Type Catalog файлы приходят в непредсказуемых кодировках (Windows-1251 для русских библиотек, Big5 для китайских, UTF-8 без BOM).

**Решение**: Интеграция библиотеки `UTF.Unknown` (алгоритм Mozilla Universal Charset Detector):
1. Сначала пробуем UTF-8 без BOM
2. При обнаружении replacement chars (`U+FFFD`) — запускаем `CharsetDetector.DetectFromBytes()`
3. Confidence > 0.7 → используем detected encoding
4. Иначе fallback на system ANSI codepage

**Alternative considered**: Жёстко кодировать Windows-1251 для русских библиотек. Отклонено: не масштабируется, ломается на UTF-8 файлах.

### FM-023-004: RFC 4180 CSV Parser

Type Catalog — это CSV-файл с особенностями:
- Header: имя параметра, единицы, тип данных
- Rows: имя типа, значения через запятую
- Значения могут содержать кавычки, запятые, переносы строк

**Решение**: Чистый C# парсер `TypeCatalogParser` (без внешних CSV-библиотек), реализующий RFC 4180:
- Корректная обработка escaped quotes (`""`)
- Поддержка quoted fields с запятыми внутри
- Пропуск пустых строк и комментариев

**Alternative considered**: Использовать `CsvHelper` NuGet. Отклонено: лишняя зависимость, наша задача специфичнее generic CSV (header structure отличается).

### FM-023-005: Virtual Types for Families Without Types

Семейства с `Types.Size == 0` (нет ни одного типа в `.rfa`) отображаются как **Virtual Type** — единственный тип-заглушка без имени (`TypeId = null`).

В UI: в дереве под семейством создаётся единственный узел "(без типов)". Параметры отображаются без type selector.

**Rationale**: Нельзя оставить семейство без типов в UI — пользователь не сможет разместить его. Нельзя создать реальный тип в `.rfa` — managed storage read-only.

### FM-023-006: Temporary FamilyType for Types.Size=0 Extraction

Для семейств без типов (`Types.Size == 0`) стандартная экстракция через `FamilyManager.Types` не работает — коллекция пуста, `CurrentType` is null.

**Решение**: В `RevitFamilyDataExtractionService.ExtractCore`:
```csharp
using (Transaction tx = new Transaction(familyDoc, "SmartCon_TempTypeExtraction"))
{
    tx.Start();
    var tempType = fm.NewType("_SmartConTemp");
    // Read all parameter values from tempType
    tx.RollBack(); // RFA untouched
}
```

**Rationale**: Revit материализует значения параметров по умолчанию только при создании типа. `OwnerFamily.LookupParameter()` возвращает null для всех параметров когда `Types.Size == 0`. Transaction + RollBack безопасен — файл `.rfa` не модифицируется.

**Exception to I-03**: Эта Transaction создаётся напрямую (не через `ITransactionService`), потому что:
1. Она read-only — только чтение значений
2. Она откатывается (`RollBack`) — никогда не сохраняется
3. Она работает с family document, а не с активным project document
4. `ITransactionService` предназначен для операций над активным документом пользователя

### FM-023-007: Per-Type Loading Strategy

Размещение типа через Drag & Drop использует разные Revit API в зависимости от типа:

| Тип | API | Почему |
|-----|-----|--------|
| Real (из Type Catalog или `.rfa`) | `LoadFamilySymbol` | Загружает конкретный FamilySymbol по имени, быстро, не засоряет проект лишними типами |
| Virtual (Types.Size == 0) | `LoadFamily` | В `.rfa` нет типов, загружаем всё семейство целиком |

**Alternative considered**: Всегда использовать `LoadFamily` для всех. Отклонено: загружает ВСЕ типы из `.rfa` в проект, засоряя модель. `LoadFamilySymbol` загружает только нужный тип.

### FM-023-008: Merge Mode for Extraction

Экстракция из `.rfa` (Revit API) работает в **merge** mode, не в **replace**:
1. Type Catalog создаёт типы и их значения (source of truth)
2. Экстракция добавляет значения только для отсутствующих параметров (`existingKeys.Contains(key)` → skip)
3. Не перезаписывает существующие значения

**Rationale**: Type Catalog содержит актуальные данные от производителя. Экстракция — "best effort" дополнение. Перезапись могла бы испортить данные из `.txt`.

### FM-023-009: Unmatched Types Propagation

Если `.rfa` содержит типы, которых нет в Type Catalog (или наоборот):
1. Значения несовпадающих типов не отбрасываются
2. Они пропагируются как **shared params** на ВСЕ существующие типы в каталоге

**Example**: Type Catalog имеет типы A, B. `.rfa` имеет типы A, C. Значения типа C пропагируются на A и B.

**Rationale**: Лучше иметь избыточные данные, чем потерять информацию. Инженер вручную выберет нужный тип.

### FM-023-010: Duplicate Type Name Deduplication

Type Catalog файлы иногда содержат дублирующиеся имена типов (баг в библиотеке производителя).

**Решение**: При парсинге `.txt`:
```csharp
var seenTypeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
if (!seenTypeNames.Add(entry.TypeName))
{
    // Skip duplicate, keep first occurrence
    continue;
}
```

**Rationale**: SQLite имеет UNIQUE constraint на `(catalog_item_id, type_name)`. Дубли вызывают `SQLite Error 19`. First-wins логика предсказуема — первое определение типа (обычно полное) имеет приоритет.

### FM-023-011: Remove "Place Family" Command

Команда "Разместить семейство" (без указания типа) удалена из UI.

**Rationale**: В типо-центричном подходе размещение без типа бессмысленно. Пользователь всегда выбирает конкретный тип в дереве и делает DnD.

### FM-023-012: Schema v10

Миграция БД:
- Добавлен индекс `ix_family_types_name` на `family_types(name)` для ускорения lookup по имени типа
- Версионное удаление в `SaveTypesForRunAsync`: при `IncrementVersion` старые типы удаляются перед вставкой новых (предотвращает UNIQUE constraint violation)

## SQLite Schema

### Таблица family_types (расширена)

```sql
CREATE TABLE family_types (
    id TEXT PRIMARY KEY,
    catalog_item_id TEXT NOT NULL,
    name TEXT NOT NULL,
    sort_order INTEGER DEFAULT 0,
    version_id TEXT,
    file_id TEXT,
    extraction_run_id TEXT,
    FOREIGN KEY (catalog_item_id) REFERENCES catalog_items(id),
    FOREIGN KEY (version_id) REFERENCES catalog_versions(id),
    FOREIGN KEY (file_id) REFERENCES family_files(id),
    FOREIGN KEY (extraction_run_id) REFERENCES family_data_import_runs(id),
    UNIQUE(catalog_item_id, name)
);

CREATE INDEX ix_family_types_name ON family_types(name);
```

### Таблицы Type Catalog values

Type Catalog значения хранятся в той же таблице `extracted_attribute_values` (ADR-017), что и экстракция из `.rfa`:
- `TypeId` = FK на `family_types.id` (для реальных типов)
- `TypeId = null` (для untyped/virtual значений)
- `Status = Found` (Type Catalog всегда имеет значения)
- `ExtractionRunId` = FK на `family_data_import_runs` (отдельный run для Type Catalog)

## Layers

| Layer | Responsibility |
|---|---|
| `SmartCon.Core/Models/FamilyManager/` | TypeCatalogEntry, TypeCatalogParseResult, enums |
| `SmartCon.Core/Services/Implementation/` | TypeCatalogParser (pure C# RFC 4180) |
| `SmartCon.Core/Services/Interfaces/` | IFamilyLoadService (LoadFamilySymbol/LoadFamily per-type) |
| `SmartCon.Revit/FamilyManager/` | RevitFamilyDataExtractionService (temporary type pattern), RevitFamilyLoadService (per-type loading), RevitFamilyPlacementService |
| `SmartCon.FamilyManager/Services/LocalCatalog/` | LocalFamilyImportService.TypeCatalog.cs (encoding detection, deduplication), FamilyDataImportService.cs (merge logic, propagation) |
| `SmartCon.FamilyManager/ViewModels/` | FamilyTypeNodeViewModel (tree nodes), FamilyManagerMainViewModel.LoadPlace.cs (per-type placement) |
| `SmartCon.FamilyManager/Views/` | FamilyManagerPaneControl.xaml (removed Place command), FamilyPropertiesView.xaml (virtual type display) |

## Consequences

### Positive

- FamilyManager теперь отражает реальную BIM-модель: инженер видит типоразмеры, а не абстрактные семейства
- Официальные библиотеки Autodesk (с `.txt`) работают из коробки — 300+ типов с полными параметрами
- Drag & Drop размещает конкретный тип, а не требует дополнительного выбора
- Семейства без типов (`Types.Size == 0`) корректно обрабатываются — виртуальный тип + экстракция через temporary type
- Universal encoding поддерживает мультиязычные библиотеки без конфигурации

### Negative

- Усложнение UI: дерево теперь двухуровневое (Family → Type), требует больше вертикального пространства
- Merge логика добавляет сложности: нужно отслеживать `existingKeys`, обрабатывать `skippedExisting`, `unmatchedSharedParams`
- Temporary type pattern — хак вокруг ограничения Revit API. Может сломаться в будущих версиях Revit
- Type Catalog precedence означает, что ручные изменения в `.rfa` (через Family Editor) могут быть проигнорированы при повторном импорте

### Risks

- **Encoding detection false positives**: `UTF.Unknown` confidence threshold 0.7 — может ошибиться на смешанных файлах. Мониторить логи `Charset detection failed`.
- **Temporary type side effects**: `fm.NewType("_SmartConTemp")` теоретически может триггернуть formula recalculation или parameter validation. Откат (`RollBack`) должен отменить всё, но Revit API не гарантирует полную изоляцию.
- **Duplicate deduplication data loss**: First-wins может отбросить более полное определение типа. В текущих библиотеках Autodesk дубли — идентичные строки, но это не гарантируется.

## References

- ADR-017: FamilyManager Attribute Extraction Foundation (schema v6, extraction foundation)
- ADR-015: FamilyManager Published Storage (managed storage, read-only files)
- docs/invariants.md I-01: Revit API thread safety
- docs/invariants.md I-03: Transaction via ITransactionService (exception documented in FM-023-006)
- Autodesk Revit API: `FamilyManager.NewType()`, `Transaction.RollBack()`
- UTF.Unknown: https://github.com/CharsetDetector/UTF-unknown (Mozilla Universal Charset Detector port)
- RFC 4180: Common Format and MIME Type for CSV Files

## Metrics

- Stress test: 23 семейства, 32 462 значения добавлено, 0 ошибок
- Type Catalog parsing: поддержка Windows-1251, UTF-8, UTF-16 LE/BE, Big5, Shift-JIS
- Log volume: 5800 строк (с диагностикой) → 148 строк (production)
- Tests: TypeCatalogParserTests (10 unit), TypeCatalogEncodingTests (4 integration), все существующие 1128/1128