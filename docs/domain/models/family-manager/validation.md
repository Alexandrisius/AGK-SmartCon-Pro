---
module: family-manager
---
# Модели FamilyManager — Import Validation Gate

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`.

Подсистема валидации семейств при импорте и смене категории (ADR-059).
Двухфазная модель: **health-check** при чтении .rfa (системные ошибки Revit)
и **rule-check** при назначении категории (правила на атрибутах категории).
Жёсткий единый гейт: в категорию с правилами семейство попадает только
пройдя их — импорт, новая версия, DnD и окно свойств проверяются одинаково.

## ValidationRule

Одно правило валидации, привязанное к category-attribute binding'у.
Числовые пороги (`ValueNumber`, `MinValue`, `MaxValue`) хранятся в
DISPLAY-единицах параметра (что пользователь вводит в редакторе правил)
и сравниваются с распарсенным display-числом значения (DisplayNumber-first,
fallback на internal units в движке). `UnitTypeId` зарезервирован (всегда
`null`): правило не пинает единицу — смена display-формата параметра молча
пересчитывает числовые правила (задокументированное ограничение).

**Файл:** `Models/FamilyManager/ValidationRule.cs`

```csharp
public sealed record ValidationRule(
    string Id,
    string BindingId,
    ValidationRuleOperator Operator,
    string? ValueText,
    double? ValueNumber,
    double? MinValue,
    double? MaxValue,
    string? UnitTypeId,
    int SortOrder,
    bool IsEnabled);
```

---

## ValidationRuleOperator

Операторы правил валидации. Главный — `HasValue` (параметр заполнен).

**Файл:** `Models/FamilyManager/ValidationRuleOperator.cs`

```csharp
public enum ValidationRuleOperator
{
    IsPresent = 0,
    HasValue = 1,
    IsEmpty = 2,
    Equals = 3,
    NotEquals = 4,
    Contains = 5,
    NotContains = 6,
    GreaterThan = 7,
    GreaterOrEqual = 8,
    LessThan = 9,
    LessOrEqual = 10,
    Between = 11,
}
```

Семантика при пустом значении (null/whitespace): `Equals`/`Contains`/числовые
→ FAIL; `NotEquals`/`NotContains` → PASS; `IsEmpty` при отсутствующем
параметре → PASS. `Between` — inclusive, epsilon 1e-9 relative.

---

## EffectiveValidationRule

Правило, разрешённое против effective-набора атрибутов категории
(прямые + унаследованные binding'и), с display-контекстом атрибута,
который само правило не несёт.

**Файл:** `Models/FamilyManager/EffectiveValidationRule.cs`

```csharp
public sealed record EffectiveValidationRule(
    string AttributeName,
    bool IsInherited,
    ValidationRule Rule);
```

- `AttributeName` — имя атрибута (параметра), с которым матчатся параметры типов семейства.
- `IsInherited` — `true`, когда binding (и правило) пришёл от родительской категории.

---

## ParameterValidationValue

Нормализованное значение параметра, потребляемое `IFamilyValidationEngine`.
Маппится из `FamilyParameterValue`/`SystemParameterValue` (import-time snapshot)
или из `ExtractedAttributeValue` (данные каталога после импорта) — движок
никогда не видит исходные модели.

**Файл:** `Models/FamilyManager/ParameterValidationValue.cs`

```csharp
public sealed record ParameterValidationValue(
    string ParameterName,
    bool IsPresent,
    string? ValueText,
    double? ValueNumber,
    string? UnitTypeId,
    double? DisplayNumber = null);
```

- `IsPresent` — параметр существует на типе семейства (независимо от значения);
  `false` маппит MissingParameter/NotInFamily статусы экстракции.
- `ValueText` — текст значения (или имя элемента для ElementId); whitespace-only
  считается пустым. SOURCE-DEPENDENT: строки каталога могут нести display-строку
  («300 мм») вместо канонического invariant-текста — строковые операторы
  предназначены для текстовых параметров.
- `ValueNumber` — число в internal units Revit, если применимо.
- `DisplayNumber` — число в DISPLAY-единицах, распарсенное из display-строки
  (`DisplayValueParser`); `null` для непарсящихся форматов (imperial).
  Числовые операторы сравнивают `DisplayNumber ?? ValueNumber`.

---

## FamilyTypeValidationData

Один тип семейства с нормализованными значениями параметров — единица,
против которой движок вычисляет правила. Каждое правило проверяется на
КАЖДОМ типе; один падающий тип роняет семейство.

**Файл:** `Models/FamilyManager/FamilyTypeValidationData.cs`

```csharp
public sealed record FamilyTypeValidationData(
    string TypeName,
    IReadOnlyList<ParameterValidationValue> Values);
```

---

## FamilyValidationInput

Нормализованный вход валидации одного семейства: все типы со значениями
параметров, source-agnostic (import snapshot или catalog extraction).
Производится SnapshotValidationMapper / ExtractedValuesValidationMapper.

**Файл:** `Models/FamilyManager/FamilyValidationInput.cs`

```csharp
public sealed record FamilyValidationInput(
    IReadOnlyList<FamilyTypeValidationData> Types);
```

---

## FamilyValidationReport

Результат валидации семейства против effective-правил категории.

**Файл:** `Models/FamilyManager/FamilyValidationReport.cs`

```csharp
public sealed record FamilyValidationReport(
    bool IsValid,
    IReadOnlyList<RuleViolation> Violations,
    int RulesEvaluated);
