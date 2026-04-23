# Правило зависимостей (Dependency Rule)

> Загружать: **ВСЕГДА**. Это фундаментальное архитектурное правило.

## Диаграмма

```
                    SmartCon.Core
              (RevitAPI.dll compile-time only)
                 /    |    |    \     \        \
                /     |    |     \     \        \
          Revit    UI   App  PipeConnect  ProjectManagement  Tests
```

**Стрелка «зависит от» направлена вверх.** Все проекты зависят от Core. Core не знает ни о ком.

## Матрица зависимостей

| Проект | Core | Revit | UI | App | PipeConnect | ProjectManagement |
|---|---|---|---|---|---|---|
| **Core** | — | — | — | — | — | — |
| **Revit** | да | — | — | — | — | — |
| **UI** | да | — | — | — | — | — |
| **App** | да | да | да | — | да | да |
| **PipeConnect** | да | — | да | — | — | — |
| **ProjectManagement** | да | — | да | — | — | — |
| **Tests** | да | — | — | — | да | да |

## Жёсткие запреты

1. **Core -> Revit:** `SmartCon.Core` НЕ ссылается на `SmartCon.Revit`. Core ссылается на `RevitAPI.dll` как compile-time reference (CopyLocal=false) только для типов-carriers (`ElementId`, `XYZ`, и т.д.). Core **не вызывает** методы Revit API. См. I-09.
2. **Core -> UI:** `SmartCon.Core` НЕ ссылается на `SmartCon.UI`. Core не содержит `using System.Windows`.
3. **PipeConnect -> Revit:** `SmartCon.PipeConnect` НЕ ссылается на `SmartCon.Revit` напрямую. Вся работа с Revit API — через интерфейсы Core, реализованные в Revit. PipeConnect ссылается на RevitAPI.dll / RevitAPIUI.dll (CopyLocal=false) для `IExternalCommand` и `[Transaction]`.
4. **UI -> Revit:** `SmartCon.UI` НЕ ссылается на `SmartCon.Revit`.
5. **ProjectManagement -> Revit:** `SmartCon.ProjectManagement` НЕ ссылается на `SmartCon.Revit` напрямую. Вся работа с Revit API — через интерфейсы Core. ProjectManagement ссылается на RevitAPI.dll / RevitAPIUI.dll (CopyLocal=false) для `IExternalCommand` и `[Transaction]`.

## Как это работает

- **Интерфейсы** объявлены в `SmartCon.Core/Services/Interfaces/`
- **Реализации** живут в `SmartCon.Revit/`
- **DI-контейнер** в `SmartCon.App/DI/ServiceRegistrar.cs` связывает интерфейсы с реализациями
- **PipeConnect** получает зависимости через конструктор (Constructor Injection)

```csharp
// SmartCon.PipeConnect получает интерфейсы, не зная о Revit
public class PipeConnectEditorViewModel
{
    public PipeConnectEditorViewModel(
        ITransactionService transactionService,    // реализация в Revit
        IElementSelectionService selectionService,  // реализация в Revit
        IFittingMapper fittingMapper,              // реализация в Core
        IRevitContext revitContext)                 // реализация в Revit
    { ... }
}
```

## Проверка при коммите

Перед каждым коммитом убедиться:
- `SmartCon.Core/*.cs` не содержит **вызовов** Revit API (I-09). `using Autodesk.Revit.DB` допускается для типов-carriers.
- `SmartCon.Core/*.cs` не содержит `using System.Windows`
- `.csproj` файлы не содержат запрещённых ProjectReference
