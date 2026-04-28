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
├── SmartCon.ProjectManagement <- Модуль ProjectManagement: Share Project (ISO 19650).
├── SmartCon.FamilyManager     <- Модуль FamilyManager: управление библиотекой семейств Revit (dockable panel, SQLite catalog).
├── SmartCon.Tests             <- Unit + ViewModel тесты (xUnit + Moq).
└── SmartCon.Updater           <- Standalone .NET 8 updater: применяет pending
                                   update при закрытии Revit (staging-based).
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
│   ├── PipeConnectSessionContext.cs   <- immutable context для builder
│   ├── PipeConnectState.cs            <- enum состояний state machine
│   ├── ConnectionGraph.cs             <- граф соединённых элементов
│   ├── ConnectionEdge.cs              <- ребро графа
│   ├── FittingMapping.cs              <- правило маппинга фитинга
│   ├── FittingMappingRule.cs          <- расширенное правило (FromType, ToType, семейства)
│   ├── ConnectorTypeDefinition.cs     <- определение типа коннектора
│   ├── ParameterDependency.cs         <- зависимость параметра коннектора
│   ├── FamilyInfo.cs                  <- семейство фитинга (после фильтрации)
│   ├── FamilySizeOption.cs            <- типоразмер для dynamic size
│   ├── IFittingCtcSetupItem.cs        <- абстракция для CTC setup (реализация в PipeConnect.ViewModels)
│   ├── VirtualCtcStore.cs             <- виртуальное хранилище CTC overrides
│   ├── NetworkSnapshot.cs             <- снапшот сети для chain rollback
│   ├── NetworkSnapshotStore.cs        <- хранилище снапшотов
│   ├── CtcGuesser.cs                  <- логика угадывания CTC
│   ├── LookupColumnConstraint.cs      <- ограничение колонки LookupTable
│   ├── UpdateInfo.cs                  <- информация об обновлении
│   └── UpdateSettings.cs              <- настройки обновлений
├── Services/
│   ├── ServiceHost.cs                   <- статический резолвер DI
│   ├── LocalizationService.cs           <- RU/EN локализация строк
│   ├── Interfaces/
│   │   ├── IRevitContext.cs
│   │   ├── IRevitContextWriter.cs
│   │   ├── ITransactionService.cs
│   │   ├── ITransactionGroupSession.cs  <- TransactionGroup session
│   │   ├── IElementSelectionService.cs
│   │   ├── IConnectorService.cs
│   │   ├── ITransformService.cs
│   │   ├── IFittingMapper.cs
│   │   ├── IFittingMappingRepository.cs
│   │   ├── IFittingInsertService.cs     <- вставка фитинга
│   │   ├── IFittingFamilyRepository.cs
│   │   ├── IDynamicSizeResolver.cs      <- dynamic size resolution
│   │   ├── INetworkMover.cs             <- перемещение сети
│   │   ├── IFormulaSolver.cs
│   │   ├── IParameterResolver.cs
│   │   ├── ILookupTableService.cs
│   │   ├── IElementChainIterator.cs
│   │   ├── IDialogService.cs
│   │   ├── IFamilyConnectorService.cs
│   │   ├── IUpdateService.cs
│   │   └── IUpdateSettingsRepository.cs
│   ├── Implementation/
│   │   ├── FormulaSolver.cs             <- AST-парсер формул Revit
│   │   └── FittingMapper.cs             <- подбор фитингов по маппингу
│   └── Storage/                         <- ADR-012: сериализация маппинга (pure C#)
│       ├── FittingMappingJsonSerializer.cs
│       ├── MappingPayload.cs
│       └── Dto/                         <- MappingPayloadDto, ConnectorTypeDto, …
├── Math/
│   ├── Vec3.cs                        <- 3D-вектор (ADR-009)
│   ├── VectorUtils.cs                 <- базовые векторные операции
│   ├── ConnectorAligner.cs            <- вычисление матриц поворота и перемещения
│   └── FormulaEngine/                 <- AST-парсер формул
├── Logging/
│   └── SmartConLogger.cs              <- файловый логгер
├── Compatibility/
│   ├── ElementIdCompat.cs             <- абстракция над 32/64-bit ElementId (multi-version)
│   ├── NetFrameworkCompat.cs          <- polyfills для .NET Framework 4.8
│   └── SimplePriorityQueue.cs         <- lightweight priority queue (no BCL dependency)
└── Constants.cs                       <- Units, Tolerance
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
│   └── ConnectorWrapper.cs            <- создание ConnectorProxy из Connector
├── Extensions/
│   ├── ConnectorExtensions.cs
│   ├── ElementExtensions.cs
│   └── XYZExtensions.cs
├── Events/
│   ├── ActionExternalEventHandler.cs  <- универсальный Action Queue handler (ADR-008)
│   └── PipeConnectExternalEvent.cs    <- специализированный handler для PipeConnect workflow
├── Storage/                            <- ADR-012: per-project ExtensibleStorage
│   ├── FittingMappingSchema.cs        <- SchemaBuilder (GUID, VendorId, fields)
│   └── RevitFittingMappingRepository.cs <- IFittingMappingRepository (DataStorage)
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

Модуль PipeConnect: команды, ViewModel-ы, окна, сервисы.

```
SmartCon.PipeConnect/
├── Commands/
│   ├── PipeConnectCommand.cs          <- IExternalCommand (точка входа)
│   └── AboutCommand.cs                <- IExternalCommand (About)
├── ViewModels/
│   ├── PipeConnectEditorViewModel.cs       <- core: Init, rotation, size, fields
│   ├── PipeConnectEditorViewModel.Connect.cs <- Connect, Cancel, Validate
│   ├── PipeConnectEditorViewModel.Insert.cs  <- InsertFitting, InsertReducer, CTC
│   ├── PipeConnectEditorViewModel.Chain.cs   <- ChainDepth, ConnectAllChain
│   ├── PipeConnectEditorViewModel.Ctc.cs     <- Virtual CTC helpers
│   ├── AboutViewModel.cs                   <- About window VM
│   ├── MappingEditorViewModel.cs           <- Settings window VM
│   ├── MiniTypeSelectorViewModel.cs        <- Type selector VM
│   ├── FamilySelectorViewModel.cs          <- Family picker VM
│   ├── FittingCtcSetupViewModel.cs         <- CTC setup VM
│   ├── FittingCardBuilder.cs               <- Builds FittingCardItem lists
│   ├── FittingCardItem.cs                  <- Fitting/reducer card for UI
│   ├── ConnectorItem.cs                    <- Connector display item
│   ├── ConnectorTypeItem.cs                <- Type display item
│   └── MappingRuleItem.cs                  <- Rule display item
├── Views/
│   ├── PipeConnectEditorView.xaml       <- PostProcessing editor
│   ├── MappingEditorView.xaml           <- Settings (tabs: types + rules)
│   ├── MiniTypeSelectorView.xaml        <- Type selector
│   ├── FamilySelectorView.xaml          <- Family picker
│   ├── FittingCtcSetupView.xaml         <- CTC assignment
│   └── AboutView.xaml                   <- About + updates
├── Services/
│   ├── PipeConnectSessionBuilder.cs     <- S1-S6 session construction
│   ├── PipeConnectDialogService.cs      <- IDialogService implementation (через IDialogPresenter)
│   ├── PipeConnectDiagnostics.cs        <- Connector state logging
│   ├── ConnectExecutor.cs               <- Validate + ConnectTo execution
│   ├── ChainOperationHandler.cs         <- Chain increment/decrement
│   ├── ConnectorCycleService.cs         <- Connector cycling + alignment
│   ├── DynamicSizeLoader.cs             <- Dynamic size loading
│   ├── CtcResolutionService.cs          <- CTC resolution (extracted)
│   ├── CtcGuessService.cs               <- CTC auto-guessing
│   ├── CtcFamilyWriter.cs               <- Write CTC to family via EditFamily
│   ├── FittingCtcManager.cs             <- Facade over CTC services
│   ├── PipeConnectInitHandler.cs        <- Init: disconnect, align, sizing
│   ├── PipeConnectRotationHandler.cs    <- Rotation execution
│   ├── PipeConnectSizeHandler.cs        <- Size change execution
│   ├── PositionCorrector.cs             <- Post-connect position correction
│   ├── IPipeConnectViewModelFactory.cs  <- A-1: factory interface (eliminates Service Locator)
│   ├── PipeConnectViewModelFactory.cs   <- default factory impl
│   ├── IAboutViewModelFactory.cs
│   ├── AboutViewModelFactory.cs
│   ├── ISettingsViewModelFactory.cs
│   ├── SettingsViewModelFactory.cs
│   ├── LanguageManager.cs               <- ResourceDictionary swap + WeakReference window tracking
│   └── StringLocalization.cs            <- программные RU/EN ResourceDictionary
└── Events/
    └── PipeConnectExternalEvent.cs      <- IExternalEventHandler
```

> **Примечание:** `BoolToVisibilityConverter` раньше существовал в двух местах
> (SmartCon.UI и SmartCon.PipeConnect). После B-6 локальный дубликат в
> PipeConnect удалён — используется версия из `SmartCon.UI.Converters`.

---

## SmartCon.ProjectManagement

Модуль ProjectManagement: автоматизация шаринга проектов по ISO 19650.
Commands, ViewModels, Views для операции Share Project.

```
SmartCon.ProjectManagement/
├── Commands/
│   ├── ShareProjectCommand.cs
│   └── ShareSettingsCommand.cs
├── ViewModels/
│   ├── ShareSettingsViewModel.cs
│   ├── ShareProgressViewModel.cs
│   ├── ExportNameDialogViewModel.cs
│   ├── FileNameBlockItem.cs
│   ├── ViewSelectionItem.cs
│   ├── ExportMappingItem.cs
│   ├── FieldDefinitionItem.cs
│   ├── ParseRuleViewModel.cs
│   ├── FieldLibraryViewModel.cs
│   ├── AllowedValuesViewModel.cs
│   └── EnumOption.cs
├── Views/
│   ├── ShareSettingsView.xaml/.cs
│   ├── ShareProgressView.xaml/.cs
│   ├── ExportNameDialog.xaml/.cs
│   ├── ParseRuleView.xaml/.cs
│   ├── FieldLibraryView.xaml/.cs
│   └── AllowedValuesView.xaml/.cs
└── Services/
    ├── IShareSettingsViewModelFactory.cs
    └── ShareSettingsViewModelFactory.cs
```

> **Примечание:** Интерфейсы и модели — в SmartCon.Core.
> Реализации Revit API — в SmartCon.Revit/Sharing/ и SmartCon.Revit/Storage/.
> SmartCon.ProjectManagement зависит только от Core + UI (как PipeConnect).

---

## SmartCon.FamilyManager

Модуль FamilyManager: управление библиотекой семейств Revit через dockable panel.

```
SmartCon.FamilyManager/
├── Commands/
│   └── FamilyManagerToggleCommand.cs   <- IExternalCommand для показа/скрытия панели
├── ViewModels/
│   ├── FamilyManagerViewModel.cs       <- основной VM каталога
│   ├── FamilyImportViewModel.cs        <- VM импорта семейств
│   └── FamilyLoadViewModel.cs          <- VM загрузки в проект
├── Views/
│   ├── FamilyManagerView.xaml/.cs      <- dockable panel (IDockablePaneProvider)
│   ├── FamilyImportView.xaml/.cs       <- окно импорта
│   └── FamilyLoadView.xaml/.cs         <- окно загрузки
├── Services/
│   ├── LocalCatalogProvider.cs         <- IWritableFamilyCatalogProvider
│   ├── FamilyFileResolver.cs           <- IFamilyFileResolver
│   ├── FamilyImportService.cs          <- IFamilyImportService
│   ├── LocalProjectFamilyUsageRepository.cs <- IProjectFamilyUsageRepository
│   ├── FamilyManagerDialogService.cs   <- IFamilyManagerDialogService
│   └── IFamilyManagerViewModelFactory.cs
└── Events/
    └── FamilyManagerExternalEvent.cs   <- IExternalEventHandler
```

> **Примечание:** Интерфейсы и модели — в SmartCon.Core.
> Реализации Revit API (IFamilyLoadService, IFamilyMetadataExtractionService) — в SmartCon.Revit/FamilyManager/.
> SmartCon.FamilyManager зависит только от Core + UI (как PipeConnect и ProjectManagement).

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
├── PipeConnect/
│   └── ViewModels/
│       └── PipeConnectEditorViewModelTests.cs
└── ProjectManagement/
    ├── FileNameParserTests.cs
    ├── ShareSettingsJsonSerializerTests.cs
    ├── PurgeOptionsTests.cs
    └── ShareSettingsViewModelTests.cs
```
