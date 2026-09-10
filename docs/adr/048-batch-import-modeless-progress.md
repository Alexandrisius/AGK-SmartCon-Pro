# ADR-048: Batch Import — modeless диалог с живым прогрессом, паузой и поэлементным pipeline

**Date:** 2026-07-16  
**Status:** accepted  
**Related:** Issue #127, ADR-015 (Published Storage), ADR-040 (OverwriteCurrent), ADR-041 (MakeActive)

## Context

Batch-диалог импорта семейств (`FamilyBatchImportView`) работал так: пользователь нажимает **Import** → диалог **закрывается** (`ShowDialog` возвращает `true`) → импорт выполняется «в фоне» (staging через `IFamilyManagerAwaitableEvent`, затем `ImportBatchAsync` + extraction). Пользователь не видел прогресс, не мог остановиться и не понимал, когда Revit снова станет доступен. Для 100+ файлов это критичный UX-провал.

Ограничения:

- Все Revit API вызовы — только через `IFamilyManagerAwaitableEvent` (I-01), выполняющемся на Revit main thread. Пока WPF-диалог модален/UI-thread занят, внешнее событие не дренируется честно поэлементно.
- Существующая оркестрация (UC-1/UC-3/UC-4) зрелая (v2.0.0): staging после подтверждения диалога, snapshot-based extraction, per-item error handling «warn + continue». Полная переработка = высокий риск регрессий.
- UC-3/UC-4: строка диалога — это системная категория или loadable-семейство, файлы создаются staging'ом уже ПОСЛЕ диалога (placeholder-пути `system://`, `loadable://`).

## Decision

### 1. Modeless диалог (`Show()` вместо `ShowDialog()`)

`IDialogPresenter.ShowModeless(object)` создаёт view через существующий VM→View mapping, ставит `WindowInteropHelper.Owner = UIApplication.MainWindowHandle`, на net48 подключает `BatchDialogRenderRecovery` (dispose по `window.Closed`), вызывает `Show()`. Это единственный корректный способ дать `ExternalEvent` нормально дренировать очередь между поэлементными `RaiseAsync` (Autodesk External Events framework, Jeremy Tammik). Внутренний референс — `ShareProgressView`.

Жизненный цикл: вызывающий код создаёт VM с executor'ом, показывает modeless, `await vm.DialogCompletion` (TCS, завершается при любом закрытии: OK, Cancel, X, stop→close).

### 2. Исполнение внутри диалога, executor — чистая оркестрация

`ImportCommand` больше не закрывает диалог, а запускает `IFamilyBatchImportExecutor.ExecuteAsync` на thread-pool (`Task.Run`). Executor — **pure C# оркестрация** без Revit-типов в сигнатурах: поэлементный цикл `stage → import → extract`, отчёты `IProgress<FamilyBatchImportProgress>`, пауза `PauseGate`, отмена `CancellationToken` между элементами. Revit-bound staging вынесен в отдельные интерфейсы без Revit-типов (`IFileFamilyStagingService` / `IProjectFamilyStagingService`), что делает executor полностью юнит-тестируемым (skill smartcon-testing: SUT, трогающий `Document`, юнит-тестам не поддаётся — значит `Document` выносим за шов).

