---
module: drag-drop-contracts
---
# Drag & Drop контракты

> Загружать: при работе с WPF drag-and-drop.
> Источник истины: `src/SmartCon.Core/Services/Interfaces/*.cs`.

Минимальные контракты для drag-and-drop операций. Core определяет интерфейсы, UI-слой предоставляет реализации.

## IDragInfo

Payload drag-операции. Используется behavior'ом для передачи данных между drag source и drop target.

**Файл:** `SmartCon.Core/Services/Interfaces/IDragInfo.cs`
**Реализация:** `SmartCon.UI/Behaviors/TreeViewDragInfo.cs`

```csharp
public interface IDragInfo
{
    object? Payload { get; }
    string? DisplayText { get; }
}
```

---

## IDropInfo

Контекст drop target. Содержит dragged payload и target item.

**Файл:** `SmartCon.Core/Services/Interfaces/IDropInfo.cs`
**Реализация:** `SmartCon.UI/Behaviors/TreeViewDropInfo.cs`

```csharp
public interface IDropInfo
{
    object? Payload { get; }
    object? Target { get; }
}
```
