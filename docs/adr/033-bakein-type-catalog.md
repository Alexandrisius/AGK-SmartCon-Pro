# ADR-033: Bake-in Type Catalog в .rfa при импорте

**Status:** implemented
**Date:** 2026-06-22
**Phase:** 25
**Issue:** [#74](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/74)
**Supersedes:** ADR-032 (Type Catalog Simulation) — bake-in заменяет simulation для managed .rfa

## Контекст

### 1. Проблема (issue #74)

ADR-032 решает issue #66 через **simulation**: при чтении managed `.rfa` с `.txt` sidecar временно создаёт каждый тип из каталога, вычисляет формулы через `Regenerate`, читает значения и откатывает транзакцию. Это работает для отображения значений в FamilyManager, но оставляет `.txt` как отдельную сущность в managed storage.

Недостатки подхода:

- **Два файла на хранении** — `.rfa` + `.txt` рядом. Нарушает принцип "single source of truth" (I-16 managed storage immutability допускает только готовый `.rfa`).
- **Семейство без `.txt` неполноценно** — если `.txt` потерян/не скопирован, managed `.rfa` содержит только дефолтные типы.
- **Сложная синхронизация** — при обновлении версии нужно следить за тем, чтобы `.txt` обновлялся вместе с `.rfa`.
- **100% покрытие не гарантировано** — simulation пропускает битые типоразмеры, чтобы не валить остальные.

### 2. Цель

При импорте семейства с Type Catalog **запечатать** все типы из `.txt` прямо в managed `.rfa`. После импорта managed storage содержит только готовый `.rfa` со всеми типоразмерами и корректно вычисленными формулами. `.txt` как sidecar-файл больше не хранится.

### 3. Архитектурные ограничения

- **I-01** (потокобезопасность Revit API): все операции — только через `IFamilyManagerAwaitableEvent` callback.
- **I-03**: транзакции только через `ITransactionService`; прямой `new Transaction(doc)` запрещён.
- **I-05**: нельзя хранить `Element`/`Connector`/`FamilyType` между транзакциями.
- **I-16**: managed storage immutable — сохраняем только готовый `.rfa`.
- **I-09**: `SmartCon.Core` не вызывает Revit API.
- **ADR-023-002**: `.txt` больше не является source of truth; source of truth — managed `.rfa`.

## Решение

### BAKE-001: Selective formula disable

Ключевое отличие от предыдущей неудачной попытки (BAKE-015): вместо удаления формул через `SetFormula(param, null)` без сохранения значения, что делало семейство invalid, мы **сохраняем last computed value** перед удалением формулы.

Алгоритм:

1. Для каждого formula-driven параметра, формула которого ссылается на catalog input parameters:
   - Прочитать текущее значение через `FamilyType.AsDouble/AsInteger/AsString/AsElementId`.
   - Вызвать `FamilyManager.SetFormula(param, null)` — формула удаляется, значение сохраняется.
   - Вызвать `FamilyManager.Set(param, currentValue)` — явно фиксируем значение для текущего типа.
2. После того как все такие формулы отключены, семейство остаётся **valid**.
3. Создаём все типы из `.txt` через `FamilyManager.NewType`. Новые типы наследуют значения от текущего.
4. Для каждого нового типа устанавливаем input values из `.txt`.
5. Восстанавливаем формулы в топологическом порядке (зависимости сначала).
6. Один финальный `Document.Regenerate()` пересчитывает все формулы для всех типов.
7. `Commit()` + `SaveAs()`.

### BAKE-002: Удаление существующих типов

Перед созданием типов из каталога:

1. Создаём/выбираем current type.
2. Переименовываем его в anchor (`__SmartCon_BakeAnchor__`).
3. Удаляем все остальные типы через `FamilyManager.DeleteCurrentType`.
4. После создания типов из каталога удаляем anchor (если создались реальные типы).

### BAKE-003: Formula dependency parser

Для определения формул, которые нужно отключить, используем существующий `IFormulaSolver.ExtractVariables`. Параметр считается зависимым от каталога, если хотя бы одна переменная из его формулы присутствует в `TypeCatalogParseResult.ParameterNames`.

Для восстановления формул в правильном порядке строим dependency graph по извлечённым переменным и применяем topological sort.

### BAKE-004: Интеграция в импорт с диска

Точка входа — `LocalFamilyImportService.ImportFileAsync`:

1. `ComputeManagedRfaPath` вычисляет целевой путь managed `.rfa`.
2. `PrepareManagedRfaAsync` ищет `.txt`:
   - Если `.txt` найден и содержит типы — парсит его и вызывает `IFamilyTypeCatalogBaker.BakeAsync(sourceRfaPath, catalog, managedRfaPath)`, который открывает исходный `.rfa`, печёт типы и сохраняет результат напрямую в managed storage.
   - Если `.txt` отсутствует/пустой — копирует исходный `.rfa` в managed storage.
3. Из managed `.rfa` извлекаются финальные `sha256`/`size_bytes`/`file_name` и записываются в `family_files` / `catalog_versions`.
4. `ImportParsedTypeCatalogAsync` сохраняет типы и сырые значения из `.txt` в SQLite (для отображения в FamilyManager), но **не копирует `.txt`** в managed storage.

### BAKE-005: Без fallback

Если bake падает — импорт считается неуспешным. Нет fallback на simulation. Это принудительно гарантирует, что managed `.rfa` всегда содержит 100% типов (или импорт не проходит).

## Архитектура

### Слои

| Layer | Типы |
|---|---|
| `SmartCon.Core/Services/Interfaces/` | `IFamilyTypeCatalogBaker` |
| `SmartCon.Core/Models/FamilyManager/` | `FamilyTypeCatalogBakingResult` |
| `SmartCon.Core/Services/Interfaces/` | расширен `IFormulaSolver` — добавлен `ExtractVariables` |
| `SmartCon.Core/Math/FormulaEngine/Solver/` | расширен `FormulaSolver` — публичный `ExtractVariables` |
| `SmartCon.Revit/FamilyManager/` | `RevitFamilyTypeCatalogBaker` |
| `SmartCon.FamilyManager/Services/LocalCatalog/` | изменён `LocalFamilyImportService` + partial `LocalFamilyImportService.TypeCatalog` |
| `SmartCon.Revit/FamilyManager/` | упрощён `RevitFamilyDataExtractionService` — удалена simulation (ADR-032) |
| `SmartCon.App/DI/` | регистрация `IFamilyTypeCatalogBaker → RevitFamilyTypeCatalogBaker` |
| `SmartCon.Tests/FamilyManager/Repository/` | обновлены `TempCatalogFixture` и тесты импорта |

### Алгоритм `RevitFamilyTypeCatalogBaker.BakeAsync` (псевдокод)

```
1. OpenDocumentFile(sourceRfaPath)
2. RunInTransaction(familyDoc, "SmartCon_BakeTypeCatalog"):
   a. EnsureCurrentType()
   b. RenameCurrentType("__SmartCon_BakeAnchor__")
   c. RemoveOtherTypes()
   d. formulaStates = CollectFormulaStates(catalogParamNames)
   e. For each state in formulaStates:
      - value = ReadCurrentValue(state.Parameter)
      - SetFormula(state.Parameter, null)
      - Set(state.Parameter, value)
   f. For each entry in catalog.Entries:
      - newType = NewType(entry.TypeName)
      - CurrentType = newType
      - ApplyCatalogValues(entry)
   g. RemoveAnchorType()
   h. RestoreFormulas(formulaStates) // topological order
   i. Regenerate()
3. SaveAs(outputRfaPath)
4. Close(false)
```

## Call sites

- `LocalFamilyImportService.ImportFileAsync` — вычисляет managed `.rfa` path, вызывает `PrepareManagedRfaAsync`, затем `ImportParsedTypeCatalogAsync`.
- `LocalFamilyImportService.UpdateFamilyAsync` — аналогично для обновления версии.
- `LocalFamilyImportService.Database.cs` (`OverwriteCurrentAsync`) — аналогично перезаписывает текущий managed `.rfa`.
- `FamilyManagerMainViewModel.ExtractFromManagedFileAsync` — больше не симулирует; читает уже запечённые типы.

## Логирование (skill `smartcon-logging`)

| Правило | Применение |
|---|---|
| L8 (basename в scope) | `Path.GetFileName(sourceRfaPath)` и `Path.GetFileName(outputRfaPath)` |
| L9 (Warn → `[Action: ...]`) | bake fail, formula disable/restore fail, type delete fail |
| Category vocabulary | `TypeCatalogBake` |

## Тестирование (skill `smartcon-testing`)

### Unit-тесты

- `TempCatalogFixture` предоставляет `FakeFamilyTypeCatalogBaker`, имитирующий успешный bake копированием исходного `.rfa` на выходной путь.
- Тесты `LocalFamilyImportServiceTypeCatalogTests` проверяют, что типы из `.txt` сохраняются в SQLite.
- Удалена проверка копирования `.txt` в managed storage; добавлена проверка, что managed `.rfa` существует, а `.txt` рядом с ним отсутствует.

### Integration-тесты (ручные в Revit)

| # | Сценарий | Ожидаемый результат |
|---|---|---|
| 1 | Импорт семейства с `.txt` | managed `.rfa` содержит все типы; `.txt` отсутствует в managed storage |
| 2 | Открытие properties в FamilyManager | значения formula-driven параметров разные для разных типов |
| 3 | Семейство без `.txt` | импорт без bake, обычное поведение |
| 4 | Bake падает (несовместимая формула) | импорт неуспешен, managed `.rfa` не сохраняется |
| 5 | MEP-семейства с кириллическими именами параметров | формулы корректно отключаются/восстанавливаются |

## Совместимость с версиями Revit

- Поддержка R19, R21, R24, R25 (multi-version build).
- `FamilyManager.SetFormula(param, null)` поддерживается во всех версиях.
- `FamilyManager.DeleteCurrentType` доступен во всех версиях.
- `Document.Regenerate()` и `SaveAs()` доступны во всех версиях.

## Sources

- [Jeremy Tammik — The Revit Family API](https://jeremytammik.github.io/tbc/a/0199_family_api.htm)
- [Autodesk Revit API — `FamilyManager.SetFormula`](https://www.revitapidocs.com/2025/cdc3156c-0334-0bba-70af-1df78fb18b50.htm)
- [Autodesk Revit API — `FamilyManager.DeleteCurrentType`](https://www.revitapidocs.com/2025/9ba3e824-e354-943b-141c-89b5c5e8cea2.htm)
- [Autodesk Revit API Forum — clear parameter formula](https://forums.autodesk.com/t5/revit-api-forum/revit-family-clear-parameter-formula/td-p/8570788)
- [Autodesk Developer Blog — Set Family Parameter Requires Type](https://blog.autodesk.io/set-family-parameter-requires-type/)
- ADR-023 (Type-Centric Workflow) — частично обновлён: `.txt` больше не source of truth
- ADR-032 (Type Catalog Simulation) — заменён bake-in для managed .rfa
- Issue [#74](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/74)

## Consequences

### Positive

- **Managed storage содержит только `.rfa`** — нет раздельных `.txt` sidecar.
- **100% типов** либо запечены, либо импорт неуспешен.
- **Формулы работают без simulation** — при открытии properties в FamilyManager значения уже вычислены.
- **Упрощена архитектура** — удалён `SilentFailurePreprocessor` и simulation-код из `RevitFamilyDataExtractionService`.
- **Переиспользуется FormulaSolver** — `ExtractVariables` покрывает `[Parameter Name]`, кириллицу и функции.

### Negative

- **Импорт занимает больше времени** — bake требует `OpenDocumentFile`, транзакции и `SaveAs`. Приемлемо для разовой операции импорта.
- **Больше нет graceful degradation** — если bake падает, весь импорт падает. Это осознанный выбор issue #74.
- **Memory pressure** — `OpenDocumentFile` держит семейство в памяти Revit во время bake.

### Risks

- **`SetFormula(param, null)` на параметрах с циклическими зависимостями** может бросить `InvalidOperationException`. Митигация: топологический порядок восстановления.
- **Кириллические имена параметров** — `FormulaSolver.Tokenizer` поддерживает Cyrillic; но trailing soft-sign и другие edge cases могут потребовать дополнительной проверки на реальных семействах.
- **Family type parameters (nested family types)** — не тестировались; формулы на них могут вести себя иначе.

## Freeze workaround: REVIT-236376 / REVIT-237190

Bake-in выполняет `OpenDocumentFile` + `SaveAs` + `Close` для каждого импортируемого `.rfa`. На R2023 (net48) / Windows 11 family upgrade dialog от Revit может оставлять WPF render thread позади UI thread — DockablePane "замерзает" (LMB не работает, выпадающие списки и ПКМ возвращают UI к жизни). Это известный системный баг Autodesk, не наша регрессия — присутствовал ещё до bake-in, на 5fb16890.

**Workaround:** после каждого `Close` в `RevitFamilyTypeCatalogBaker.BakeAsync` и `RevitFamilyDataExtractionService.Extract*` вызываем `RevitBalloonNudge.Nudge(...)`. Он показывает near-invisible InfoCenter balloon через `AdWindows.dll`, чьё собственное Win32-окно инициирует focus event — то же самое, что делает ПКМ пользователя на панели. Это community-confirmed workaround (Fausto Mendez, Autodesk Community "Loading a rfa file into a document using LoadFamily() freezes Revit UI").

**Альтернативы, которые НЕ сработали в нашем тестировании:**
- `BringWindowToTop + SetForegroundWindow + SetFocus` (Win32 focus flip)
- `Dispatcher.BeginInvoke(ApplicationIdle)` + `InvalidateVisual + UpdateLayout` (WPF pump)

**См. также:** REVIT-236376, REVIT-237190 в Autodesk JIRA; официальный fix для R2023 — update 2023.1.8 ("Fixed an issue that Revit UI became unresponsive in some cases with Windows 11"). Реализация: `src/SmartCon.Revit/Util/RevitBalloonNudge.cs`.
