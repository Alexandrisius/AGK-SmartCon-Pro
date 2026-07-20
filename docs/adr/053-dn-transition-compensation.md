# ADR-053: DN-компенсация по уровням — переходные элементы вместо resize-каскада

**Date:** 2026-07-20  
**Status:** accepted  
**Related:** ADR-052 (per-level displacement absorption), Issue #146, #138 (валидные DN-комбинации), #139

## Context

При подключении уровня сети элемент, чей радиус коннектора не совпадает с
родителем, ресайзился равномерно: `ChainOperationHandler.AdjustRelatedFamilyConnectors`
принудительно вызывал `TrySetConnectorRadius(targetRadius)` для **всех**
коннекторов семейства, входящих в граф. Метод появился при рефакторинге
(`2b3a3d91`/`7e332693`) без issue/design-doc и игнорировал:

- независимые DN-параметры портов (`BP_NominalDiameter_1/_2` — резолвер умеет
  per-connector через `MEPFamilyConnectorInfo.GetAssociateFamilyParameterId`);
- переходные строки lookup-таблиц (DN1=20, DN2=25 — мульти-колоночная
  машинерия `SizeTableRow`/`LookupColumnConstraint`/`BestSizeMatcher` уже есть).

Результат: тройник DN25×DN25 при подключении DN20 становился DN20×DN20 вместо
переходного DN20×DN25 → resize-каскад по всей сети ниже, лишняя работа на
каждом уровне «Подключить всё».

## Decision

### Иерархия DN-компенсации (per-level, зеркало ADR-052)

В `AdjustElementSize` при `delta > 1e-5` применяются стратегии по приоритету:

1. **TRANSITION** — элемент сам становится переходным. Конфигурация ищется
   `TransitionSizeMatcher.FindBestTransition` среди prefetch-нутых
   `IDynamicSizeResolver.GetAvailableFamilySizes` (валидация комбинаций из
   lookup-таблицы/типоразмеров — #138 соблюдён автоматически): target-порт
   обязан совпасть точно, остальные порты меняются минимально (идеально — 0).
   Применение: `PipeConnectSizeHandler.ApplyQueryParamsIfExists`, иначе
   per-connector `TrySetConnectorRadius` только для изменившихся портов.
   Опции со сменой FamilySymbol исключены (только param-driven конфигурации).
2. **REDUCER** — редьюсер из маппинга CTC (`NetworkMover.InsertReducer`),
   элемент и сеть полностью не трогаем. Для FamilyInstance предпочтительнее
   равномерного resize (бизнес-решение пользователя).
3. **RESIZE** — классический resize; каскад на следующий уровень, где
   стратегии 1–2 применяются снова. Каскад **останавливается на первом
   DN-поглощающем элементе** ниже по сети — логика зеркалит остановку
   смещения на первой поглощающей трубе (ADR-052).

### Гейтинг `AdjustRelatedFamilyConnectors`

Принудительная доводка остальных портов применяется только когда DN-параметр
порта **общий** с primary (`DnParamsShared` — сравнение
`DirectParamName ?? RootParamName` из `GetConnectorRadiusDependencies`;
для `BuiltIn` — сравнение enum; неизвестность → консервативно shared).
Независимые порты сохраняют свой DN — точечный фикс исходного бага.

### Prefetch вне транзакции

`GetAvailableFamilySizes` использует `EditFamily` и требует
`IsModifiable == false` — вызывать внутри транзакции уровня нельзя.
`PrefetchTransitionOptions` выполняется в snapshot-фазе `IncrementLevel`
(вне транзакции) только для FamilyInstance с реальным несовпадением радиусов.

### Known limitation (зафиксировано по решению пользователя)

Семейства без lookup-таблицы и типов, у которых размерные атрибуты зашиты
в километровые nested-IF формулы с ожиданием конкретных DN: такое семейство
выглядит как «свободно-переходное» (разные writable DN-параметры портов),
но реально ожидает ограниченный набор значений. Стратегия TRANSITION может
применить невалидную комбинацию. Кейс редкий, осознанно не обрабатывается —
задокументирован здесь, чтобы не потерять.

## Consequences

**Плюсы:**
- Multi-DN семейства становятся переходными вместо равномерного resize —
  сеть ниже по потоку сохраняет свои диаметры.
- Меньше resize-уровней → чаще срабатывает раннее запечатывание цепи
  (`TrySealQuietChain`, ADR-052) → «Подключить всё» завершается раньше.
- Валидация комбинаций DN — только из lookup-таблицы/типоразмеров (#138).
- Откат без изменений: `ElementSnapshot.ConnectorRadii` per-connector,
  редьюсеры удаляются через `TrackReducer`.

**Минусы / ограничения:**
- Symbol-changing переходы не поддержаны (только param-driven).
- Known limitation про nested-IF семейства (выше).

## Verification

- Build R19/R21/R24/R25: 0 warnings / 0 errors.
- Tests: 2065/2065 (6 новых `TransitionSizeMatcherTests`: переходная строка
  предпочтительнее равномерной, нет совпадения target, auto-select ignored,
  symbol-change skipped, тройник 3 порта, подсчёт OtherPortsDelta).
