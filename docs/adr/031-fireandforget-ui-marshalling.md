# ADR-031: FireAndForget — обязательный UI-marshalling (Phase 4c post-mortem)

**Status:** accepted
**Date:** 2026-06-19
**Phase:** 4c post-mortem
**Supersedes (partially):** ADR-018 §4 (FireAndForget design)

## Контекст

### 1. Исходный дизайн (ADR-018 §4)

`FireAndForget` был спроектирован как `async void` метод, чтобы `await` внутри
сохранял UI `SynchronizationContext`:

```csharp
private static async void FireAndForget(Func<Task> taskFactory)
{
    try { await taskFactory(); }   // ← сохраняет SyncContext → UI thread
    catch (Exception ex) { ... }
}
```

В Phase 4c (`7c344e6`, 5 июня 2026) `async void` был заменён на
`Task.Run + ConfigureAwait(false)` ради соответствия I-13 (anti-pattern `async void`):

```csharp
private static void FireAndForget(Func<Task> taskFactory, string operationName)
{
    _ = Task.Run(async () =>
    {
        try { await taskFactory().ConfigureAwait(false); }   // ← thread pool
        catch (...) { ... }
    });
}
```

### 2. Что сломалось

`ConfigureAwait(false)` **отрезает UI SyncContext**. Все `await` внутри
`taskFactory` (и весь downstream) **остаются на thread pool**. Любой
setter UI-свойства (`TreeNodes = rootNodes`, `RootNodes = ...`, `AttributeItems = ...`)
срабатывает не на UI thread.

#### 2.1. Конкретный баг #1: «типы не появляются после импорта»

**Сценарий:** импорт `ADSK_Бытовой вентилятор накладной.rfa` через FamilyManager.

**Лог** (`smartcon.log:34` от 5 июня 2026):

```
20:36:34.757 [OpId=1a20ba3c Op=FamilyDataExt Method=Extract] RESULT: 3 named types
20:36:34.807 [DBG]  [AwaitableEvent] Execute[1]: completed (pending=0)
20:36:37.358 [DBG]  [AwaitableEvent] RaiseAsync: enqueued (pending=1)   ← user pressed Refresh
```

**До 7c344e6** (`Import.cs:227-249`):
```csharp
FireAndForget(async () =>
{
    try { ... save ... }
    catch (...) { ... }
    await LoadTreeAsync();    // ← ВОТ ЭТА СТРОКА БЫЛА
});
```

UI обновлялся **дважды**: до save (без типов) + после save (с типами). ✅

**После 7c344e6**:
```diff
-                await LoadTreeAsync();
-            });
+            }, nameof(ExtractTypesForImportedFamilies));
```

UI обновляется только **один раз** (до save, без типов). ❌

#### 2.2. Конкретный баг #2: WPF render thread freeze в net48 (Revit 2023)

**Симптомы (юзер):**
> в net 4.8 после импорта семейства всё дерево нельзя использовать левой кнопкой мыши и выделение курсором не работает и только после ПКМ происходит разлог и всё работает хорошо

**Совпадает с `transaction-callback-freeze.md`** (`.agents/skills/revit-api-best-practice/references/`):
> UI freezes after button click in WPF DockablePane or modeless dialog that uses `ITransactionService` callbacks. **The freeze resolves only on next user interaction (right-click, window resize, or Revit window focus change).**

#### 2.3. Сравнение логов 2023 (net48) vs 2025 (net8) — диагностический момент

| | 2025 (net8) | 2023 (net48) |
|---|---|---|
| `Application.Current?.Dispatcher` после save | `HasShutdownStarted=False` | `HasShutdownStarted=<null>` (**null**!) |
| `LoadTreeAsync: end` после save | ✅ есть, 3,1мс | ❌ **не вызван** |
| Время от save до следующего действия юзера | сразу | **2.3 сек** (freeze) |

**Корень #2:** `Application.Current?.Dispatcher` возвращает **null** в net48 в FireAndForget (thread pool thread). В net8 — не null.

