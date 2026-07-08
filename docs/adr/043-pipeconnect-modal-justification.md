# ADR-043: PipeConnectEditor — модальность как единственно возможное решение

**Статус:** accepted
**Дата:** 2026-07-08
**Supersedes (partially):** ADR-006, ADR-008 (уточняет — их описание modeless PipeConnect было ошибкой проектирования)
**Связан:** ADR-003 (TransactionGroup pattern)

## Контекст

### Исходный дизайн (ошибка)

ADR-006 («IExternalEventHandler для WPF -> Revit API», 2026-03-25) и
ADR-008 («Action Queue паттерн для ExternalEvent», 2026-03-25) проектировали
`PipeConnectEditor` как **немодальное (modeless) WPF-окно**, в котором все
вызовы Revit API идут через `IExternalEventHandler.Raise()`. Тот же дизайн
повторялся в `docs/pipeconnect/ui-spec.md` («Тип: Немодальное (modeless) окно»)
и `docs/domain/glossary.md` («PipeConnectEditor | Немодальное WPF-окно»).

**Фактически код реализован модально** (`PipeConnectCommand.cs:33` —
`view.ShowDialog()`), а ViewModel обращается к Revit API **напрямую** из
`[RelayCommand]`-методов через `_groupSession.RunInTransaction(...)`, без
`ExternalEvent`. `PipeConnectExternalEvent` зарегистрирован в DI
(`ServiceRegistrar.cs:107-111`) но **нигде не вызывается** — это
«архитектурный задел» под modeless, который не был интегрирован.

### Повод для ревизии

Пользователь запросил переход на modeless чтобы разрешить навигацию по
виду модели (зум/пан/орбит) перед применением коннекта. Проведено глубокое
Exa-исследование (8+ запросов, Autodesk forums, Jeremy Tammik blog,
Arnošt Löbel statements, StackOverflow accepted answers, GitHub open source).

## Решение (подтверждённое исследованием)

**Текущее модальное решение — единственно возможное для текущего UX**
(live real-element preview + single-undo cancel + TransactionGroup rollback).

Это не компромисс, а следствие трёх фундаментальных ограничений Revit API,
подтверждённых авторитетными источниками.

### Ограничение 1: TransactionGroup откатывается при возврате из IExternalCommand.Execute

Arnošt Löbel (Revit dev team), The Building Coder «More on Transaction Groups
and Assimilation» (2018):

> «it should not be possible to leave your command context with either a
> transaction or transaction group still open... Revit will roll them back
> for you... Before an external command was launched, Revit would start an
> internal transaction group. Then the command was executed and upon
> returning from it... Revit would test whether there were any transactions
> or transaction groups open. If there are, **Revit will force them to be
> rolled back**.»

**Следствие:** нельзя открыть `TransactionGroup` в `IExternalCommand.Execute`,
показать `view.Show()` (modeless) и вернуть `Result.Succeeded` — группа будет
автоматически откатана Revit. Это убивает план «Init синхронно до Show».

### Ограничение 2: TransactionGroup откатывается при возврате из IExternalEventHandler.Execute

Arnošt Löbel, Autodesk forum «Transactions and Document Events» (2015):

> «for your transactions happen during an event (not a command) we do not
> allow any even handler to leave any transaction-related scope open upon
> returning from the handler. **If Revit sees such a scope left open, it
> forces it to close and then deletes everything that the handler has done
> to the model.**»

**Следствие:** нельзя держать долгоживущий `TransactionGroup` между
`ExternalEvent` raises — каждый `Execute` должен закрыть все scopes. Это
убивает план «долгоживущий TransactionGroup + awaitable ExternalEvent queue».

### Ограничение 3: Hide() на modal WPF в Revit НЕ разблокирует Revit UI

Autodesk forum «Ask user for element selection while a Winform is showing»
(2016, accepted answer):

> «`this.Hide(); ... PickObjects ... this.Show();` → I can see the checkbox
> but **the revit document seems to be locked (most stuff grayed out). I can
> not make any selection though the form is hidden.** So somehow the focus
> seems to be still on the Winform and blocking the revit file.»

Дополнительно, WPF имеет баг: `Hide()` на modal окне может закрыть его
(StackOverflow «How do I Hide() a Modal WPF Window without it closing?»):

> «The very moment that I hide the window... the `ShowDialog()` code
> releases the window and closes it. This is absolutely a bug in the WPF code.»

