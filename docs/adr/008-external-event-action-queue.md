# ADR-008: Action Queue паттерн для ExternalEvent

**Статус:** accepted
**Дата:** 2026-03-25

## Контекст

PipeConnect использует немодальное WPF-окно, которое инициирует множество разных операций через Revit API: поворот, выравнивание, вставка фитинга, ConnectTo, смена коннектора и т.д.

ADR-006 определяет, что все вызовы идут через `IExternalEventHandler`. Но не специфицирует механизм передачи конкретной операции от ViewModel к Handler.

## Решение

Использовать **Action Queue** паттерн: единый `ExternalEventHandler` хранит очередь `Action<UIApplication>`, ViewModel записывает действие перед вызовом `Raise()`.

### Реализация

```csharp
// SmartCon.Revit/Events/ActionExternalEventHandler.cs
public sealed class ActionExternalEventHandler : IExternalEventHandler
{
    private volatile Action<UIApplication>? _pendingAction;

    public void Raise(ExternalEvent externalEvent, Action<UIApplication> action)
    {
        _pendingAction = action;
        externalEvent.Raise();
    }

    public void Execute(UIApplication app)
    {
        _pendingAction?.Invoke(app);
        _pendingAction = null;
    }

    public string GetName() => "SmartCon.ActionHandler";
}
```

### Использование из ViewModel

```csharp
// В PipeConnectEditorViewModel:
[RelayCommand]
private void RotateLeft()
{
    _eventHandler.Raise(_externalEvent, app =>
    {
        var doc = app.ActiveUIDocument.Document;
        _transactionService.RunInTransaction("Rotate", doc =>
        {
            // Поворот элемента
        });
    });
}
```

### Потокобезопасность

- `volatile` гарантирует видимость `_pendingAction` между потоками
- `Raise()` вызывается из WPF UI thread
- `Execute()` вызывается Revit из main thread (idle loop)
- Между `Raise()` и `Execute()` может пройти неопределённое время
- Не вызывать `Raise()` повторно до завершения `Execute()` — Revit это не поддерживает

### Обратная связь к ViewModel

После выполнения действия в `Execute()`, результат передаётся обратно в ViewModel через `Dispatcher`:
```csharp
Application.Current.Dispatcher.Invoke(() =>
{
    viewModel.OnOperationCompleted(result);
});
```

## Последствия

**Плюсы:**
- Один Handler на все операции (не нужно создавать отдельный IExternalEventHandler для каждой команды)
- Гибкость: любая логика передаётся как лямбда
- Чистый код: ViewModel не знает о деталях диспетчеризации

**Минусы:**
- Только одно действие за раз (ограничение ExternalEvent)
- Нужно блокировать UI кнопки пока действие не завершено (через CanExecute)

## Альтернативы

1. **Enum-based dispatch:** Handler получает enum операции и switch. Жёсткая связь, много boilerplate.
2. **Множество Handler-ов:** По одному ExternalEventHandler на операцию. Много объектов, сложнее DI.
3. **Revit.Async:** Библиотека для task-based pattern. Дополнительная зависимость, скрывает механику.
