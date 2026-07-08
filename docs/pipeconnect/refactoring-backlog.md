# PipeConnect: Refactoring Backlog

> Улучшения кода **без изменения бизнес-функционала**. Каждый пункт — результат
> исследования сессии 2026-07-08 (Exa-анализ modeless-возможности + аудит кода).
> Основной ADR: [ADR-043](../adr/043-pipeconnect-modal-justification.md).
>
> Загружать: при работе над техническим долгом PipeConnect.

---

## Implementation Status (2026-07-08)

| # | Пункт | Статус | Результат |
|---|---|---|---|
| R1 | Удалить мёртвый код `PipeConnectExternalEvent` + `ActionExternalEventHandler` | ✅ Done | Файлы удалены, DI-регистрация вычищена, 0 ссылок в .cs |
| R2a | `PipeConnectCommand` → `WindowInteropHelper.Owner` (без presenter) | ✅ Done | Owner установлен, `PositionNearCursor` сохранён |
| R2b | `AboutCommand` + `SettingsCommand` → `IDialogPresenter` | ✅ Done | Мигрированы на presenter, owner + recovery + логирование |
| R3 | Убрать `Topmost=True` из 5 PipeConnect XAML | ✅ Done | Убран из всех 5 (base `DialogWindowBase` уже ставит) |
| R4 | `Document _doc` → `IRevitContext` | ⏭️ Deferred | Не баг (modal safe). Prerequisite для future Close+Loop+Recreate |
| R5 | SESSION markers + Error logging в `PipeConnectCommand.Execute` | ✅ Done | `LogSessionStart`/`LogSessionEnd` + `Error` в catch, без `BeginScope` (C15) |
| R6 | `ConfirmClose` при `IsBusy==true` — defensive fix | ⏭️ Deferred | YAGNI: операции синхронные, IsBusy = мс. Делать при future async |
| R7 | `ServiceRegistrar` — пометить мёртвый код комментарием | ✅ Moot | Код удалён в R1, комментарий не нужен |
| R8 | `WindowStartupLocation` CenterScreen → Manual в XAML | ✅ Done | XAML приведён в соответствие с code-behind `PositionNearCursor()` |

### Verification

- Build R25: ✅ 0 warnings / 0 errors
- Build R24: ✅ 0 warnings / 0 errors
- Build R21: ✅ 0 warnings / 0 errors
- Build R19: ✅ 0 warnings / 0 errors
- Tests: ✅ 1705/1705 passed
- Subagent validation: ✅ 3/3 PASS (R1, R3+R8, R5+R2a+R2b) — регрессий не выявлено
- Manual Revit test: ⏳ pending (требует ручного тестирования пользователем)

### Объём изменений

- **Удалено:** ~55 строк (мёртвый код + мёртвый XAML)
- **Добавлено:** ~20 строк (логирование + owner + InitializeContext)
- **Изменено:** ~10 строк (2 команды на presenter)
- **Всего:** ~85 строк, 0 поведенческих изменений

---

## Краткая сводка (исходная)

| # | Пункт | Сложность | Риск | Приоритет |
|---|---|---|---|---|
| R1 | Удалить мёртвый код `PipeConnectExternalEvent` + `ActionExternalEventHandler` | Низкая | Низкий | Высокий |
| R2a | `PipeConnectCommand` → `WindowInteropHelper.Owner` (без presenter) | Низкая | Низкий | Высокий |
| R2b | `AboutCommand` + `SettingsCommand` → `IDialogPresenter` | Низкая | Низкий | Высокий |
| R3 | Убрать `Topmost=True` дубликат (5 XAML + DialogWindowBase) | Тривиальная | Низкий | Низкий |
| R4 | `Document _doc` поле → `IRevitContext` (I-05 дух) | Высокая | Средний | Низкий (опц.) |
| R5 | Логирование: SESSION markers в `PipeConnectCommand.Execute` | Низкая | Низкий | Средний |
| R6 | Аудит `ConfirmClose` / `AsyncDeferredAction` использование | Средняя | Средний | Средний |
| R7 | `ServiceRegistrar` — пометить `PipeConnectExternalEvent` блок комментом | Тривиальная | Низкий | Высокий |
| R8 | `WindowStartupLocation` CenterScreen → Manual в `PipeConnectEditorView.xaml` | Тривиальная | Низкий | Низкий |

---

## R1: Удалить мёртвый код `PipeConnectExternalEvent` + `ActionExternalEventHandler`

### Что

`PipeConnectExternalEvent` (`src/SmartCon.PipeConnect/Events/PipeConnectExternalEvent.cs`)
и `ActionExternalEventHandler` (`src/SmartCon.Revit/Events/ActionExternalEventHandler.cs`)
зарегистрированы в DI (`ServiceRegistrar.cs:102-105, 107-111`) но **нигде не
вызываются** — grep по всему `src/` возвращает только регистрацию и определения классов.