Отклонено (из первоначального плана issue): pipeline-абстракция `IFamilyBatchImportStep` + движок (YAGNI для двух executor'ов) и shared SQLite connection (`IFamilyImportConnectionContext`) — `LocalFamilyImportService` и так работает per-call connections, регрессии нет, оптимизация преждевременна.

### 3. Поэлементный pipeline вместо «stage всех → import всех»

Раньше: сначала staging ВСЕХ элементов (один `RaiseAsync` на фазу, UI-thread заблокирован), потом импорт ВСЕХ, потом extract ВСЕХ — честный прогресс «X из Y» невозможен. Теперь каждый элемент проходит весь путь сразу; прогресс линейный по строкам, точки паузы/отмены между элементами. Побочный плюс: окно orphan-файлов в managed storage сужается (файл создаётся непосредственно перед своей записью в каталоге, а не все файлы до всех записей).

Оркестраторы (`ISystemFamilyImportOrchestrator`, `ILoadableFamilyImportOrchestrator`) и `IFamilyImportService.ImportBatchAsync` **не изменены** — вызываются per-item списками из одного элемента.

### 4. Пауза «Остановить» вместо односторонней отмены

Бизнес-решение (пользователь): «Остановить» = пауза с выбором. `PauseGate` (SemaphoreSlim-паттерн, `SmartCon.Core.Threading`): executor между элементами ждёт `WaitWhilePausedAsync()` и репортит фазу `Paused`. Состояния диалога:

```
Setup → Importing ⇄ Paused → Summary → Close
```

- **Importing**: грид заблокирован, кнопка «Остановить» (прогресс + «Импорт X из Y — имя»).
- **Paused**: «Продолжить» (resume) / «Закрыть» (`cts.Cancel()` + `Resume()` → executor завершается с `WasStopped=true` → диалог закрывается без summary; частичный импорт остаётся в каталоге — отката нет by design).
- **Summary**: «Импортировано: X, пропущено: Y, ошибок: Z» + OK.
- Ошибка элемента → красный крестик в строке (tooltip — текст ошибки), цикл продолжается (как и раньше).
- X во время импорта = «Остановить» (`ICloseAwareViewModel.ConfirmClose` отменяет закрытие и запрашивает паузу).

### 5. Progress по индексу строки

`FamilyBatchImportProgress.CurrentIndex` — индекс строки в `Items` диалога (грид заблокирован во время импорта, порядок стабилен). Это надёжнее сопоставления по имени (переименования невозможны в заблокированном гриде). `ItemState == null` — элемент начат (строка → Running); иначе — завершён с этим состоянием.

## Consequences

**Плюсы:**
- Диалог не закрывается: живой прогресс по строкам, пауза/resume/закрыть, summary. Все UC (1/3/4) на одном механизме.
- Executor чистый → 12 unit-тестов на оркестрацию (прогресс, пауза, отмена, per-item ошибки) без Revit API.
- VM-partials похудели на ~700 строк (staging/extraction переехали в `Services/Import/`).
- Ошибки изоляции per-item сохранены; cleanup (`CloseAllPreparedDocumentsAsync`) — `finally` в executor'ах на всех путях (отмена/ошибка/X).

**Минусы / риски:**
- Modeless-диалог технически позволяет кликать в Revit во время импорта. Принято: staging читает активный проект snapshot'ами и held-open документами; пользователь по бизнес-флоу ждёт окончания.
- Вызов оркестраторов per-item чуть дороже, чем одним списком (повторная инициализация на элемент) — пренебрежимо на фоне Revit-операций.
- `FamilyBatchImportViewModel` без executor'а (unit-тесты) сохраняет legacy-поведение «Import → Close(true)» — backward compat.
- **Известное ограничение (подтверждено пользователем, решение принято осознанно):** во время staging одного элемента (SaveAs / EditFamily / mini-rvt) UI-поток занят внутри Revit-вызова, поэтому окно показывает «задумчивый» курсор и не обрабатывает ввод до ближайшей контрольной точки между элементами. Клики по «Остановить» **не теряются** — Windows ставит их в очередь сообщений потока, и пауза включается сразу после окончания текущего элемента (что и есть спроектированная cooperative-семантика). Единственное полное устранение — вынос диалога на отдельный STA-поток (консенсус Autodesk forums / StackOverflow); отклонено из-за рисков: thread-affine singleton-ресурсы темы SmartCon, net48 render recovery (#95), фокусы/Owner, marshalling — отдельная большая задача при реальной необходимости. `Application.DoEvents` отклонён как анти-паттерн (reentrancy в Revit).
- `ImportCommand` — `AsyncRelayCommand` с `AllowConcurrentExecutions = true`: иначе «Продолжить» недоступно в Paused, т.к. первый запуск команды ещё в полёте (фикс бага приёмки #1). Реальная конкурентность исключена state machine (Import доступен только в Setup/Paused/Summary).

**Verification:** build R19/R21/R24/R25 — 0 warnings / 0 errors; 1940/1940 tests passed (+19 новых: PauseGate 7, executor 5, VM execution 9, минус дубликаты). Ручной тест в Revit — по acceptance criteria issue #127.
