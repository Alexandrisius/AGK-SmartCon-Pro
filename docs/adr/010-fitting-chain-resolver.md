# ADR-010: FittingChainResolver — единая система подбора цепочек фитингов

> Статус: accepted | Дата: 2026-04-17

## Контекст

Модуль PipeConnect поддерживает соединение MEP-элементов через промежуточные фитинги и редьюсеры. До данного решения логика подбора цепочки была размазана по 5+ файлам:
- `FittingMapper` — поиск правил маппинга
- `PipeConnectSizeHandler.DetectReducer*` — определение необходимости редьюсера
- `ConnectExecutor` — 4 захардкоженных ветви валидации и соединения
- `ViewModel.Insert` — ручное определение порядка вставки
- `FittingCardBuilder` — построение карточек UI

**Проблема:** Кейс `reducer + fitting` (reducer ДО фитинга) не поддерживался. Добавление новых топологий (multi-fitting цепочки через Дейкстру) требовало бы ещё больше дублирования.

## Решение

Ввести **IFittingChainResolver** — единую точку принятия решений о цепочке элементов.

### Новые модели (SmartCon.Core/Models/)

- `ChainTopology` — enum всех поддерживаемых топологий (Direct, ReducerOnly, FittingOnly, ReducerFitting, FittingReducer, ...)
- `FittingChainNodeType` — Fitting | Reducer
- `FittingChainLink` — одно звено цепочки (тип, правило, семейство, CTC на входе/выходе, радиус на входе/выходе)
- `FittingChainPlan` — полный план цепочки (иммутабельный, содержит Links[])

### Новый интерфейс

```csharp
IFittingChainResolver.Resolve(staticCtc, dynCtc, staticR, dynR) → FittingChainPlan
IFittingChainResolver.ResolveAlternatives(...) → IReadOnlyList<FittingChainPlan>
```

### Алгоритм Resolve

4 стратегии, применяемые по приоритету:
1. **Direct** — DN=, CTC=
2. **ReducerOnly** — DN≠, CTC=
3. **FittingOnly** — DN=, CTC≠ (или фитинг покрывает оба DN)
4. **Mixed** — DN≠, CTC≠ → подбор между FittingReducer, ReducerFitting и (future) multi-hop

### Интеграция

- `PipeConnectSessionContext.ChainPlan` — план передаётся в ViewModel
- `ViewModel.Init()` — маршрутизация по `ChainPlan.Topology` (ReducerFitting → отдельный метод)
- `ConnectExecutor.ExecuteConnectTo()` — принимает `ChainTopology` для определения порядка ConnectTo

## Последствия

### Положительные
- Единая точка принятия решений (was: 5 файлов, now: 1 сервис)
- Поддержка кейса ReducerFitting (reducer ДО фитинга)
- Легко добавить новые топологии (ChainV2) — достаточно расширить FittingChainResolver
- 12 unit-тестов покрывают все стратегии

### Риски
- FittingChainResolver работает только с mapping rules, без знания о реальных FamilySymbol (это Revit-layer concern)
- Для ChainV2 потребуется более сложный алгоритм (Dijkstra + DN matching на каждом шаге)

## Маркеры для будущих агентов

```
TODO [ChainV2] — для поиска по всей кодовой базе
```
