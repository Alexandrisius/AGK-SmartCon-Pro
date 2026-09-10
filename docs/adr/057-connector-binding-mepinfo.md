# ADR-057: Привязка коннектор→параметр через MEPFamilyConnectorInfo — без геометрического матчинга

- **Status:** Accepted
- **Date:** 2026-07-27
- **Issue:** [#161](https://github.com/Alexandrisius/AGK-SmartCon-Pro/issues/161)
- **Supersedes:** подход «origin/direction matching» project connector ↔ ConnectorElement (legacy, 2024-2026)

## Context

Для подбора/записи размера коннектора (DN) и CTC-записи в семейство нужно знать, какой
FamilyParameter управляет размером конкретного коннектора и какой ConnectorElement (CE)
в familyDoc соответствует проектному коннектору.

Legacy-подход сопоставлял коннекторы по **геометрии**: проектный origin коннектора
переводился в координаты семейства (`instanceTransform.Inverse.OfPoint` + flip-коррекция)
и матчился с `ConnectorElement.Origin` по расстоянию/направлению из центра (score ≥ 0.99).

**Проблема (#161):** геометрия шаблона семейства ≠ геометрия экземпляра, когда
экземплярные параметры (угол отвода, DN) отличаются от шаблона. Отвод Kan-therm с
шаблоном 45° в проекте под 90° давал score=0.7071 → параметр размера «не найден» →
пустой список DN; CTC могла записаться в чужой коннектор. Любое семейство с
экземплярной геометрией — потенциальная жертва.

## Decision

**Привязка коннектор→параметр размера — только через API проекта:**

```csharp
connector.GetMEPConnectorInfo() as MEPFamilyConnectorInfo
    ?.GetAssociateFamilyParameterId(new ElementId(BuiltInParameter.CONNECTOR_RADIUS | CONNECTOR_DIAMETER))
```

— возвращает ElementId семейного параметра напрямую из проекта (Revit 2017+).
Никакого EditFamily и никакой геометрии. Реализовано в `ConnectorSizeBindingResolver`
(SmartCon.Revit/Parameters).

**Формулы** (FamilyParameter.Formula, недоступны из проекта) читаются в familyDoc
**по имени параметра** — без поиска CE. Снапшот формул кэшируется
(`FamilyFormulaCache`, 1 EditFamily на семейство за сессию вместо 6-7).
**Таблицы** читаются `FamilySizeTableManager` прямо из проектного документа.

**Матчинг CE для записи (CTC)** — детерминированный каскад без геометрии
(`CtcFamilyWriter.BuildConnectorCtcMap`):
1. `IsPrimary` — один primary-коннектор на дисциплину (точный якорь для 2-коннекторных фитингов);
2. index-order — индекс коннектора сериализован на ConnectorElement и растёт с порядком
   создания (Tammik, mep_connector_number); `CE.Id` (ElementId) следует тому же порядку,
   поэтому сортировка обеих сторон выравнивает одни и те же физические коннекторы.

## Consequences

### Положительные
- Класс багов «шаблон ≠ экземпляр» устранён навсегда (угол, DN, flips).
- EditFamily в read-путях: 6-7 открытий → 1 (кэш) + 1 на CTC-запись; −534 строки мёртвого кода.
- `isDiameter` детерминирован (radius-first), не зависит от порядка итерации COM-коллекций.

### Отрицательные / риски
- Кэш формул живёт на сессию Revit; устаревает только при редактировании семейства
  вне PipeConnect с последующей перезагрузкой — принимаемый риск (инвалидация через
  `FamilyFormulaCache.Invalidate`).
- Index-order — эвристика (не документирована API), но подтверждена Tammik и всей
  историей PipeConnect (`ConnectorIndex == (int)Connector.Id`); полнота матчинга
  контролируется (`result.Count != items.Count → null → positional fallback + Warn`).
- Юнит-тестов нет: Revit API не мокируется (см. smartcon-testing); покрытие —
  ручные тесты в Revit + валидация структурных логов (Size binding / FormulaCache HIT-MISS /
  Primary match).

## Alternatives considered

1. **Синхронизация familyDoc с экземпляром** (тип + instance-параметры + Regenerate) —
   точная геометрия, но тяжёлая (транзакция+regen на каждое открытие, обход формул
   как в ADR-033 bake-in). Отвергнута: MEPFamilyConnectorInfo решает задачу даром.
2. **Комбинированный скоринг** (axis+origin+radius) — маскирует причину, ломается на
   переходах и произвольных углах.
3. **Ось коннектора (BasisZ)** вместо origin-из-центра — инвариантна к DN, но НЕ к
   экземплярному углу отвода (проверено логами: axisScore=0.7071).

## References

- Issue #161 (root cause, логи, верификация)
- Jeremy Tammik: tbc/a/1527_mep_connector_number (индекс сериализован на ConnectorElem),
  tbc/a/0312_connector_orientation (BasisY = height)
- Autodesk forum: «Set Pipe Fittings Diameter» (accepted answer — MEPFamilyConnectorInfo)
- revitapidocs: MEPFamilyConnectorInfo (Since 2017), FamilySizeTableManager
  («Family owned document or a project document»)
- ADR-033 (bake-in: обход формул при записи параметров — для будущих write-сценариев)
