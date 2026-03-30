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