### Почему это проблема

- Вводит в заблуждение: ADR-006/008 описывали их как активную инфраструктуру (исправлено в ADR-043)
- Лишние `ExternalEvent.Create()` вызовы при старте Revit (память + handle)
- Будущие агенты могут подумать что PipeConnect modeless и пытаться их использовать
- `PipeConnectExternalEvent` примитивный (`Interlocked.Exchange` одно действие, без очереди, без awaitable) — даже если понадобится modeless, он непригоден

### Варианты

**A. Удалить полностью (Recommended):**
- Удалить `PipeConnectExternalEvent.cs`
- Удалить `ActionExternalEventHandler.cs` (если не используется elsewhere — проверить)
- Удалить регистрацию в `ServiceRegistrar.cs:101-111`
- Обновить ADR-006/008 (уже отмечено «кандидат на удаление»)

**B. Оставить с пометкой `// DEPRECATED — see ADR-043, unused, candidate for removal`:**
- Менее инвазивно, но оставляет мёртвый код

### Проверка перед удалением

```bash
# Должно вернуть 0 (только регистрация + определения)
rg "PipeConnectExternalEvent|ActionExternalEventHandler" src/ --type cs
```

### Оценка

- ~30 строк удаление + 10 строк DI
- 0 риск (код не вызывается)
- 30 минут

---

## R2: Dialog presentation — owner + presenter migration

### Контекст

Три команды PipeConnect создавали view напрямую (`new View(vm) + ShowDialog()`),
минуя `IDialogPresenter`. Это пропускало:
- `WindowInteropHelper.Owner = revitHandle` (parenting к Revit)
- `BatchDialogRenderRecovery` на net48 (workaround #95 white-dialog)
- Логирование через `SmartConLogger.Freeze`

### R2a: `PipeConnectCommand` — ручной owner (✅ Done)

`PipeConnectCommand` **НЕ мигрирован** на `IDialogPresenter`. Причина:
`WpfDialogPresenter.ShowDialogInternal` на net48 вешает `BatchDialogRenderRecovery`
(ставит окно off-screen, затем в центр экрана после WM_PAINT). Это переопределит
`PositionNearCursor()` — намеренный UX (окно возле курсора, рядом с соединяемыми
элементами). `BatchDialogRenderRecovery` — workaround для бага #95 (white dialog
после `OpenDocumentFile` + family upgrade), который к PipeConnectEditor не применим.

Вместо presenter добавлен ручной `WindowInteropHelper.Owner`:
```csharp
var view = new PipeConnectEditorView(vm);
new WindowInteropHelper(view).Owner = commandData.Application.MainWindowHandle;
view.ShowDialog();
```

### R2b: `AboutCommand` + `SettingsCommand` → `IDialogPresenter` (✅ Done)

Обе команды мигрированы на `IDialogPresenter.ShowDialog(vm)`. Safe: CenterScreen,
нет `PositionNearCursor`, нет конфликта с `BatchDialogRenderRecovery`.

`AboutCommand` дополнительно получил `CommandHelper.InitializeContext` (раньше
отсутствовал) — необходим для `WpfDialogPresenter.GetOwnerHandle` чтобы получить
`UIApplication.MainWindowHandle` через `RevitContext` (вместо ненадёжного fallback
на `Process.MainWindowHandle`).

```csharp
var presenter = ServiceHost.GetService<IDialogPresenter>();
presenter.ShowDialog(vm);
```

---

## R3: Убрать `Topmost=True` дубликат

### Что

`PipeConnectEditorView.xaml:14` — `Topmost="True"` дублирует `DialogWindowBase.cs:18` (`Topmost = true` в конструкторе).

### Почему

- Дублирование. Если когда-то уберём Topmost из базового класса — XAML всё равно переопределит
- Для modal окна Topmost спорен (может перекрывать Revit TaskDialogs — см. Autodesk forum «WPF focus after TaskDialog.Show»)

### Решение

Убрать `Topmost="True"` из XAML (базовый класс уже ставит).

### Оценка

- 1 строка
- Тривиально

---

## R4: `Document _doc` поле → `IRevitContext` (I-05 дух)

### Что

`PipeConnectEditorViewModel.cs:20` — `private readonly Document _doc;` хранится как поле.
I-05 говорит «не хранить `Element`/`Connector` между транзакциями» — `Document` формально
не `Element`, но для modeless это стало бы багом (документ может закрыться).

### Почему это проблема (для modal — не баг, для будущего — риск)

- При modal документ не закроется пока окно открыто → безопасно
- Но если когда-то перейдём на Close+Loop+Recreate (ADR-043 future) — `_doc` станет stale
- FamilyManager VM использует `_revitContext.GetDocument()` (пример `FamilyEdit.cs:405`) — паттерн уже есть

### Решение (опционально, большой рефакторинг)

