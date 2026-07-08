# ADR-006: IExternalEventHandler для WPF -> Revit API

**Статус:** accepted (уточнён ADR-043)
**Дата:** 2026-03-25
**Уточнение:** 2026-07-08 — изначально описывал PipeConnect как modeless, что было ошибкой проектирования. См. ADR-043.

## Контекст

WPF работает в отдельном UI-потоке, а Revit API строго однопоточен — любой
вызов API из не-основного потока приведёт к crash или непредсказуемому поведению.
Для **modeless** WPF-окон (FamilyManager dockable panel) нужен мост между
WPF UI thread и Revit main thread.

**Важно:** Этот ADR применяется к **modeless** окнам. `PipeConnectEditor` —
**модальное** окно и использует прямые вызовы Revit API из ViewModel
(см. ADR-043 для обоснования почему modeless невозможен для PipeConnect).

## Решение

Все вызовы Revit API из **modeless** WPF инициируются через `IExternalEventHandler`:

```
WPF UI Thread                     Revit Main Thread
-----------------                 -----------------
ViewModel.Command()
  --> _externalEvent.Raise()  ------->  Handler.Execute(UIApplication)
                                           --> _transactionService.RunInTransaction(...)
  <-- PropertyChanged  <--------------  Результат через INotifyPropertyChanged
ViewModel обновляет UI
```

### Реализация (FamilyManager)

`FamilyManagerAwaitableEvent` (pure C# queue + `TaskCompletionSource` +
`RunContinuationsAsynchronously`) + `RevitFamilyManagerAwaitableEvent`
(IExternalEventHandler adapter в SmartCon.App). Контракт:
`IFamilyManagerAwaitableEvent` — `RaiseAsync(Action<object>)`,
`RaiseAsync<T>(Func<object,T>)`, `RaiseAsyncTask`, `ProcessQueue`, `Initialize`.

### Где НЕ применяется (PipeConnectEditor)

`PipeConnectEditor` показывается через `view.ShowDialog()` внутри
`IExternalCommand.Execute`. При `ShowDialog` WPF UI thread == Revit main
thread, поэтому прямые вызовы Revit API из `[RelayCommand]`-методов
безопасны (нет гонок с idle loop). `TransactionGroup` живёт в command
context до закрытия окна. См. ADR-043.

`PipeConnectExternalEvent` (`SmartCon.PipeConnect/Events/`) зарегистрирован
в DI (`ServiceRegistrar.cs:107-111`) как «задел» под modeless, но **нигде не
вызывается** — кандидат на удаление (см. refactoring backlog).

## Последствия

**Плюсы (для modeless / FamilyManager):**
- Гарантированная потокобезопасность
- Стандартный паттерн Revit API (документированный Autodesk)
- Revit не зависает при работе UI

**Минусы (для modeless):**
- Асинхронность: ViewModel не может синхронно получить результат
- Нужен механизм передачи «команд» от ViewModel к Handler
- Raise() не гарантирует немедленное выполнение — Revit обрабатывает event в idle
- **TransactionGroup не переживает возврат из Execute** — нельзя держать
  долгоживущую группу между raises (см. ADR-043, Ограничение 2)

## Альтернативы

1. **Модальные окна (PipeConnect):** Можно вызывать API напрямую, но окно блокирует Revit. Единственно возможное для live real-element preview + single-undo cancel (ADR-043).
2. **UIApplication.DoEvents():** Хак, не поддерживается официально.
3. **External DB Application:** Для фоновых задач, не подходит для интерактивного UI.