```

- `IsValid` — нарушений нет (или нет включённых правил у категории).
- `Violations` — все упавшие вычисления, по одному на (тип × правило).
- `RulesEvaluated` — всего вычислений (тип × включённое правило): позволяет
  отличить «правил нет» от «правила есть, все прошли».

---

## RuleViolation

Одно упавшее вычисление правила на одном типе семейства. Значения сырые
(invariant culture); UI локализует оператор и форматирует числа в
display-единицах через unit-идентификаторы.

**Файл:** `Models/FamilyManager/RuleViolation.cs`

```csharp
public sealed record RuleViolation(
    string TypeName,
    string AttributeName,
    ValidationRuleOperator Operator,
    string? ExpectedValue,
    double? ExpectedMin,
    double? ExpectedMax,
    string? ActualValue,
    string? UnitTypeId);
```

---

## FamilyHealthReport

Результат import health-check одного family-документа: системные
ошибки/предупреждения, собранные переключением всех типов с Regenerate
(pyRevit Family Quick Check pattern) плюс накопленные document warnings.
`IsHealthy == false` при наличии хотя бы одной Error-проблемы — batch-диалог
блокирует такие строки (hard gate).

**Файл:** `Models/FamilyManager/FamilyHealthReport.cs`

```csharp
public sealed record FamilyHealthReport(
    bool IsHealthy,
    IReadOnlyList<FamilyHealthIssue> Issues)
{
    public static readonly FamilyHealthReport Healthy;
    public static FamilyHealthReport FromIssues(IReadOnlyList<FamilyHealthIssue> issues);
}
```

---

## FamilyHealthIssue

Одна системная проблема внутри family-документа, найденная health-check'ом
(битая формула, ошибка регенерации типа, document warning). Отличается от
`RuleViolation`: health-проблемы приходят от самого Revit, а не от правил
категории.

**Файл:** `Models/FamilyManager/FamilyHealthIssue.cs`

```csharp
public sealed record FamilyHealthIssue(
    string? TypeName,
    FamilyHealthIssueSeverity Severity,
    string Description);
```

- `TypeName` — тип, к которому привязана проблема, или `null` для
  family-level проблем (накопленные warnings).
- `Severity` — Warning: информационно (импорт возможен); Error: блокирует.

---

## FamilyHealthIssueSeverity

Серьёзность health-проблемы.

**Файл:** `Models/FamilyManager/FamilyHealthIssueSeverity.cs`

```csharp
public enum FamilyHealthIssueSeverity
{
    Warning = 0,
    Error = 1,
}
```

---

## FamilyRowGateStatus

Комбинированный статус валидационного гейта строки batch-импорта,
показываемый в колонке статусов ДО запуска импорта (пока
`FamilyBatchImportRowState` остаётся Pending).

**Файл:** `Models/FamilyManager/FamilyRowGateStatus.cs`

```csharp
public enum FamilyRowGateStatus
{
    NotChecked = 0,
    Checking = 1,
    Passed = 2,
    Warning = 3,
    Failed = 4,
}
```

- `NotChecked` — категория не назначена, rule-check не выполнялся.
- `Checking` — rule-check выполняется (async, после назначения категории).
- `Passed` — health OK и все правила пройдены (или правил нет).
- `Warning` — правила пройдены/отсутствуют, но есть warning-проблемы health.
- `Failed` — health-ошибки или нарушения правил: строка заблокирована
  (forced Skip, hard gate).

---

## FamilyValidationEngine

Pure-реализация `IFamilyValidationEngine`. Без Revit API и БД — полностью
unit-testable. Правила проверяются на каждом типе; один падающий тип роняет
семейство (hard gate semantics). Числа сравниваются через
`DisplayNumber ?? ValueNumber` с relative epsilon 1e-9. Поиск параметра —
case-insensitive (`OrdinalIgnoreCase`).

**Файл:** `Services/Implementation/FamilyValidationEngine.cs`

```csharp
public sealed class FamilyValidationEngine : IFamilyValidationEngine
{
    public FamilyValidationReport Validate(
        FamilyValidationInput input,
        IReadOnlyList<EffectiveValidationRule> rules);
}
```

---

## DisplayValueParser

Static-парсер числовой части Revit display-строки («300 мм», «16 бар»,
«1 200 мм», «1,200 mm») в double. Используется мапперами валидации, чтобы
числовые правила, authored в display-единицах, сравнивались с display-числами,
а не с internal feet.

Тысячные разделители нормализуются ДО парсинга: пробелы/NBSP/NNBSP
вырезаются; comma/dot группировка разрешается эвристикой (смешанные —
последний разделитель десятичный; одиночная запятая с 3-значным хвостом и
не-кириллической единицей — тысячи en-US «1,200» → 1200; кириллическая
единица — ru-десятичная «16,500 бар» → 16.5). Imperial-форматы
(«1'-6\"», «1/2\"») отклоняются (`null`) — вызывающий код откатывается
на internal-unit значение вместо сравнения неверного числа.

**Файл:** `Services/Implementation/DisplayValueParser.cs`

```csharp
public static class DisplayValueParser
{
    public static double? TryParseNumber(string? displayText);
}
```
