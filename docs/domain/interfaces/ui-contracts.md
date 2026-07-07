---
module: ui-contracts
---
# UI контракты

> Загружать: при работе с WPF-окнами и ViewModel.
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

Минимальные контракты для WPF UI: закрытие окон и запросы на закрытие из ViewModel.

## IObservableRequestClose

Интерфейс для ViewModel, который уведомляет View о необходимости закрытия окна.

**Файл:** `SmartCon.Core/Services/Interfaces/IObservableRequestClose.cs`
**Реализация:** Реализуется множеством ViewModel (например, `PipeConnectEditorViewModel`, `FamilyMetadataEditViewModel`).

```csharp
public interface IObservableRequestClose
{
    event Action<bool?>? RequestClose;
}
```

---

## ICloseAwareViewModel

Интерфейс для ViewModel, который может перехватывать или подтверждать закрытие окна пользователем.

**Файл:** `SmartCon.Core/Services/Interfaces/ICloseAwareViewModel.cs`
**Реализация:** Реализуется множеством ViewModel (например, `PipeConnectEditorViewModel`, `CategoryTreeEditorViewModel`).

```csharp
public sealed class CloseConfirmationArgs
{
    public bool Cancel { get; set; }
    public bool? DialogResult { get; set; }
    public Action? DeferredAction { get; set; }
}

public interface ICloseAwareViewModel
{
    void ConfirmClose(CloseConfirmationArgs args);
}
```

---

## ISaveableViewModel

Enterprise pattern: интерфейс для диалоговых ViewModel, которые модифицируют данные и должны показывать диалог подтверждения "Сохранить / Сбросить / Отмена" при закрытии окна.

**Файл:** `SmartCon.Core/Services/Interfaces/ISaveableViewModel.cs`
**Реализация:** Реализуется ViewModel с редактируемыми данными (например, `CategoryTreeEditorViewModel`, `AttributeLibraryViewModel`, `FamilyPropertiesViewModel`).

```csharp
public interface ISaveableViewModel
{
    bool HasUnsavedChanges { get; }
    Task SaveAsync();
}
```

---

## IUiFreezeRecoveryService

Platform-specific workaround that forces the WPF/render thread to re-sync
with the UI thread. On Revit net48 this is typically implemented by
triggering an InfoCenter balloon, whose Win32 window creation produces the
focus flip needed to recover from the well-known WPF render-thread zombie
state (REVIT-236376 / REVIT-237190).

**Файл:** `SmartCon.Core/Services/Interfaces/IUiFreezeRecoveryService.cs`
**Реализация:** `RevitUiFreezeRecoveryService` (`SmartCon.Revit/FamilyManager/RevitUiFreezeRecoveryService.cs`) — делегирует в `RevitBalloonNudge.Nudge`.

```csharp
public interface IUiFreezeRecoveryService
{
    void Nudge(string message);
}
```

Используется для единого финального балуна «Импорт N/N семейств завершён»
в конце `ShowBatchImportDialogAsync`.
