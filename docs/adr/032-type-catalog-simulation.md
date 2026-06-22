# ADR-032: Type Catalog Simulation — вычисление формул для типов из .txt

**Status:** superseded by ADR-033
**Date:** 2026-06-21
**Phase:** 25
**Issue:** [#66](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/66)
**Superseded by:** ADR-033 (Bake-in Type Catalog)
**Supersedes (partially):** ADR-023 §FM-023-006 — расширяет паттерн Temporary FamilyType с 1 типа на N типов

## Контекст

> **DEPRECATED:** ADR-032 заменён ADR-033. Для managed `.rfa` Type Catalog теперь запекается (bake-in) при импорте, а не симулируется при чтении. Симуляция остаётся только в истории как промежуточное решение issue #66.

### 1. Проблема (issue #66)

В FamilyManager при импорте семейства с `.txt`-каталогом типоразмеров значения параметров с формулами **одинаковые для всех типоразмеров**. Например, для воздуховода с формулой `w = 0.5 × Длина × Ширина × 2 × Фланец` все типоразмеры (`400×200`, `800×600`, `1200×800`) показывают одно и то же значение `w` (дефолтное из `.rfa`).

**Шаги воспроизведения** (из issue):

1. Открыть Family Manager
2. Подгрузить семейство с каталогом типоразмеров
3. Зайти в свойства семейства
4. Пролистать свойства каждого типоразмера — все значения одинаковые

### 2. Корневая причина

`RevitFamilyDataExtractionService.ExtractCore` (ADR-023-006) создаёт **один** временный тип `_SmartConTemp` и читает его значения параметров. Для семейств с `.txt` каталогом:

- В самом `.rfa` обычно `Types.Size == 0` (все типы живут в `.txt`)
- Создаётся `_SmartConTemp` с **дефолтными** значениями параметров
- Формулы в этот момент **не вычисляются** (нет Regenerate)
- `MergeMissingValuesAsync` (ADR-023-008) размазывает эти дефолтные значения на все типоразмеры из `.txt`

**Результат:** каждое значение формулы — это дефолт, одинаковый для всех типоразмеров.

### 3. Предыдущая попытка (откаченная в issue #66)

Предыдущий агент пытался реализовать симуляцию через `ITypeCatalogSimulationService` с повторным `OpenDocumentFile` уже открытого семейства. Две принципиальные проблемы (по комментарию автора issue):

1. **Повторное открытие `.rfa`** — Revit уже держит активный документ открытым, `app.OpenDocumentFile` создаёт конфликт. Решение автора — `SaveAs` в temp + `OpenDocumentFile` temp-файла. Нарушает требование «не открывать `.rfa` повторно».
2. **Модальный диалог Revit** при `Document.Regenerate()` — ошибка `InvalidOperationException` («Параметр w имеет недопустимое значение») не перехватывалась, пробивалась до UI.

### 4. Архитектурные ограничения

- **I-01** (потокобезопасность Revit API): все операции — только через `IFamilyManagerAwaitableEvent` callback
- **I-03 / I-03b**: `new Transaction(familyDoc, ...)` допустимо только для family document
- **I-05**: нельзя хранить `Element`/`Connector`/`FamilyType` между транзакциями
- **I-09**: `SmartCon.Core` НЕ вызывает Revit API, `Document` только как opaque parameter
- **ADR-023-002**: `.txt` = source of truth, типы из `.rfa` имеют приоритет ниже
- **ADR-023-008**: merge mode — экстракция дополняет, не заменяет

## Решение

### SIM-001: Симуляция ТОЛЬКО при наличии `.txt` каталога

Если у `.rfa` есть `.txt` рядом — типы из `.rfa` **полностью игнорируются**. `.txt` = single source of truth. Это **усиление** ADR-023-002: не «take precedence», а «исключительное использование».

**Алгоритм входа в `ExtractFromManagedFile`:**
1. `txtPath = Path.ChangeExtension(managedRfaPath, ".txt")`
2. `hasTypeCatalog = File.Exists(txtPath)`
3. `hasTypeCatalog ? ExtractWithTypeCatalog(doc, txtPath) : Extract(doc, [])` — fallback на legacy

### SIM-002: Per-type изоляция ошибок

Каждый типоразмер из `.txt` обрабатывается в **отдельном `try/catch`**. Битый типоразмер (любая причина: `Set` упал, `Regenerate` выбросил `InvalidOperationException`, `NewType` вернул null) **не валит** остальные. Все ошибки логируются с `[Action: ...]` (L9 из skill `smartcon-logging`).

**Критично:** это устраняет проблему предыдущей попытки — Revit больше **не показывает** модальный диалог пользователю, потому что исключение `Regenerate` ловится внутри per-type блока.

### SIM-003: Per-parameter изоляция внутри типа

Внутри одного типоразмера каждый `FamilyManager.Set` обёрнут в свой `try/catch`. Если `Set` упал на одном параметре (например, формула read-only или InvalidFormat значения) — пропускаем **этот параметр**, продолжаем с остальными.

### SIM-004: `Regenerate` один раз на тип

`Document.Regenerate()` вызывается **один раз после установки всех параметров** типа, а не после каждого `Set`. Это:
- Имитирует нормальный workflow Revit'а (пользователь меняет несколько параметров → один Regenerate)
- Быстрее (Regenerate дорогая операция)
- Снижает шанс ошибки (промежуточные состояния не вызывают валидацию)

### SIM-005: Один общий Transaction + финальный RollBack

```csharp
using (Transaction tx = new Transaction(familyDoc, "SmartCon_TypeCatalogSimulation"))
{
    try
    {
        tx.Start();
        // ... hot loop per-type ...
        tx.RollBack(); // Все временные типы исчезают, .rfa не изменён
    }
    catch
    {
        if (tx.GetStatus() == TransactionStatus.Started)
        {
            try { tx.RollBack(); } catch { /* ignore */ }
        }
        throw;
    }
}
```

Гарантия: исходный `.rfa` файл на диске **не изменяется ни на байт** (I-16 managed storage read-only + I-03b).

### SIM-006: Префикс `__SCAT__` для временных типов

Все временные типы создаются с префиксом `__SCAT__` (`SmartCon Catalog Temporary`). Причины:
- Некоторые `.rfa` содержат дефолтный тип с тем же именем, что в `.txt` — префикс исключает коллизию
- Логи становятся grep-friendly: `grep "__SCAT__" smartcon.log` показывает все симуляции
- При откате транзакции типы исчезают — префикс не «загрязняет» документ

### SIM-007: Чистая логика валидации в Core

Парсинг значений из `.txt` в типизированные значения (`double`/`int`/`string`/`ElementId`) вынесен в pure C# интерфейс `ITypeCatalogValueApplier` в `SmartCon.Core`. Это позволяет:
- Unit-тестировать без Revit API (skill `smartcon-testing` §"What cannot be mocked")
- Переиспользовать в других модулях (если понадобится)
- Не нарушать I-09 (Core не вызывает Revit API)

## Архитектура

### Слои

| Layer | Новые типы |
|---|---|
| `SmartCon.Core/Logging/` | `HotLoopCounter` (sampling утилита) |
| `SmartCon.Core/Services/Interfaces/` | `ITypeCatalogValueApplier` + `TypeCatalogValueApplyResult` |
| `SmartCon.Core/Services/Implementation/` | `TypeCatalogValueApplier` (pure C#) |
| `SmartCon.Core/Services/Interfaces/` | расширен `IFamilyDataExtractionService` — добавлен `ExtractFromManagedFile(path, names, ct)` |
| `SmartCon.Revit/FamilyManager/` | расширен `RevitFamilyDataExtractionService` — `ExtractFromManagedFile` + `ExtractWithTypeCatalog` |
| `SmartCon.App/DI/` | регистрация `ITypeCatalogValueApplier → TypeCatalogValueApplier` (Singleton) |
| `SmartCon.FamilyManager/ViewModels/` | новый `FamilyManagerMainViewModel.Extract.cs` (partial helper), 3 call site обновлены |
| `SmartCon.Tests/Core/Logging/` | `HotLoopCounterTests` (4 теста) |
| `SmartCon.Tests/Core/Services/` | `TypeCatalogValueApplierTests` (10+ тестов) |

### Алгоритм `ExtractWithTypeCatalog` (псевдокод)

```
1. ParseTypeCatalog(txtPath) → TypeCatalogParseResult
   - использует существующий TypeCatalogParser (ADR-023-004, 10 unit-тестов)
   - encoding detection через существующий UTF.Unknown helper (ADR-023-003)

2. Validation
   - если parseResult.Entries.Count == 0 → Warn + fallback на ExtractCore

3. Build paramMap
   - Dictionary<string, FamilyParameter> (Ordinal) из fm.Parameters
   - НЕ хранится между транзакциями (I-05)

4. Open Transaction("SmartCon_TypeCatalogSimulation")
   tx.Start()

5. HOT LOOP по parseResult.Entries (с HotLoopCounter для логов):
   for each (entry, rowIndex) in entries:
     try:
       tempType = fm.NewType("__SCAT__" + entry.TypeName)
       if tempType == null: continue (Warn + skipped++)

       // Per-parameter Set
       for each (columnName, value) in entry.Parameters:
         try:
           param = paramMap[columnName]
           if param == null: continue
           if param.IsReadOnly: continue
           
           applyResult = ITypeCatalogValueApplier.Apply(value, param.StorageType)
           if applyResult.Status != Success: continue (Warn)
           
           fm.Set(param, applyResult.Value)
         catch: Warn + continue

       // Per-type Regenerate
       try:
         familyDoc.Regenerate()
       catch: Warn + continue (errors++)

       // Read back values
       values = []
       for each param in fm.Parameters:
         if tempType.HasValue(param):
           values.Add(ExtractValueForParameter(tempType, ...))
       
       results.Add(new FamilyExtractionTypeValues(entry.TypeName, rowIndex, values))
     catch: Warn + continue

6. tx.RollBack() // .rfa не изменён

7. Return FamilyExtractionResult(results, ...)
```

## Call sites (3 места, единообразная замена)

**Было** (везде одинаково):
```csharp
var extraction = _extractionService.Extract(resolved.AbsolutePath, Array.Empty<string>());
```

**Стало**:
```csharp
var extraction = await ExtractFromManagedFileAsync(
    resolved.AbsolutePath, Array.Empty<string>(), CancellationToken.None);
```

Где `ExtractFromManagedFileAsync` — helper в новом `FamilyManagerMainViewModel.Extract.cs` (partial), оборачивает sync `_extractionService.ExtractFromManagedFile(...)` через `_awaitableEvent.RaiseAsync` (skill `revit-api-best-practice` §"AsyncBridge.RunSync DANGEROUS for Revit API").

**Места вызова** (все три заменяются одинаково):
- `FamilyManagerMainViewModel.FamilyEdit.cs:532` (`ExtractAttributesForLoadableTasks`)
- `FamilyManagerMainViewModel.FamilyEdit.cs:586` (`ExtractAttributesForImportedFamilies`)
- `FamilyManagerMainViewModel.Import.cs:216` (`ExtractTypesForImportedFamilies`)

## Логирование (skill `smartcon-logging`)

| Правило | Применение |
|---|---|
| L8 (basename в scope) | `Path.GetFileName(txtPath)` в логах |
| L9 (Warn → `[Action: ...]`) | 4 места: per-type Regenerate fail, per-parameter Set fail, parse fail, fm.NewType null |
| C15 (не оборачивать долгие методы) | `ExtractWithTypeCatalog` БЕЗ `BeginScope` (может >1 сек на 30+ типах) — `Info` START/END маркеры |
| Counter pattern | `HotLoopCounter` для per-type логов |
| Category vocabulary | `TypeCatalogSim` (новая категория, добавляем в vocabulary) |

## Тестирование (skill `smartcon-testing`)

### Unit-тесты (запускаются через `dotnet test`)

- `HotLoopCounterTests` (4): first/last/sample, edge case для total=0
- `TypeCatalogValueApplierTests` (10+): String/Integer/Double/ElementId парсинг, fallback на CurrentCulture для запятых, InvalidFormat/Unsupported, edge cases

### Integration-тесты (ручные в Revit 2025)

| # | Сценарий | Ожидаемый результат |
|---|---|---|
| 1 | Семейство воздуховода из issue #66 | Значения `w` разные для типоразмеров |
| 2 | `.txt` с 10 строками, у 8-й битая комбинация | 9 импортировано, 8-й в логе с `[Action: ...]`, NO диалог Revit |
| 3 | Семейство без `.txt` (regression) | Fallback на старый `Extract` |
| 4 | Обычный импорт 5 `.rfa` с `.txt` с диска | Все формулы корректные |
| 5 | Loadable family с `.txt` из проекта | Корректные значения |
| 6 | Untitled active family | Работает через temp + симуляция |
| 7 | Smoke: temp папка чистится | `%TEMP%\SmartCon\FMLoad\` пустая |

## Совместимость с версиями Revit

- Поддержка R19, R21, R24, R25 (multi-version build)
- `ITypeCatalogValueApplier` использует только `StorageType` enum — совместим со всеми версиями
- `Document.Regenerate()` доступен во всех поддерживаемых версиях
- `Transaction.RollBack()` доступен во всех версиях
- `Marshal.ReleaseComObject` — no-op в net8 (Revit API не real COM), не ломает net48

## Sources

- [Jeremy Tammik — Type Catalog and Lookup Tables](https://thebuildingcoder.typepad.com/blog/2014/09/lookup-table-and-type-catalog.html)
- [Autodesk Revit API — `FamilyManager.NewType` method](https://www.revitapidocs.com/2025/f0d4a79d-1f5d-7d76-3f53-bf3a638da5ae.htm)
- [Autodesk Revit API — `Document.Regenerate` method](https://www.revitapidocs.com/2025/b6fbc02e-2c3f-f7fa-b64b-0af036bc6243.htm)
- ADR-023 (Type-Centric Workflow) — SIM-001 усиливает FM-023-002
- ADR-024 (Active Family Import Preparer) — используем существующий `ActiveFamilyPreparationResult`
- ADR-005 (Formula Solver AST) — рассмотрен, отвергнут: покрывает только подмножество формул Revit, не масштабируется на редкие функции
- ADR-026 (Logging Migration) — scope-based logging
- Issue [#66](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/66)

## Consequences

### Positive

- **Issue #66 исправлен** для всех сценариев импорта (active file / files с диска / loadable из проекта)
- **Revit не показывает** модальные диалоги — ошибки изолированы и логируются
- **`.rfa` не изменяется** — финальный `RollBack` гарантирует
- **Регрессии закрыты** — fallback на старое поведение для семейств без `.txt`
- **Тестируемость** — pure C# логика (`TypeCatalogValueApplier`) покрыта 10+ unit-тестами
- **Архитектура чище** — один публичный метод `ExtractFromManagedFile` вместо 3 call site с дублями

### Negative

- **Симуляция добавляет время** к импорту (100-500ms на тип в среднем, 5-10 сек на 30 типоразмеров). Приемлемо — это разовая операция при импорте, не критичный path
- **Логи могут быть объёмными** на больших каталогах (30+ типов). Митигация: `MinLevel=Info` в production отсекает Debug-строки
- **Не все редкие формулы Revit** покрыты (например, `Concat`, `FromUnit`). Если у семейства такая формула и она падает на Regenerate — типоразмер пропускается с Warn. Пользователь видит меньше типоразмеров, но хотя бы видит остальные

### Risks

- **`Regenerate` на семействах с большим количеством geometry** может быть медленным (1-2 сек на тип). Для типичных MEP-семейств (воздуховоды, трубы) — десятки ms
- **`Marshal.ReleaseComObject` на документе, который мы НЕ открывали** (через `IRevitContext.GetDocument().Application.OpenDocumentFile`) — потенциально может вернуть COM-объект, но skill `revit-api-best-practice` явно рекомендует этот паттерн для prevention Family Upgrade Freeze
- **Encoding detection в `.txt`** — уже реализован в `LocalFamilyImportService.TypeCatalog.cs` через `UTF.Unknown`, переиспользуем без изменений
