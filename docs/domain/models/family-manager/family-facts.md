---
module: family-manager
---
# Модели FamilyManager — Family Facts (ADR-055)

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

## FamilyFact / FamilyFactsData (ADR-055)

Один machine-readable «факт» о catalog item, извлечённый из файла при импорте/актуализации по правилам `FamilyFactRuleSet`. Item-level метаданные — в content hash НЕ входят.

**Файл:** `Models/FamilyManager/FamilyFact.cs`

```csharp
public sealed record FamilyFact(
    string FactKey,
    string ValueKey,
    string ValueDisplay);

public sealed record FamilyFactsData(
    int? RevitCategoryId,
    IReadOnlyList<FamilyFact> Facts);
```

- `FactKey` — стабильный машинный ключ факта (`"part_type"`).
- `ValueKey` — стабильное машинное значение (ординал enum строкой, напр. `"5"` = Elbow; для будущего расширенного поиска). Пустая строка — sentinel «факт вычислен, но параметр в семействе отсутствует»: детект миграции гаснет, UI скрывает строку.
- `ValueDisplay` — человекочитаемый fallback на момент извлечения (имя члена enum, напр. `"Elbow"`). UI предпочитает `PartTypeLabelMap` (следует за языком UI), fallback — на это поле.
- `FamilyFactsData` — read-модель окна свойств: ординал категории (`catalog_items.revit_category_id`; `null` = pre-V22 строка, не актуализирована) + все факты итема.

---

## FamilyFactRule (ADR-055)

Правило «для категории X извлекай built-in параметр P и храни под ключом K» (nested в `FamilyFactRuleSet.cs`).

**Файл:** `Models/FamilyManager/FamilyFactRuleSet.cs`

```csharp
public sealed record FamilyFactRule(
    int CategoryId,
    string FactKey,
    string LabelKey,
    int ParameterId);
```

- `CategoryId`/`ParameterId` — сырые int-ординалы BuiltInCategory/BuiltInParameter, НЕ enum: Core грузится тестами без RevitAPI (I-09, Nice3point runtime-excluded). Ординалы верифицированы по revitapidocs 2025/2026.

---

## FamilyFactRuleSet (ADR-055)

Статический реестр `FamilyFactRule`. Единая точка расширения подсистемы фактов: реестр управляет извлечением (SmartCon.Revit), детектом миграции (`family-facts-v1` генерирует SQL из реестра) и UI (label по `LabelKey`). Новый category-driven атрибут = одна строка в `Rules` — без DDL, новой задачи и правок UI.

**Файл:** `Models/FamilyManager/FamilyFactRuleSet.cs`

```csharp
public static class FamilyFactRuleSet
{
    public const string PartTypeFactKey = "part_type";
    public const string PartTypeLabelKey = "FM_Fact_PartType";
    public static IReadOnlyList<FamilyFactRule> Rules { get; }
    public static IReadOnlyCollection<int> CategoryIdsWithRules { get; }
    public static IReadOnlyList<FamilyFactRule> GetRulesForCategory(int categoryId);
    public static FamilyFactRule? FindRule(int categoryId, string factKey);
}
```

- Текущий scope: `part_type` для 4 MEP фитинговых категорий (OST_PipeFitting=-2008049, OST_DuctFitting=-2008010, OST_CableTrayFitting=-2008126, OST_ConduitFitting=-2008128; FAMILY_CONTENT_PART_TYPE=-1114206). Арматура (accessories) исключена продуктовым решением.

---

## PartTypeLabelMap (ADR-055)

Локализованные подписи значений Part Type: ординал → RU/EN по текущему языку UI (`LocalizationService.CurrentLanguage`). **RU-строки дословно повторяют официальную русскую локализацию Revit** (help.autodesk.com/cloudhelp/2023/RUS, таблицы GUID-54F9DD0A / GUID-4DA88E95 — «Мультипорт», «Соединение», «Механическое сочленение» и т.д.) — свои переводы не выдумываем. Неизвестные/будущие ординалы → `null` (caller fallback'ит на `FamilyFact.ValueDisplay`). Значения — по enum PartType Revit 2025 API.

**Файл:** `Models/FamilyManager/PartTypeLabelMap.cs`

```csharp
public static class PartTypeLabelMap
{
    public static string? TryGetLabel(string valueKey);
    public static string? TryGetLabel(int ordinal);
}
```
