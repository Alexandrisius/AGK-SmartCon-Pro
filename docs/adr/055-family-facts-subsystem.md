# ADR-055: Family Facts — category-driven факты семейства (Part Type первым) с registry-driven детектом

**Date:** 2026-07-23  
**Status:** accepted  
**Related:** ADR-054 (движок актуализации — задача `family-facts-v1`), ADR-036 (V15+ FK-паттерн), ADR-049 (hash v2 — факты НЕ входят в хэш), Issue #126, #155 (локализация/scope Part Type), #156 (UC-4 терял данные снапшота), I-09 (Core без runtime-Revit)

## Context

Окно свойств FamilyManager показывает «Версия» и «Категория Revit». У фитинговых
категорий (**только фитинги, арматура осознанно исключена продуктовым решением, #155**):

| Категория | BuiltInCategory | Ординал |
|---|---|---|
| Трубопроводные фитинги | `OST_PipeFitting` | -2008049 |
| Фитинги воздуховодов | `OST_DuctFitting` | -2008010 |
| Фитинги кабельных лотков | `OST_CableTrayFitting` | -2008126 |
| Фитинги коробов | `OST_ConduitFitting` | -2008128 |

есть второй определяющий атрибут — **Part Type** («Тип детали»: Отвод, Тройник,
Переход, Мультипорт…), параметр `FAMILY_CONTENT_PART_TYPE` (ординал **-1114206**)
на элементе `Family` (`doc.OwnerFamily`), который задаётся в редакторе семейств и
нужен пользователю при идентификации семейства в каталоге. Ординалы верифицированы
по revitapidocs 2025/2026 и decompiled BuiltInParameter (Revit 2021) — вечные
константы, от версии Revit не зависят. Требования:

1. Показывать «Тип детали» рядом с «Категория Revit» — только у категорий,
   где атрибут осмыслен.
2. Хранить в `catalog.db` (читается без открытия файла).
3. Заполнять для существующих баз — через движок актуализации (ADR-054).
4. **Гибкость:** завтра появятся атрибуты других категорий (оборудование,
   арматура) — добавление не должно требовать DDL, новой задачи или правок UI.

## Decision

### 1. Реестр правил `FamilyFactRuleSet` (Core) — единая точка расширения

```csharp
public sealed record FamilyFactRule(int CategoryId, string FactKey, string LabelKey, int ParameterId);
```

Одна запись = «для категории X извлекай built-in параметр P и храни под ключом K».
Реестр управляет **тремя контурами сразу**:

- **Извлечение** (`RevitFamilySnapshotExtractor`): для категории семейства читает
  параметры правил с `doc.OwnerFamily` (Family — Element, параметр читается
  `get_Parameter((BuiltInParameter)rule.ParameterId)` — паттерн подтверждён
  существующим `FittingFamilyRepository`).
- **Детект миграции**: задача `family-facts-v1` **генерирует** свой SQL-фрагмент
  из реестра (один OR-блок на категорию, NOT EXISTS по каждому обязательному
  ключу) — новая запись в реестре автоматически расширяет детект старых баз.
- **UI** (`FamilyPropertiesViewModel.LoadFactsAsync`): label строки берётся по
  `rule.LabelKey` из `LanguageManager`.

**`CategoryId`/`ParameterId` — сырые int, не enum.** Core загружается юнит-тестами,
где RevitAPI runtime-excluded (Nice3point `ExcludeAssets=runtime`): enum-typed член
в модели падает с FileNotFoundException при первом обращении (поймано на этой
сессии — 33 теста). Ординалы BuiltInCategory/BuiltInParameter вечные, верифицированы
по revitapidocs 2025/2026 и задокументированы в реестре.

### 2. Хранение: колонка + EAV-таблица (schema V22)

- `catalog_items.revit_category_id INTEGER` — ординал категории для матчинга правил.
  Display name (`revit_category`) для этого не годится: локалезависим.
  `NULL` = «facts-aware код строку ещё не обрабатывал» (сигнал детекта);
  `-1` = sentinel «категория нечитаема при извлечении» (гасит детект, не выдумывает
  значение; зеркалит `''` sentinel строковой колонки из revit-category-v1).
- `family_facts (catalog_item_id, fact_key, value_key, value_display, PK(item,key), FK CASCADE)`:
  - `value_key` — стабильный машинный ключ (ординал enum строкой; для будущего
    расширенного поиска/фильтров);
  - `value_display` — человекочитаемый fallback на момент извлечения
    (имя члена enum, напр. `"Elbow"`);
  - **sentinel `value_key=''`** = «факт вычислен, но параметр в семействе отсутствует»
    — гасит детект (вечного pending нет), UI скрывает строку.

EAV вместо колонки-на-атрибут: новый факт = новая строка, не новая миграция схемы.

### 3. Локализация значения: официальная локализация Autodesk, не своя

`PartTypeLabelMap` (Core) маппит ординал → RU/EN по **текущему языку UI** — значение
не зависит от локали Revit в момент импорта (в отличие от `AsValueString()`, который
к тому же для FAMILY_CONTENT_PART_TYPE отдаёт сырое число, не имя enum).
**RU-строки дословно повторяют официальную русскую локализацию Revit**
(help.autodesk.com/cloudhelp/2023/RUS/Revit-Customize, таблицы GUID-54F9DD0A и
GUID-4DA88E95: «Мультипорт», «Соединение», «Механическое сочленение», «Косой тройник»…)
— свои переводы не выдумываем, иначе пользователь видит расхождение с Revit UI.
Неизвестные/будущие ординалы Autodesk → fallback на `value_display`.
`PartType = Undefined (-1)` показывается как «Не определён» (осознанное решение:
видно, что тип в семействе не задан), а отсутствующий параметр — скрыт (sentinel).

### 4. Записи: forward-fill при импорте + self-heal + задача

- `FamilySnapshot` += опциональные `CategoryId`/`Facts` (хвостовые с дефолтом —
  25+ call sites не тронуты). **В хэш FHV2 не входят** (каноническая строка строится
  из явных полей; тест `ComputeForLoadable_CategoryIdAndFacts_DoNotShiftHash`).
- Batch-executor маппит из уже имеющихся `LoadableSnapshot`/`SystemSnapshot`
  в `FamilyImportRequest`/`FamilyUpdateRequest`; INSERT пишет колонку и факты,
  UPDATE-пути — `COALESCE(revit_category_id, @id)` + полная замена фактов
  (переимпорт освежает Part Type). Импорт без снапшота (папка, legacy) оставляет
  NULL → задача дозаполнит.
- `FamilyFactsActualizationTask` (`family-facts-v1`, optional, Order=50, scope
  loadable+system, active label): apply пишет category id (sentinel не затирает
  реальный id) + replace фактов. System-группы получают только category id из
  `ExtractSystemCategoryAsync` (правил на system-категории нет).

### 5. UI: ItemsControl вместо захардкоженного TextBlock

Хедер свойств: `ItemsControl` по `FactRows` (`FamilyFactDisplayRow{Label,Value}`)
рядом с «Категория Revit». Несколько фактов одной категории в будущем = несколько
строк без правок XAML. `Visibility` = `HasFactRows`.

## Alternatives considered

- **Колонка `part_type` в `catalog_items`** — отклонено: не масштабируется на
  будущие атрибуты (каждый = DDL + миграция), нарушает требование гибкости.
- **Хранить `AsValueString()` (локаль извлечения)** — отклонено как основной
  вывод: смена языка UI не переведёт значение; оставлено как `value_display` fallback.
- **`AsValueString()` как display** дополнительно отклонено тем, что для
  FAMILY_CONTENT_PART_TYPE возвращает число, а не имя enum — display берётся из
  каста `(PartType)int`.
- **Матчинг правил по display name категории** — отклонено: локалехрупко
  («Pipe Fittings» vs «Трубопроводные фитинги»), поэтому `revit_category_id`.
- **Critical задача** — отклонено по таблице ADR-054: факты косметические,
  запись в старую базу без них не плодит дубли/рассинхрон (не входят в хэш).

## Consequences

- Новый category-driven атрибут = одна строка в `FamilyFactRuleSet.Rules`
  (+ локализация label + при желании map значений): детект, извлечение и UI
  подхватывают автоматически.
- Расширенный поиск по Part Type в будущем фильтрует по стабильному
  `value_key`, а не по локализованному тексту.
- Старые базы доезжают через «Обновить базу» (optional, янтарь).

## Pitfalls найденные на ручном тесте (зафиксировано, чтобы не повторить)

1. **Enum-члены в Core-моделях недопустимы** (I-09 runtime): `BuiltInParameter`/
   `BuiltInCategory` как тип поля реестра роняли 33 теста с
   `FileNotFoundException: RevitAPI` (Nice3point runtime-excluded). Только сырые
   int-ординалы + каст обратно в Revit-слое.
2. **Добавить поле в request ≠ данные доедут**: конструкций `FamilyImportRequest`
   несколько — batch-executor (`ImportBatchAsync`), folder import и прямой путь
   `ProcessFamilyImportAsync` → `ImportFileAsync` (UC-3/UC-4, single-item).
   Executor-маппинг был сделан, прямой путь пропущен → #156. Правило: при
   расширении request перечислить ВСЕ точки конструирования (`rg "new FamilyImportRequest"`).
3. **Своя локализация доменных значений Revit запрещена**: пользователь сверяет
   с Revit UI. Источник истины — официальная локализация Autodesk
   (help.autodesk.com/cloudhelp/{ver}/RUS), зафиксированная в `PartTypeLabelMap`
   (#155: «Многопортовый»→«Мультипорт», «Муфта»→«Соединение» и т.д.).
4. **Scope-реестра — продуктовое решение, не техническое**: Part Type физически
   есть и у арматуры (ValveNormal/Damper…), но показываем только фитингам (#155).
   Возврат арматуры = одна строка в реестре, данные в БД уже на месте.
