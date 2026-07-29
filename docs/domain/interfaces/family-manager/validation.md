---
module: family-manager
---
# Интерфейсы FamilyManager — Import Validation Gate

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

Контракты подсистемы валидации семейств (ADR-059): хранение правил,
pure-движок вычисления, health-check семейства (Revit-слой), оркестрация
гейта импорта и гейта смены категории.

## IValidationRuleRepository

CRUD правил валидации в локальном catalog.db (таблица
`category_validation_rules`, FK на binding с CASCADE — unbind атрибута
удаляет его правила каскадом). Реализация: `LocalValidationRuleRepository`
(SmartCon.FamilyManager, I-14: SQLite только через `LocalCatalogDatabase`).

**Файл:** `Services/Interfaces/IValidationRuleRepository.cs`

```csharp
public interface IValidationRuleRepository
{
    Task<IReadOnlyList<ValidationRule>> GetRulesForBindingAsync(string bindingId, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationRule>> GetRulesForBindingsAsync(IEnumerable<string> bindingIds, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> GetRuleCountsForBindingsAsync(IEnumerable<string> bindingIds, CancellationToken ct = default);
    Task<IReadOnlyDictionary<string, int>> GetRuleCountsForAttributesAsync(IEnumerable<string> attributeIds, CancellationToken ct = default);
    Task<ValidationRule> CreateRuleAsync(ValidationRule rule, CancellationToken ct = default);
    Task<ValidationRule> UpdateRuleAsync(ValidationRule rule, CancellationToken ct = default);
    Task<bool> DeleteRuleAsync(string ruleId, CancellationToken ct = default);
    Task DeleteRulesForBindingAsync(string bindingId, CancellationToken ct = default);
}
```

---

## IFamilyValidationEngine

Pure-движок валидации: вычисляет нормализованные значения параметров
семейства против effective-правил категории. Без Revit API и БД —
полностью unit-testable. Правила проверяются на каждом типе семейства;
один падающий тип роняет семейство (hard gate semantics).

**Файл:** `Services/Interfaces/IFamilyValidationEngine.cs`
**Реализация:** `SmartCon.Core/Services/Implementation/FamilyValidationEngine.cs`

```csharp
public interface IFamilyValidationEngine
{
    FamilyValidationReport Validate(FamilyValidationInput input, IReadOnlyList<EffectiveValidationRule> rules);
}
```

---

## IFamilyHealthChecker

Health-check семейства при импорте: детектит системные проблемы внутри
family-документа (битые формулы, per-type ошибки регенерации, накопленные
warnings), чтобы «мусор» не попадал в каталог. Реализуется в
SmartCon.Revit (`RevitFamilyHealthChecker`); параметр `Document` для Core
opaque (I-09).

**Файл:** `Services/Interfaces/IFamilyHealthChecker.cs`

```csharp
public interface IFamilyHealthChecker
{
    FamilyHealthReport CheckFamilyDocument(Document familyDoc, CancellationToken ct = default);
    FamilyHealthReport CheckActiveFamilyDocument(Document familyDoc);
}
```

- `CheckFamilyDocument` — полная проверка фонового family-документа
  (UC-1 импорт файла): итерирует типы через `FamilyManager.CurrentType` +
  `Regenerate()` внутри откатываемых транзакций (I-03b), собирает failure
  messages. Документ остаётся неизменным; warnings подавляются в UI Revit
  и попадают в отчёт (`IFailuresPreprocessor` + `SetClearAfterRollback(true)`).
- `CheckActiveFamilyDocument` — лёгкая проверка АКТИВНОГО family-документа
  (UC-2 импорт из Family Editor): только накопленные document warnings,
  без переключения типов (нет фликера в редактируемом документе).

---

## IFamilyImportValidationService

Оркестратор гейта валидации импорта: разрешает effective-правила категории
(прямые + унаследованные binding'и) и гоняет pure-движок по данным
семейства — из in-memory Prepare snapshot (путь импорта) или из
persisted extracted values (путь смены категории). Без Revit API.

**Файл:** `Services/Interfaces/IFamilyImportValidationService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Validation/FamilyImportValidationService.cs`

```csharp
public interface IFamilyImportValidationService
{
    Task<IReadOnlyList<EffectiveValidationRule>> GetEffectiveRulesAsync(string? categoryId, CancellationToken ct = default);
    FamilyValidationReport ValidateFromSnapshots(
        FamilySnapshot? loadableSnapshot,
        SystemFamilySnapshot? systemSnapshot,
        IReadOnlyList<EffectiveValidationRule> rules);
    Task<FamilyValidationReport?> ValidateCatalogItemAsync(
        string catalogItemId,
        IReadOnlyList<EffectiveValidationRule> rules,
        CancellationToken ct = default);
}
```

- `GetEffectiveRulesAsync` — все включённые правила, effective для категории;
  пустой список для `null`-категории или категории без правил (такие
  семейства проходят гейт свободно).
- `ValidateFromSnapshots` — движок по in-memory snapshot'ам строки batch-диалога
  (без повторного открытия .rfa). Ровно один snapshot не-null.
- `ValidateCatalogItemAsync` — движок по persisted extracted values активной
  версии элемента каталога (гейт смены категории). `null` — нет данных
  экстракции: вызывающий код показывает «cannot verify» (hard gate блокирует,
  ShowInfo «Обновить базу»).

---

## ICategoryChangeGateService

Гейт валидации СМЕНЫ КАТЕГОРИИ внутри каталога (DnD в дереве, пикер
категории в свойствах семейства). Та же hard-gate семантика, что и при
импорте: в категорию с правилами семейство попадает только пройдя их.
Проверка идёт по persisted extracted values активной версии — без
повторного открытия .rfa.

**Файл:** `Services/Interfaces/ICategoryChangeGateService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/Validation/CategoryChangeGateService.cs`

```csharp
public interface ICategoryChangeGateService
{
    Task<bool> EnsureFamilyPassesAsync(
        string catalogItemId,
        string familyName,
        string? targetCategoryId,
        string targetCategoryPath,
        CancellationToken ct = default);
}
```

Возвращает `true`, когда смена разрешена (нет правил / правила пройдены /
цель — «Без категории»). Возвращает `false`, когда смена ЗАБЛОКИРОВАНА —
при этом сервис уже показал пользователю диалог (отчёт о нарушениях или
нотис об отсутствии данных экстракции), вызывающий код просто прерывает
операцию.
