# PipeConnect: Refactoring Backlog

> **Status:** Active (2 items deferred) | Загружать: при работе над техническим долгом PipeConnect.
>
> Основной ADR: [ADR-043](../adr/043-pipeconnect-modal-justification.md).
>
> История: backlog 2026-07-08 содержал 8 пунктов (R1-R8), 6 из которых выполнены и
> удалены из этого файла. Git history сохраняет полную копию.

---

## Открытые пункты

| # | Пункт | Статус | Риск | Приоритет |
|---|---|---|---|---|
| R4 | `Document _doc` поле → `IRevitContext` | ⏭️ Deferred | Средний | Низкий (опц.) |
| R6 | `ConfirmClose` при `IsBusy==true` | ⏭️ Deferred | Средний | Низкий (YAGNI) |

### R4: `Document _doc` → `IRevitContext`

**Контекст:** `PipeConnectEditorViewModel` хранит `Document _doc` как поле. При modal
это безопасно, но для future Close+Loop+Recreate (ADR-043) `_doc` может стать stale.

**Решение (опционально):**
- Добавить `IRevitContext` в конструктор VM
- Заменить `_doc` на `_revitContext.GetDocument()` в ~30 call sites
- Обновить `PipeConnectViewModelFactory`

**Оценка:** ~150 строк, 4 часа + smoke-test. Prerequisite для future modeless/async.

### R6: `ConfirmClose` при `IsBusy==true`

**Контекст:** `PipeConnectEditorViewModel.ConfirmClose` отменяет закрытие если
`IsBusy == true`, но не ставит `DeferredAction`. Пользователь теоретически может
застрять.

**Почему отложено:** Все операции сейчас синхронные и быстрые (миллисекунды).
Шанс попасть в окно `IsBusy` ≈ 0. Станет актуальным при переходе на async
(Close+Loop+Recreate).

---

## Связанные документы

- [ADR-043](../adr/043-pipeconnect-modal-justification.md) — почему modal обязателен
- [ADR-003](../adr/003-transaction-group-pattern.md) — TransactionGroup pattern
- [ADR-006](../adr/006-external-event-pattern.md) — ExternalEvent (modeless)
- [ADR-008](../adr/008-external-event-action-queue.md) — Action Queue (FamilyManager)
- [invariants.md](../invariants.md) I-01a — исключение для modal command context
- [known-workarounds.md](../known-workarounds.md) — modal PipeConnect workaround
