# Dependency Injection

> **Status:** Active | Загружать: при добавлении нового сервиса/ViewModel или вопросах «куда регистрировать зависимость?».

## Стек

- **DI-контейнер:** `Microsoft.Extensions.DependencyInjection` 8.0.1
- **Точка регистрации:** `src/SmartCon.App/DI/ServiceRegistrar.cs`
- **Точка разрешения:** `src/SmartCon.App/DI/ServiceLocator.cs` (MEDI service provider)
- **Паттерн:** Constructor Injection

## Архитектура

```
SmartCon.Core                  (интерфейсы + модели)
       ↑
SmartCon.App/DI/ServiceRegistrar (регистрирует интерфейс → реализация)
       ↑
SmartCon.Revit                 (реализации интерфейсов Core через Revit API)
SmartCon.PipeConnect           (зависит от Core + UI, получает реализации через DI)
SmartCon.ProjectManagement       (зависит от Core + UI, получает реализации через DI)
SmartCon.FamilyManager         (зависит от Core + UI, получает реализации через DI)
```

## Правило добавления нового сервиса

1. **Интерфейс** — `src/SmartCon.Core/Services/Interfaces/INewService.cs`
2. **Реализация** — `src/SmartCon.Revit/NewService.cs` (если нужен Revit API)
3. **Регистрация** — `ServiceRegistrar.cs`:
   ```csharp
   services.AddSingleton<INewService, NewService>();
   ```
4. **Использование** — Constructor Injection в ViewModel/Service модуля.

## Пример

```csharp
// SmartCon.Core/Services/Interfaces/ITransactionService.cs
public interface ITransactionService
{
    void RunInTransaction(string name, Action<Document> action);
}

// SmartCon.Revit/Transactions/RevitTransactionService.cs
public sealed class RevitTransactionService : ITransactionService { ... }

// SmartCon.App/DI/ServiceRegistrar.cs
services.AddSingleton<ITransactionService, RevitTransactionService>();

// SmartCon.PipeConnect/ViewModels/PipeConnectEditorViewModel.cs
public partial class PipeConnectEditorViewModel
{
    private readonly ITransactionService _transactionService;

    public PipeConnectEditorViewModel(
        ITransactionService transactionService,
        ...)
    {
        _transactionService = transactionService;
    }
}
```

## ViewModel Factory

Модули не создают ViewModel через `new`. Для каждого модуля есть factory:

| Модуль | Factory | Файл |
|---|---|---|
| PipeConnect | `IPipeConnectViewModelFactory` | `SmartCon.PipeConnect/Services/PipeConnectViewModelFactory.cs` |
| PipeConnect | `IAboutViewModelFactory` | `SmartCon.PipeConnect/Services/AboutViewModelFactory.cs` |
| PipeConnect | `ISettingsViewModelFactory` | `SmartCon.PipeConnect/Services/SettingsViewModelFactory.cs` |
| ProjectManagement | `IShareSettingsViewModelFactory` | `SmartCon.ProjectManagement/Services/ShareSettingsViewModelFactory.cs` |
| FamilyManager | `IFamilyManagerViewModelFactory` | `SmartCon.FamilyManager/Services/FamilyManagerViewModelFactory.cs` |

## ServiceLocator vs Constructor Injection

- **Предпочтительно:** Constructor Injection.
- **ServiceLocator** (`ServiceHost.GetService<T>()`) — допустим только в точках, где DI-контейнер недоступен (например, static helpers, legacy code). ADR-028 фиксирует план постепенного сокращения.

## DI Readiness

См. [ADR-028](../adr/028-di-readiness.md) — аудит 15 использований `ServiceHost.GetService<>()` и 11 static classes. Все обоснованы, план Tier 1/2/3.

## Связанные документы

- [ADR-001](../adr/001-clean-architecture.md) — Clean Architecture
- [ADR-018](../adr/018-familymanager-refactoring.md) — VM Factory pattern, DialogResult enum
- [ADR-028](../adr/028-di-readiness.md) — DI Readiness audit
- [architecture/dependency-rule.md](dependency-rule.md) — правило зависимостей между слоями
