# TransactionGroup — полный rollback на кнопку ОТМЕНА

Cancel = RollBack, Connect = Assimilate.

---

## ⚠️ КРИТИЧЕСКИЙ УРОК ИЗ ДИАГНОСТИКИ

**Корневая причина предыдущего сбоя:**  
`TransactionGroup`, открытая **внутри** `ExternalEvent.Execute()`, **автоматически закрывается Revit** (~500мс) когда handler завершается.  
Это фундаментальное ограничение Revit API — группа не может пережить выход из ExternalEvent handler.

**Диагностический лог (подтверждение):**
```
12:06:49.497  [STATE] IMMEDIATE POST-DISPATCHER: _groupSession.IsActive = True
12:06:50.002  [ERR]   [POLL] CRITICAL: Group became INACTIVE after 0,504s!
```

**Вывод:** TransactionGroup ОБЯЗАНА быть открыта в `PipeConnectCommand.Execute()` — единственном контексте, который живёт всё время пока окно открыто (при использовании `ShowDialog()`).

---

## Архитектурное решение

### Схема жизненного цикла (ПРАВИЛЬНАЯ)

```
PipeConnectCommand.Execute()          ← Revit Main Thread
  │
  ├─ [read-only анализ S1..S5]
  ├─ groupSession = txService.BeginGroupSession(...)    ← открыть ДО ShowDialog
  ├─ groupSession.RunInTransaction("S4 ...")            ← если нужно
  ├─ groupSession.RunInTransaction("S3 ...")
  ├─ передать groupSession в ViewModel
  ├─ view.ShowDialog()   ◄────── БЛОКИРУЕТ Execute() = TG остаётся живой
  │     │
  │     ├─ OnWindowLoaded: вставить фитинг по умолчанию через ExternalEvent
  │     ├─ [пользователь взаимодействует...]
  │     ├─ Connect → ExternalEvent → RunInTransaction("ConnectTo") → groupSession.Assimilate()
  │     └─ Cancel  → ExternalEvent → groupSession.RollBack()
  │
  └─ return Result.Succeeded
```

### Почему ShowDialog, а не Show

- `view.Show()` → `Execute()` возвращает → Revit закрывает TransactionGroup
- `view.ShowDialog()` → `Execute()` заблокирован → TransactionGroup остаётся активной
- WPF EventLoop внутри `ShowDialog()` обрабатывает Windows-сообщения → **ExternalEvent-ы продолжают работать**

### Почему ExternalEvent-ы работают внутри ShowDialog

`ShowDialog()` запускает вложенный message pump на том же потоке (Revit main thread).  
Revit доставляет ExternalEvent через Win32 PostMessage. Вложенный loop эти сообщения обрабатывает.  
Таким образом, `_eventHandler.Raise(app => {...})` продолжает работать без изменений.

---

## Что НЕ откатывается (ограничение Revit)

**S1.1/S2.1 `EnsureTypeCode`** — установка типа коннектора через EditFamily для FamilyInstance.  
Редкая одноразовая операция. Принимается как есть.

---

## Шаг 1 — `PipeConnectCommand.cs`

### 1.1 Открыть TransactionGroup ДО показа окна

Вернуть S3 и S4 транзакции обратно в `Execute()`, но теперь **внутри TransactionGroup**:

```csharp
// После анализа S1..S5, перед созданием ViewModel
var groupSession = txService.BeginGroupSession("PipeConnect — Сессия редактора");
SmartConLogger.TxGroupBegin("PipeConnect — Сессия редактора");

// S4: подгонка размера
if (!plan.Skip)
{
    groupSession.RunInTransaction("PipeConnect — S4 Подгонка размера", doc =>
    {
        paramResolver.TrySetConnectorRadius(doc,
            dynamicProxy.OwnerElementId, dynamicProxy.ConnectorIndex, plan.TargetRadius);
        doc.Regenerate();
    });
    // обновить dynamicProxy через RefreshConnector
}

// S3: выравнивание
groupSession.RunInTransaction("PipeConnect — S3 Выравнивание", doc =>
{
    // применить alignResult (существующий код)
});
// обновить dynamicProxy через RefreshConnector
```

### 1.2 Передать groupSession в ViewModel

```csharp
var vm = new PipeConnectEditorViewModel(
    sessionCtx, txService, connectorSvc, transformSvc,
    fittingInsertSvc, paramResolver, eventHandler,
    groupSession);   // ← новый параметр
```

### 1.3 Показать окно МОДАЛЬНО

```csharp
var view = new PipeConnectEditorView(vm);
view.ShowDialog();   // ← было Show(), ИЗМЕНИТЬ на ShowDialog()

// После закрытия диалога — если группа ещё активна (закрыли крестиком),
// откатить прямо здесь (мы на Revit main thread, Execute() не вернулся)
if (groupSession.IsActive)
{
    SmartConLogger.TxGroupRollBack("Window closed without Connect or Cancel");
    groupSession.RollBack();
}
```

Убрать `view.Activate()` и `view.Topmost = true` (не нужны для модального окна).

### 1.4 Откат при исключении

```csharp
catch (Exception)
{
    groupSession?.RollBack();
    throw;
}
```