Согласно [WPF UI: Application.Current will be null (#662)](https://github.com/lepoco/wpfui/issues/662) (GitHub, lepoco/wpfui, июнь 2023):

> **OS version:** Windows 11
> **.NET version:** net48
> **WPF-UI NuGet version:** 3.0.0 preview.3
> To Reproduce: Revit Plugins Development
> Expected behavior: In case of software plug-in development, this Application.Current will be null

Это **известная проблема WPF в Revit addins**: `Application.Current` — статическое свойство, **может быть null** если WPF `Application` не создан (а в Revit addin `Application` создаётся только при наличии WPF окна).

## Решение

### Правило #1: FireAndForget + UI → dispatcher

**Любое обновление UI** (setter `ObservableCollection<T>`, `PropertyChanged`
через `[ObservableProperty]`, доступ к `Application.Current`) **внутри FireAndForget
должно маршалиться на UI thread явно**.

### Правило #2: захват dispatcher в ctor (не в момент FireAndForget)

Поскольку `Application.Current?.Dispatcher` ненадёжен в net48 Revit addin,
**диспетчер захватывается в ctor VM** (на UI thread, гарантированно) и
используется позже в FireAndForget:

```csharp
public FamilyManagerMainViewModel(...)
{
    // ctor runs on UI thread
    _uiDispatcher = System.Windows.Application.Current?.Dispatcher
        ?? Dispatcher.CurrentDispatcher;   // ← fallback if Application.Current is null
}

private void FireAndForget(...)
{
    _ = Task.Run(async () =>
    {
        try
        {
            // ... background work on thread pool ...
            await _uiDispatcher.InvokeAsync(() => LoadTreeAsync());   // ← marshal back to UI
        }
        catch (...) { ... }
    });
}
```

**Почему не `await` + `ConfigureAwait(true)`?** Потому что FireAndForget уже
внутри `Task.Run + ConfigureAwait(false)` — `ConfigureAwait(true)` в `LoadTreeAsync`
не восстановит dispatcher SyncContext, который был потерян **на Task.Run**.

**Почему `Dispatcher.CurrentDispatcher` в ctor надёжен?**
Согласно [StackOverflow: Dispatcher.CurrentDispatcher vs Application.Current.Dispatcher](https://stackoverflow.com/questions/10448987/dispatcher-currentdispatcher-vs-application-current-dispatcher):

> `Application.Current.Dispatcher` will always give you the UI thread's dispatcher, as this is the thread that spins up the sole Application instance.
> ...
> `Dispatcher.CurrentDispatcher` gets the dispatcher for the current thread. So, if you're looking for the UI thread's Dispatcher from a background process, don't use this.

**Но** в ctor VM (на UI thread) `Dispatcher.CurrentDispatcher` возвращает UI thread dispatcher. В FireAndForget (thread pool thread) `Dispatcher.CurrentDispatcher` **создаст новый** dispatcher для thread pool, что **неправильно**. Поэтому захват в ctor обязателен.

### Правило #3: убрать двойной `LoadTreeAsync` — уменьшить UI thread work

`await LoadTreeAsync()` ДО save (быстрый показ нового семейства) **дублирует**
работу UI thread. В net48 это создаёт **2 × ~10мс = 20мс main thread block**
(вместо одного ~10мс), что критично для WPF render thread.

**Решение:** убрать `LoadTreeAsync` в `Import.cs:161`, оставить **только** после
save через dispatcher.

**UX trade-off:** UI не показывает новое семейство **сразу** при импорте. Через
~0.5-1 сек (после save + dispatcher marshalling) — появляется. Это приемлемо
потому что пользователь всё равно ждёт индикатор "Импорт...".

### Правило #4: `TreeNodes = rootNodes`, не `TreeNodes.Clear() + Add()`

**Эксперимент (провалился):** `TreeNodes.Clear() + Add()` — incremental update
без `PropertyChanged`. **Теория:** WPF TreeView получает `CollectionChanged.Add`
events и обновляет только новые items, не пересоздаёт все visual elements.

**Практика:** в net48 WPF TreeView **не обновлял** ItemsSource правильно после
`Clear()` + `Add()`. Семейство **не отображалось** в дереве до ПКМ.

**Решение:** вернуть `TreeNodes = rootNodes` (PropertyChanged). WPF полностью
пересоздаёт visual tree, но это **надёжнее** чем incremental update в net48.

### Правило #5: `Measure` и `Debug` в `LoadTreeAsync` → минимизировать I/O на UI thread

`using var _measure = Measure(...)` пишет `=== END ===` при `Dispose` —
синхронный file I/O (lock + WriteLine) **на UI thread**. Согласно
[transaction-callback-freeze.md](https://github.com/Alexandrisius/AGK-SmartCon-Pro/blob/develop/.agents/skills/revit-api-best-practice/references/transaction-callback-freeze.md) Rule 4:

> `using var _scope = SmartConLogger.BeginScope(...)` around `RunInTransaction` — **SLOW / hot path I/O** — `PopOnDispose.Dispose` writes `=== END ===` to disk via `lock + StreamWriter.WriteLine`. Sits between two Revit operations on the main UI thread.

**Решение:** убрать `Measure` и большинство `Debug` из `LoadTreeAsync`. Оставить
**только ОДИН** `Debug` в `finally` для диагностики завершения.

## Применённые изменения

### Commit `ab443c8` (Phase 4c post-mortem initial fix)

| # | Файл | Было | Стало |
|---|---|---|---|
| 1 | `Import.cs:226-274` | FireAndForget без `LoadTreeAsync` после save | FireAndForget с `await dispatcher.InvokeAsync(LoadTreeAsync())` после save |
| 2 | `FamilyManagerMainViewModel.cs:611-625` (`OnPlacementCompleted`) | `dispatcher.BeginInvoke + FireAndForget(LoadTreeAsync)` | `_ = dispatcher.InvokeAsync(() => LoadTreeAsync())` (без FireAndForget) |
| 3 | `CategoryPickerViewModel.cs:115-121` | `FireAndForget(() => LoadTreeAsync(), ...)` | `_ = dispatcher.InvokeAsync(() => LoadTreeAsync())` |
| 4 | `CategoryTreeEditorViewModel.cs:65-82` | `FireAndForget(() => LoadAttributesForCategoryAsync(value), ...)` | `_ = dispatcher.InvokeAsync(() => LoadAttributesForCategoryAsync(value))` |

### Следующие правки (net48 freeze debugging)

| # | Файл | Изменение | Зачем |
|---|---|---|---|
| 5 | `FamilyManagerMainViewModel.cs:60` | `private readonly Dispatcher _uiDispatcher` | Захват dispatcher в ctor (надёжнее чем `Application.Current?.Dispatcher` в FireAndForget) |
| 6 | `FamilyManagerMainViewModel.cs:158-167` | `_uiDispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher` + Debug | Захват в ctor |
| 7 | `FamilyManagerMainViewModel.cs:605-618` | `OnPlacementCompleted` использует `_uiDispatcher` | Был `Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher` |
| 8 | `FamilyManagerMainViewModel.cs:568-571` | `RefreshTreeViaExternalEventAsync` использует `_uiDispatcher` | Аналогично |
| 9 | `CategoryPickerViewModel.cs` | +`using System.Windows.Threading` + fallback `Dispatcher.CurrentDispatcher` | partial void в UI thread |
| 10 | `CategoryTreeEditorViewModel.cs` | +`using System.Windows.Threading` + fallback `Dispatcher.CurrentDispatcher` | partial void в UI thread |
| 11 | `Import.cs:161` | Удалён `await LoadTreeAsync()` (двойной вызов) | Уменьшил UI thread work в 2 раза |
| 12 | `Tree.cs:14-18` | Убраны `Measure` + `Debug` (start, end) | Убрал I/O на UI thread (4-5мс overhead) |
| 13 | `Tree.cs:140-144` | Добавлен ОДИН `Debug` в `finally` | Диагностика завершения метода |
| 14 | `Import.cs:230` | Удалён `Debug` перед `FireAndForget` | Убрал I/O на Revit UI thread (в ExternalEvent callback) |

**Не тронуты** (не нужно):
- `LoadPlace.cs:116, 238` и `NewCommands.cs:254` — `LoadTreeAsync().ConfigureAwait(true)` внутри `RaiseAsyncTask` (Revit UI thread → сохраняет контекст)
- `Database.cs:61, 120, 161` — `await RefreshAccessAndLoadTreeAsync()` в async-цепочке, не через FireAndForget

### Откат (провалившийся эксперимент)

| # | Файл | Было (эксперимент) | Стало (откат) |
|---|---|---|---|
| 15 | `Tree.cs:123-129` | `TreeNodes.Clear(); foreach TreeNodes.Add()` | `TreeNodes = rootNodes` |

**Причина отката:** в net48 WPF TreeView не обновлял ItemsSource правильно после
`Clear()` + `Add()`. Семейство не отображалось до ПКМ. `TreeNodes = rootNodes`
(PropertyChanged) надёжнее.

## Логирование

Добавлены `Debug` в ключевых точках для будущей диагностики:

```csharp
// FamilyManagerMainViewModel.cs:160 (ctor)
SmartConLogger.Debug($"FamilyManagerMainViewModel.ctor: _uiDispatcher captured thread={_uiDispatcher.Thread.ManagedThreadId}, Application.Current={(System.Windows.Application.Current is null ? "<null>" : "exists")}");

// Import.cs (FireAndForget блок)
SmartConLogger.Debug($"ExtractTypesForImportedFamilies: save complete on thread {Environment.CurrentManagedThreadId}, _uiDispatcher thread={_uiDispatcher.Thread.ManagedThreadId}, HasShutdownStarted={_uiDispatcher.HasShutdownStarted}");
SmartConLogger.Debug($"ExtractTypesForImportedFamilies: about to dispatcher.InvokeAsync(LoadTreeAsync) — caller thread={beforeThread}, dispatcher thread={dispatcherThread}, same={(beforeThread == dispatcherThread)}");

// Tree.cs:144 (finally)
SmartConLogger.Debug($"LoadTreeAsync: finally thread={Environment.CurrentManagedThreadId} treeNodes={TreeNodes.Count}");
```

Все новые `Warn` заканчиваются на `[Action: ...]` (правило L9):
- `ExtractTypesForImportedFamilies save failed: ... [Action: типы могут быть неполными; нажмите Refresh]`
- `Tree reload after extract failed: ... [Action: нажмите Refresh чтобы обновить дерево]`
- `OnPlacementCompleted dispatcher invoke failed: ... [Action: нажмите Refresh чтобы обновить дерево]`

## Тестирование

**Unit-тест невозможен** — `FamilyManagerMainViewModel` имеет 40+ зависимостей
через `FamilyManagerServices`, включая `IRevitContext` (Revit API типы,
sealed native — нельзя мокать). Полноценный integration test требует
запуска в Revit (ricaun.RevitTest / RevitXunit.TestAdapter, см.
`.agents/skills/smartcon-testing/references/integration-testing.md`).

**Проверка вручную** (юзер):
1. Импортировать `ADSK_Бытовой вентилятор накладной.rfa` (один файл)
2. **3/3 успешных импорта в Revit 2023 (net48)** — `LoadTreeAsync: finally` появляется, freeze отсутствует
3. **Успешный импорт в Revit 2025 (net8)** — dispatcher thread=1, caller=thread pool, `same=False`
4. В `smartcon.log`:
   - `LoadTreeAsync: finally thread=1 treeNodes=Y treeRef=Z` после save
   - `ExtractTypesForImportedFamilies: save complete on thread N, _uiDispatcher thread=1, HasShutdownStarted=False`
   - `ExtractTypesForImportedFamilies: about to dispatcher.InvokeAsync(LoadTreeAsync) — caller thread=N, dispatcher thread=1, same=False`

## Build verification

```bash
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R25  # 0 warnings, 0 errors
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R24  # 0 warnings, 0 errors
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R21  # 0 warnings, 0 errors
dotnet build src/SmartCon.App/SmartCon.App.csproj -c Debug.R19  # 0 warnings, 0 errors
dotnet test src/SmartCon.Tests/SmartCon.Tests.csproj -c Debug.R25  # 1312/1312 passed
```

## Плюсы и минусы текущего решения

### Плюсы (vs `async void FireAndForget` в Phase 4c до коммита 7c344e6)

1. **Нет deadlock** (I-13 anti-pattern `async void` устранён)
2. **Exception logging** в FireAndForget через `try/catch` (раньше — `AppDomain.UnhandledException` без возможности recover)
3. **Работает в net48** благодаря правильному UI marshalling через dispatcher
4. **Захват dispatcher в ctor** надёжнее чем `Application.Current?.Dispatcher` в background thread
5. **Меньше I/O на UI thread** (убраны `Measure` и большинство `Debug`)

### Минусы (vs `async void FireAndForget`)

1. **Любой FireAndForget, обновляющий UI, должен явно маршалить через dispatcher** — забыл = сломал UI в net48. В старом коде `await` сохранял SyncContext автоматически.
2. **UI не обновляется сразу при импорте** — `LoadTreeAsync` после save через dispatcher появляется через ~0.5-1 сек (раньше был мгновенный показ без типов + позже с типами).
3. **`Dispatcher.CurrentDispatcher` в ctor может вернуть неожиданный dispatcher** в edge-cases (если VM создаётся не на UI thread — но у нас это не так).
4. **Дополнительное поле `_uiDispatcher` в VM** — лишнее поле, но надёжнее inline fallback.
5. **`TreeNodes = rootNodes` setter** (сохранён после отката) — PropertyChanged, WPF полный rebuild visual tree, в net48 это **медленнее** чем incremental update (но `Clear() + Add()` сломалось).

## Известные ограничения

- **Open item M-019-003** (`docs/adr/025-refactoring-migration-backlog.md`):
  `Application.Current?.Dispatcher` → `IDispatcher` (5 мест, 0.5 дня). Все 4
  места из этого ADR подпадают под рефакторинг. Не блокер, но желательно
  сделать в Phase 5.
- **FireAndForget в других модулях** (PipeConnect, ProjectManagement) —
  не проверялись в этом ADR. Если будут аналогичные баги, нужен отдельный
  аудит.
- **`TreeNodes = rootNodes`** может быть медленнее, чем incremental update для
  больших tree (1000+ items). Если в будущем понадобится — исследовать
  ICollectionView + batch updates или VirtualizingStackPanel.

## Связанные документы и источники

- ADR-018 §4 — оригинальный дизайн `async void FireAndForget` (отменён Phase 4c)
- `.agents/skills/revit-api-best-practice/references/transaction-callback-freeze.md` — симптомы freeze (right-click unfreeze)
- `.agents/skills/revit-api-best-practice/references/wpf-mfc-render-freeze.md` — WPF render thread freeze
- `docs/invariants.md` I-13 (anti-pattern `async void`)
- `docs/smartcon-logging` — правила L8/L9/C15
- `.agents/skills/smartcon-testing` — ограничения юнит-тестов для Revit ViewModel
- **Exa:** [lepoco/wpfui#662 — Application.Current will be null (net48 Revit)](https://github.com/lepoco/wpfui/issues/662) — подтверждение что `Application.Current` == null в Revit addin
- **Exa:** [StackOverflow: Application.Current and App.Current is null](https://stackoverflow.com/questions/39644256/application-current-and-app-current-is-null) — workaround через `MainWindow.AppWindow.Dispatcher`
- **Exa:** [StackOverflow: Dispatcher.CurrentDispatcher vs Application.Current.Dispatcher](https://stackoverflow.com/questions/10448987/dispatcher-currentdispatcher-vs-application-current-dispatcher) — различия между двумя способами получить dispatcher
- **Exa:** [Autodesk Community: WPF DockablePane UI freezes when loading a family via ExternalEvent](https://forums.autodesk.com/t5/revit-api-forum/wpf-dockablepane-ui-freezes-when-loading-a-family-via/td-p/14021600) — UI freeze в DockablePane после ExternalEvent
- **Exa:** [Microsoft Learn: How to: Improve the Performance of a TreeView](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/how-to-improve-the-performance-of-a-treeview) — `VirtualizingStackPanel.IsVirtualizing="True"` (если в будущем понадобится)
- **Exa:** [StackOverflow: UI slow at updating ObservableCollection<T> in TreeView control](https://stackoverflow.com/questions/6252839/ui-slow-at-updating-observablecollectiont-in-treeview-control) — Clear+Add может быть медленнее, чем ожидается