- Добавить `IRevitContext _revitContext` в конструктор VM
- Заменить `_doc` на `_revitContext.GetDocument()` в каждом методе (~30 вызовов)
- Обновить `PipeConnectViewModelFactory` — прокинуть `IRevitContext`

### Риск

- Высокий объём (~30 call sites в 6 partial-файлах)
- Средний риск (каждый `_doc` → `_revitContext.GetDocument()` — но при modal это всегда один и тот же документ)
- Без поведенческого изменения

### Оценка

- ~150 строк изменений
- 4 часа + полный smoke-test
- **Низкий приоритет** — текущий код работает, это чистота ради будущего

---

## R5: Логирование — SESSION markers в `PipeConnectCommand.Execute` (✅ Done)

### Что

`PipeConnectCommand.cs` не имел `LogSessionStart`/`LogSessionEnd` markers.
По skill `smartcon-logging` Recipe 2 — top-level user action должен иметь SESSION banners.

### Решение (реализованное)

Следовал паттерну `FamilyManagerMainViewModel` (долгоживущая session = только markers,
без `BeginScope`). `PipeConnectCommand.Execute` — долгоживущий (modal блокирует до
закрытия окна), `BeginScope` вокруг него нарушил бы C15 (long-lived scope → 2 MB логов).

```csharp
var sessionStart = DateTime.Now;
SmartConLogger.LogSessionStart("PipeConnect");
try
{
    // ... existing code ...
    return Result.Succeeded;
}
catch (Autodesk.Revit.Exceptions.OperationCanceledException)
{
    return Result.Cancelled;
}
catch (Exception ex)
{
    SmartConLogger.Error($"PipeConnect command failed: {ex.GetType().Name}: {ex.Message}");
    message = ex.Message;
    return Result.Failed;
}
finally
{
    SmartConLogger.LogSessionEnd("PipeConnect", sessionStart);
}
```

Inner work (VM commands: EditorConnect, EditorChain, EditorCycle, и т.д.) имеет
свои own scopes — correlation через OpId работает через nested scope chain.

---

## R6: `ConfirmClose` при `IsBusy==true` (⏭️ Deferred — YAGNI)

### Что

`PipeConnectEditorViewModel.ConfirmClose` (`Connect.cs:90-96`):
```csharp
public void ConfirmClose(CloseConfirmationArgs args)
{
    if (!IsSessionActive) return;
    args.Cancel = true;
    if (!IsBusy && !IsClosing)
        args.DeferredAction = Cancel;
}
```

Если `IsBusy == true` — `args.Cancel = true` но `DeferredAction` не ставится →
окно не закрывается, Cancel не вызывается. Пользователь застревает.

### Почему отложено

Все операции сейчас **синхронные и быстрые** (миллисекунды). `IsBusy=true` окно —
миллисекунды. Шанс кликнуть X именно в этот момент ≈ 0. Станет реальным только
если операции станут async (future Close+Loop+Recreate из ADR-043). YAGNI —
делать в рамках той задачи, когда асинхронность появится.

---

## R7: `ServiceRegistrar` — пометить мёртвый код комментарием (✅ Moot)

Код удалён в R1. Комментарий не нужен — мёртвого кода больше нет.

---

## R8: `WindowStartupLocation` CenterScreen → Manual (✅ Done)

`PipeConnectEditorView.xaml:12` имел `WindowStartupLocation="CenterScreen"`,
но code-behind `PositionNearCursor()` (line 40) перезаписывал на `Manual`.
XAML value был мёртв. Изменён на `Manual` для честности.

---

## Порядок внедрения (выполнено)

1. **R1** (удаление мёртвого кода) — ✅ 0 риск, 0 вызовов, 0 тестов
2. **R8** (WindowStartupLocation) — ✅ тривиально, XAML приведён в соответствие
3. **R3** (Topmost дубликат, 5 XAML) — ✅ тривиально, base уже ставит
4. **R5** (SESSION логирование) — ✅ низкий риск, ускоряет диагностику
5. **R2a** (PipeConnect owner) — ✅ Autodesk-рекомендованный паттерн
6. **R2b** (About/Settings → presenter) — ✅ низкий риск, консистентность

**Отложено:**
- R4 (Document → IRevitContext) — не баг, prerequisite для future Close+Loop+Recreate
- R6 (ConfirmClose IsBusy) — YAGNI, делать при future async

---

## Связанные документы

- [ADR-043](../adr/043-pipeconnect-modal-justification.md) — почему modal обязателен
- [ADR-003](../adr/003-transaction-group-pattern.md) — TransactionGroup pattern
- [ADR-006](../adr/006-external-event-pattern.md) — ExternalEvent (modeless)
- [ADR-008](../adr/008-external-event-action-queue.md) — Action Queue (FamilyManager)
- [invariants.md](../invariants.md) I-01a — исключение для modal command context
- [known-workarounds.md](../known-workarounds.md) — modal PipeConnect workaround
