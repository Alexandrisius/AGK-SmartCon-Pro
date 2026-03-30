# ADR-006: IExternalEventHandler для WPF -> Revit API

**Статус:** accepted
**Дата:** 2026-03-25

## Контекст

PipeConnect использует немодальные (modeless) WPF-окна. WPF работает в отдельном UI-потоке, а Revit API строго однопоточен — любой вызов API из не-основного потока приведёт к crash или непредсказуемому поведению.

## Решение

Все вызовы Revit API из WPF инициируются через `IExternalEventHandler`:

```
WPF UI Thread                     Revit Main Thread
-----------------                 -----------------
ViewModel.Command()
  --> _externalEvent.Raise()  ------->  Handler.Execute(UIApplication)
                                          --> _transactionService.RunInTransaction(...)
  <-- PropertyChanged  <--------------  Результат через INotifyPropertyChanged
ViewModel обновляет UI
```

### Реализация

`SmartCon.Revit/Events/PipeConnectExternalEvent.cs` — единый обработчик для всех операций PipeConnect. Получает «команду» (enum или Action) от ViewModel и выполняет её в контексте Revit main thread.

## Последствия

**Плюсы:**
- Гарантированная потокобезопасность
- Стандартный паттерн Revit API (документированный Autodesk)
- Revit не зависает при работе UI

**Минусы:**
- Асинхронность: ViewModel не может синхронно получить результат
- Нужен механизм передачи «команд» от ViewModel к Handler
- Raise() не гарантирует немедленное выполнение — Revit обрабатывает event в idle

## Альтернативы

1. **Модальные окна:** Можно вызывать API напрямую, но окно блокирует Revit.
2. **UIApplication.DoEvents():** Хак, не поддерживается официально.
3. **External DB Application:** Для фоновых задач, не подходит для интерактивного UI.
