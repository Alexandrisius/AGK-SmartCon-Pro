---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager — UI и диалоги

> Часть документации модуля FamilyManager. Индекс и навигация: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

## IFamilyManagerDialogService

UI-диалоги модуля FamilyManager.

**Файл:** `IFamilyManagerDialogService.cs`
**Реализация:** `SmartCon.FamilyManager/Services/FamilyManagerDialogService.cs`

```csharp
public enum DialogResult
{
    None,
    OK,
    Cancel,
    Yes,
    No
}

public interface IFamilyManagerDialogService
{
    string? ShowOpenFileDialog(string title, string? initialDirectory = null);
    string? ShowImportDialog(string title, string? initialDirectory = null);
    string[]? ShowImportFilesDialog(string title, string? initialDirectory = null);
    string? ShowFolderBrowserDialog(string title, string? initialDirectory = null);
    void ShowWarning(string title, string message);
    void ShowError(string title, string message);
    void ShowInfo(string title, string message);
    string? ShowInputDialog(string title, string prompt, string defaultText = "", string placeholderText = "");
    bool ShowConfirmation(string title, string message);
    DialogResult ShowYesNoCancel(string title, string message);
    bool? ShowCategoryTreeEditor(object viewModel);
    bool? ShowProjectBaseRulesEditor(object viewModel);
    bool? ShowParseRuleEditor(object viewModel);
    bool? ShowFieldLibrary(object viewModel);
    bool? ShowAllowedValues(object viewModel);
    string? ShowCategoryPicker(object viewModel);
    string? ShowOpenJsonDialog(string title, string? initialDirectory = null);
    string? ShowOpenTextFileDialog(string title, string? initialDirectory = null);
    bool? ShowSharedParameterPicker(object viewModel);
    string? ShowSaveJsonDialog(string title, string? defaultFileName = null);
    bool? ShowProperties(object viewModel);
    string? ShowAssetOpenFileDialog(string title, FamilyAssetType assetType, string? initialDirectory = null);
    bool? ShowPresetEditor(object viewModel);
    bool? ShowAttributeLibrary(object viewModel);
    bool? ShowProfile(object viewModel);
    bool? ShowBatchImportDialog(object viewModel);
    bool? ShowValidationReport(object viewModel);
    bool? ShowStatusDetails(object viewModel);
    bool? ShowValidationRulesEditor(object viewModel);
    void ShowModelessBatchImportDialog(object viewModel);
    void ShowDatabaseUpdateProgressDialog(object viewModel);
    void ShowMissingRecordsCleanupDialog(object viewModel);
    bool? ShowAvatarCropper(object viewModel);
    SharedFamiliesLoadChoice ShowSharedFamiliesLoadModeDialog(SharedFamilyDecisionRequest request);
}
```

`ShowSharedFamiliesLoadModeDialog` показывает диалог с 3 radio-button (issue #67)
и возвращает выбор пользователя. **Должен вызываться на Revit main thread.**
При отмене пользователем возвращает `SharedFamiliesLoadChoice.UseProject` как
безопасный дефолт.

`ShowMissingRecordsCleanupDialog` (#133) — modeless-диалог «Очистить недоступные записи»
(ADR-048 паттерн): возвращает управление сразу, вызывающий запускает скан через VM
(`RunScanAsync`) и ждёт `DialogCompletion`.

`ShowStatusDetails` (#210) — диалог деталей статуса кликабельных бэйджей:
read-only список `StatusNotice` строки batch-диалога / узла дерева
(title + буллет-список имён + guidance) и опциональные follow-up действия.
Два раздельных вида: problem (warning/error + действия) и info (только связи).

`ShowValidationReport` — единый итоговый отчёт проверки семейства:
системные проблемы Revit (ошибка открытия файла, сбой регенерации,
предупреждения документа) показываются карточками с полным переносимым
и выделяемым текстом (секция «Системные проблемы (N)»), нарушения правил —
таблицей «Нарушения правил (N)» ниже; каждая секция видна только при
наличии контента, место делится 1:2. Кнопка «Копировать» выгружает весь
отчёт в буфер обмена.

---

## IFamilyManagerAwaitableEvent

Awaitable-обёртка над `Revit API ExternalEvent`. Позволяет коду из WPF/UI thread вызвать операцию в Revit API контексте и **дождаться её завершения через `await`**, а не городить `TaskCompletionSource` boilerplate в каждом VM.

**Файл:** `SmartCon.Core/Services/Interfaces/IFamilyManagerAwaitableEvent.cs`
**Реализация (pure C#, testable):** `SmartCon.FamilyManager/Events/FamilyManagerAwaitableEvent.cs`
**Адаптер к `IExternalEventHandler`:** `SmartCon.App/Events/RevitFamilyManagerAwaitableEvent.cs`

```csharp
public interface IFamilyManagerAwaitableEvent
{
    /// Поставить Action<object> в очередь, дождаться её выполнения в Revit-потоке.
    Task RaiseAsync(Action<object> actionWithApp, CancellationToken ct = default);

    /// То же, но функция возвращает значение (generic-вариант).
    Task<T> RaiseAsync<T>(Func<object, T> funcWithApp, CancellationToken ct = default);

    /// Вызывается IExternalEventHandler-адаптером в UI-потоке Revit: достаёт
    /// первый элемент из очереди, исполняет его и завершает ожидающий Task.
    void ProcessQueue(object revitUIApplication);
}
```

**Дизайн-контракт:**
- FIFO: элементы обрабатываются в порядке постановки в очередь.
- `RunContinuationsAsynchronously` — продолжение после `await` не блокирует UI-поток Revit.
- Исключения из callback пробрасываются в `Task` (наблюдаются через `await`).
- При `ct` после `RaiseAsync` — `Task` завершается как `Canceled`.
- **Не thread-safe для re-entrant вызовов** — один `Raise` должен полностью завершиться до следующего.
- `object` (а не `UIApplication`) сохраняет `SmartCon.Core` независимым от `RevitAPIUI` (I-09).
- Дополнительный overload `RaiseAsyncTask(Func<object, Task>, CancellationToken)` (Phase 4c) — для async delegate-ов. Использует `BridgeAsyncResult` (TaskCompletionSource + try/catch/OperationCanceledException). Continuation через `RunContinuationsAsynchronously`. **Отдельный name, не overload**, чтобы избежать implicit conversion C# statement lambda → `Func<object, Task>` ambiguity.
