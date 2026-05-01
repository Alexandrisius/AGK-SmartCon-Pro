# Инварианты — жёсткие правила

> Загружать: **ВСЕГДА**. Нарушение любого инварианта = баг или Fatal Error в Revit.

---

## I-01: Потокобезопасность Revit API

Revit API **однопоточен**. Любой вызов API из WPF-потока — **только** через `IExternalEventHandler.Execute()`.

**Запрещено:**
- Вызов Revit API из `async/await`
- Вызов Revit API из `Task.Run()`
- Вызов Revit API из обработчиков WPF-событий напрямую

**Правильно:**
```csharp
// WPF UI thread:
_externalEvent.Raise();
// Revit main thread (Execute):
_transactionService.RunInTransaction("name", doc => { /* Revit API здесь */ });
```

---

## I-02: Внутренние единицы

Весь Core работает в **decimal feet** (Internal Units Revit).

Конвертация в мм/м — **только** в точках входа (UI-слой):
```csharp
UnitUtils.ConvertToInternalUnits(value, UnitTypeId.Millimeters)
UnitUtils.ConvertFromInternalUnits(value, UnitTypeId.Millimeters)
```

---

## I-03: Транзакции через сервис

Создавать `new Transaction(doc)` напрямую **запрещено** вне `RevitTransactionService`.

Весь код пишет только:
```csharp
_transactionService.RunInTransaction("Name", doc => { ... });
```

Каждый `Transaction` / `TransactionGroup` оборачивается в `using`-блок внутри сервиса.

### I-03b: Исключение для Family Documents

`new Transaction(familyDoc, ...)` **разрешён** при работе с family document,
полученным через `doc.EditFamily(family)`. Family document — это отдельный
`Document`, не управляемый `ITransactionService` (у него свой стек транзакций).

**Где применяется:**
- `FittingCtcManager.ApplyFittingCtcToFamily` — запись CTC описаний коннекторов в family.
- `RevitFamilyConnectorService.SetConnectorTypeCode` — запись описания коннектора в family.

**Правило:** `new Transaction` используется только для family doc. Проектный
`Document` всегда через `ITransactionService`. После commit/load family doc
закрывается (`familyDoc.Close(false)`).

---

## I-04: Assimilate для PipeConnect

Вся операция соединения труб оборачивается в `TransactionGroup`:
- **Соединить** → `TransactionGroup.Assimilate()` — одна запись Undo (Ctrl+Z)
- **Отмена** → `TransactionGroup.RollBack()` — полный откат

---

## I-05: Хранение ссылок

Объекты `Element`, `Connector`, `FamilySymbol` — **не хранить** между транзакциями.

**Хранить:** только `ElementId` (или `UniqueId` для workshared-проектов).

**Запрашивать:** актуальный объект через `doc.GetElement(id)` в начале каждой операции.

---

## I-06: Устаревший API единиц

`DisplayUnitType` **удалён** начиная с Revit 2022. Использовать только:
- `UnitTypeId` (например `UnitTypeId.Millimeters`)
- `ForgeTypeId`

---

## I-07: IFailuresPreprocessor

Все транзакции в `RevitTransactionService` подключают `SmartConFailurePreprocessor`.

Он тихо подавляет ожидаемые предупреждения:
- "Element is slightly off axis"
- Другие предупреждения при перемещении/повороте

```csharp
var options = transaction.GetFailureHandlingOptions();
options.SetFailuresPreprocessor(new SmartConFailurePreprocessor());
transaction.SetFailureHandlingOptions(options);
```

---

## I-08: ConnectorType.Curve

Коннекторы с `ConnectorType == ConnectorType.Curve` (врезки) **исключаются** из фильтра выбора `FreeConnectorFilter`.

`CoordinateSystem.BasisZ` у них может давать неверное направление.

---

## I-09: Dependency Rule (чистота Core)

`SmartCon.Core` ссылается на RevitAPI.dll **только как compile-time reference** (CopyLocal = false).

**Разрешено в Core:**
- Использовать Revit **value-типы как data carriers** в моделях и сигнатурах интерфейсов: `ElementId`, `XYZ`, `Domain`, `BuiltInParameter`, `ForgeTypeId`
- Использовать `Document` как **opaque parameter** в интерфейсах (передаётся, но не вызываются его методы). Примеры: `IShareProjectSettingsRepository.Load(Document doc)`
- `using Autodesk.Revit.DB;` — **только** в файлах моделей и интерфейсов для объявления типов
- Для чистой математики (VectorUtils, ConnectorAligner) использовать `Vec3` вместо `XYZ` (ADR-009). Конвертация на границе Revit-слоя.

**Запрещено в Core:**
- **Вызывать** методы Revit API (`doc.GetElement()`, `Element.get_Parameter()`, `Transaction`, и т.д.)
- `using Autodesk.Revit.UI;`
- `using System.Windows;`
- Создавать экземпляры Revit-классов (кроме `new ElementId(long)`)

