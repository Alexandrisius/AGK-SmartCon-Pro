---
module: family-manager
---
# Модели FamilyManager — Type Catalog

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

## TypeCatalogEntry

Одна запись (тип) из Type Catalog (.txt) семейства Revit.

**Файл:** `TypeCatalogEntry.cs`

```csharp
public sealed record TypeCatalogEntry(
    string TypeName,
    IReadOnlyDictionary<string, string> ParameterValues);
```

---

## FamilyTypeCatalogBakingResult

Результат запекания Type Catalog (.txt) в .rfa при импорте (ADR-033).

**Файл:** `FamilyTypeCatalogBakingResult.cs`

```csharp
public sealed record FamilyTypeCatalogBakingResult(
    bool Success,
    string? OutputRfaPath,
    int BakedTypeCount,
    string? ErrorMessage);
```

---

## TypeCatalogParseResult

Результат парсинга Type Catalog (.txt) семейства Revit.

**Файл:** `TypeCatalogParseResult.cs`

```csharp
public sealed record TypeCatalogParseResult(
    IReadOnlyList<TypeCatalogColumn> Columns,
    IReadOnlyList<TypeCatalogEntry> Entries)
{
    public IReadOnlyList<string> ParameterNames => Columns.Select(c => c.Name).ToList();
    public bool HasEntries => Entries.Count > 0;
    public TypeCatalogColumn? FindColumn(string parameterName);
}
```

`Columns` хранит header колонок вместе с `##TYPE##UNITS` annotation из Revit Type Catalog
specification. `ParameterNames` остался как backward-compatible helper для мест,
где нужен только список имён (formula-variable lookup). `FindColumn` используется
baker'ом для unit conversion перед `FamilyManager.Set`. См. ADR-033 BAKE-006.

---

## TypeCatalogColumn

Один столбец из header Type Catalog (.txt). Хранит имя параметра и опциональные
annotation `##TYPE##UNITS` (например `LENGTH##MILLIMETERS`), которые говорят Revit
о единицах измерения значений в колонке.

**Файл:** `TypeCatalogColumn.cs`

```csharp
public sealed record TypeCatalogColumn(
    string Name,
    string? TypeAnnotation,
    string? UnitAnnotation)
{
    public bool HasUnitAnnotation => !string.IsNullOrEmpty(UnitAnnotation);
}
```

Используется baker'ом через `RevitUnitsCompat.CatalogCellToInternalUnits` для
конвертации raw значения в Revit internal units перед записью в параметр семейства.
См. ADR-033 BAKE-006..009.

---

## TypeCatalogUnitAlias

Pure C# маппинг unit annotation из Type Catalog header в canonical key.
Вынесен из `RevitUnitsCompat.ResolveSourceUnitTypeId` для unit-тестирования
без Revit API (см. `docs/testing/unit-conversion-coverage-gaps.md`).
Canonical key (lowercase plural) мапится в `UnitTypeId` (R21+) или
`DisplayUnitType` (R19-R20) на стороне Revit-слоя.

**Файл:** `TypeCatalogUnitAlias.cs`

```csharp
public static class TypeCatalogUnitAlias
{
    public static string? Normalize(string? rawAnnotation);
    public static IReadOnlyCollection<string> SupportedAliases { get; }
}
```

`Normalize` принимает произвольный вход (null/whitespace возвращают `null`),
trim'ит, lower-case'ит, и резолвит в canonical key:

- **Length**: `millimeters`/`millimeter`/`milimeters` (Autodesk typo)/`mm`, `centimeters`/`cm`, `decimeters`/`dm`, `meters`/`m`, `inches`/`in`, `feet`/`ft`
- **Angle**: `degrees`/`degree`/`decimal_degrees`/`deg`, `radians`/`radian`/`rad`, `grads`/`grad` (R19-R20 only)
- **Area**: `square_millimeters`/`sq_mm`, ..., `square_feet`/`sq_ft`
- **Volume**: `cubic_millimeters`/`cu_mm`, ..., `cubic_feet`/`cu_ft`
- **Power**: `watts`/`w`, `kilowatts`/`kw`
- **Electrical**: `amperes`/`amp`/`amps`/`a`, `volts`/`v`

15 unit-тестов в `TypeCatalogUnitAliasTests.cs` покрывают все aliases + edge cases
(null/empty/whitespace/unknown).

См. ADR-033 BAKE-007, §Sources + §Risks (unit conversion testing gap).

---

## UnitSymbolFixup

Pure C# коррекция известных ошибок русской локализации Autodesk в символах
единиц, которые Revit возвращает из `AsValueString` / `UnitFormatUtils.Format`.
RU-таблица символов Revit рендерит единицу давления бар как «бары», но по
ГОСТ 8.417 «бар» несклоняем — корректное отображение «16 бар». Правила —
замены хвостового токена (ordinal), неизвестные строки проходят без изменений.
Вынесено в Core для unit-тестирования без Revit API; применяется в
`RevitUnitsCompat.FormatDisplayValue` и в fallback-точках `AsValueString`.

**Файл:** `Services/Implementation/UnitSymbolFixup.cs`

```csharp
public static class UnitSymbolFixup
{
    public static string? Correct(string? formatted);
}
```

9 unit-тестов в `UnitSymbolFixupTests.cs` покрывают замену «бары»→«бар»,
no-op для корректных/английских/прочих символов, null/empty и не-хвостовые
позиции.