---

## Шаг 2 — `PipeConnectEditorViewModel.cs`

### 2.1 Конструктор — принять готовую groupSession

```csharp
// Было: private ITransactionGroupSession? _groupSession; (инициализировалась в OnWindowLoaded)
// Стало: принимается готовой через конструктор

public PipeConnectEditorViewModel(
    PipeConnectSessionContext ctx,
    ITransactionService txService,
    IConnectorService connSvc,
    ITransformService transformSvc,
    IFittingInsertService fittingInsertSvc,
    IParameterResolver paramResolver,
    IActionExternalEventHandler eventHandler,
    ITransactionGroupSession groupSession)   // ← новый параметр
{
    ...
    _groupSession = groupSession;
}
```

### 2.2 `OnWindowLoaded` — убрать открытие группы и S3/S4

`OnWindowLoaded` теперь выполняет **только**:
1. Вставку фитинга по умолчанию (если есть): `_groupSession.RunInTransaction("Вставка фитинга", ...)`
2. Загрузку списка коннекторов
3. `IsSessionActive = true`

S3, S4 и открытие TransactionGroup **удалить из OnWindowLoaded** (они уже выполнены в `Execute()`).

### 2.3 Все операции редактора — без изменений

`_groupSession.RunInTransaction(...)` уже используется везде (если рефактор шага 2 из предыдущей итерации сделан).  
Логика не меняется — просто группа теперь живая.

Затронутые методы:
- `ExecuteRotate()` — поворот
- `CycleConnector()` — смена коннектора
- `InsertFitting()` — вставка / смена фитинга
- `SizeFittingConnectors()` — размер фитинга
- `RealignAfterSizing()` — выравнивание после размера
- `Connect()` — ConnectTo

### 2.4 `Cancel()`

```csharp
_eventHandler.Raise(app =>
{
    _groupSession?.RollBack();
    _groupSession = null;
    Application.Current.Dispatcher.Invoke(() =>
    {
        IsSessionActive = false;
        RequestClose?.Invoke();
    });
});
```

### 2.5 `Connect()`

```csharp
// После RunInTransaction("ConnectTo", ...):
_groupSession!.Assimilate();
_groupSession = null;
```

### 2.6 Защита от закрытия крестиком (OnWindowClosing)

Полная логика откатывается в `PipeConnectCommand.Execute()` после `ShowDialog()` возврата (см. шаг 1.3).  
`OnWindowClosing` в ViewModel нужен только чтобы **не мешать закрытию** — убедиться что `_groupSession` не блокирует окно:

```csharp
public void OnWindowClosing()
{
    SmartConLogger.TxGroup("OnWindowClosing() called - window closing via X or Alt+F4");
    // Ничего не делаем здесь — откат выполнится в Execute() после ShowDialog() возврата
}
```

> **Почему не ExternalEvent:** после `ShowDialog()` закрытия ExternalEvent может не успеть  
> выполниться до возврата в `Execute()`. Откат прямо в `Execute()` — надёжнее.

---

## Шаг 3 — `PipeConnectEditorView.xaml.cs`

Заменить `Show()` → `ShowDialog()` уже сделано в Command (шаг 1.3).

В `Closing` событии: логика остаётся, только убедиться что `IsClosing` флаг выставляется до `RequestClose.Invoke()` чтобы не было двойного вызова Cancel.

---

## Шаг 4 — Документация

- `docs/adr/003-transaction-group-pattern.md` — обновить: TransactionGroup открывается в `Execute()`, ShowDialog блокирует Execute, ExternalEvents работают в ShowDialog message loop
- `docs/invariants.md` I-04 — обновить: "группа открывается в `PipeConnectCommand.Execute()` ДО `ShowDialog()`"

---

## Затронутые файлы

| Файл | Изменение |
|---|---|
| `PipeConnectCommand.cs` | Открыть TG; выполнить S3+S4 внутри TG; передать groupSession в VM; `ShowDialog()` |
| `PipeConnectEditorViewModel.cs` | Принять groupSession в конструкторе; убрать TG-открытие и S3/S4 из `OnWindowLoaded` |
| `docs/adr/003-transaction-group-pattern.md` | Обновить архитектурное решение |
| `docs/invariants.md` | Уточнить I-04 |

---

## Риски и митигация

| Риск | Митигация |
|---|---|
| ShowDialog блокирует Revit UI — пользователь не может работать с моделью | Ожидаемое поведение для модального редактора соединения |
| ExternalEvent не срабатывает внутри ShowDialog | Проверено: вложенный Win32 message pump внутри ShowDialog обрабатывает Revit ExternalEvent сообщения |
| Пользователь закрывает окно крестиком | Откат в `Execute()` после `ShowDialog()` возврата (шаг 1.3) — не через ExternalEvent |
| Исключение в S3/S4 в Execute() до ShowDialog | `catch` блок → `groupSession.RollBack()` |
| Revit крашится при открытой группе | `TransactionGroup` IDisposable → auto-rollback |
| ~~TransactionGroup закрывается после ExternalEvent~~  | ~~Устранено: TG открыта в Execute(), не в ExternalEvent~~ |
