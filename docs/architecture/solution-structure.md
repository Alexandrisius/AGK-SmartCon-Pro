# Структура Solution

> Загружать: при создании/перемещении файлов или при вопросе «куда положить код?»

## Проекты

```
SmartCon.sln
├── SmartCon.Core              <- Чистый C#. Модели, интерфейсы, алгоритмы.
│                                 Запрет: using Autodesk.Revit.DB, using System.Windows.
├── SmartCon.Revit             <- Реализации интерфейсов Core через Revit API.
├── SmartCon.UI                <- Общая WPF-библиотека: стили, контролы, RelayCommand, базовые VM.
├── SmartCon.App               <- Точка входа: IExternalApplication, Ribbon, DI-регистрация.
├── SmartCon.PipeConnect       <- Модуль PipeConnect: Commands, ViewModels, Views.
├── SmartCon.Tests             <- Unit + ViewModel тесты (xUnit + Moq).
└── SmartCon.Installer         <- Инсталлятор: копирует DLL + .addin в папки Revit.
```

---

## SmartCon.Core

Чистый C# без зависимостей от Revit и WPF. Содержит доменную логику, модели, интерфейсы и алгоритмы.

```
SmartCon.Core/
├── Models/
│   ├── ConnectorProxy.cs              <- иммутабельный снапшот коннектора
│   ├── ConnectionTypeCode.cs          <- строго типизированный код типа соединения
│   ├── PipeConnectionSession.cs       <- мутабельный контекст сессии
│   ├── PipeConnectState.cs            <- enum состояний state machine
│   ├── ConnectionGraph.cs             <- граф соединённых элементов
│   ├── ConnectionEdge.cs              <- ребро графа
│   ├── FittingMapping.cs              <- правило маппинга фитинга
│   ├── FittingMappingRule.cs          <- расширенное правило (FromType, ToType, семейства)
│   ├── ConnectorTypeDefinition.cs     <- определение типа коннектора (code, name, description)
│   └── ParameterDependency.cs         <- зависимость параметра коннектора
├── Services/
│   ├── ServiceHost.cs                   <- статический резолвер DI (для IExternalCommand)
│   ├── Interfaces/
│   │   ├── IRevitContext.cs
│   │   ├── IRevitContextWriter.cs       <- ISP: обновление UIApplication
│   │   ├── ITransactionService.cs
│   │   ├── IElementSelectionService.cs
│   │   ├── IFittingMapper.cs
│   │   ├── IFittingMappingRepository.cs
│   │   ├── IFormulaSolver.cs
│   │   ├── IParameterResolver.cs
│   │   ├── ILookupTableService.cs
│   │   ├── IElementChainIterator.cs
│   │   └── IDialogService.cs
│   └── Implementation/
│       ├── PathfinderService.cs       <- алгоритм Дейкстры для цепочки фитингов
│       ├── FormulaSolver.cs           <- AST-парсер формул Revit
│       ├── FittingMapper.cs           <- подбор фитингов по маппингу
│       ├── JsonFittingMappingRepository.cs <- CRUD маппинга в JSON
│       └── ConnectionSessionManager.cs
└── Math/
    ├── ConnectorAligner.cs            <- вычисление матриц поворота и перемещения
    └── VectorUtils.cs                 <- базовые векторные операции
```

---

## SmartCon.Revit

Реализации интерфейсов Core через Revit API. Единственный проект с прямой зависимостью от Autodesk.Revit.DB.

```
SmartCon.Revit/
├── Context/
│   └── RevitContext.cs                <- IRevitContext
├── Transactions/
│   ├── RevitTransactionService.cs     <- ITransactionService
│   └── SmartConFailurePreprocessor.cs <- IFailuresPreprocessor
├── Selection/
│   ├── FreeConnectorFilter.cs         <- ISelectionFilter
│   ├── ElementSelectionService.cs     <- IElementSelectionService
│   └── ElementChainIterator.cs        <- IElementChainIterator
├── Parameters/
│   ├── RevitParameterResolver.cs      <- IParameterResolver
│   └── RevitLookupTableService.cs     <- ILookupTableService
├── Wrappers/
│   ├── ConnectorWrapper.cs            <- создание ConnectorProxy из Connector
│   └── ElementWrapper.cs
├── Extensions/
│   ├── ConnectorExtensions.cs
│   ├── ElementExtensions.cs
│   └── XYZExtensions.cs
├── Events/
│   ├── ActionExternalEventHandler.cs  <- универсальный Action Queue handler (ADR-008)
│   └── PipeConnectExternalEvent.cs    <- специализированный handler для PipeConnect workflow
└── Transform/
    └── RevitTransformService.cs       <- MoveElement + RotateElement через Revit API
```

---

## SmartCon.UI

Общая WPF-библиотека. Переиспользуемые стили, контролы и базовые классы ViewModel.

```
SmartCon.UI/
├── Styles/
│   ├── SmartConTheme.xaml             <- цвета, шрифты, кнопки
│   └── Controls.xaml                  <- стили общих контролов
├── Controls/
│   └── (кастомные контролы при необходимости)
└── Converters/
    └── BoolToVisibilityConverter.cs

Примечание: ViewModelBase и RelayCommand НЕ нужны — используем CommunityToolkit.Mvvm:
- `ObservableObject` заменяет ViewModelBase
- `RelayCommand` / `AsyncRelayCommand` — из CommunityToolkit
- `[ObservableProperty]` — source generator для свойств
- `[RelayCommand]` — source generator для команд
```

---

## SmartCon.App

Точка входа плагина. DI-контейнер и Ribbon.

```
SmartCon.App/
├── App.cs                             <- IExternalApplication (OnStartup/OnShutdown)
├── Ribbon/
│   └── RibbonBuilder.cs              <- создание кнопок на ленте Revit
├── DI/
│   ├── ServiceLocator.cs             <- IoC-контейнер (MEDI)
│   └── ServiceRegistrar.cs           <- регистрация всех сервисов
└── Resources/
    ├── SmartCon.addin                 <- манифест для Revit
    └── Icons/                         <- иконки для Ribbon
```

---

## SmartCon.PipeConnect

Модуль PipeConnect: команды, ViewModel-ы, окна.

```
SmartCon.PipeConnect/
├── Commands/
│   └── PipeConnectCommand.cs          <- IExternalCommand (точка входа с Ribbon)
├── ViewModels/
│   ├── PipeConnectEditorViewModel.cs  <- ViewModel финального окна (S6)
│   ├── MiniTypeSelectorViewModel.cs   <- ViewModel мини-окна выбора типа
│   └── MappingEditorViewModel.cs      <- ViewModel окна маппинга
├── Views/
│   ├── PipeConnectEditorView.xaml     <- финальное окно PostProcessing
│   ├── MiniTypeSelectorView.xaml      <- мини-окно выбора типа коннектора
│   └── MappingEditorView.xaml         <- окно управления маппингом
└── Services/
    └── PipeConnectDialogService.cs    <- IDialogService (открытие окон)
```

---

## SmartCon.Tests

```
SmartCon.Tests/
├── Core/
│   ├── Models/
│   │   ├── ConnectorProxyTests.cs
│   │   └── ConnectionTypeCodeTests.cs
│   ├── Math/
│   │   ├── VectorUtilsTests.cs
│   │   └── ConnectorAlignerTests.cs
│   └── Services/
│       ├── FormulaSolverTests.cs
│       ├── FittingMapperTests.cs
│       └── PathfinderServiceTests.cs
└── PipeConnect/
    └── ViewModels/
        └── PipeConnectEditorViewModelTests.cs
```