**Принцип:** Core *описывает* контракты через Revit-типы, но никогда не *вызывает* Revit API. Вся логика вызовов — в `SmartCon.Revit`.

Проверять при каждом коммите.

---

## I-10: MVVM без Code-Behind

Файлы `*.xaml.cs` содержат **только**:
```csharp
public partial class SomeView : Window
{
    public SomeView(SomeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
```

- Вся логика — во ViewModel (наследуют `ObservableObject` из CommunityToolkit.Mvvm)
- Свойства — через `[ObservableProperty]` source generator
- Команды — через `[RelayCommand]` source generator или `RelayCommand` из CommunityToolkit
- Все биндинги — через `{Binding}` в XAML
- Открытие окон — через `IDialogService`, не `new Window().ShowDialog()`

---

## I-11: ElementIdCompat — единственный RevitAPI-зависимый класс в Core

`ElementIdCompat` в `SmartCon.Core/Compatibility/` — единственный допустимый класс в Core, зависящий от RevitAPI (carrier-тип `ElementId`). Новые классы с RevitAPI-зависимостью в Core — запрещены без явного ревью архитектора.

**Мотивация:** Multi-version support (Revit 2021-2025) требует абстракции над различиями 32/64-bit ElementId. `ElementIdCompat` решает это через `#if REVIT2024_OR_GREATER`.

---

## I-12: DataGridColumn.Header — программная установка

`DataGridColumn` не наследует от `FrameworkElement`, поэтому `{DynamicResource}` в `Header` не резолвится когда `Application.Current == null` (Revit не создаёт WPF Application).

**Правильно:** задавать заголовки программно через `x:Name` в code-behind:

```xml
<!-- XAML -->
<DataGridTextColumn x:Name="ColCode" Binding="{Binding Code}" Width="80">
```

```csharp
// Code-behind
ColCode.Header = LanguageManager.GetString(StringLocalization.Keys.Col_Code);
```

**Запрещено:** `Header="{DynamicResource Col_Code}"` — работает только на net8.0 (Revit 2025), молча пусто на net48 (Revit 2021-2024).

Подробнее: [`multi-version-guide.md`](multi-version-guide.md), Правило 4.

---

## I-13: Маппинг фитингов хранится только в ExtensibleStorage активного проекта

Настройки `ConnectorTypes` и `FittingMappingRules` хранятся исключительно в
`DataStorage` (`ExtensibleStorage.Schema` = `SmartConFittingMappingSchema`)
текущего Revit `Document` — ADR-012.

**Запрещено:**
- Автоматически читать/мигрировать данные из `%APPDATA%\AGK\SmartCon\connector-mapping.json`.
- Открывать второй файловый кэш параллельно с DataStorage (единый источник правды).
- Обращаться к ExtensibleStorage напрямую из Core — только через
  `IFittingMappingRepository` (реализация `RevitFittingMappingRepository`
  в `SmartCon.Revit/Storage/`).

**Разрешено:**
- Ручной Import/Export JSON через окно Settings (`ShowOpenJsonDialog` /
  `ShowSaveJsonDialog`). Диалог Импорта может по умолчанию открываться в
  AppData-папке для удобства миграции.
- Читать DataStorage напрямую для диагностики (AccessLevel = `Public`),
  но не писать (WriteAccess = `Vendor`).

**Мотивация:** Разные `.rvt` содержат разные семейства фитингов → правила
должны быть привязаны к проекту и путешествовать вместе с моделью.
Авто-миграция из AppData создаёт «невидимые» импорты, которые
дезориентируют пользователя и могут загрузить устаревшие правила в новый
проект.

Подробнее: [`adr/012-per-project-extensible-storage.md`](adr/012-per-project-extensible-storage.md).

---

## I-14: SQLite Thread Safety

Все SQLite-операции идут через `LocalCatalogDatabase`:

- WAL journal mode для конкурентных чтений
- Только один writer одновременно (ограничение SQLite)
- `LocalCatalogDatabase.SwitchToPath` защищён lock-ом
- `new SqliteConnection()` вне `LocalCatalogDatabase` **запрещён**

---

## I-15: Dockable Panel Lifecycle

- `FamilyManagerPaneProvider` — singleton, регистрируется один раз в `OnStartup`
- Панель **не пересоздаётся** при клике на кнопку — только `Show()`/`Hide()` через `DockablePane`
- ViewModel — singleton на экземпляр панели
- Состояние панели сохраняется между show/hide

---

## I-16: Managed Storage Immutability

`.rfa` файлы в managed storage (`%APPDATA%\SmartCon\FamilyManager\databases\{id}\storage\`) — **read-only** после импорта:

- Прямое изменение файлов **запрещено** — изменения = новая версия (ADR-016)
- `Sha256FileHasher` верифицирует целостность файла при чтении
- `IFamilyFileResolver` — единственная точка входа для доступа к файлам