**Следствие:** нельзя `Hide()` modal окна чтобы разблокировать Revit для
навигации — Revit остаётся locked. Это убивает первоначальный Вариант C
(Hide/Show overlay).

### Дополнительные неподтверждённые/хрупкие подходы

| Подход | Статус | Почему отвергнут |
|---|---|---|
| `PostableCommand.Undo` для отката N транзакций | Autodesk forum «How to execute Undo?» | Откатывает только последнюю, не N. Jeremy Tammik: «Is easier to record every ElementId you created and delete.» |
| `UIFrameworkServices.QuickAccessToolBarService.performMultipleUndoRedoOperations(true, N)` | Autodesk forum | Undocumented, «can take some adverse effects», хрупко (例外 «The referenced object is not valid») |
| `Dispatcher.Run` + Close+Loop+Recreate modal | Autodesk forum «Keep A Command Running» | Работает, но сложный VM state serialization (`ConnectorProxy`/`FittingChainPlan`/`ConnectionGraph`), ghost elements баг Revit, риск deadlock. Отложен как отдельный подпроект (см. «Future: Close+Loop+Recreate» ниже). |
| `DirectContext3D` preview (Вариант B) | Autodesk forum «Preview Graphics» | Чистая одна Undo запись, но меняет UX (нет live real-element preview) + требует новой инфраструктуры DirectContext3D. Отдельная итерация. |

## Почему модальное решение ИДЕАЛЬНО для текущего UX

Текущий UX `PipeConnectEditor`:
- Пользователь видит **реальные** фитинги/редьюсеры в модели (live real-element preview)
- Поворачивает, меняет размер, вставляет фитинги — каждое действие = `Transaction` commit, видит результат сразу
- «Соединить» → `TransactionGroup.Assimilate()` = **одна** Undo запись (Ctrl+Z)
- «Отмена» → `TransactionGroup.RollBack()` = **полный** откат всех изменений

Модальное решение обеспечивает все 4 свойства одновременно:

| Свойство | Как обеспечивается | Почему modeless не может |
|---|---|---|
| Live real-element preview | Каждая операция = `Transaction` commit внутри `TransactionGroup` | Modeless: TransactionGroup не переживает возврат из Execute (Ограничение 1/2) |
| Single Undo (Ctrl+Z) | `TransactionGroup.Assimilate()` сливает N Transaction в 1 undo | Modeless: нельзя держать группу открытой между операциями |
| Полный Cancel | `TransactionGroup.RollBack()` откатывает все | Modeless: группа уже откатана Revit, откатывать нечего |
| Прямые вызовы API из VM | При `ShowDialog` WPF UI thread == Revit main thread — гонок нет | Modeless: UI thread ≠ Revit idle loop → «Starting a transaction from outside API context» |

ADR-003:33 честно фиксирует: «TransactionGroup держит модель заблокированной
пока открыто окно» — но не объяснял ПОЧЕМУ modal. Этот ADR заполняет пробел.

## Исключение из I-01 (формальное)

Инвариант I-01 запрещает «Вызов Revit API из обработчиков WPF-событий напрямую».
`PipeConnectEditorViewModel` нарушает букву I-01: все `[RelayCommand]` методы
вызывают `_groupSession.RunInTransaction(...)`, `_connSvc.*`, `_transformSvc.*`
напрямую из WPF UI thread.

**Это безопасно и допустимо** потому что:
1. `view.ShowDialog()` блокирует Revit main thread внутри `IExternalCommand.Execute`
2. WPF message pump качает command bindings, но Revit idle loop НЕ качает
3. UI thread == Revit main thread (один поток) — гонок нет
4. `TransactionGroup` живёт в command context (не возвращается до закрытия окна)

**Это НЕ исключение для modeless.** Любой переход на modeless обязан обернуть
ВСЕ Revit API вызовы в `ExternalEvent.Raise()` (как FamilyManager). Прямые
вызовы из modeless UI = гонки и `InvalidOperationException`.

См. обновлённый I-01 в `invariants.md` (добавлено исключение для modal command context).

## Future: Close+Loop+Recreate (если навигация станет критичной)

Если навигация по виду во время настройки станет обязательной, единственный
рабочий паттерн (подтверждён Autodesk forum «Keep A Command Running»,
«PostCommand & Modal dialog», «Repeat External Command» — 3 accepted треда):

