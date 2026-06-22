# Type Catalog Unit Conversion — Testing Coverage Gaps

**Status:** accepted (known limitation)
**Date:** 2026-06-23
**Related:** ADR-033 BAKE-006..009, [smartcon-testing skill](../../.agents/skills/smartcon-testing/SKILL.md), [revit-mocking reference](../../.agents/skills/smartcon-testing/references/revit-mocking.md)

## Context

ADR-033 (`docs/adr/033-bakein-type-catalog.md`) добавляет unit conversion для Type
Catalog (.txt) колонок при bake-in. Логика разделена на 3 слоя:

1. **Pure string → canonical key** (`SmartCon.Core/Services/Implementation/TypeCatalogUnitAlias.cs`)
2. **Canonical key → Revit type** (`SmartCon.Revit/Compatibility/RevitUnitsCompat.cs`)
3. **Integration с `FamilyParameter`** (`SmartCon.Revit/FamilyManager/RevitFamilyTypeCatalogBaker.cs`)

Только слой 1 покрыт unit-тестами. Слои 2-3 требуют running Revit.

## Что покрыто unit-тестами

### `TypeCatalogUnitAlias.Normalize` (15 тестов, 53 параметризованных case)

**Файл:** `src/SmartCon.Tests/Core/Services/TypeCatalogUnitAliasTests.cs`

**Покрытие:**
- **Length** (7 categories): `millimeters` / `mm` / singular / typo `milimeters` / case / trim
- **Centimeters / decimeters / meters / inches / feet**: singular/plural/short aliases
- **Angle** (3): `degrees` (incl. `decimal_degrees`, `deg`), `radians` (incl. `rad`), `grads` (R19-R20 only)
- **Area / Volume**: `square_millimeters`, `cubic_meters` + `sq_mm`/`cu_m` short aliases
- **Power / Electrical**: `watts`, `amperes` + `w`/`amp`/`amps` short aliases
- **Edge cases**: null / empty / whitespace → `null`; unknown units → `null`
- **Diagnostics**: `SupportedAliases` коллекция содержит ожидаемые canonical keys

**Что это покрывает:** ~80% unit-conversion logic — все edge cases normalization
(singular/plural/typo/case/whitespace/short alias) могут сломать bake, и теперь
protected тестами.

## Что НЕ покрыто unit-тестами

### `RevitUnitsCompat.ResolveSourceUnitTypeId` / `ResolveSourceDisplayUnitType`

**Файл:** `src/SmartCon.Revit/Compatibility/RevitUnitsCompat.cs`

**Что остаётся untested:**
- Финальный mapping `canonical key → UnitTypeId` (R21+) / `DisplayUnitType` (R19-R20)
- Multi-version conditional compilation (`#if REVIT2022_OR_GREATER` для `SquareCentimeters`/etc.)
- R2025 removal `UnitTypeId.Grads` → null mapping

**Почему:** `UnitTypeId`, `DisplayUnitType`, `ForgeTypeId` — sealed native
types из `RevitAPI.dll`. См. `.agents/skills/smartcon-testing/references/revit-mocking.md`:
Castle.DynamicProxy не может proxy sealed native типы, native `RevitAPI.dll` отсутствует
в CI (`Nice3point.Revit.Api.RevitAPI` is `ExcludeAssets=runtime`).

### `RevitUnitsCompat.CatalogCellToInternalUnits` + `TryConvertUnit`

**Файл:** `src/SmartCon.Revit/FamilyManager/RevitFamilyTypeCatalogBaker.cs`

**Что остаётся untested:**
- `UnitUtils.IsValidUnit(SpecTypeId.Length, sourceUnit)` валидация
- `UnitUtils.ConvertToInternalUnits(rawValue, sourceUnit)` математика
- `FamilyParameter.GetUnitTypeId()` spec detection (R21+)
- `param.DisplayUnitType` spec detection (R19-R20)
- `TryConvertUnit` 3 outcomes (Skipped/Converted/Failed)

**Почему:** Все эти методы принимают `FamilyParameter` — sealed native Revit type.
Ни один из них не может быть инстанцирован или замокан в `SmartCon.Tests`.

## Mitigations в production

### Production log validation

В `RevitFamilyTypeCatalogBaker.BakeInFamilyDocument` после транзакции пишется
**один Info summary**:

