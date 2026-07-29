# ADR-059: Import Validation Gate — валидация семейств при импорте и смене категории

**Date:** 2026-07-29  
**Status:** accepted  
**Related:** ADR-017 (attribute extraction), ADR-054 (actualization engine), ADR-056 (FHV3), I-03b, I-09, I-14

## Context

В каталог FamilyManager попадал «мусор»: семейства с битыми формулами
(Revit показывает error-dialog при открытии), семейства без обязательных
параметров, семейства с значениями вне допустимых диапазонов. Менеджеры
каталога не имели инструмента контроля качества контента ни при импорте,
ни при ручной перекладке семейства в другую категорию (DnD, окно свойств).

Требования, собранные с пользователем:
- системные ошибки .rfa (битые формулы, ошибки регенерации типов,
  document warnings) должны собираться БЕЗ диалогов Revit — сводным
  отчётом;
- правила качества (обязательность параметра, диапазоны значений)
  настраиваются на атрибутах категорий и наследуются;
- гейт должен быть ЖЁСТКИМ и ЕДИНЫМ: в защищённую категорию нельзя
  попасть ни импортом, ни новой версией, ни DnD, ни через свойства;
- «Без категории» — карантин, невидимый для read-only ролей (Engineer).

## Decision

### 1. Двухфазная модель: health-check + rule-check

**Health-check** (фаза чтения .rfa, `IFamilyHealthChecker` в SmartCon.Revit):
- UC-1 (файловый импорт): перебор типов `FamilyManager.CurrentType` +
  `Regenerate()` внутри транзакций с `ProceedWithRollBack` +
  `SetClearAfterRollback(true)` (без него Revit покажет error-dialog на
  каждый битый тип); warnings глотаются из UI через `IFailuresPreprocessor`
  и попадают в `FamilyHealthReport`. Документ не модифицируется.
- UC-2 (импорт из активного Family Editor): только накопленные document
  warnings — без переключения типов (нет фликера в редактируемом документе).
- White-dialog bug #95/#92 (net48) запрещает `get_Geometry` + Rollback на
  held-open family doc до диалога — поэтому только Transaction+Regenerate
  (прецедент bake-in).

**Rule-check** (фаза назначения категории): правила на binding'ах
категории проверяются ПО КАЖДОМУ типу семейства чистым движком
`FamilyValidationEngine` (Core, без Revit API — I-09).

### 2. Правила = свойство binding'а (category + attribute)

Таблица `category_validation_rules` (schema v25) с FK на binding и
`ON DELETE CASCADE`: правила наследуются вместе с атрибутами от
родительских категорий (effective attributes несут `BindingId`), unbind
атрибута удаляет его правила каскадом.

### 3. Display-units-first для чисел

Правила хранят и вводят числа в DISPLAY-единицах параметра (то, что
пользователь видит в свойствах семейства). Движок сравнивает
`DisplayNumber ?? ValueNumber`: display-число парсится из display-строки
значения (`DisplayValueParser`, дизамбигуация «1,200 mm» en-thousands vs
«16,500 бар» ru-decimal по кириллице единицы; imperial отклоняется),
fallback — internal units. `UnitTypeId` у правила зарезервирован (`null`):
смена display-формата параметра молча пересчитывает правила
(задокументированное ограничение).

### 4. Два источника данных, один движок

`IFamilyValidationEngine.Validate(FamilyValidationInput, rules)` — pure.
Мапперы нормализуют в `FamilyValidationInput`:
- путь импорта — из in-memory `FamilySnapshot`/`SystemFamilySnapshot`
  (Prepare уже открыл файл один раз — повторного открытия .rfa нет);
- путь смены категории — из persisted `extracted_attribute_values`
  активной версии. Нет данных экстракции → `null` → ShowInfo «Обновить
  базу» (нельзя проверить = блок).

### 5. Жёсткий единый гейт во всех точках входа

- **Batch-диалог импорта**: колонка кликабельных иконок статуса
  (`FamilyRowGateStatus`), health-error или rule-violation → forced Skip
  (`SetActionSilently`, без broadcast на выделение); смена категории строки
  → async перевалидация (`_pendingValidation`, await при Import);
  мутации строк только через `_dispatcher.Invoke` (ADR-031).
- **DnD в дереве** и **пикер категории в свойствах** —
  `ICategoryChangeGateService.EnsureFamilyPassesAsync`: блок-диалог =
  тот же `ValidationReportView` с баннером, операция прерывается.
- **RBAC**: `ExcludeUncategorized` в `FamilyCatalogQuery` — SQL-фильтр
  узла «Без категории» для не-IsEditorRole.

### 6. Перенос правил в metadata-пакете v3

`ValidationRules` внутри binding'а пакета; при импорте в другую базу
неизвестные операторы скипаются, существующий binding не затирается.

## Consequences

- Валидационный слой полностью unit-testable (движок, мапперы, репозиторий,
  gate-сервисы): 2501 зелёный тест.
- Health-check — единственный Revit-boundary компонент; кандидат на
  интеграционные тесты (Nice3point.TUnit.Revit).
- Известные ограничения: `UnitTypeId` не пинается; imperial display-строки
  не парсятся (fallback internal); SortOrder правил не переносится при
  импорте пакета; `Enum.TryParse` принимает числовые строки (движок
  `_ => true` спасает).
