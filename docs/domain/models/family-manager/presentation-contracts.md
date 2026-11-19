---
module: family-manager
---
# Модели FamilyManager — Presentation-контракты (build-speedup)

> Часть документации модуля FamilyManager. Индекс: [README.md](README.md).
> Источник истины: `src/SmartCon.Core/Models/FamilyManager/*.cs`, `src/SmartCon.Core/Services/FamilyManager/*.cs`.
> Вынесены из SmartCon.FamilyManager в Core для устранения WPF local-type XAML
> (ADR-078): SmartCon.UI селекторы/поведения и XAML ссылаются на эти типы как на
> внешние (`assembly=SmartCon.Core`), без MarkupCompilePass2 / `_wpftmp` double-compile.

## DatabaseListItem

VM-обёртка над `DatabaseConnection` с кэшированным статусом совпадения project-base,
чтобы ComboBox баз показывал иконку (general / match / mismatch) без пересчёта на
каждый рендер. Перенесена из `SmartCon.FamilyManager/Selectors/DatabaseListItemIconSelector.cs`
в Core (зависит только от Core-типов: `DatabaseConnection`, `BaseType`, `ProjectBaseMatchKind`).
Пересобирается `FamilyManagerMainViewModel.Database.RefreshConnections`.

**Файл:** `Models/FamilyManager/DatabaseListItem.cs`

```csharp
public sealed class DatabaseListItem
{
    public DatabaseConnection Connection { get; }
    public BaseType Kind => Connection.Kind;
    public ProjectBaseMatchKind MatchStatus { get; }
    public string Name => Connection.Name;
    public string? MismatchReason { get; }
    public static implicit operator DatabaseConnection(DatabaseListItem dli);
}
```

## StatusNoticeSeverity

Severity статус-уведомления (порядок объявления важен: худшая severity списка = `Max()`).
Перечисление перенесено из `SmartCon.FamilyManager/ViewModels/StatusNotice.cs` в Core,
чтобы XAML (`FamilyBatchImportView`) ссылался на него через `assembly=SmartCon.Core`
без local-type markup compile. Record `StatusNotice` остаётся в FamilyManager.

**Файл:** `Models/FamilyManager/StatusNoticeSeverity.cs`

```csharp
public enum StatusNoticeSeverity { Info, Warning, Error }
```

## CropViewportMetrics

Канонические константы вьюпорта/рамки диалога кадрирования аватара (#131, ADR-047):
`ViewportWidth=600`, `ViewportHeight=450`, `DefaultFrameWidth=400`, `DefaultFrameHeight=300`.
Общие для `CropAvatarViewModel` (алиасы) и SmartCon.UI crop-поведений, чтобы поведения
не ссылались на конкретный ViewModel.

**Файл:** `Services/FamilyManager/CropViewportMetrics.cs`

```csharp
public static class CropViewportMetrics
{
    public const double ViewportWidth = 600;
    public const double ViewportHeight = 450;
    public const double DefaultFrameWidth = 400;
    public const double DefaultFrameHeight = 300;
}
```