```
[INF] Type Catalog baked: created 6 type(s), 36 unit(s) converted, 0 unit(s) failed, restored 5 formula(s)
[INF] Type Catalog baked: created 23 type(s), 184 unit(s) converted, 0 unit(s) failed, restored 4 formula(s)
```

Если `Failed > 0` — оператор сразу видит проблему в логе. Счётчики приходят
из `BakeStats` агрегатора (zero-allocation `readonly record struct`).

### Smoke-test checklist

`docs/testing/smoke-test-checklist.md` — manual integration test scenarios
для bake-in path с реальным Revit (3-family import).

### Defensive programming в коде

Все места, где может бросить native API, обёрнуты в `try { ... } catch { ... }`:

- `RevitUnitsCompat.cs:255-256` — `param.GetUnitTypeId()` может бросить на edge cases
- `RevitUnitsCompat.cs:266-277` — `UnitUtils.IsValidUnit` на custom spec может бросить
- `RevitUnitsCompat.cs:393-397` — `SafeGetColumnUnitTypeId` для FamilySizeTableColumn

При исключении возвращается fallback (null), caller skip parameter — что
**логируется** как `Failed` в summary.

## Future work (backlog)

### Вариант A: ricaun.RevitTest integration suite (рекомендуется)

Создать `src/SmartCon.Revit.Tests/` с проектом который:
- Ссылается на `SmartCon.Revit` (compile-time)
- Запускается через ricaun.RevitTest framework
- Содержит `[RevitFact]` тесты которые реально открывают Revit и тестируют
  `RevitUnitsCompat.CatalogCellToInternalUnits` с реальными `FamilyParameter`

**Трудозатраты:** ~2-3 дня на infrastructure + ~10-15 integration тестов.

**Преимущества:** реальное покрытие sealed native API.

**Недостатки:** требует запущенного Revit для каждого CI прогона, медленнее
unit-тестов, flake-prone.

### Вариант B: Test seam через абстракцию (overhead)

Создать `IFamilyParameterAccessor` в Core с методами:
```csharp
public interface IFamilyParameterAccessor
{
    bool HasUnitAnnotation { get; }
    string? GetUnitTypeIdString(); // canonical "millimeters" etc.
    StorageType StorageType { get; }
    bool IsReadOnly { get; }
    string? Formula { get; }
}
```

Реализация `RevitFamilyParameterAccessor` обёртывает sealed `FamilyParameter`.
Production: baker получает accessor через DI. Tests: hand-written fake.

**Трудозатраты:** ~1 день на abstraction + ~10 unit-тестов.

**Недостатки:** добавляет слой абстракции в hot path (overhead ~100ns per param).
Усложняет API.

### Вариант C: Mock `FamilyParameter` через Subclass + Reflection (НЕ рекомендуется)

Технически можно создать `class TestableFamilyParameter : FamilyParameter { ... }`,
но `FamilyParameter` sealed — нельзя наследовать. Можно использовать
`Castle.DynamicProxy.Generators.Emitters` для создания proxy, но это:
- Хрупкое (ломается на новых версиях Revit API)
- Загрязняет тесты infrastructure
- Не сработает на `Document`/`ElementId` (нужны дополнительные wrappers)

**Вердикт:** не рекомендуется.

## Recommendation

**Текущий статус:** Layer 1 (80% логики) покрыт. Layer 2-3 покрыты через
production log validation + manual smoke tests.

**Следующий шаг:** внедрить Вариант A (ricaun.RevitTest) в отдельном PR
после merge этой ветки в develop. До тех пор mitigation через
`BakeStats.Failed` счётчик + production logging достаточно для catch regressions.

## См. также

- [ADR-033 BAKE-006..009](docs/adr/033-bakein-type-catalog.md) — design + sources
- [smartcon-testing skill](../../.agents/skills/smartcon-testing/SKILL.md) — testing conventions
- [revit-mocking reference](../../.agents/skills/smartcon-testing/references/revit-mocking.md) — what cannot be mocked
- [stale-detection-coverage-gaps.md](stale-detection-coverage-gaps.md) — аналогичный gap документ (template)
- [smoke-test-checklist.md](smoke-test-checklist.md) — manual integration tests