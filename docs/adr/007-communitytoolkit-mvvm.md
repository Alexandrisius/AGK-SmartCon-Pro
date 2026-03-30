# ADR-007: CommunityToolkit.Mvvm вместо ручной MVVM-инфраструктуры

**Статус:** accepted
**Дата:** 2026-03-25

## Контекст

Проекту нужна MVVM-инфраструктура для WPF: `INotifyPropertyChanged`, `ICommand`, возможно async-команды. Традиционный подход — писать `ViewModelBase`, `RelayCommand`, `AsyncRelayCommand` вручную.

## Решение

Использовать **CommunityToolkit.Mvvm** (8.4.0) — официальный MVVM Toolkit от Microsoft.

### Что заменяет

| Было (ручное) | Стало (CommunityToolkit) |
|---|---|
| `ViewModelBase : INotifyPropertyChanged` | `ObservableObject` (базовый класс) |
| Ручной `RelayCommand : ICommand` | `RelayCommand` / `RelayCommand<T>` |
| Ручной `AsyncRelayCommand` | `AsyncRelayCommand` / `AsyncRelayCommand<T>` |
| Ручные свойства с `OnPropertyChanged()` | `[ObservableProperty]` source generator |
| Ручное создание команд | `[RelayCommand]` source generator |

### Пример ViewModel

```csharp
public partial class PipeConnectEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private double _rotationAngleDeg;

    [ObservableProperty]
    private bool _moveEntireChain;

    [RelayCommand]
    private void RotateLeft()
    {
        RotationAngleDeg -= RotationStep;
        _externalEvent.Raise();
    }

    [RelayCommand]
    private async Task CommitAsync()
    {
        // ...
    }
}
```

Source generators автоматически создают:
- Свойства `RotationAngleDeg`, `MoveEntireChain` с `OnPropertyChanged()`
- Команды `RotateLeftCommand`, `CommitCommand` типа `IRelayCommand`

### Где подключается

NuGet-пакет добавляется в `SmartCon.UI`. Проекты `SmartCon.PipeConnect` и другие получают его транзитивно.

## Последствия

**Плюсы:**
- Меньше boilerplate-кода (~70% меньше строк в ViewModel)
- Source generators — compile-time, zero reflection, zero runtime overhead
- Поддерживается Microsoft, используется в Microsoft Store
- `partial` классы работают идеально с .NET 8 / C# 12
- Встроенная поддержка `INotifyDataErrorInfo` для валидации

**Минусы:**
- Дополнительная NuGet-зависимость (но всего ~100 KB, no transitive deps)
- Source generators требуют `partial` класс — незначительное ограничение

## Альтернативы

1. **Ручная реализация:** Полный контроль, но ~200 строк boilerplate + поддержка.
2. **Prism:** Избыточен, тянет навигацию и модули которые не нужны.
3. **ReactiveUI:** Мощный, но крутая кривая обучения, избыточен для Revit-плагина.