```
Execute (command context живёт всё время):
  groupSession = BeginGroupSession()          // живёт в command-local переменной
  savedState = null
  while (true):
    vm = create VM(groupSession, savedState)  // восстановление state
    view = new View(vm)
    view.ShowDialog()                          // modal, Revit locked, TxGroup работает
    switch (vm.UserAction):
      case Navigate:
        savedState = vm.ExportState()          // сериализация VM state
        // groupSession НЕ закрываем
        overlay = new Overlay("Навигация — нажмите Готово")
        overlay.Show()                         // modeless
        Dispatcher.Run()                       // блокирует Execute, качает pump
        overlay.Close()
        continue                               // loop → recreate Editor
      case Connect:  groupSession.Assimilate(); return Succeeded
      case Cancel:   groupSession.RollBack();  return Cancelled
```

**Риски (должны быть устранены до реализации):**
- R1: `Dispatcher.Run` конфликт с Revit message pump (DoEvents workaround, Autodesk blog «DevDay Munich»)
- R2: VM state serialization — `ConnectorProxy`, `FittingChainPlan`, `ConnectionGraph` нужно сериализовать (I-05: только `ElementId` между транзакциями)
- R3: Ghost elements баг Revit при TransactionGroup rollback + повтор (Autodesk forum «TransactionGroup doesn't entirely remove elements»)
- R4: Мигание окна при recreate, позиция, focus
- R5: `Dispatcher.Run` deadlock если overlay не закроется (timeout/fallback)
- R6: Document closed во время навигации → `groupSession` невалиден (нужен `DocumentClosing` guard)

**Оценка:** ~700 строк + тесты, отдельная итерация. Не включать в текущий scope.

## Verification (исследовательская сессия)

Исследование проведено 2026-07-08 через Exa. Источники (по убыванию авторитетности):

1. **Arnošt Löbel** (Revit dev team) — The Building Coder «More on Transaction Groups and Assimilation» (2018) — Ограничение 1
2. **Arnošt Löbel** — Autodesk forum «Transactions and Document Events» (2015) — Ограничение 2
3. **Autodesk forum** «Ask user for element selection while a Winform is showing» (2016, accepted) — Ограничение 3
4. **StackOverflow** «How do I Hide() a Modal WPF Window without it closing?» — WPF Hide() баг
5. **Autodesk forum** «How to execute Undo?» (2024, Jeremy Tammik) — PostableCommand.Undo не работает для N>1
6. **Autodesk forum** «How to implement an undo rollback» (2020) — performMultipleUndoRedoOperations adverse effects
7. **Autodesk forum** «Keep A Command Running Until the User Stops It» (2022, accepted) — while loop pattern
8. **Autodesk forum** «PostCommand & Modal dialog» (2021, accepted) — Close+Loop+Recreate pattern
9. **Autodesk forum** «Repeat External Command» (2015, accepted) — while loop + Transaction
10. **The Building Coder** «Modeless Dialogues in Revit» — modal vs modeless context
11. **The Building Coder** «Ten Years Anniversary» — ExternalEvent для modeless, Autodesk рекомендует modal
12. **Autodesk forum** «Multiple transactions in Modeless Form» (2022) — TransactionGroup inside EventHandler
13. **Autodesk forum** «TransactionGroup doesn't entirely remove elements» (2020) — ghost elements баг
14. **GitHub** varolomer/RevitModelessForms, Nice3point/RevitToolkit, jeremytammik/Revit.Async — pattern references

## Последствия

**Плюсы:**
- Документация честно отражает код (устранено расхождение docs ↔ code)
- I-01 исключение для modal зафиксировано явно (не «нарушение», а «допустимый паттерн при ShowDialog»)
- Будущие агенты/разработчики не будут тратить время на modeless-рефакторинг PipeConnect (уже исследован — невозможно)
- Рефакторинг Close+Loop+Recreate задокументирован как future option с рисками

**Минусы:**
- Пользователь не может навигировать по виду во время настройки PipeConnect (принято — ограничение Revit API)
- `PipeConnectExternalEvent` + `ActionExternalEventHandler` — мёртвый код (см. refactoring backlog)

## Альтернативы

1. **Modeless + ExternalEvent + долгоживущий TransactionGroup** — НЕВОЗМОЖНО (Ограничения 1+2)
2. **Modeless + snapshot-based rollback** — N Undo записей, сложный restore, хрупкость. Отвергнуто пользователем.
3. **Modeless + DirectContext3D preview** — меняет UX, большой подпроект. Отдельная итерация.
4. **Modal + Close+Loop+Recreate** — работает, но сложный. Future option (см. выше).
5. **Оставить modal как есть + задокументировать** — **ПРИНЯТО** (этот ADR).
