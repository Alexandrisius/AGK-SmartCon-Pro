---
module: family-manager-interfaces
---
# Интерфейсы FamilyManager — Presentation-контракты (build-speedup)

> Часть документации модуля FamilyManager. Индекс: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/ICatalogTreeNode.cs`,
> `src/SmartCon.Core/Services/FamilyManager/ICropAvatarController.cs`.
> WPF-free контракты (I-09): SmartCon.UI селекторы/поведения работают через них
> вместо конкретных ViewModel, чтобы XAML панели не содержал local-type ссылок (ADR-078).

## ICatalogTreeNode

Presentation-контракт узла дерева каталога (категория / семейство / тип). Реализуется
`CatalogTreeNodeViewModel`. Позволяет `CatalogTreeNodeTemplateSelector` и
`FamilyTreeItemStyleSelector` (SmartCon.UI) выбирать шаблон/стиль по дискриминатору
`IsCategory`/`IsType`, не ссылаясь на конкретные VM.

**Файл:** `Models/FamilyManager/ICatalogTreeNode.cs`

```csharp
public interface ICatalogTreeNode
{
    string DisplayName { get; }
    bool IsCategory { get; }
    bool IsType { get; }
    ICatalogTreeNode? Parent { get; }
    IEnumerable<ICatalogTreeNode> Children { get; }
}
```

## ICategoryNode

Узел-категория (несёт счётчик семейств `FamilyCount`). Наследует `ICatalogTreeNode`.
Реализуется `CategoryNodeViewModel`; используется `StickyCategoryHeaderBehavior` (SmartCon.UI).

**Файл:** `Models/FamilyManager/ICatalogTreeNode.cs`

```csharp
public interface ICategoryNode : ICatalogTreeNode
{
    int FamilyCount { get; }
}
```

## ICropAvatarController

Presentation-контракт контроллера вьюпорта диалога кадрирования аватара. Реализуется
`CropAvatarViewModel`. SmartCon.UI crop-поведения (`CropPanZoomBehavior`, `CropFrameDragBehavior`,
`CropFrameResizeBehavior`) кастуют `DataContext` к этому интерфейсу вместо конкретного VM.

**Файл:** `Services/FamilyManager/ICropAvatarController.cs`

```csharp
public interface ICropAvatarController
{
    void PanBy(double dx, double dy);
    void ZoomAt(double factor, double viewX, double viewY);
    void MoveFrame(double dx, double dy);
    void ResizeFrame(CropCorner corner, double dx, double dy);
}
```