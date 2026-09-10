# ADR-008: Action Queue паттерн для ExternalEvent

**Статус:** accepted (уточнён ADR-043)
**Дата:** 2026-03-25
**Уточнение:** 2026-07-08 — изначально описывал PipeConnect как modeless с Action Queue, что было ошибкой проектирования. PipeConnect использует modal + прямые вызовы. Action Queue паттерн реализован в FamilyManager через `FamilyManagerAwaitableEvent`. См. ADR-043.

## Контекст

**Modeless** WPF-окно (FamilyManager dockable panel) инициирует множество
разных операций через Revit API: импорт семейств, извлечение атрибутов,
переименование, placement и т.д.

ADR-006 определяет, что все вызовы идут через `IExternalEventHandler`. Но
не специфицирует механизм передачи конкретной операции от ViewModel к Handler.

**Важно:** Этот ADR применяется к **modeless** окнам. `PipeConnectEditor` —
**модальное** окно и НЕ использует Action Queue (прямые вызовы API из VM,
см. ADR-043).

## Решение

Использовать **Action Queue** паттерн: единый `ExternalEventHandler` хранит
очередь `Action<UIApplication>`, ViewModel записывает действие перед вызовом
`Raise()`. Реализовано как **awaitable** queue (`FamilyManagerAwaitableEvent`):
`TaskCompletionSource` + `RunContinuationsAsynchronously` → VM может
`await _awaitableEvent.RaiseAsync(app => { ... Revit API ... })`.

### Реализация (FamilyManager)

`FamilyManagerAwaitableEvent` (pure C#, `SmartCon.FamilyManager/Events/`):
`ConcurrentQueue<Entry>` + `TaskCompletionSource<bool/T>` с
`TaskCreationOptions.RunContinuationsAsynchronously`. FIFO порядок. Cancellation
будит awaiter даже если очередь не дренажится (Revit занят).

`RevitFamilyManagerAwaitableEvent` (IExternalEventHandler adapter,
`SmartCon.App/Events/`): `Execute(UIApplication) => _awaitable.ProcessQueue(app)`.

Регистрация: `ServiceRegistrar.cs:250-256` —
`ExternalEvent.Create(fmHandler)` + `fmAwaitable.Initialize(() => fmEvent.Raise())`.

### Где НЕ применяется (PipeConnectEditor)

`PipeConnectEditorViewModel.RotateLeft()` делает **прямой** вызов
`_rotationHandler.ExecuteRotation(_doc, _groupSession!, ...)` без Raise.
Это безопасно при `ShowDialog` (UI thread == Revit main thread). См. ADR-043.

`ActionExternalEventHandler` (`SmartCon.Revit/Events/`) зарегистрирован в DI
(`ServiceRegistrar.cs:102-105`) но **нигде не вызывается** — кандидат на
удаление (см. refactoring backlog).

### Пример использования из FamilyManager ViewModel

```csharp
// SmartCon.FamilyManager/ViewModels/FamilyManagerMainViewModel.Import.cs
[RelayCommand(CanExecute = nameof(CanEditOps))]
private async Task ImportSelectedElementsAsync()
{
    IsLoading = true;
    SelectedElementsAnalysis? analysis = null;
    await _awaitableEvent.RaiseAsync(_ =>
    {
        try
        {
            var doc = _revitContext.GetDocument();
            analysis = _systemFamilyRevitOps.PickSelectedElements();
        }
        catch (Exception ex) { StatusMessage = ...; }
    });
    if (analysis is null) { IsLoading = false; return; }
    // ... обработка результата на UI thread ...
}
```

### Потокобезопасность

- `ConcurrentQueue` гарантирует безопасный enqueue из любого потока
- `RaiseAsync` вызывается из WPF UI thread
- `ProcessQueue` (внутри `IExternalEventHandler.Execute`) вызывается Revit из main thread (idle loop)
- `RunContinuationsAsynchronously` — continuation НЕ на Revit UI thread (не блокирует message loop)
- Cancellation будит awaiter даже если очередь не дренажится

### Обратная связь к ViewModel

После выполнения действия в `ProcessQueue`, результат передаётся через
`TaskCompletionSource.TrySetResult/TrySetException` → `await` continuation
на UI thread WPF. Для мутации `ObservableCollection` используется
`_dispatcher.InvokeAsync(...)` (см. `FamilyManagerMainViewModel.cs:639`).

## Последствия

**Плюсы:**
- Один Handler на все операции (не нужно создавать отдельный IExternalEventHandler для каждой команды)
- Гибкость: любая логика передаётся как лямбда
- Awaitable: VM может `await` результат и sequencе зависимые шаги
- Чистый код: ViewModel не знает о деталях диспетчеризации

**Минусы:**
- FIFO: только одно действие за раз (ограничение ExternalEvent) — но queue гарантирует порядок
- Нужно блокировать UI кнопки пока действие не завершено (через CanExecute + IsBusy)
- **TransactionGroup не переживает возврат из Execute** — нельзя держать долгоживущую
  группу между raises (см. ADR-043, Ограничение 2). FamilyManager не использует
  TransactionGroup — каждая операция = отдельный commit.

## Альтернативы

1. **Enum-based dispatch:** Handler получает enum операции и switch. Жёсткая связь, много boilerplate.
2. **Множество Handler-ов:** По одному ExternalEventHandler на операцию. Много объектов, сложнее DI.
3. **Revit.Async:** Библиотека для task-based pattern. Дополнительная зависимость, скрывает механику.
4. **Modal + прямые вызовы (PipeConnect):** Не требует ExternalEvent вообще. См. ADR-043.
