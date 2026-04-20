# Future Work — отслеживание TODO и planned features

> **Назначение:** единый tracking-документ для всех `TODO`/`HACK`/`FIXME`
> комментариев в коде. Каждый TODO в `.cs`-файлах должен быть отражён здесь
> или удалён. Для контрибьюторов: если видите TODO без записи в этом файле —
> либо реализуйте, либо добавьте сюда.

---

## Планируемые фичи

### `[ChainV2]` — Multi-fitting chains (3+ links)

**Статус:** Planned · **Priority:** Feature · **Owner:** TBD

Текущая реализация `FittingChainResolver` поддерживает топологии с максимум
двумя звеньями: `FittingReducer`, `ReducerFitting`. Для более сложных кейсов
(три и более фитингов между static и dynamic соединителями) требуется:

- `FittingChainPlan.ValidateChain()` — проверка совместимости каждого звена с
  соседями (по CTC и DN).
- `FittingChainPlan.IntermediateCtc(int index)` / `IntermediateRadius(int
  index)` — методы для работы с произвольной длиной цепочки.
- `FittingChainResolver` Strategy D — Dijkstra multi-hop через
  `IFittingMapper.FindShortestFittingPath()`.
- `IFittingChainResolver.RecalculateWithRadius(plan, linkIndex, newRadius)`
  и `ResolveWithMaxChainLength(int maxLength)`.
- `ConnectExecutor.ExecuteConnectTo` — заменить `if/else` по `ChainTopology`
  на обобщённый цикл по `FittingChainPlan.Links`.
- `PipeConnectEditorViewModel._currentFittingId` → `List<ElementId>
  _fittingChain` (ordered).

**Файлы с TODO:**
- `src/SmartCon.Core/Models/FittingChainPlan.cs`
- `src/SmartCon.Core/Services/Interfaces/IFittingChainResolver.cs`
- `src/SmartCon.Core/Services/Implementation/FittingChainResolver.cs`
- `src/SmartCon.PipeConnect/Services/ConnectExecutor.cs`
- `src/SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs`

### `[Phase 6B]` — Multi-Column LookupTable parsing

**Статус:** Planned · **Priority:** Feature · **Owner:** TBD

Текущий `SizeLookupParser.FindFirst` находит только **один** `SizeLookupNode`
в AST формулы семейства. Для полноценного multi-column lookup нужно:

- Собирать **все** `SizeLookupNode` из всех формул всех `FamilyParameter`.
- Построить маппинг `{tableName → [{columnIndex, parameterName,
  connectorIndex}]}`.
- Использовать `SizeLookupParser.FindAll()` (уже реализован) совместно с
  анализом `FamilyParameter.Formula`.

**Файлы с TODO:**
- `src/SmartCon.Core/Math/FormulaEngine/SizeLookupParser.cs`
- `src/SmartCon.Core/Math/FormulaEngine/Ast/SizeLookupNode.cs`

### SubTransaction миграция на `ITransactionService`

**Статус:** TechDebt · **Priority:** Low · **Owner:** TBD

В `RevitLookupTableService.GetFamilySymbolSizes` используется `Transaction`
напрямую (обёрнут в finally с RollBack). Нужно мигрировать на
`ITransactionService` (инвариант I-03). Отложено, т.к. API service работает
с project document, а там нужна read-only транзакция для активации
FamilySymbol.

---

## Как работать с TODO

1. **Не создавайте новые TODO без записи в этом файле** — каждый TODO
   должен быть tracked.
2. **Формат комментария в коде:** `// TODO [Tag]: Описание` (Tag — ссылка на
   секцию в этом файле).
3. **При реализации:** удалите TODO-комментарий и соответствующую секцию
   в этом документе.
4. **При рефакторинге:** если TODO больше не актуален — удалите.

---

## Связанные документы

- [`roadmap.md`](roadmap.md) — фазы разработки и зависимости
- [`plans/oss-perfection-plan.md`](../.opencode/plans/oss-perfection-plan.md) — план OSS-готовности
