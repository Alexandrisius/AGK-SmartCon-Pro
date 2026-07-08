# PipeConnect

> **Status:** Active | Флагманский модуль SmartCon: соединение MEP-элементов в Revit двумя кликами.
>
> SSOT: `src/SmartCon.PipeConnect/` (интерфейсы и модели — в `src/SmartCon.Core/`).

## Структура папки

```
docs/pipeconnect/
├── README.md              # этот файл
├── state-machine.md       # состояния S1-S6, кейсы, матрица решений
├── algorithms.md          # выравнивание, параметры, фитинги, цепочки
├── ui-spec.md             # окна PipeConnectEditor, MiniTypeSelector, MappingEditor, FamilySelector
└── refactoring-backlog.md # открытый технический долг (2 пункта)
```

## Карта документов

| Документ | Загружать когда | Ключевые темы |
|---|---|---|
| [state-machine.md](state-machine.md) | Реализация/отладка логики соединения | S1-S6, Dynamic=первый клик, Static=второй клик, модальность S6, матрица решений, RollBack |
| [algorithms.md](algorithms.md) | ConnectorAligner, FittingMapper, FormulaSolver, Chain graph | Выравнивание, S4 параметры, S5 фитинги, IFittingChainResolver, BFS цепочки |
| [ui-spec.md](ui-spec.md) | Окна, биндинги, MVVM | PipeConnectEditor, MiniTypeSelector, MappingEditor, FamilySelector |
| [refactoring-backlog.md](refactoring-backlog.md) | Технический долг | R4 (Document→IRevitContext), R6 (ConfirmClose IsBusy) |

## Обязательные ADR

| ADR | Почему обязателен |
|---|---|
| [ADR-001](../adr/001-clean-architecture.md) | Clean Architecture: Core/Revit/UI разделение |
| [ADR-003](../adr/003-transaction-group-pattern.md) | TransactionGroup + Assimilate для PipeConnect |
| [ADR-005](../adr/005-formula-solver-ast.md) | FormulaSolver: Tokenizer→Parser→AST→Evaluator→Solver |
| [ADR-006](../adr/006-external-event-pattern.md) | IExternalEventHandler для modeless WPF→Revit |
| [ADR-008](../adr/008-external-event-action-queue.md) | Action Queue pattern (используется в других модулях) |
| [ADR-009](../adr/009-vec3-for-core-math.md) | Vec3 вместо XYZ для математики в Core |
| [ADR-010](../adr/010-fitting-chain-resolver.md) | IFittingChainResolver — подбор цепочек фитингов |
| [ADR-011](../adr/011-dn-symbol-name-in-dropdown.md) | Отображение DN в выпадающем списке |
| [ADR-012](../adr/012-per-project-extensible-storage.md) | ExtensibleStorage для маппинга фитингов |
| [ADR-043](../adr/043-pipeconnect-modal-justification.md) | Почему PipeConnectEditor — модальное окно |

## Ключевые инварианты

- **I-01a:** PipeConnectEditor — модальное окно, прямые вызовы Revit API из VM допустимы только в этом контексте.
- **I-03:** Все транзакции через `ITransactionService`.
- **I-04:** Вся операция PipeConnect — `TransactionGroup` с `Assimilate()` (или `RollBack()`).
- **I-05:** Не хранить `Element`/`Connector` между транзакциями, только `ElementId`.
- **I-08:** Исключать `ConnectorType.Curve` из фильтра free-коннекторов.
- **I-13:** Маппинг фитингов хранится только в ExtensibleStorage активного проекта.

## Ключевые замечания для агентов

- **S1 = AwaitingDynamicSelection** (первый клик, движущийся элемент).  
- **S2 = AwaitingStaticSelection** (второй клик, неподвижный элемент).  
  Старые документы могли инвертировать эти названия — источник правды: `state-machine.md` и `docs/domain/glossary.md`.
- **S6 (PostProcessing)** — модальное окно. Modeless невозможен для live real-element preview + single-undo cancel (ADR-043).
- Все модели и интерфейсы PipeConnect — в `SmartCon.Core`. Реализации Revit API — в `SmartCon.Revit`.
